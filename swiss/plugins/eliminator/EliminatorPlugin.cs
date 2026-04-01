using System.IO.Enumeration;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using plugins;
using utils;

namespace swiss.plugins.eliminator
{
    class EliminatorPlugin : Plugin
    {
        public override string Name => "eliminator";
        public override string Description => "Tool avanzato per la cancellazione e l'archiviazione massiva dei file";

        private readonly struct StackFileInfo
        {
            public string Name { get; }
            public string FullName { get; }
            public string Extension { get; }
            public DateTime CreationTime { get; }
            public DateTime LastAccessTime { get; }
            public DateTime LastWriteTime { get; }
            public long Length { get; }
            public bool IsDirectory { get; }

            public StackFileInfo(ref FileSystemEntry entry)
            {
                Name = entry.FileName.ToString();
                FullName = entry.ToFullPath();
                Extension = Path.GetExtension(Name);
                CreationTime = entry.CreationTimeUtc.LocalDateTime;
                LastAccessTime = entry.LastAccessTimeUtc.LocalDateTime;
                LastWriteTime = entry.LastWriteTimeUtc.LocalDateTime;
                Length = entry.Length;
                IsDirectory = entry.IsDirectory;
            }
        }

        private struct ThreadLocalState
        {
            public long Processed { get; set; }
            public long Actions { get; set; }
            public long BytesSaved { get; set; }
            public ThreadLocalState()
            {
                Processed = 0;
                Actions = 0;
                BytesSaved = 0;
            }
        }

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            if (args.Length < 1)
            {
                Help();
                return;
            }

            // ---------------------------------------------------------
            // 1. PARSING E VALIDAZIONE
            // ---------------------------------------------------------
            string targetPath = args[0];
            if (!Directory.Exists(targetPath))
            {
                PrintError($"La directory target non esiste: {targetPath}");
                return;
            }

            var options = ParseArguments(args, 1);

            // flag booleani
            bool isDebug = options.ContainsKey("--debug") || options.ContainsKey("-d");
            bool isRecursive = options.ContainsKey("--recursive") || options.ContainsKey("-r");
            bool isRollback = options.ContainsKey("--rollback");
            bool isForce = options.ContainsKey("--force") || options.ContainsKey("-f") || options.ContainsKey("-y");
            bool regexIgnoreCase = options.ContainsKey("--ignore-case") || options.ContainsKey("-i");
            bool targetDirs = options.ContainsKey("--dirs");
            bool isParallel = options.ContainsKey("--parallel") || options.ContainsKey("-p");

            // estrazione chiavi-valori
            string? regexPattern = options.TryGetValue("--regex", out var r) && r != "true" ? r : null;

            string? backupPath = options.TryGetValue("--backup-path", out var b) && b != "true" ? b : null;

            int? olderThanDays = null;
            if (options.TryGetValue("--older-than", out var otStr) && otStr != "true")
            {
                if (int.TryParse(otStr, out int days) && days >= 0) olderThanDays = days;
                else { PrintError("Il valore di --older-than deve essere un numero intero positivo."); return; }
            }

            string dateType = options.TryGetValue("--date-type", out var dt) && dt != "true" ? dt.ToLower() : "m";

            // default 1 quindi no multithread
            int threads = 1;
            options.TryGetValue("--threads", out var thStr);
            if (!String.IsNullOrEmpty(thStr) && int.TryParse(thStr, out var thInt) && thInt > 0 && thInt < 64)
            {
                threads = thInt;
                isParallel = true;
            }
            else if (isParallel)
            {
                // se è attivo il parallelismo ma non è stato definito il numero di threads
                threads = Environment.ProcessorCount;
            }

            // ---------------------------------------------------------
            // 2. LOGICA DI ROLLBACK
            // ---------------------------------------------------------
            if (isRollback)
            {
                await Rollback(targetPath, isDebug, ct);
                return;
            }

