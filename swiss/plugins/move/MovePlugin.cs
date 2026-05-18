using System.IO.Enumeration;
using System.Threading.Channels;
using lib.io;
using lib.utils;
using lib.utils.span;
using lib.console;
using lib.io.stack;
using Spectre.Console;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;

namespace plugins.move
{
    class MovePlugin : Plugin
    {
        public override string Name => "move";
        public override string Description => "Tool multithreaded per lo spostamento di file e cartelle";

        // dimensione padding per prevenire false-sharing su MovedFilesCountList
        private const int CounterStride = 8;
        private const int FlushMask = 127;
        private MoveState State = new();
        private Diagnostics Stats = new();
        private ConcurrentBag<MoveException> _errorsBag = [];

        private struct MoveException(string section, Exception ex)
        {
            public string Section { get; set; } = section;
            public string Message { get; set; } = $"{ex.Message} in {ex.Source}";
        }
        #region state
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
            public FileAttributes AttributesToSkip { get; set; }
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
            public int DirectoryCreated;
        }
        #endregion

        /// <summary>
        /// Classe per tracciare alcune statistiche
        /// </summary>
        private class Diagnostics
        {
            public double TempoCreazioneCartelle { get; set; } = 0;
        }
        #region RunAsync
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
            Stats = new Diagnostics();

            // 1. parsing e validazione delle settings
            if (!ParseAndValidateSettings(settings))
            {
                return;
            }

            ConsolePlus.Write($"[Cyan]#[/] Avvio spostamento verso [Yellow]{State.DestinationPath}[/] ... {(State.IsDebug ? "(DEBUG)" : "")}");

            // 2. inizializzo i canali di comunicazione
            InitializeChannels();

            // 3. inizializzo i task dei producer (scannerizzano l'albero in BFS)
            var producerTask = RunProducer(ct);

            // 4. inizializzo i task dei consumer (spostano i file direttamente)
            var consumerTasks = CreateConsumerTasks(ct);

            // 5. inizializzo il task per il monitor UI
            var monitorTask = CreateMonitorTask(ct);

            // # gestione chiusura channels e tasks
            // attendo che i consumer svuotino il channel
            await Task.WhenAll(consumerTasks);
            await producerTask;
            // i consumer hanno finito, l'operazione di move è tecnicamente completa.
            State.IsProcessing = false;
            await monitorTask;

