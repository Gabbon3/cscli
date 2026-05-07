using System.IO.Enumeration;
using System.Threading.Channels;
using lib.io;
using lib.utils;
using lib.console;
using lib.io.stack;
using Spectre.Console;

namespace plugins.move
{
    class MovePlugin : Plugin
    {
        public override string Name => "move";
        public override string Description => "Tool massivo ad altissime prestazioni per lo spostamento di file e cartelle";

        // dimensione padding per prevenire false-sharing su MovedFilesCountList
        private const int CounterStride = 8;
        private const int FlushMask = 127;
        private MoveState State = new();

        // # Stato interno
        /// <summary>
        /// Contiene lo stato completo dell'operazione di spostamento.
        /// Include configurazioni, canali di comunicazione e contatori di progresso.
        /// </summary>
        private class MoveState
        {
            public string SourcePath { get; set; } = "";
            public string DestinationPath { get; set; } = "";

            public bool IsDebug { get; set; }
            public bool IsRecursive { get; set; }
            public bool Overwrite { get; set; }
            public int ProducerCount { get; set; }
            public int ConsumerCount { get; set; }
            public FileSystemFilter? FileFilter { get; set; }

            // Canali di comunicazione
            public Channel<string>? DirectoryChannel { get; set; }
            public Channel<StackFileInfo>? FileChannel { get; set; }

            public long[] MovedFilesCountList { get; set; } = [];
            public long[] BytesMovedList { get; set; } = [];
            public bool IsProcessing { get; set; } = true;

            // WaitGroup per il producer: tiene traccia delle cartelle in sospeso
            public int ActiveDirCount;
        }

        // # Esecuzione Principale
        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            var settings = ParseSettings<MoveSettings>(args);

            // Controlliamo che entrambi gli argomenti fixed siano stati forniti
            if (args.Contains("--help") || string.IsNullOrEmpty(settings.SourcePath) || string.IsNullOrEmpty(settings.DestinationPath))
            {
                Help();
                return;
            }

            State = new MoveState();

            // 1. parsing e validazione delle settings
            if (!ParseAndValidateSettings(settings))
            {
                return;
            }

            ConsolePlus.Write($"[Cyan]#[/] Avvio spostamento verso [Yellow]{State.DestinationPath}[/] ... {(State.IsDebug ? "(DEBUG)" : "")}");

            // 2. inizializzo i canali di comunicazione
            InitializeChannels();

            // 3. inizializzo i task dei producer (scannerizzano l'albero in BFS)
            var producerTasks = CreateProducerTasks(ct);

            // 4. inizializzo i task dei consumer (spostano i file direttamente)
            var consumerTasks = CreateConsumerTasks(ct);

            // 5. inizializzo il task per il monitor UI
            var monitorTask = CreateMonitorTask(ct);

            // # gestione chiusura channels e tasks
            // attendo che i consumer svuotino il channel
            await Task.WhenAll(consumerTasks);
            // i consumer hanno finito, l'operazione di move è tecnicamente completa.
            State.IsProcessing = false;
            await monitorTask;
            await Task.WhenAll(producerTasks);

            // 6. stampa statistiche finali
            PrintFinalStatistics();
        }

        // # ---------------------------------- #
        // Parsing e validazione Settings
        // # ---------------------------------- #