            // ---------------------------------------------------------
            // 3. FILTRI
            // ---------------------------------------------------------
            var filters = new List<Func<StackFileInfo, bool>>();

            if (!string.IsNullOrEmpty(regexPattern))
            {
                try
                {
                    var regexOptions = RegexOptions.Compiled;
                    if (regexIgnoreCase)
                    {
                        regexOptions |= RegexOptions.IgnoreCase;
                    }
                    var compiledRegex = new Regex(regexPattern, regexOptions);
                    filters.Add(info => compiledRegex.IsMatch(info.Name));
                }
                catch (ArgumentException ex)
                {
                    PrintError($"Pattern Regex non valido: {ex.Message}");
                    return;
                }
            }

            if (olderThanDays.HasValue)
            {
                DateTime cutoffDate = DateTime.Now.AddDays(-olderThanDays.Value);

                // regola di filtro sul tipo di data
                filters.Add(dateType switch
                {
                    "c" => file => file.CreationTime < cutoffDate,
                    "a" => file => file.LastAccessTime < cutoffDate,
                    _ => file => file.LastWriteTime < cutoffDate // Default: Modifica
                });
            }

            // preparo il filtro per i dati
            Func<StackFileInfo, bool> shouldProcess = file => filters.All(f => f(file));

            // ---------------------------------------------------------
            // 4. BACKUP
            // ---------------------------------------------------------
            if (!isDebug && !isForce)
            {
                // Se l'input è redirezionato (es. sessione remota senza TTY), avvisiamo che il prompt fallirà
                if (Console.IsInputRedirected)
                {
                    PrintError("Sessione remota o non interattiva rilevata. Usa il flag --force (o -f, -y) per confermare l'operazione.");
                    return;
                }
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\nATTENZIONE: Stai per operare su {targetPath}");
                if (filters.Count == 0) Console.WriteLine("PERICOLO: Nessun filtro impostato. Verranno colpiti TUTTI i file.");
                Console.Write("Vuoi davvero procedere? [s/N]: ");
                Console.ResetColor();

                string? answer = Console.ReadLine();
                if (answer?.ToLower() != "s" && answer?.ToLower() != "si")
                {
                    Console.WriteLine("Operazione annullata.");
                    return;
                }
            }

            // setup backup (creazione cartella e file CSV)
            StreamWriter? csvWriter = null;
            if (!string.IsNullOrEmpty(backupPath))
            {
                backupPath = Path.Combine(backupPath, $"EliminatorBackup_{DateTime.Now:yyyyMMdd_HHmmss}");
                if (!Directory.Exists(backupPath)) Directory.CreateDirectory(backupPath);

                string csvPath = Path.Combine(backupPath, $"eliminator_log.csv");
                var outStream = new FileStream(csvPath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 65536, useAsync: true);
                csvWriter = new StreamWriter(outStream, System.Text.Encoding.UTF8);
                await csvWriter.WriteLineAsync("OriginalPath;BackupPath;Size;Date");
            }

            // ---------------------------------------------------------
            // 5. ESECUZIONE PRINCIPALE
            // ---------------------------------------------------------
            Console.WriteLine(isDebug ? "--- AVVIO (DEBUG) ---" : "--- AVVIO ---");

            long processedCount = 0, actionCount = 0, bytesSaved = 0;

            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = isRecursive,
                BufferSize = 64 * 1024
            };