            // 6. stampa statistiche finali
            PrintFinalStatistics();
        }

        // # ---------------------------------- #
        // Parsing e validazione Settings
        // # ---------------------------------- #
        #endregion
        #region parse settings
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

            State.AttributesToSkip = FileAttributes.System;
            if (!settings.IncludeHidden) State.AttributesToSkip |= FileAttributes.Hidden;


            // Calcolo della distribuzione thread: 1/4 producer, 3/4 consumer
            // Nel caso non-recursive: 1 producer, tutti gli altri consumer
            int totalThreads = settings.Threads ?? Environment.ProcessorCount;
            State.ConsumerCount = totalThreads;

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
        /// - FileChannel (bounded): producer spedisce file ai consumer
        /// </summary>
        private void InitializeChannels()
        {
            // Canale dei file limitato per backpressure: evita che i producer spediscano
            // troppi file contemporaneamente se i consumer sono lenti
            State.FileChannel = Channel.CreateBounded<StackFileInfo>(new BoundedChannelOptions(8192)
            {
                SingleWriter = false,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            State.DirectoryCreated = 0;
        }

        // # ---------------------------------- #
        // Creazione Task dei Producer (BFS)
        // # ---------------------------------- #
        #endregion
        #region producer
        /// <summary>
        /// Crea un unico task producer che enumera l'albero sfruttando la ricorsione nativa di .NET.
        /// 1. Delega la navigazione profonda a FileSystemEnumerable.
        /// 2. Filtra i file on-the-fly.
        /// 3. Crea la cartella di destinazione in modalità "lazy", ma ottimizzata tramite SequenceEqual.
        /// 4. Spedisce i file validi al FileChannel per i consumer.
        /// </summary>
        private async Task RunProducer(CancellationToken ct)
        {
            string baseDestination = State.DestinationPath;
            int sourceRootLength = State.SourcePath.Length;
            if (!State.SourcePath.EndsWith(Path.DirectorySeparatorChar))
            {
                sourceRootLength++;
            }

            // mi tengo largo per ospitare qualsiasi albero
            char[] directoryBuffer = ArrayPool<char>.Shared.Rent(4096);

            // La variabile chiave per la lazy creation: ricorda l'ultima cartella creata
            string? lastProcessedSourceDir = null;

            try
            {
                var enumOptions = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    // Lasciamo che sia .NET a gestire la ricorsione in C++ nativo (molto più veloce)
                    RecurseSubdirectories = State.IsRecursive,
                    BufferSize = 64 * 1024,
                    AttributesToSkip = State.AttributesToSkip
                };

                var entries = new FileSystemEnumerable<StackFileInfo>(
                    State.SourcePath,
                    (ref FileSystemEntry entry) => new StackFileInfo(ref entry),
                    enumOptions
                )
                {
                    ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    {
                        ct.ThrowIfCancellationRequested();

                        // DIRECTORY: Le ignoriamo, ci interessano solo i file per il FileChannel
                        if (entry.IsDirectory) return false;

                        // FILE: Filtro
                        if (State.FileFilter != null && !State.FileFilter(ref entry)) return false;

                        // FILE OK: Creazione Lazy Directory ottimizzata
                        if (!State.IsDebug)
                        {
                            ReadOnlySpan<char> currentSourceDir = entry.Directory;

                            // Entriamo nel blocco di creazione SOLO se la cartella è cambiata rispetto al file precedente
                            if (lastProcessedSourceDir == null ||
                                    currentSourceDir.Length != lastProcessedSourceDir.Length ||
                                    !currentSourceDir.SequenceEqual(lastProcessedSourceDir.AsSpan()))
                            {
                                if (State.IsRecursive && currentSourceDir.Length > sourceRootLength)
                                {
                                    ReadOnlySpan<char> relativeSpan = currentSourceDir[sourceRootLength..];

                                    Span<char> remaining = directoryBuffer.AsSpan();
                                    remaining = remaining.PathCombine(baseDestination.AsSpan(), endWithSeparator: true);
                                    remaining = remaining.PathCombine(relativeSpan, endWithSeparator: false);

                                    int writtenChars = directoryBuffer.Length - remaining.Length;
                                    string newDirectory = directoryBuffer.AsSpan(0, writtenChars).ToString();

                                    // profiling sul tempo di creazione della cartella
                                    long startTimestamp = Stopwatch.GetTimestamp();
                                    try
                                    {
                                        Directory.CreateDirectory(newDirectory);
                                        State.DirectoryCreated++;
                                    }
                                    catch (Exception ex)
                                    {
                                        throw new Exception($"errore Directory.CreateDirectory:\n\t[Yellow]{newDirectory}[/]\n\t{ex.Message}");
                                    }
                                    finally
                                    {
                                        // Calcoliamo quanto tempo è passato
                                        TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                                        // Aggiungiamo i millisecondi al totale in modo thread-safe
                                        Stats.TempoCreazioneCartelle += elapsed.TotalMilliseconds;
                                    }
                                }

                                // Aggiorniamo la cache per i prossimi file che troveremo in questa stessa cartella
                                lastProcessedSourceDir = currentSourceDir.ToString();
                            }
                        }

                        return true; // Il file passa allo step successivo
                    }
                };

                // Invio ai consumer: l'enumerazione fisica avviene qui
                foreach (var entry in entries)
                {
                    await State.FileChannel!.Writer.WriteAsync(entry, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _errorsBag.Add(new MoveException("Producer", ex));
                // mi fermo se ce stato un errore nel Producer
                return;
            }
            finally
            {
                // un unico producer: quando finisce lui, ha finito tutto il sistema.
                State.FileChannel?.Writer.TryComplete();

                // Restituzione al pool
                ArrayPool<char>.Shared.Return(directoryBuffer);
            }
        }

        // # ---------------------------------- #
        // Creazione Task dei Consumer
        // # ---------------------------------- #
        #endregion
        #region consumer
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

                                    remaining = remaining.PathCombine(State.DestinationPath.AsSpan(), true);

                                    // aggiungo relative path solo se necessario
                                    if (sourceDirSpan.Length > sourceRootLength)
                                    {
                                        ReadOnlySpan<char> relativeSpan = sourceDirSpan[sourceRootLength..];
                                        remaining = remaining.PathCombine(relativeSpan, true);
                                    }

                                    remaining = remaining.AppendNext(name);
                                }
                                else
                                {
                                    remaining = remaining
                                        .PathCombine(State.DestinationPath.AsSpan())
                                        .PathCombine(name);
                                }

                                // calcolo automatico della dimensione effettiva del path
                                int actualPathSize = targetFullPath.Length - remaining.Length;

                                // Spostamento fisico
                                var destination = targetFullPath[..actualPathSize];
                                try
                                {
                                    NativeIO.Move(item.AsPathSpan(), destination, State.Overwrite);
                                }
                                catch (Exception ex)
                                {
                                    throw new Exception($"errore File.Move da:\n\t[Yellow]{item.AsPathSpan()}[/]\n\ta\n\t[Yellow]{destination}[/]: {ex.Message}");
                                }
                                // Aggiornamento contatori
                                localFlushMoved++;
                                localFlushBytes += item.Length;

                                // Flush
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
                            catch (Exception ex)
                            {
                                _errorsBag.Add(new MoveException("Consumer", ex));
                            }
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
        #endregion
        #region monitor
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
                                    .AddColumn(new GridColumn().Width(4).NoWrap()) // T-00 - thread
                                    .AddColumn(new GridColumn().Width(11).RightAligned().NoWrap()) // 999.999.999 - file spostati
                                    .AddColumn(new GridColumn().NoWrap().LeftAligned()); // barra scorrimento

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
                                        $"[cyan]{totalMoved:N0}[/]",
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

        #endregion
        #region stats
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
            ConsolePlus.Write($"[Cyan]*[/] File spostati   : [Cyan]{totalMoved:N0}[/]");
            ConsolePlus.Write($"[Cyan]*[/] Dati trasferiti : [Green]{Formatter.Bytes(totalBytesMoved)}[/]");
            ConsolePlus.Write($"[Cyan]*[/] Cartelle create : [Cyan]{State.DirectoryCreated:N0}[/]");
            ConsolePlus.Write($"[Cyan]*[/] Tempo cartelle  : [Green]{Stats.TempoCreazioneCartelle:F2} ms[/]");
            ConsolePlus.WriteHr(25);
            // # se ci sono stati errori li leggo
            if (!_errorsBag.IsEmpty)
            {
                ConsolePlus.Write($"[Red]#[/] sono state riscontrate le seguenti eccezioni:");
                int i = 0;
                foreach (var ex in _errorsBag)
                {
                    i++;
                    ConsolePlus.Write($"[Red]#[/] {i}. [Red]{ex.Section}[/]: {ex.Message}\n[Red]#[/]");
                }
            }
        }
        #endregion
        public override void Help()
        {
            PrintHelp<MoveSettings>();
        }
    }
}