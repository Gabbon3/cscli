using System.IO.Enumeration;
using System.Threading.Channels;
using lib.io;
using lib.utils;
using lib.console;
using lib.io.stack;

namespace plugins.eliminator
{
    class EliminatorPlugin : Plugin
    {
        public override string Name => "eliminator";
        public override string Description => "Tool avanzato per la cancellazione e l'archiviazione massiva dei file";

        private string GlobalTrashPath = "";
        private string DriveRoot = "C:\\";
        // dimensione padding per prevenire false-sharing su BytesSavedList e DroppedFilesCountList
        private const int CounterStride = 8;
        // 
        private int FlushMask = 511;
        private EliminationState State = new();

        // # Stato interno
        /// <summary>
        /// Contiene lo stato completo dell'operazione di eliminazione.
        /// Include configurazioni, canali di comunicazione e contatori di progresso.
        /// </summary>
        private class EliminationState
        {
            public string TargetPath { get; set; } = "";
            public bool IsDebug { get; set; }
            public bool IsRecursive { get; set; }
            public bool DropInstant { get; set; }
            public int ThreadNumber { get; set; }
            public FileSystemFilter? FileFilter { get; set; }

            public Channel<StackFileInfo>? WorkChannel { get; set; }
            public long[] DroppedFilesCountList { get; set; } = [];
            public long[] BytesSavedList { get; set; } = [];
            public bool IsProcessing { get; set; } = true;
        }

        // # Esecuzione Principale
        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            var settings = ParseSettings<EliminatorSettings>(args);

            if (args.Contains("--help") || string.IsNullOrEmpty(settings.TargetPath))
            {
                Help();
                return;
            }

            State = new EliminationState();

            // 1. parsing e validazione delle settings
            if (!ParseAndValidateSettings(settings))
            {
                return;
            }

            // 2. inizializzazione del percorso di trashing temporaneo
            if (!InitializeTrashPath())
            {
                return;
            }

            ConsolePlus.Write($"[Cyan]#[/] Avvio cancellazione ... {(State.IsDebug ? "(DEBUG)" : "")}");

            // 3. inizializzo il task di producer
            var producerTask = CreateProducerTask(ct);

            // 4. inizializzo i task dei consumer (i workers veri e propri)
            var workers = CreateWorkerTasks(ct);

            // 5. inizializzo il task per il monitor UI
            var monitorTask = CreateMonitorTask(ct);

            // avvio e attendo tutti i workers
            await Task.WhenAll(workers);
            State.IsProcessing = false;
            await monitorTask;
            await producerTask;

            // pulizia finale
            CleanupTrashPath();

            // 6. stampa statistiche finali
            PrintFinalStatistics();
        }

        // # ---------------------------------- #
        // Parsing e validazione Settings
        // # ---------------------------------- #

        /// <summary>
        /// Analizza e valida i parametri di input.
        /// Accede a: State (per popolarlo)
        /// </summary>
        private bool ParseAndValidateSettings(EliminatorSettings settings)
        {
            string? targetPath = ParsePath(settings.TargetPath);
            if (string.IsNullOrEmpty(targetPath))
                return false;

            State.TargetPath = targetPath;
            State.IsDebug = settings.Debug;
            State.IsRecursive = settings.Recursive;
            State.DropInstant = settings.DropInstant;
            State.ThreadNumber = settings.Threads ?? Environment.ProcessorCount;

            var filterOpts = new FileFilterFactory.FilterOptions(
                Pattern: ParseMatchPattern(settings.Pattern),
                MatchType: settings.FixedMatch ? FilterFileNameMatchType.Fixed : FilterFileNameMatchType.Regex,
                IgnoreCase: settings.IgnoreCase,
                ModifiedBefore: settings.OlderThan,
                ModifiedAfter: settings.Since
            );

            State.FileFilter = FileFilterFactory.CreateFilter(filterOpts);

            // Inizializza gli array per i contatori
            State.DroppedFilesCountList = new long[State.ThreadNumber * CounterStride];
            State.BytesSavedList = new long[State.ThreadNumber * CounterStride];

            return true;
        }

        // # ---------------------------------- #
        // Inizializzazione Percorso di Trash
        // # ---------------------------------- #

        /// <summary>
        /// Inizializza il percorso del cestino globale e crea la struttura di directory.
        /// Accede a: State.TargetPath, State.DropInstant
        /// Popola: GlobalTrashPath, DriveRoot
        /// </summary>
        private bool InitializeTrashPath()
        {
            DriveRoot = Path.GetPathRoot(Path.GetFullPath(State.TargetPath)) ?? "C:\\";
            GlobalTrashPath = Path.Combine(DriveRoot, $".swiss_trash_{Guid.NewGuid()}");

            if (State.DropInstant)
                return true;

            try
            {
                Directory.CreateDirectory(GlobalTrashPath);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                PrintError($"Non è possibile creare la cartella '{GlobalTrashPath}', non si dispone dei permessi necessari");
                return false;
            }
            catch (IOException)
            {
                PrintError($"Errore I/O sul disco, non è stato possibile creare '{GlobalTrashPath}'");
                return false;
            }
            catch (Exception ex)
            {
                PrintError($"Non è stato possibile creare la cartella '{GlobalTrashPath}': {ex.Message}");
                return false;
            }
        }