            IEnumerable<StackFileInfo> itemsToScan = new FileSystemEnumerable<StackFileInfo>(
                targetPath,
                (ref FileSystemEntry entry) => new StackFileInfo(ref entry),
                enumOptions
            );

            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(50000) { SingleReader = true });
            Task? consumerTask = null;

            if (csvWriter != null)
            {
                consumerTask = Task.Run(async () =>
                {
                    await foreach (var line in channel.Reader.ReadAllAsync(ct))
                    {
                        await csvWriter.WriteLineAsync(line);
                    }
                });
            }

            try
            {
                // FOREACH PARALLELO
                if (isParallel && threads > 1)
                {
                    ParallelOptions parallelOptions = new()
                    {
                        MaxDegreeOfParallelism = threads,
                        CancellationToken = ct
                    };
                    // foreach sincrono multithread
                    Parallel.ForEach(
                        itemsToScan,
                        parallelOptions,
                        // struct mutabile per gestire le variabili locali di ogni thread
                        () => new ThreadLocalState(),
                        // esecuzione principale del thread
                        (item, loopState, localState) =>
                        {
                            localState.Processed++;

                            if (!shouldProcess(item)) return localState;

                            long size = ExecuteItemAction(item, backupPath, isDebug, channel.Writer);

                            if (size >= 0)
                            {
                                localState.Actions++;
                                localState.BytesSaved += size;
                            }

                            return localState;
                        },
                        // esecuzione finale prima di chiudere il thread
                        (finalState) =>
                        {
                            Interlocked.Add(ref actionCount, finalState.Actions);
                            Interlocked.Add(ref processedCount, finalState.Processed);
                            Interlocked.Add(ref bytesSaved, finalState.BytesSaved);
                        }
                    );
                }
                // FOREACH SEQUENZIALE
                else
                {
                    foreach (var item in itemsToScan)
                    {
                        ct.ThrowIfCancellationRequested();
                        processedCount++;
                        if (processedCount % 25000 == 0) Console.Write($"\rElementi analizzati: {processedCount}...");

                        if (!shouldProcess(item)) continue;

                        long size = ExecuteItemAction(item, backupPath, isDebug, channel.Writer);

                        if (size >= 0)
                        {
                            actionCount++;
                            bytesSaved += size;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { Console.WriteLine("\nOperazione interrotta dall'utente."); }
            finally
            {
                channel.Writer.Complete();
                if (consumerTask != null)
                {
                    await consumerTask;
                }
                if (csvWriter != null)
                {
                    await csvWriter.FlushAsync();
                    csvWriter.Dispose();
                }
            }

            // REPORT FINALE
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n\nOperazione Conclusa.");
            Console.WriteLine($"- File analizzati : {processedCount}");
            Console.WriteLine($"- File colpiti    : {actionCount}");
            Console.WriteLine($"- Spazio coinvolto: {Formatter.Bytes(bytesSaved)}");
            Console.ResetColor();
        }

        private long ExecuteItemAction(StackFileInfo item, string? backupPath, bool isDebug, ChannelWriter<string>? logWriter)
        {
            long itemSize = item.IsDirectory ? 0 : item.Length;

            if (isDebug)
            {
                Console.WriteLine($"[DEBUG] {(item.IsDirectory ? "DIR " : "FILE")}: {item.FullName}");
                return itemSize;
            }

            try
            {
                if (!string.IsNullOrEmpty(backupPath))
                {
                    // BACKUP
                    string destItem = Path.Combine(backupPath, item.Name);
                    if (item.IsDirectory ? Directory.Exists(destItem) : File.Exists(destItem))
                    {
                        string ext = item.Extension;
                        string nameOnly = Path.GetFileNameWithoutExtension(item.Name);
                        destItem = Path.Combine(backupPath, $"{nameOnly}_{Guid.NewGuid().ToString("N")[..8]}{ext}");
                    }

                    if (item.IsDirectory) Directory.Move(item.FullName, destItem);
                    else File.Move(item.FullName, destItem);

                    logWriter?.TryWrite($"{item.FullName};{destItem};{itemSize};{DateTime.Now:O}");
                }
                else
                {
                    // ELIMINAZIONE
                    if (item.IsDirectory) Directory.Delete(item.FullName, recursive: true);
                    else
                    {
                        bool success = NativeIO.DeleteFile(item.FullName);
                        if (!success) PrintWarning($"Impossibile cancellare {item.FullName}");
                    }
                }

                return itemSize;
            }
            catch (Exception ex)
            {
                PrintError($"eccezione su {item.Name}: {ex.Message}");
                return -1; // -1 indica errore
            }
        }

        private async Task<bool> Rollback(string targetPath, bool isDebug, CancellationToken ct)
        {
            string csvPath = Path.Combine(targetPath, "eliminator_log.csv");

            if (!File.Exists(csvPath))
            {
                PrintError($"File di log non trovato per il rollback in: {targetPath}");
                return false;
            }

            Console.WriteLine(isDebug ? "--- AVVIO ROLLBACK (DEBUG) ---" : "--- AVVIO ROLLBACK ---");

            long restoredCount = 0, failedCount = 0;

            await using var inStream = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 65536, useAsync: true);
            using var reader = new StreamReader(inStream, System.Text.Encoding.UTF8);

            string? currentLine = await reader.ReadLineAsync();

            while ((currentLine = await reader.ReadLineAsync()) != null)
            {
                ct.ThrowIfCancellationRequested();

                string[] parts = currentLine.Split(';');
                if (parts.Length < 2) continue;

                string originalPath = parts[0];
                string currentBackupPath = parts[1];

                if (string.IsNullOrWhiteSpace(originalPath) || string.IsNullOrWhiteSpace(currentBackupPath)) continue;

                if (isDebug)
                {
                    Console.WriteLine($"[DEBUG] Ripristino: {Path.GetFileName(currentBackupPath)} -> {originalPath}");
                    restoredCount++;
                    continue;
                }

                try
                {
                    if (File.Exists(currentBackupPath))
                    {
                        string? destDir = Path.GetDirectoryName(originalPath);
                        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                        {
                            Directory.CreateDirectory(destDir);
                        }

                        File.Move(currentBackupPath, originalPath, overwrite: true);
                        restoredCount++;
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"\r[AVVISO] File non trovato nel backup: {currentBackupPath}");
                        Console.ResetColor();
                        failedCount++;
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\r[ERRORE] Impossibile ripristinare {Path.GetFileName(currentBackupPath)}: {ex.Message}");
                    Console.ResetColor();
                    failedCount++;
                }
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\nOperazione di Rollback Conclusa.");
            Console.WriteLine($"- File ripristinati : {restoredCount}");
            if (failedCount > 0) Console.WriteLine($"- File ignorati/err : {failedCount}");
            Console.ResetColor();

            return true;
        }

        public override void Help()
        {
            Console.WriteLine("Uso: swiss eliminator <target_path> [opzioni]");
            Console.WriteLine("\nOpzioni:");
            Console.WriteLine("  --regex <pattern>     : Filtra i file in base a un'espressione regolare sul nome");
            Console.WriteLine("  --ignore-case, -i     : Rende case insensitive la regex");
            Console.WriteLine("  --older-than <giorni> : Colpisce solo i file più vecchi di X giorni");
            Console.WriteLine("  --date-type <m|c|a>   : Tipo di data per --older-than (m=Modifica, c=Creazione, a=Accesso). Default: m");
            Console.WriteLine("  --backup-path <path>  : Invece di eliminare, sposta i file in questa cartella e genera un log CSV");
            Console.WriteLine("  --rollback            : Ripristina i file dalle posizioni di un backup precedente");
            Console.WriteLine("  --debug, -d           : Simula l'operazione senza toccare i file sul disco");
            Console.WriteLine("  --recursive, -r       : Scansiona anche le sottocartelle");
            Console.WriteLine("  --force, -f, -y       : Procedi senza chiedere nessuna conferma di esecuzione");
            Console.WriteLine("  --dirs                : Applica i filtri e le operazioni alle CARTELLE anziché ai file");
            Console.WriteLine("  --parallel, -p        : Esegue l'operazione in multithreading");
            Console.WriteLine("  --threads, -t <num>   : Specifica il numero massimo di thread (default: numero di core della CPU)");
        }
    }
}