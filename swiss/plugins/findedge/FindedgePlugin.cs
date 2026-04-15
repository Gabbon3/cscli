using stack;
using System.IO.Enumeration;
using utils;
using utils.console;

namespace plugins.findedge
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
                recursive = args[2] == "--recursive";
            }

            if (!Directory.Exists(targetPath))
            {
                PrintError("La directory specificata non esiste.");
                return;
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Inizio la ricerca del file...");
            Console.ResetColor();

            await Task.Run(() => ScanDirectory(targetPath, mode, recursive, ct), ct);
        }

        private void ScanDirectory(string path, EdgeMode mode, bool recursive, CancellationToken ct)
        {
            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = recursive,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
                BufferSize = 64 * 1024
            };

            var enumerable = new FileSystemEnumerable<StackFileInfo>(
                path,
                (ref FileSystemEntry entry) => new StackFileInfo(ref entry),
                enumOptions
            )
            {
                // filtro per escludere le directory che di fatto non servono
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory
            };

            StackFileInfo edgeFile = default;
            long fileCount = 0;

            try
            {
                using var enumerator = enumerable.GetEnumerator();

                Func<StackFileInfo, StackFileInfo, bool> isBetter = mode switch
                {
                    EdgeMode.Oldest => (current, best) => current.LastWriteTime < best.LastWriteTime,
                    EdgeMode.Newest => (current, best) => current.LastWriteTime > best.LastWriteTime,
                    EdgeMode.Smallest => (current, best) => current.Length < best.Length,
                    EdgeMode.Largest => (current, best) => current.Length > best.Length,
                    _ => (current, best) => false
                };

                if (!enumerator.MoveNext())
                {
                    Console.WriteLine("Nessun file trovato nella directory.");
                    return;
                }

                edgeFile = enumerator.Current;
                fileCount++;

                while (enumerator.MoveNext())
                {
                    ct.ThrowIfCancellationRequested();
                    StackFileInfo current = enumerator.Current;
                    fileCount++;

                    if (fileCount % 50000 == 0) Console.Write($"\rFile analizzati: {fileCount}...");

                    if (isBetter(current, edgeFile))
                    {
                        edgeFile.Dispose();
                        edgeFile = current;
                    }
                    else
                    {
                        current.Dispose();
                    }
                }

                Console.WriteLine();
                ConsolePlus.Write($"[Green]#[/] Ricerca terminata:");
                ConsolePlus.Write($"[Green]#[/] File: [Cyan]{edgeFile.GetFullPath()}");
                ConsolePlus.Write($"[Green]#[/] Data Modifica: [Magenta]{edgeFile.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                ConsolePlus.Write($"[Green]#[/] Dimensione: [Gray]{Formatter.Bytes(edgeFile.Length)}[/]");
                Console.ResetColor();
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
            finally
            {
                edgeFile.Dispose();
            }
        }

        public override void Help()
        {
            Console.WriteLine("Uso: swiss findedge <path> <flag> <recursive>");
            Console.WriteLine("\n<flag> Flags disponibili:");
            Console.WriteLine("  --oldest   : trova il file con la data di modifica più remota");
            Console.WriteLine("  --newest   : trova il file con la data di modifica più recente");
            Console.WriteLine("  --smallest : trova il file con la dimensione minore in byte");
            Console.WriteLine("  --largest  : trova il file con la dimensione maggiore in byte");
            Console.WriteLine("\n<recursive> Per usare la ricorsione (default false) basta aggiungere:");
            Console.WriteLine("  --recursive");
        }
    }
}