        /// <summary>
        /// Analizza e valida i parametri di input per sorgente e destinazione.
        /// Accede a: State (per popolarlo)
        /// </summary>
        private bool ParseAndValidateSettings(MoveSettings settings)
        {
            // Valida Sorgente (deve esistere)
            string? sourcePath = ParsePath(settings.SourcePath, checkPath: true);
            if (string.IsNullOrEmpty(sourcePath)) return false;

            // Valida Destinazione (non è detto che esista, la creiamo se manca)
            string? destPath = ParsePath(settings.DestinationPath, checkPath: false);
            if (string.IsNullOrEmpty(destPath)) return false;

            // Evita un loop infinito se si cerca di spostare una cartella dentro se stessa
            if (destPath.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                PrintError("La destinazione non può essere una sottocartella della cartella di origine.");
                return false;
            }

            State.SourcePath = sourcePath;
            State.DestinationPath = destPath;
            State.IsDebug = settings.Debug;
            State.IsRecursive = settings.Recursive;
            State.Overwrite = settings.Overwrite;

            // Calcolo della distribuzione thread: 1/4 producer, 3/4 consumer
            // Nel caso non-recursive: 1 producer, tutti gli altri consumer
            int totalThreads = settings.Threads ?? Environment.ProcessorCount;
            if (!State.IsRecursive)
            {
                State.ProducerCount = 1;
                State.ConsumerCount = totalThreads - 1;
            }
            else
            {
                State.ProducerCount = Math.Max(1, totalThreads / 4);
                State.ConsumerCount = totalThreads - State.ProducerCount;
            }

            var filterOpts = new FileFilterFactory.FilterOptions(
                Pattern: ParseMatchPattern(settings.Pattern),
                MatchType: settings.FixedMatch ? FilterFileNameMatchType.Fixed : FilterFileNameMatchType.Regex,
                IgnoreCase: settings.IgnoreCase,
                ModifiedBefore: settings.OlderThan,
                ModifiedAfter: settings.Since
            );

            State.FileFilter = FileFilterFactory.CreateFilter(filterOpts);

            // Inizializza gli array per i contatori thread-safe
            State.MovedFilesCountList = new long[State.ConsumerCount * CounterStride];
            State.BytesMovedList = new long[State.ConsumerCount * CounterStride];

            // Creazione cartella di destinazione root
            if (!State.IsDebug && !Directory.Exists(State.DestinationPath))
            {
                try { Directory.CreateDirectory(State.DestinationPath); }
                catch (Exception ex)
                {
                    PrintError($"Impossibile creare la directory di destinazione: {ex.Message}");
                    return false;
                }
            }

            return true;
        }

        // # ---------------------------------- #
        // Inizializzazione dei Canali
        // # ---------------------------------- #

