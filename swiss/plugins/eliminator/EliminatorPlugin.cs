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
            if (args.Length < 1)
            {
                Help();
                return;
            }

            // PARSING
            string? targetPath = ParsePath(args[0]);
            if (string.IsNullOrEmpty(targetPath)) return;

            ParseArguments(args, 1);

            // flag booleani
            bool isDebug = OptionsContains("--debug", "-d");
            bool isRecursive = OptionsContains("--recursive", "-r");
            int threadNumber = GetOptionInt("--threads", "-t") ?? Environment.ProcessorCount;
            // filtri opzioni
            var filterOpts = new FileFilterFactory.FilterOptions(
                Pattern: GetOptionValue("--pattern", "-p"),
                MatchType: OptionsContains("--fixed", "-f") ? FilterFileNameMatchType.Fixed : FilterFileNameMatchType.Regex,
                IgnoreCase: OptionsContains("--ignore-case", "-i"),
                ModifiedBefore: GetOptionAge("--since", "-s"),
                ModifiedAfter: GetOptionAge("--older-than", "-o")
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
                                    ct.ThrowIfCancellationRequested();
                                }
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
                // se il debug è attivo allora non attivo la ui
                if (isDebug)
                {
                    return;
                }
                // preparazione righe vuote
                for (int i = 0; i < threadNumber; i++) Console.WriteLine();
                int consoleWidth = 80; // Default di sicurezza
                try { consoleWidth = Console.WindowWidth; } catch { } // Ignora eccezioni se in esecuzione remota/re-diretta
                try
                {
                    while (isProcessing && !ct.IsCancellationRequested)
                    {
                        // Spostiamo il cursore in su di 'n' righe per sovrascrivere esattamente il nostro blocco
                        try { Console.SetCursorPosition(0, Console.CursorTop - threadNumber); } catch { }

                        for (int i = 0; i < threadNumber; i++)
                        {
                            int totalDropped = droppedFilesCountList[i];
                            int currentBatch = totalDropped / 4096;
                            int currentProgress = totalDropped % 4096;
                            // Calcoliamo la lunghezza della barra (max 40 caratteri, 1 trattino ogni ~100 file)
                            int dashesCount = currentProgress / 102;
                            string bar = new string('-', dashesCount).PadRight(40, ' ');
                            // Formattiamo la stringa: Thread 01 [Batch 005] 0020480 |-------    |
                            string line = $"T-{i:D2} [B-{currentBatch:D3}] {totalDropped:D7} |{bar}|";
                            // PadRight pulisce i "rimasugli" di testo se la riga precedente era più lunga
                            Console.WriteLine(line.PadRight(consoleWidth - 1));
                        }
                        await Task.Delay(150, ct);
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
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n\nOperazione Conclusa.");
            Console.WriteLine($"- File cancellati  : {totalDropped}");
            Console.WriteLine($"- Spazio coinvolto : {Formatter.Bytes(totalBytesSaved)}");
            Console.ResetColor();
        }

        public override void Help()
        {
            ConsolePlus.WriteHr();
            ConsolePlus.Write("[Cyan]#[/] Uso: [Yellow]swiss [Magenta]eliminator [DarkGray]<percorso> [opzioni]");
            ConsolePlus.Write("[Cyan]#[/] - percorso : usa . per la cartella corrente oppure definisci un percorso completo");
            ConsolePlus.Write("[Cyan]#[/] Opzioni:");
            ConsolePlus.Write("[Cyan]#[/]  --regex <pattern>     : Filtra i file in base a un'espressione regolare sul nome");
            ConsolePlus.Write("[Cyan]#[/]  --ignore-case, -i     : Rende case insensitive la regex");
            ConsolePlus.Write("[Cyan]#[/]  --older-than <giorni> : Colpisce solo i file più vecchi di X giorni");
            ConsolePlus.Write("[Cyan]#[/]  --date-type <m|c|a>   : Tipo di data per --older-than (m=Modifica, c=Creazione, a=Accesso). Default: m");
            ConsolePlus.Write("[Cyan]#[/]  --backup-path <path>  : Invece di eliminare, sposta i file in questa cartella e genera un log CSV");
            ConsolePlus.Write("[Cyan]#[/]  --rollback            : Ripristina i file dalle posizioni di un backup precedente");
            ConsolePlus.Write("[Cyan]#[/]  --debug, -d           : Simula l'operazione senza toccare i file sul disco");
            ConsolePlus.Write("[Cyan]#[/]  --recursive, -r       : Scansiona anche le sottocartelle");
            ConsolePlus.Write("[Cyan]#[/]  --force, -f, -y       : Procedi senza chiedere nessuna conferma di esecuzione");
            ConsolePlus.Write("[Cyan]#[/]  --dirs                : Applica i filtri e le operazioni alle CARTELLE anziché ai file");
            ConsolePlus.Write("[Cyan]#[/]  --parallel, -p        : Esegue l'operazione in multithreading");
            ConsolePlus.Write("[Cyan]#[/]  --threads, -t <num>   : Specifica il numero massimo di thread (default: numero di core della CPU)");
            ConsolePlus.WriteHr();
        }
    }
}