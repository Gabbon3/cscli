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
        private int FlushMask = 511;
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
            public int ThreadNumber { get; set; }
            public FileSystemFilter? FileFilter { get; set; }

            public Channel<StackFileInfo>? WorkChannel { get; set; }
            public long[] MovedFilesCountList { get; set; } = [];
            public long[] BytesMovedList { get; set; } = [];
            public bool IsProcessing { get; set; } = true;
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

            // 2. inizializzo il task di producer
            var producerTask = CreateProducerTask(ct);

            // 3. inizializzo i task dei consumer (i workers veri e propri)
            var workers = CreateWorkerTasks(ct);

            // 4. inizializzo il task per il monitor UI
            var monitorTask = CreateMonitorTask(ct);

            // avvio e attendo tutti i workers
            await Task.WhenAll(workers);
            State.IsProcessing = false;
            await monitorTask;
            await producerTask;

            // 5. stampa statistiche finali
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
            State.ThreadNumber = settings.Threads ?? Environment.ProcessorCount;

            var filterOpts = new FileFilterFactory.FilterOptions(
                Pattern: ParseMatchPattern(settings.Pattern),
                MatchType: settings.FixedMatch ? FilterFileNameMatchType.Fixed : FilterFileNameMatchType.Regex,
                IgnoreCase: settings.IgnoreCase,
                ModifiedBefore: settings.OlderThan,
                ModifiedAfter: settings.Since
            );

            State.FileFilter = FileFilterFactory.CreateFilter(filterOpts);

            // Inizializza gli array per i contatori thread-safe
            State.MovedFilesCountList = new long[State.ThreadNumber * CounterStride];
            State.BytesMovedList = new long[State.ThreadNumber * CounterStride];

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
        // Creazione Task del Producer
        // # ---------------------------------- #

        /// <summary>
        /// Crea il task che enumera i file dal file system e li invia al canale di lavoro.
        /// </summary>
        private Task CreateProducerTask(CancellationToken ct)
        {
            State.WorkChannel = Channel.CreateBounded<StackFileInfo>(new BoundedChannelOptions(50000)
            {
                SingleWriter = true,
                SingleReader = false
            });

            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = State.IsRecursive,
                BufferSize = 64 * 1024
            };

            IEnumerable<StackFileInfo> itemsToScan = new FileSystemEnumerable<StackFileInfo>(
                State.SourcePath,
                (ref FileSystemEntry entry) => new StackFileInfo(ref entry),
                enumOptions
            )
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    if (entry.IsDirectory) return false;
                    if (State.FileFilter != null)
                    {
                        return State.FileFilter(ref entry);
                    }
                    return true;
                }
            };

            return Task.Run(async () =>
            {
                try
                {
                    foreach (var item in itemsToScan)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (State.IsDebug)
                        {
                            ConsolePlus.Write($"[Cyan]{item.AsNameSpan()}[/]");
                            item.Dispose();
                        }
                        else
                        {
                            await State.WorkChannel.Writer.WriteAsync(item, ct);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { PrintError($"\n[Errore Scanner]: {ex.Message}"); }
                finally
                {
                    State.WorkChannel.Writer.Complete();
                }
            }, ct);
        }

        // # ---------------------------------- #
        // Creazione dei Task[] dei Workers
        // # ---------------------------------- #

        /// <summary>
        /// Crea i task worker che consumano i file dal canale e li spostano fisicamente.
        /// Ottimizzato per allocare il minimo indispensabile di stringhe.
        /// </summary>
        private Task[] CreateWorkerTasks(CancellationToken ct)
        {
            var workers = new Task[State.ThreadNumber];

            // Pre-calcoliamo le lunghezze per evitare controlli continui nel loop
            int sourceRootLength = State.SourcePath.Length;
            // Assicuriamoci che sourcePath finisca con il separatore per un calcolo corretto del relativo
            if (!State.SourcePath.EndsWith(Path.DirectorySeparatorChar))
            {
                sourceRootLength++;
            }

            for (int i = 0; i < State.ThreadNumber; i++)
            {
                int workerId = i;
                workers[i] = Task.Run(async () =>
                {
                    long localFlushMoved = 0;
                    long localFlushBytes = 0;
                    int slot = workerId * CounterStride;

                    try
                    {
                        await foreach (var item in State.WorkChannel!.Reader.ReadAllAsync(ct))
                        {
                            try
                            {
                                string targetFullPath;

                                // Calcolo della destinazione ZERO-ALLOCATION (finché possibile)
                                if (State.IsRecursive)
                                {
                                    // 1. Otteniamo il percorso completo in formato Span (senza allocare stringhe)
                                    // StackFileInfo.AsSpan() deve restituire il path completo
                                    ReadOnlySpan<char> srcSpan = item.AsPathSpan();

                                    // 2. Estraiamo la parte relativa tagliando la radice della sorgente
                                    // (Questo equivale a Path.GetRelativePath ma senza allocare)
                                    ReadOnlySpan<char> relativeSpan = srcSpan.Length > sourceRootLength
                                        ? srcSpan[sourceRootLength..]
                                        : ReadOnlySpan<char>.Empty;

                                    // 3. Allocazione inevitabile: Combiniamo la destinazione con il percorso relativo
                                    targetFullPath = string.Join(Path.DirectorySeparatorChar, State.DestinationPath, relativeSpan.ToString());
                                }
                                else
                                {
                                    // Allocazione inevitabile: Combiniamo la root di destinazione con il solo nome file
                                    targetFullPath = string.Join(Path.DirectorySeparatorChar, State.DestinationPath, item.AsNameSpan().ToString());
                                }

                                // Spostamento fisico
                                if (!State.IsDebug)
                                {
                                    // Creazione directory padre ZERO-ALLOCATION
                                    ReadOnlySpan<char> targetSpan = targetFullPath.AsSpan();
                                    int lastSeparatorIndex = targetSpan.LastIndexOf(Path.DirectorySeparatorChar);

                                    if (lastSeparatorIndex > 0)
                                    {
                                        ReadOnlySpan<char> parentDirSpan = targetSpan[..lastSeparatorIndex];

                                        // Dobbiamo creare la stringa solo se la directory NON esiste.
                                        // Purtroppo Directory.Exists vuole un loop o una stringa, ma possiamo
                                        // usare un piccolo trucco: tenere un HashSet locale delle cartelle già create
                                        // per evitare chiamate al file system. Per mantenere il codice semplice, allochiamo
                                        // solo la stringa della directory se ci rendiamo conto di doverla passare a Exists.

                                        string parentDirStr = parentDirSpan.ToString();
                                        if (!Directory.Exists(parentDirStr))
                                        {
                                            Directory.CreateDirectory(parentDirStr);
                                        }
                                    }

                                    // L'API File.Move richiede purtroppo ancora stringhe.
                                    // Fortunatamente GetFullPath() viene chiamato solo qui.
                                    File.Move(item.GetFullPath(), targetFullPath, State.Overwrite);
                                }

                                // Aggiornamento contatori
                                localFlushMoved++;
                                localFlushBytes += item.Length;

                                // ogni 512 elementi faccio il flush nell'array condiviso
                                if ((localFlushMoved & FlushMask) == 0)
                                {
                                    State.MovedFilesCountList[slot] += localFlushMoved;
                                    State.BytesMovedList[slot] += localFlushBytes;

                                    localFlushMoved = 0;
                                    localFlushBytes = 0;
                                }

                                ct.ThrowIfCancellationRequested();
                            }
                            catch (Exception)
                            {
                                // In un'operazione di move massiva, se un file è in uso (lock)
                                // salta semplicemente l'eccezione e non incrementa il contatore per quel file
                            }
                            finally
                            {
                                item.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        // invio gli ultimi dati rimasti appesi
                        State.MovedFilesCountList[slot] += localFlushMoved;
                        State.BytesMovedList[slot] += localFlushBytes;
                    }
                }, ct);
            }

            return workers;
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
                    await AnsiConsole.Live(new Grid())
                        .Cropping(VerticalOverflowCropping.Bottom)
                        .StartAsync(async ctx =>
                        {
                            while (State.IsProcessing && !ct.IsCancellationRequested)
                            {
                                long currentTotalMoved = 0;
                                long currentTotalBytes = 0;

                                var grid = new Grid()
                                    .AddColumn(new GridColumn().NoWrap())
                                    .AddColumn(new GridColumn().NoWrap().RightAligned())
                                    .AddColumn(new GridColumn().NoWrap());

                                for (int i = 0; i < State.ThreadNumber; i++)
                                {
                                    long totalMoved = Volatile.Read(ref State.MovedFilesCountList[i * CounterStride]);
                                    long totalBytes = Volatile.Read(ref State.BytesMovedList[i * CounterStride]);

                                    currentTotalMoved += totalMoved;
                                    currentTotalBytes += totalBytes;

                                    long currentProgress = totalMoved % 4096;
                                    int dashesCount = (int)currentProgress / 102;

                                    string bar = new string('-', dashesCount).PadRight(40, ' ');

                                    string threadInfo = $"[yellow]T-{i:D2}[/]";
                                    string moveCount = $"[cyan]{totalMoved}[/]";
                                    string coloredBar = $"[darkgray]|[/][green]{bar}[/][darkgray]|[/]";

                                    grid.AddRow(threadInfo, moveCount, coloredBar);
                                }

                                double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;

                                // File al secondo
                                double filesPerSecond = elapsedSeconds > 0
                                    ? (currentTotalMoved - lastTotalMoved) / elapsedSeconds
                                    : 0;

                                // Megabyte al secondo (velocità di rete/disco)
                                double bytesPerSecond = elapsedSeconds > 0
                                    ? (currentTotalBytes - lastTotalBytes) / elapsedSeconds
                                    : 0;

                                lastTotalMoved = currentTotalMoved;
                                lastTotalBytes = currentTotalBytes;
                                stopwatch.Restart();

                                grid.AddEmptyRow();

                                grid.AddRow(new Markup($"[magenta]>[/] Totale Spostati: [cyan]{currentTotalMoved:N0}[/]"));
                                grid.AddRow(new Markup($"[magenta]>[/] Dati Trasferiti: [magenta]{Formatter.Bytes(currentTotalBytes)}[/]"));
                                grid.AddRow(new Markup($"[magenta]>[/] Velocità Rete/Disco: [green]{Formatter.Bytes((long)bytesPerSecond)}/s[/] [darkgray]({filesPerSecond:N0} file/s)[/]"));

                                ctx.UpdateTarget(grid);

                                await Task.Delay(200, ct);
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

            for (int i = 0; i < State.ThreadNumber; i++)
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