        /// <summary>
        /// Crea i due canali di comunicazione:
        /// - DirectoryChannel (unbounded): producer si passano le cartelle da scansionare
        /// - FileChannel (bounded): producer spedisce file ai consumer
        /// </summary>
        private void InitializeChannels()
        {
            // Canale delle directory non limitato, perché inviamo cartelle quando le scopriamo
            // durante l'enumerazione e non vogliamo bloccare il producer
            State.DirectoryChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleWriter = false,
                SingleReader = false
            });

            // Canale dei file limitato per backpressure: evita che i producer spediscano
            // troppi file contemporaneamente se i consumer sono lenti
            State.FileChannel = Channel.CreateBounded<StackFileInfo>(new BoundedChannelOptions(50000)
            {
                SingleWriter = false,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            State.ActiveDirCount = 1;
            // Incoda la cartella di partenza (root sorgente)
            State.DirectoryChannel.Writer.TryWrite(State.SourcePath);
        }

        // # ---------------------------------- #
        // Creazione Task dei Producer (BFS)
        // # ---------------------------------- #

        /// <summary>
        /// Crea i task producer che enumerano l'albero in BFS.
        /// Ogni producer:
        /// 1. Pesca una cartella dal DirectoryChannel
        /// 2. Enumera i file del livello 0 (non ricorsivo)
        /// 3. Spedisce i file idonei al FileChannel
        /// 4. Pushes le subdirectory nel DirectoryChannel per altri producer
        /// </summary>
        private Task[] CreateProducerTasks(CancellationToken ct)
        {
            var tasks = new Task[State.ProducerCount];

            int sourceRootLength = State.SourcePath.Length;
            if (!State.SourcePath.EndsWith(Path.DirectorySeparatorChar))
            {
                sourceRootLength++;
            }

            for (int i = 0; i < State.ProducerCount; i++)
            {
                int producerId = i;
                tasks[i] = Task.Run(async () =>
                {
                    try
                    {
                        // leggo dal channel finche vive
                        await foreach (var currentDir in State.DirectoryChannel!.Reader.ReadAllAsync(ct))
                        {
                            try
                            {
                                var enumOptions = new EnumerationOptions
                                {
                                    IgnoreInaccessible = true,
                                    RecurseSubdirectories = false,
                                    BufferSize = 64 * 1024
                                };

                                // Traccia se abbiamo già creato la cartella di destinazione per questa source dir
                                bool destinationDirCreated = false;
                                string? currentDestDir = null;

                                // Enumera il livello 0 della cartella corrente
                                var entries = new FileSystemEnumerable<StackFileInfo>(
                                    currentDir,
                                    (ref FileSystemEntry entry) => new StackFileInfo(ref entry),
                                    enumOptions
                                )
                                {
                                    ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                                    {
                                        ct.ThrowIfCancellationRequested();

                                        // Se è una directory, la pusho nel channel per un altro producer
                                        if (entry.IsDirectory)
                                        {
                                            // WaitGroup +1
                                            Interlocked.Increment(ref State.ActiveDirCount);
                                            if (!State.DirectoryChannel.Writer.TryWrite(Path.Combine(currentDir, entry.FileName.ToString())))
                                            {
                                                // REVERT SE FALLISCE WaitGroup -1
                                                Interlocked.Decrement(ref State.ActiveDirCount);
                                            }
                                            return false;
                                        }

                                        // è un file: controlla i filtri
                                        if (State.FileFilter != null && !State.FileFilter(ref entry))
                                        {
                                            return false;
                                        }

                                        // Il file passa i filtri: creazione lazy della cartella di destinazione
                                        // SOLO SE NO DEBUG
                                        if (!State.IsDebug && !destinationDirCreated)
                                        {
                                            if (State.IsRecursive)
                                            {
                                                // Calcola il percorso relativo della cartella
                                                ReadOnlySpan<char> currentDirSpan = currentDir.AsSpan();
                                                ReadOnlySpan<char> relativeSpan = currentDirSpan.Length > sourceRootLength
                                                    ? currentDirSpan[sourceRootLength..]
                                                    : [];

                                                currentDestDir = relativeSpan.IsEmpty
                                                    ? State.DestinationPath
                                                    : Path.Combine(State.DestinationPath, relativeSpan.ToString());
                                            }
                                            else
                                            {
                                                currentDestDir = State.DestinationPath;
                                            }

                                            // Crea la cartella di destinazione
                                            if (!Directory.Exists(currentDestDir))
                                            {
                                                Directory.CreateDirectory(currentDestDir);
                                            }

                                            destinationDirCreated = true;
                                        }

                                        return true;
                                    }
                                };

                                // per ogni file passato
                                foreach (var entry in entries)
                                {
                                    try
                                    {
                                        if (State.IsDebug)
                                        {
                                            ConsolePlus.Write($"[DarkGray]{entry.AsDirectorySpan()}{Path.DirectorySeparatorChar}[Cyan]{entry.AsNameSpan()}[/]");
                                            continue;
                                        }
                                        await State.FileChannel!.Writer.WriteAsync(entry, ct);
                                    }
                                    catch (ChannelClosedException) { /* canale chiuso */ }
                                }
                            }
                            catch (OperationCanceledException) { }
                            catch (Exception ex)
                            {
                                PrintError($"\n[Errore Producer {producerId}]: {ex.Message}");
                            }
                            finally
                            {
                                // DECREMENTO A FINE SCANSIONE CARTELLA E CONTROLLO CHIUSURA
                                if (Interlocked.Decrement(ref State.ActiveDirCount) == 0)
                                {
                                    State.DirectoryChannel.Writer.TryComplete();
                                    State.FileChannel!.Writer.TryComplete();
                                }
                            }
                        }
                    }
                    finally { }
                }, ct);
            }

            return tasks;
        }

        // # ---------------------------------- #
        // Creazione Task dei Consumer
        // # ---------------------------------- #

        /// <summary>
        /// Crea i task consumer che leggono dal FileChannel e spendono i file direttamente.
        /// I consumer NON si preoccupano di creare le cartelle: i producer l'hanno già fatto.
        /// </summary>
        private Task[] CreateConsumerTasks(CancellationToken ct)
        {
            var tasks = new Task[State.ConsumerCount];

            for (int i = 0; i < State.ConsumerCount; i++)
            {
                int consumerId = i;
                tasks[i] = Task.Run(async () =>
                {
                    long localFlushMoved = 0;
                    long localFlushBytes = 0;
                    int slot = consumerId * CounterStride;
                    // definisco un buffer riutilizzabile
                    char[] pathBuffer = new char[1024];

                    try
                    {
                        await foreach (var item in State.FileChannel!.Reader.ReadAllAsync(ct))
                        {
                            try
                            {
                                // creo lo span dentro il ciclo
                                // muore ad ogni iterazione
                                Span<char> targetFullPath = pathBuffer.AsSpan();
                                Span<char> remaining = targetFullPath;

                                ReadOnlySpan<char> name = item.AsNameSpan();

                                if (State.IsRecursive)
                                {
                                    int sourceRootLength = State.SourcePath.Length;
                                    ReadOnlySpan<char> sourceDirSpan = item.AsDirectorySpan();

                                    remaining = remaining
                                        .AppendNext(State.DestinationPath.AsSpan())
                                        .AppendNext(Path.DirectorySeparatorChar);

                                    // aggiungo relative path solo se necessario
                                    if (sourceDirSpan.Length > sourceRootLength)
                                    {
                                        ReadOnlySpan<char> relativeSpan = sourceDirSpan[sourceRootLength..];
                                        remaining = remaining
                                            .AppendNext(relativeSpan)
                                            .AppendNext(Path.DirectorySeparatorChar);
                                    }

                                    remaining = remaining.AppendNext(name);
                                }
                                else
                                {
                                    remaining = remaining
                                        .AppendNext(State.DestinationPath.AsSpan())
                                        .AppendNext(Path.DirectorySeparatorChar)
                                        .AppendNext(name);
                                }

                                // calcolo automatico della dimensione effettiva del path
                                int actualPathSize = targetFullPath.Length - remaining.Length;

                                // Spostamento fisico
                                File.Move(item.GetFullPath(), targetFullPath[..actualPathSize].ToString(), State.Overwrite);

                                // Aggiornamento contatori
                                localFlushMoved++;
                                localFlushBytes += item.Length;

                                // Flush ogni 512 file
                                if ((localFlushMoved & FlushMask) == 0)
                                {
                                    State.MovedFilesCountList[slot] += localFlushMoved;
                                    State.BytesMovedList[slot] += localFlushBytes;
                                    localFlushMoved = 0;
                                    localFlushBytes = 0;
                                }

                                ct.ThrowIfCancellationRequested();
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception) { /* errore move o altro */ }
                            finally
                            {
                                item.Dispose();
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    finally
                    {
                        // Flush dei dati rimasti
                        State.MovedFilesCountList[slot] += localFlushMoved;
                        State.BytesMovedList[slot] += localFlushBytes;
                    }
                }, ct);
            }

            return tasks;
        }

        // # ---------------------------------- #
        // Creazione del Monitor UI
        // # ---------------------------------- #

        /// <summary>
        /// Monitor UI con Spectre.Console per le metriche di spostamento
        /// </summary>
        private Task CreateMonitorTask(CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                if (State.IsDebug) return;

                long lastTotalMoved = 0;
                long lastTotalBytes = 0;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    // Usiamo un contenitore generico all'inizio
                    await AnsiConsole.Live(new Text(""))
                        .Cropping(VerticalOverflowCropping.Bottom)
                        .StartAsync(async ctx =>
                        {
                            while (State.IsProcessing && !ct.IsCancellationRequested)
                            {
                                long currentTotalMoved = 0;
                                long currentTotalBytes = 0;

                                // 1. GRID DEI WORKER (Colonne fisse per la precisione)
                                var workerGrid = new Grid()
                                    .AddColumn(new GridColumn().Width(6).NoWrap())
                                    .AddColumn(new GridColumn().Width(10).RightAligned().NoWrap())
                                    .AddColumn(new GridColumn().NoWrap().LeftAligned());

                                for (int i = 0; i < State.ConsumerCount; i++)
                                {
                                    long totalMoved = Volatile.Read(ref State.MovedFilesCountList[i * CounterStride]);
                                    long totalBytes = Volatile.Read(ref State.BytesMovedList[i * CounterStride]);

                                    currentTotalMoved += totalMoved;
                                    currentTotalBytes += totalBytes;

                                    long currentProgress = totalMoved % 4096;
                                    int dashesCount = (int)(currentProgress / 102);
                                    string bar = new string('-', dashesCount).PadRight(40, ' ');

                                    workerGrid.AddRow(
                                        $"[yellow]C-{i:D2}[/]",
                                        $"[cyan]{totalMoved}[/]",
                                        $"[grey]|[/][green]{bar}[/][grey]|[/]"
                                    );
                                }

                                // Calcoli velocità
                                double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                                double filesPerSecond = elapsedSeconds > 0 ? (currentTotalMoved - lastTotalMoved) / elapsedSeconds : 0;
                                double bytesPerSecond = elapsedSeconds > 0 ? (currentTotalBytes - lastTotalBytes) / elapsedSeconds : 0;
                                lastTotalMoved = currentTotalMoved;
                                lastTotalBytes = currentTotalBytes;
                                stopwatch.Restart();

                                // 2. GRID DEL SOMMARIO (Una sola colonna larga che NON wrappa)
                                var summaryGrid = new Grid().AddColumn(new GridColumn().NoWrap());
                                summaryGrid.AddEmptyRow();

                                summaryGrid.AddRow($"[magenta]>[/] [white]Totale Spostati :[/] [cyan]{currentTotalMoved:N0}[/]");
                                summaryGrid.AddRow($"[magenta]>[/] [white]Dati Trasferiti :[/] [magenta]{Formatter.Bytes(currentTotalBytes)}[/]");

                                string speedStr = Formatter.Bytes((long)bytesPerSecond);
                                string fpsStr = $"{(int)filesPerSecond:N0} f/s";
                                summaryGrid.AddRow($"[magenta]>[/] [white]Velocità Rete   :[/] [green]{speedStr}/s[/] [grey]({fpsStr})[/]");

                                // 3. UNIAMO TUTTO (Rows permette di impilare le due Grid)
                                ctx.UpdateTarget(new Rows(workerGrid, summaryGrid));

                                await Task.Delay(250, ct);
                            }
                        });
                }
                catch (TaskCanceledException) { }
            }, ct);
        }

        // # ---------------------------------- #
        // Stampa delle statistiche finali
        // # ---------------------------------- #

        private void PrintFinalStatistics()
        {
            long totalMoved = 0;
            long totalBytesMoved = 0;

            for (int i = 0; i < State.ConsumerCount; i++)
            {
                totalMoved += State.MovedFilesCountList[i * CounterStride];
                totalBytesMoved += State.BytesMovedList[i * CounterStride];
            }

            ConsolePlus.WriteHr(25);
            ConsolePlus.Write($"[Cyan]#[/] Operazione Conclusa.");
            ConsolePlus.Write($"[Cyan]*[/] File spostati   : {totalMoved:N0}");
            ConsolePlus.Write($"[Cyan]*[/] Dati trasferiti : {Formatter.Bytes(totalBytesMoved)}");
            ConsolePlus.WriteHr(25);
        }

        public override void Help()
        {
            PrintHelp<MoveSettings>();
        }
    }
}