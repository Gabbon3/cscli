using System.Text;

namespace plugins.indexer
{
    class IndexerPlugin : Plugin
    {
        public override string Name => "index";
        public override string Description => "Censisce tutti i file a stack iterativo per evitare errori in ricorsione";

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            if (args.Length < 3)
            {
                Help();
                return;
            }

            string rootPath = args[0];
            string outputFile = args[1];
            string errorFile = args[2];

            if (!Directory.Exists(rootPath))
            {
                PrintError($"Il percorso \"{rootPath}\" non esiste o il disco non è montato.");
                return;
            }

            Console.WriteLine($"Target: {rootPath}");
            Console.WriteLine($"Output: {outputFile}");
            Console.WriteLine($"Errors: {errorFile}");
            Console.WriteLine("Modalità: ITERATIVA");
            Console.WriteLine("------------------------------------------------");

            long filesCount = 0;
            long totalBytes = 0;
            long errorsCount = 0;

            // Buffer aumentato a 4MB per ridurre I/O sul disco di sistema
            const int BUFFER_SIZE = 4 * 1024 * 1024;

            try
            {
                using var writer = new StreamWriter(outputFile, false, Encoding.UTF8, BUFFER_SIZE);
                await writer.WriteLineAsync("Path;SizeBytes;Extension;CreationTime;LastWriteTime");

                using var errorWriter = new StreamWriter(errorFile, false, Encoding.UTF8, 65536);
                await errorWriter.WriteLineAsync("Path;Type;ErrorMessage");

                var directories = new Stack<string>();
                directories.Push(rootPath);

                while (directories.Count > 0)
                {
                    // Check annullamento manuale (CTRL+C)
                    if (ct.IsCancellationRequested)
                    {
                        Console.WriteLine("\n\n!!! INTERRUZIONE RICHIESTA DALL'UTENTE !!!");
                        Console.WriteLine("Salvataggio buffer in corso...");
                        break;
                    }

                    string currentDir = directories.Pop();

                    // 1. FILTRO CARTELLE SPAZZATURA/SISTEMA
                    // Evitiamo di entrare in cartelle che sappiamo gia daranno errori o loop
                    var dirName = Path.GetFileName(currentDir);
                    if (!string.IsNullOrEmpty(dirName))
                    {
                        if (dirName.StartsWith("$") || // $RECYCLE.BIN, $Extend
                            dirName.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Equals(".Trashes", StringComparison.OrdinalIgnoreCase) || // Mac junk
                            dirName.Equals(".fseventsd", StringComparison.OrdinalIgnoreCase)) // Mac junk
                        {
                            continue;
                        }
                    }

                    try
                    {
                        foreach (var filePath in Directory.EnumerateFiles(currentDir, "*", SearchOption.TopDirectoryOnly))
                        {
                            if (ct.IsCancellationRequested) break;

                            try
                            {
                                var info = new FileInfo(filePath);

                                string line = $"{CleanStr(filePath)};{info.Length};{CleanStr(info.Extension)};{info.CreationTime:yyyy-MM-dd HH:mm:ss};{info.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
                                await writer.WriteLineAsync(line);

                                filesCount++;
                                totalBytes += info.Length;

                                if (filesCount % 2000 == 0)
                                {
                                    Console.Write($"\r[SCAN] Files: {filesCount:N0} | Size: {FormatSize(totalBytes)} | Errs: {errorsCount} | Dir: {Shorten(currentDir)}   ");
                                }
                            }
                            catch (Exception ex)
                            {
                                // errore sul singolo file
                                errorsCount++;
                                errorWriter.WriteLine($"{filePath};FILE_ERROR;{CleanStr(ex.Message)}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // errore sull intera cartella
                        errorsCount++;
                        await errorWriter.WriteLineAsync($"{currentDir};DIR_ENUM_BREAK;{CleanStr(ex.Message)}");
                    }

                    // 3. RICERCA SOTTOCARTELLE (Push nello Stack)
                    try
                    {
                        foreach (var subDir in Directory.GetDirectories(currentDir))
                        {
                            directories.Push(subDir);
                        }
                    }
                    catch (Exception ex)
                    {
                        // errori durante la scoperta delle sottocartelle
                        errorsCount++;
                        await errorWriter.WriteLineAsync($"{currentDir};SUBDIR_LIST_ERROR;{CleanStr(ex.Message)}");
                    }
                }

                // flush finale forzato per sicurezza
                await writer.FlushAsync();
                await errorWriter.FlushAsync();
            }
            catch (Exception ex)
            {
                PrintError($"ERRORE CRITICO DI SISTEMA (Disco scollegato?): {ex.Message}");
            }

            Console.WriteLine("\n\n------------------------------------------------");
            Console.WriteLine("SCANSIONE COMPLETATA (O INTERROTTA)");
            Console.WriteLine("------------------------------------------------");
            Console.WriteLine($"File censiti:    {filesCount:N0}");
            Console.WriteLine($"Dimensione tot:  {FormatSize(totalBytes)}");
            Console.WriteLine($"Errori gestiti:  {errorsCount:N0}");
            Console.WriteLine("------------------------------------------------");
        }

        // --- Helpers ---

        private string CleanStr(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            // Rimuove caratteri che rompono il CSV
            return input.Replace(";", "_").Replace("\r", " ").Replace("\n", " ");
        }

        private string Shorten(string path)
        {
            if (path.Length > 40) return "..." + path.Substring(path.Length - 40);
            return path.PadRight(43);
        }

        public override void Help()
        {
            Console.WriteLine("Uso: swiss index <root_path> <output_csv> <error_log>");
        }

        private string FormatSize(long bytes)
        {
            double gb = bytes / (1024.0 * 1024.0 * 1024.0);
            if (gb >= 1) return $"{gb:N2} GB";
            double mb = bytes / (1024.0 * 1024.0);
            if (mb >= 1) return $"{mb:N2} MB";
            return $"{bytes:N0} B";
        }
    }
}