        // # ---------------------------------- #
        // Creazione Task del Producer
        // # ---------------------------------- #

        /// <summary>
        /// Crea il task che enumera i file dal file system e li invia al canale di lavoro.
        /// Accede a: State.TargetPath, State.IsDebug, State.IsRecursive, State.FileFilter, State.WorkChannel
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
                State.TargetPath,
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
                            ConsolePlus.Write($"[DarkGray]{item.AsDirectorySpan()}[Cyan]{item.AsNameSpan()}[/]");
                            item.Dispose();
                        }
                        else
                        {
                            await State.WorkChannel.Writer.WriteAsync(item, ct);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { PrintError($"\n[Errore I/O]: {ex.Message}"); }
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
        /// Crea i task worker che consumano i file dal canale e li eliminano/archiviano.
        /// Accede a: State.ThreadNumber, State.DropInstant, State.DroppedFilesCountList, 
        ///           State.BytesSavedList, State.WorkChannel, GlobalTrashPath
        /// </summary>
        private Task[] CreateWorkerTasks(CancellationToken ct)
        {
            var workers = new Task[State.ThreadNumber];

            for (int i = 0; i < State.ThreadNumber; i++)
            {
                int workerId = i;
                workers[i] = Task.Run(async () =>
                {
                    List<Task> backgroundWorkerDrops = [];
                    int batchId = 0;
                    string workerRoot = Path.Combine(GlobalTrashPath, workerId.ToString());
                    string currentBatchPath = Path.Combine(workerRoot, batchId.ToString());

                    if (!State.DropInstant)
                    {
                        Directory.CreateDirectory(workerRoot);
                        Directory.CreateDirectory(currentBatchPath);
                    }

                    long localFlushDropped = 0;
                    long localFlushBytes = 0;
                    long totalDropped = 0;
                    int slot = workerId * CounterStride;

                    try
                    {
                        await foreach (var item in State.WorkChannel!.Reader.ReadAllAsync())
                        {
                            try
                            {
                                totalDropped++;
                                localFlushDropped++;
                                localFlushBytes += item.Length;

                                if (State.DropInstant)
                                {
                                    NativeIO.DeleteFile(item.GetFullPath());
                                }
                                else
                                {
                                    string destPath = $"{workerRoot}{Path.DirectorySeparatorChar}{totalDropped}.tmp";
                                    File.Move(item.GetFullPath(), destPath);
                                }

                                // ogni 512 elementi faccio il flush dell'array
                                if ((localFlushDropped & FlushMask) == 0)
                                {
                                    State.DroppedFilesCountList[slot] += localFlushDropped;
                                    State.BytesSavedList[slot] += localFlushBytes;

                                    localFlushDropped = 0;
                                    localFlushBytes = 0;

                                }

                                // Ogni 4096 elementi, elimina il batch corrente
                                // solo se non elimino subito i file
                                if ((totalDropped & 4095) == 0)
                                {
                                    batchId++;
                                    // gestisco le cartelle di batch solo se non sto eliminando direttamente i file
                                    if (!State.DropInstant)
                                    {
                                        string folderToDrop = currentBatchPath;
                                        backgroundWorkerDrops.Add(Task.Run(() =>
                                        {
                                            try { Directory.Delete(folderToDrop, true); }
                                            catch { }
                                        }));

                                        currentBatchPath = Path.Combine(workerRoot, batchId.ToString());
                                        Directory.CreateDirectory(currentBatchPath);
                                    }
                                }
                                ct.ThrowIfCancellationRequested();
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
                        State.DroppedFilesCountList[slot] += localFlushDropped;
                        State.BytesSavedList[slot] += localFlushBytes;
                        // attendo i task di cancellazione delle cartelle
                        await Task.WhenAll(backgroundWorkerDrops);
                        // se non stavo cancellando subito ed esiste al working directory allora la cancello
                        if (!State.DropInstant && Directory.Exists(workerRoot))
                        {
                            try { Directory.Delete(workerRoot, true); }
                            catch { }
                        }
                    }
                }, ct);
            }

            return workers;
        }

        // # ---------------------------------- #
        // Creazione del Monitor UI
        // # ---------------------------------- #

        /// <summary>
        /// Crea il task che monitora e visualizza il progresso in tempo reale.
        /// Accede a: State.ThreadNumber, State.IsDebug, State.DroppedFilesCountList, 
        ///           State.IsProcessing
        /// </summary>
        private Task CreateMonitorTask(CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                if (State.IsDebug) return;

                int uiLines = State.ThreadNumber + 3;
                for (int i = 0; i < uiLines; i++)
                    Console.WriteLine();

                int consoleWidth = 80;
                try { consoleWidth = Console.WindowWidth; }
                catch { }

                long lastTotalDropped = 0;
                long lastTotalBytesSaved = 0;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    while (State.IsProcessing && !ct.IsCancellationRequested)
                    {
                        try { Console.SetCursorPosition(0, Math.Max(0, Console.CursorTop - uiLines)); }
                        catch { }

                        long currentTotalDropped = 0;
                        long currentTotalBytesSaved = 0;

                        // Stampa righe dei singoli worker
                        for (int i = 0; i < State.ThreadNumber; i++)
                        {
                            long totalDropped = Volatile.Read(ref State.DroppedFilesCountList[i * CounterStride]);
                            long totalBytes = Volatile.Read(ref State.BytesSavedList[i * CounterStride]);

                            currentTotalDropped += totalDropped;
                            currentTotalBytesSaved += totalBytes;

                            long currentBatch = totalDropped / 4096;
                            long currentProgress = totalDropped % 4096;
                            int dashesCount = (int)currentProgress / 102;

                            string bar = new string('-', dashesCount).PadRight(40, ' ');
                            string threadStr = $"T-{i:D2}";
                            string batchNum = currentBatch.ToString().PadLeft(3);
                            string dropStr = totalDropped.ToString().PadLeft(7);

                            string coloredLine = $"[Yellow]{threadStr}[/] [DarkGray](B:[/][White]{batchNum}[/][DarkGray])[/] [Cyan]{dropStr}[/] [DarkGray]|[/][Green]{bar}[/][DarkGray]|[/]";
                            int visibleLength = 63;
                            string padding = new string(' ', Math.Max(0, consoleWidth - 1 - visibleLength));

                            ConsolePlus.Write(coloredLine + padding, newLine: true);
                        }

                        // Calcolo della velocità
                        double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                        double filesPerSecond = elapsedSeconds > 0
                            ? (currentTotalDropped - lastTotalDropped) / elapsedSeconds
                            : 0;

                        lastTotalDropped = currentTotalDropped;
                        lastTotalBytesSaved = currentTotalBytesSaved;
                        stopwatch.Restart();

                        string stats =
                            $"[Magenta]>[/] Totale Eliminati: [Magenta]{currentTotalDropped:N0}[/]\n" +
                            $"[Magenta]>[/] Velocità Attuale: [Green]{filesPerSecond:N0}[/] file/s\n" +
                            $"[Magenta]>[/] Spazio liberato: [Magenta]{Formatter.Bytes(currentTotalBytesSaved)}[/]";

                        // Stampa statistiche globali
                        ConsolePlus.Write(stats);

                        await Task.Delay(200, ct);
                    }
                }
                catch (TaskCanceledException) { }
            }, ct);
        }

        // # ---------------------------------- #
        // Stampa delle statistiche finali
        // # ---------------------------------- #

        /// <summary>
        /// Calcola e stampa le statistiche finali dell'operazione.
        /// Accede a: State.ThreadNumber, State.DroppedFilesCountList, State.BytesSavedList
        /// </summary>
        private void PrintFinalStatistics()
        {
            long totalDropped = 0;
            long totalBytesSaved = 0;

            for (int i = 0; i < State.ThreadNumber; i++)
            {
                totalDropped += State.DroppedFilesCountList[i * CounterStride];
                totalBytesSaved += State.BytesSavedList[i * CounterStride];
            }

            ConsolePlus.WriteHr(25);
            ConsolePlus.Write($"[Cyan]#[/] Operazione Conclusa.");
            ConsolePlus.Write($"[Cyan]*[/] File cancellati  : {totalDropped}");
            ConsolePlus.Write($"[Cyan]*[/] Spazio coinvolto : {Formatter.Bytes(totalBytesSaved)}");
            ConsolePlus.WriteHr(25);
        }

        // # ---------------------------------- #
        // Metodi secondari di Help
        // # ---------------------------------- #

        /// <summary>
        /// Elimina la cartella del cestino temporaneo.
        /// Dipende da: GlobalTrashPath
        /// </summary>
        private void CleanupTrashPath()
        {
            if (Directory.Exists(GlobalTrashPath))
            {
                try { Directory.Delete(GlobalTrashPath, true); }
                catch { }
            }
        }

        public override void Help()
        {
            PrintHelp<EliminatorSettings>();
        }
    }
}