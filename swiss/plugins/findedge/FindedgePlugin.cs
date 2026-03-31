using plugins;
using utils;

namespace swiss.plugins.findedge
{
    public enum EdgeMode
    {
        Oldest,
        Newest,
        Smallest,
        Largest
    }

    class FindEdgePlugin : Plugin
    {
        public override string Name => "findedge";
        public override string Description => "Trova il file agli estremi (più vecchio, nuovo, piccolo o grande) in una directory";

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            if (args.Length < 2)
            {
                Help();
                return;
            }

            string targetPath = args[0];
            EdgeMode mode;
            bool recursive = false;
            int bufferSizeKB = 0;

            switch (args[1].ToLower())
            {
                case "--oldest": mode = EdgeMode.Oldest; break;
                case "--newest": mode = EdgeMode.Newest; break;
                case "--smallest": mode = EdgeMode.Smallest; break;
                case "--largest": mode = EdgeMode.Largest; break;
                default:
                    PrintError($"Flag non riconosciuto: {args[1]}");
                    Help();
                    return;
            }

            if (args.Length >= 3)
            {
                bool converted = int.TryParse(args[2], out bufferSizeKB);
                if (!converted || bufferSizeKB < 4 || bufferSizeKB > 16384)
                {
                    PrintError($"Il valore inserito in <bufferSize> non è valido: {args[2]}");
                    return;
                }
            }

            if (args.Length >= 4)
            {
                recursive = args[3] == "--recursive";
            }

            if (!Directory.Exists(targetPath))
            {
                PrintError("La directory specificata non esiste.");
                return;
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Inizio la ricerca del file...");
            Console.ResetColor();

            await Task.Run(() => ScanDirectory(targetPath, mode, recursive, bufferSizeKB, ct), ct);
        }

        private void ScanDirectory(string path, EdgeMode mode, bool recursive, int bufferSizeKB, CancellationToken ct)
        {
            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = recursive,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
                BufferSize = bufferSizeKB * 1024 // converto in bytes
            };

            var dirInfo = new DirectoryInfo(path);
            FileInfo? edgeFile = null;
            long fileCount = 0;

            try
            {
                using var enumerator = dirInfo.EnumerateFiles("*", enumOptions).GetEnumerator();

                if (!enumerator.MoveNext())
                {
                    Console.WriteLine("Nessun file trovato nella directory.");
                    return;
                }

                edgeFile = enumerator.Current;
                fileCount++;

                Func<FileInfo, FileInfo, bool> isNewEdge = mode switch
                {
                    EdgeMode.Oldest => (current, best) => current.LastWriteTimeUtc < best.LastWriteTimeUtc,
                    EdgeMode.Newest => (current, best) => current.LastWriteTimeUtc > best.LastWriteTimeUtc,
                    EdgeMode.Smallest => (current, best) => current.Length < best.Length,
                    EdgeMode.Largest => (current, best) => current.Length > best.Length,
                    _ => (current, best) => false
                };

                while (enumerator.MoveNext())
                {
                    ct.ThrowIfCancellationRequested();
                    var file = enumerator.Current;
                    fileCount++;

                    if (fileCount % 50000 == 0)
                        Console.Write($"\rFile analizzati: {fileCount}...");

                    if (isNewEdge(file, edgeFile!)) edgeFile = file;
                }

                if (edgeFile != null)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"\n--- Risultato per la ricerca: {mode.ToString().ToUpper()} ---");
                    Console.WriteLine($"File: {edgeFile.FullName}");
                    Console.WriteLine($"Data Modifica: {edgeFile.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine($"Dimensione: {Formatter.Bytes(edgeFile.Length)}");
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine("Nessun file trovato nella directory.");
                }
            }
            catch (OperationCanceledException)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\nOperazione annullata dall'utente (Ctrl+C).");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                PrintError($"Errore durante la scansione: {ex.Message}");
            }
        }

        public override void Help()
        {
            Console.WriteLine("Uso: swiss findedge <path> <flag> <bufferSize> <recursive>");
            Console.WriteLine("\n<flag> Flags disponibili:");
            Console.WriteLine("  --oldest   : trova il file con la data di modifica più remota");
            Console.WriteLine("  --newest   : trova il file con la data di modifica più recente");
            Console.WriteLine("  --smallest : trova il file con la dimensione minore in byte");
            Console.WriteLine("  --largest  : trova il file con la dimensione maggiore in byte");
            Console.WriteLine("\n<bufferSize> definisce la dimensione del buffer della EnumerateFiles in KB:");
            Console.WriteLine("  * default 0 (utilizza le impostazioni del sistema)");
            Console.WriteLine("  * valore massimo 4096 (4MB)");
            Console.WriteLine("  * minimo 4 (KB)");
            Console.WriteLine("\n<recursive> Per usare la ricorsione (default false) basta aggiungere:");
            Console.WriteLine("  --recursive");
        }
    }
}