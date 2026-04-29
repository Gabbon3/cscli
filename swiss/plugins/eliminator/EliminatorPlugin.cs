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

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            var settings = ParseSettings<EliminatorSettings>(args);

            if (args.Contains("--help") || string.IsNullOrEmpty(settings.TargetPath))
            {
                Help();
                return;
            }

            // PARSING
            string? targetPath = ParsePath(args[0]);
            if (string.IsNullOrEmpty(targetPath)) return;

            // flag booleani
            bool isDebug = settings.Debug;
            bool isRecursive = settings.Recursive;
            bool dropInstant = settings.DropInstant;
            int threadNumber = settings.Threads ?? Environment.ProcessorCount;
            // filtri opzioni
            var filterOpts = new FileFilterFactory.FilterOptions(
                Pattern: ParseMatchPattern(settings.Pattern),
                MatchType: settings.FixedMatch ? FilterFileNameMatchType.Fixed : FilterFileNameMatchType.Regex,
                IgnoreCase: settings.IgnoreCase,
                ModifiedBefore: settings.OlderThan,
                ModifiedAfter: settings.Since
            );

            var fileFilter = FileFilterFactory.CreateFilter(filterOpts);

            ConsolePlus.Write($"[Cyan]#[/] Avvio cancellazione ... {(isDebug ? "(DEBUG)" : "")}");

            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = isRecursive,
                BufferSize = 64 * 1024
            };

            // # -------------- #
            // #    PRODUCER    #
            // # -------------- #

            IEnumerable<StackFileInfo> itemsToScan = new FileSystemEnumerable<StackFileInfo>(
                targetPath,
                (ref FileSystemEntry entry) => new StackFileInfo(ref entry),
                enumOptions
            )
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    if (entry.IsDirectory) return false;
                    if (fileFilter != null)
                    {
                        return fileFilter(ref entry);
                    }
                    return true;
                }
            };

            var workChannel = Channel.CreateBounded<StackFileInfo>(new BoundedChannelOptions(50000)
            {
                SingleWriter = true,
                SingleReader = false
            });

            var producerTask = Task.Run(async () =>
            {
                try
                {
                    foreach (var item in itemsToScan)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (isDebug)
                        {
                            ConsolePlus.Write($"[DarkGray]{item.AsDirectorySpan()}[Cyan]{item.AsNameSpan()}[/]");
                            item.Dispose();
                        }
                        else
                        {
                            await workChannel.Writer.WriteAsync(item, ct);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { PrintError($"\n[Errore I/O]: {ex.Message}"); }
                finally
                {
                    workChannel.Writer.Complete();
                }
            }, ct);

            // # -------------- #
            // #    CONSUMER    #
            // # -------------- #

            // CONSUMER
            DriveRoot = Path.GetPathRoot(Path.GetFullPath(targetPath)) ?? "C:\\";
            GlobalTrashPath = Path.Combine(DriveRoot, $".swiss_trash_{Guid.NewGuid()}");

            var workers = new Task[threadNumber];
            // sono tutti inizializzati gia a 0
            var droppedFilesCountList = new int[threadNumber];
            var bytesSavedList = new long[threadNumber];

            for (int i = 0; i < threadNumber; i++)
            {
                int workerId = i;
                workers[i] = Task.Run(async () =>
                {
                    List<Task> backgroundWorkerDrops = [];
                    int batchId = 0;
                    // cartella di lavoro del worker
                    string workerRoot = Path.Combine(GlobalTrashPath, workerId.ToString());
                    Directory.CreateDirectory(workerRoot);
                    // cartella di batch corrente
                    string currentBatchPath = Path.Combine(workerRoot, batchId.ToString());
                    Directory.CreateDirectory(currentBatchPath);
                    // counter files cancellati da questo worker
                    int filesDroppedCounter = 0;

                    try
                    {
                        await foreach (var item in workChannel.Reader.ReadAllAsync())
                        {
                            try
                            {
                                filesDroppedCounter++;
                                droppedFilesCountList[workerId] = filesDroppedCounter;
                                // Se si decide di cancellare subito
                                if (dropInstant)
                                {
                                    bytesSavedList[workerId] += item.Length;
                                    NativeIO.DeleteFile(item.GetFullPath());
                                    continue;
                                }
                                // altrimenti
                                else
                                {
                                    string destPath = $"{workerRoot}{Path.DirectorySeparatorChar}{filesDroppedCounter}.tmp";
                                    if (item.IsDirectory)
                                    {
                                        Directory.Move(item.GetFullPath(), destPath);
                                    }
                                    else
                                    {
                                        bytesSavedList[workerId] += item.Length;
                                        File.Move(item.GetFullPath(), destPath);
                                    }
                                }
                                // ogni 4096 elementi cancello la cartella == a n % 4096 == 0
                                if ((filesDroppedCounter & 4095) == 0)
                                {
                                    string folderToDrop = currentBatchPath;
                                    backgroundWorkerDrops.Add(Task.Run(() =>
                                    {
                                        try { Directory.Delete(folderToDrop, true); } catch { }
                                    }));
                                    batchId++;
                                    currentBatchPath = Path.Combine(workerRoot, batchId.ToString());
                                    Directory.CreateDirectory(currentBatchPath);
                                    // controllo se è stato lanciato il ct
                                }
                                ct.ThrowIfCancellationRequested();
                            }
                            finally
                            {
                                // restituisco ArrayPool
                                item.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        // attendo tutte le cancellazioni e poi elimino tutto
                        await Task.WhenAll(backgroundWorkerDrops);
                        if (Directory.Exists(workerRoot))
                        {
                            try { Directory.Delete(workerRoot, true); } catch { }
                        }
                    }
                });
            }

            // UI monitor
            bool isProcessing = true;
            var monitorTask = Task.Run(async () =>
            {
                if (isDebug) return;

                for (int i = 0; i < threadNumber; i++) Console.WriteLine();
                int consoleWidth = 80;
                try { consoleWidth = Console.WindowWidth; } catch { }

                try
                {
                    while (isProcessing && !ct.IsCancellationRequested)
                    {
                        try { Console.SetCursorPosition(0, Console.CursorTop - threadNumber); } catch { }

                        for (int i = 0; i < threadNumber; i++)
                        {
                            int totalDropped = droppedFilesCountList[i];
                            int currentBatch = totalDropped / 4096;
                            int currentProgress = totalDropped % 4096;

                            int dashesCount = currentProgress / 102;
                            string bar = new string('-', dashesCount).PadRight(40, ' ');

                            string threadStr = $"T-{i:D2}";
                            string batchNum = currentBatch.ToString().PadLeft(3);
                            string dropStr = totalDropped.ToString().PadLeft(7);

                            // FIX: Usiamo le parentesi tonde (B-  0) per non far impazzire il parser ConsolePlus
                            string coloredLine = $"[Yellow]{threadStr}[/] [DarkGray](B:[/][White]{batchNum}[/][DarkGray])[/] [Cyan]{dropStr}[/] [DarkGray]|[/][Green]{bar}[/][DarkGray]|[/]";

                            // La lunghezza visibile ora è esattamente 63 caratteri (i tag spariscono)
                            int visibleLength = 63;
                            string padding = new string(' ', Math.Max(0, consoleWidth - 1 - visibleLength));

                            // newLine: true ora funzionerà perfettamente senza creare scalette
                            ConsolePlus.Write(coloredLine + padding, newLine: true);
                        }
                        await Task.Delay(200, ct);
                    }
                }
                catch (TaskCanceledException) { /* Uscita pulita */ }
            });
            // UI end

            await Task.WhenAll(workers);
            isProcessing = false; // Segnala alla UI di fermarsi
            await monitorTask;
            await producerTask;

            if (Directory.Exists(GlobalTrashPath))
            {
                try { Directory.Delete(GlobalTrashPath, true); } catch { }
            }
            // ---
            long totalDropped = 0;
            long totalBytesSaved = 0;
            for (int i = 0; i < threadNumber; i++)
            {
                totalDropped += droppedFilesCountList[i];
                totalBytesSaved += bytesSavedList[i];
            }
            // ---
            ConsolePlus.WriteHr(25);
            ConsolePlus.Write($"[Cyan]#[/] Operazione Conclusa.");
            ConsolePlus.Write($"[Cyan]*[/] File cancellati  : {totalDropped}");
            ConsolePlus.Write($"[Cyan]*[/] Spazio coinvolto : {Formatter.Bytes(totalBytesSaved)}");
            ConsolePlus.WriteHr(25);
        }

        public override void Help()
        {
            PrintHelp<EliminatorSettings>();
        }
    }
}