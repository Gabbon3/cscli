using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace plugins.searcher
{
    class SearcherPlugin : Plugin
    {
        public override string Name => "search";
        public override string Description => "Cerca e salva CSV (Path; Line -> Match | Line -> Match). Utilizza come indice due tipi di file CSV ottenibili dal plugin index o da Everything";

        private const string RGA_EXE = "rga.exe";
        private const int MAX_CMD_LENGTH = 30000;
        private const int MAX_BATCH_FILES = 1000;
        private const long LARGE_FILE_THRESHOLD = 500 * 1024 * 1024; // 100 MB

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length < 5)
            {
                Help();
                return;
            }

            string indexFile = args[0];
            string errorFile = args[1]; 
            string checkpointFile = args[2];
            string resultsFile = args[3];
            string searchTerm = args[4];
            
            string? exclusionPatterns = args.Length > 5 ? args[5] : null;
            // se non viene definito un numero specifico di thread nThread diventa 0 utilizzando tutti i core su rga
            int nThreads = 0;
            if (args.Length > 6)
            {
                int.TryParse(args[6], out nThreads);
            }

            Regex? exclusionRegex = null;
            if (!string.IsNullOrWhiteSpace(exclusionPatterns))
            {
                try 
                {
                    exclusionRegex = new Regex(exclusionPatterns, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                    Console.WriteLine($"[FILTER] Filtro attivo: \"{exclusionPatterns}\"");
                }
                catch (Exception ex)
                {
                    PrintError($"Errore nella Regex di esclusione: {ex.Message}");
                    return;
                }
            }

            if (!File.Exists(indexFile))
            {
                PrintError($"File indice non trovato: {indexFile}");
                return;
            }

            if (!CheckRgaHealth()) return;

            // --- RESUME ---
            long lastProcessedLine = 0;
            if (File.Exists(checkpointFile))
            {
                try
                {
                    string content = await File.ReadAllTextAsync(checkpointFile);
                    if (long.TryParse(content, out long savedLine)) lastProcessedLine = savedLine;
                }
                catch { }
            }

            using var resultWriter = new StreamWriter(new FileStream(resultsFile, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
            using var errorWriter = new StreamWriter(new FileStream(errorFile, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);

            // Intestazioni CSV: Ora sono solo 2 colonne
            if (new FileInfo(resultsFile).Length == 0) await resultWriter.WriteLineAsync("FilePath;MatchesContext");
            if (new FileInfo(errorFile).Length == 0) await errorWriter.WriteLineAsync("FilePath;ErrorDetail");

            Console.WriteLine($"\nAvvio ricerca (Output Smart Aggregato) di \"{searchTerm}\"...");

            using var reader = new StreamReader(new FileStream(indexFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            
            long currentLine = 0;
            List<string> batchFiles = new();
            int currentBatchLength = 0;
            long totalMatches = 0;
            long scannedFiles = 0;

            var sw = Stopwatch.StartNew();

            while (!reader.EndOfStream)
            {
                if (ct.IsCancellationRequested) break;

                string? line = await reader.ReadLineAsync();
                currentLine++;

                if (currentLine <= lastProcessedLine) continue;
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Salta intestazione Everything
                if (line.StartsWith("\"Nome\"") || line.StartsWith("\"Name\"")) continue;

                string fullPath = "";
                long size = 0;

                try 
                {
                    // --- PARSER EVERYTHING ---
                    int splitNamePath = line.IndexOf("\",\"");
                    int splitPathSize = line.LastIndexOf("\",");

                    if (splitNamePath != -1 && splitPathSize != -1 && splitPathSize > splitNamePath)
                    {
                        string fileName = line.Substring(1, splitNamePath - 1);
                        int startPath = splitNamePath + 3;
                        int lengthPath = splitPathSize - startPath;
                        string dirPath = line.Substring(startPath, lengthPath);
                        fullPath = Path.Combine(dirPath, fileName);

                        string remainder = line.Substring(splitPathSize + 2);
                        int commaAfterSize = remainder.IndexOf(',');
                        string sizeStr = (commaAfterSize != -1) ? remainder.Substring(0, commaAfterSize) : remainder;
                        long.TryParse(sizeStr, out size);
                    }
                    else if (!line.Contains('"')) 
                    {
                        var parts = line.Split(';');
                        if (parts.Length >= 2)
                        {
                            fullPath = parts[0];
                            long.TryParse(parts[1], out size);
                        }
                    }
                }
                catch { continue; } 

                if (string.IsNullOrWhiteSpace(fullPath)) continue;
                if (exclusionRegex != null && exclusionRegex.IsMatch(fullPath)) continue;

                bool isLargeFile = size > LARGE_FILE_THRESHOLD;
                int pathLen = fullPath.Length + 3;

                if (isLargeFile || (currentBatchLength + pathLen > MAX_CMD_LENGTH) || (batchFiles.Count >= MAX_BATCH_FILES))
                {
                    if (batchFiles.Count > 0)
                    {
                        totalMatches += await ProcessBatchJsonAsync(batchFiles, searchTerm, resultWriter, errorWriter, nThreads, ct);
                        scannedFiles += batchFiles.Count;
                        SaveCheckpointSafe(checkpointFile, currentLine - 1);
                        
                        Console.Write($"\r[SEARCH] Idx: {currentLine:N0} | Files: {scannedFiles:N0} | Matches: {totalMatches:N0} | {sw.Elapsed:hh\\:mm\\:ss}");
                        
                        batchFiles.Clear();
                        currentBatchLength = 0;
                    }
                }

                if (isLargeFile)
                {
                    var singleFileBatch = new List<string> { fullPath };
                    totalMatches += await ProcessBatchJsonAsync(singleFileBatch, searchTerm, resultWriter, errorWriter, nThreads, ct);
                    scannedFiles++;
                    SaveCheckpointSafe(checkpointFile, currentLine); 
                }
                else
                {
                    batchFiles.Add(fullPath);
                    currentBatchLength += pathLen;
                }
            }

            if (batchFiles.Count > 0 && !ct.IsCancellationRequested)
            {
                totalMatches += await ProcessBatchJsonAsync(batchFiles, searchTerm, resultWriter, errorWriter, nThreads, ct);
                scannedFiles += batchFiles.Count;
                SaveCheckpointSafe(checkpointFile, currentLine);
            }

            sw.Stop();
            Console.WriteLine("\n\n------------------------------------------------");
            Console.WriteLine("RICERCA COMPLETATA");
            Console.WriteLine($"Tempo totale: {sw.Elapsed}");
            Console.WriteLine($"Match totali: {totalMatches:N0}");
            Console.WriteLine("------------------------------------------------");
        }

        private async Task<long> ProcessBatchJsonAsync(List<string> files, string term, StreamWriter resWriter, StreamWriter errWriter, int nThreads, CancellationToken ct)
        {
            if (files.Count == 0) return 0;
            long matchCount = 0;

            var args = new StringBuilder();
            if (nThreads > 0)
            {
                args.Append($"-j {nThreads} ");
            }
            args.Append($"--json -z -n -i --no-config "); 
            args.Append($"\"{term}\" ");

            foreach (var f in files)
            {
                string safePath = f.Replace("\"", "\\\"");
                args.Append($"\"{safePath}\" ");
            }

            var psi = new ProcessStartInfo
            {
                FileName = RGA_EXE,
                Arguments = args.ToString(),
                RedirectStandardOutput = true,
                RedirectStandardError = true, 
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            try
            {
                using var process = new Process { StartInfo = psi };
                process.Start();

                // --- BUFFER PER AGGREGAZIONE ---
                string? pendingPath = null;
                // Lista di stringhe formattate tipo "110 -> LDH"
                List<string> pendingContexts = new List<string>();

                while (!process.StandardOutput.EndOfStream)
                {
                    if (ct.IsCancellationRequested)
                    {
                        process.Kill();
                        return matchCount;
                    }

                    string? jsonLine = await process.StandardOutput.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(jsonLine)) continue;

                    try
                    {
                        JsonNode? root = JsonNode.Parse(jsonLine);
                        if (root == null) continue;

                        string type = root["type"]?.ToString() ?? "";

                        if (type == "match")
                        {
                            string currentPath = root["data"]?["path"]?["text"]?.ToString() ?? "Unknown";
                            string currentLineNum = root["data"]?["line_number"]?.ToString() ?? "0";

                            // Estrai i submatches
                            var submatches = root["data"]?["submatches"]?.AsArray();
                            var currentMatchesFound = new List<string>();
                            if (submatches != null)
                            {
                                foreach (var item in submatches)
                                {
                                    string? txt = item?["match"]?["text"]?.ToString();
                                    // Puliamo il testo matchato per non rompere il CSV
                                    if (!string.IsNullOrEmpty(txt)) currentMatchesFound.Add(CleanCsvContent(txt));
                                }
                            }
                            
                            // Creiamo la stringa "110 -> LDH - 0018"
                            // Se ci sono più match sulla stessa riga li uniamo con " - "
                            string matchContent = string.Join(" - ", currentMatchesFound);
                            string formattedEntry = $"{currentLineNum} -> {matchContent}";

                            // --- LOGICA DI AGGREGAZIONE ---
                            if (currentPath != pendingPath)
                            {
                                // Cambio file: flush del precedente
                                if (pendingPath != null)
                                {
                                    // Uniamo tutte le righe trovate con " | "
                                    string joinedContexts = string.Join(" | ", pendingContexts);
                                    await resWriter.WriteLineAsync($"{pendingPath};{joinedContexts}");
                                    matchCount++; 
                                }

                                // Reset buffer
                                pendingPath = currentPath;
                                pendingContexts.Clear();
                                pendingContexts.Add(formattedEntry);
                            }
                            else
                            {
                                // Stesso file: aggiungi alla lista
                                pendingContexts.Add(formattedEntry);
                            }
                        }
                        else if (type == "error")
                        {
                            string errText = root["data"]?["text"]?.ToString() ?? "Unknown Error";
                            string refFile = files.Count == 1 ? files[0] : "Batch_Error";
                            await errWriter.WriteLineAsync($"{refFile};{CleanCsvContent(errText)}");
                        }
                    }
                    catch (JsonException) { }
                }
                
                // --- FLUSH FINALE DEL BUFFER ---
                if (pendingPath != null)
                {
                    string joinedContexts = string.Join(" | ", pendingContexts);
                    await resWriter.WriteLineAsync($"{pendingPath};{joinedContexts}");
                    matchCount++;
                }
                // -------------------------------

                string stderr = await process.StandardError.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                     if (!stderr.Contains("DEBUG") && stderr.Length < 1000)
                     {
                         string refFile = files.FirstOrDefault() ?? "Batch";
                         await errWriter.WriteLineAsync($"{refFile};STDERR: {CleanCsvContent(stderr)}");
                     }
                }

                await process.WaitForExitAsync(ct);
            }
            catch (Exception ex)
            {
                string refFile = files.FirstOrDefault() ?? "BatchUnknown";
                await errWriter.WriteLineAsync($"{refFile} [BATCH_CRASH];{CleanCsvContent(ex.Message)}");
            }

            await resWriter.FlushAsync();
            await errWriter.FlushAsync();
            return matchCount;
        }

        private string CleanCsvContent(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            // Rimuoviamo ; e a capo per non rompere il CSV
            string clean = input.Replace(";", "_").Replace("\r", "").Replace("\n", "").Trim();
            // Accorciamo leggermente i singoli match per evitare righe chilometriche
            if (clean.Length > 100) clean = clean.Substring(0, 100) + "...";
            return clean;
        }

        private void SaveCheckpointSafe(string finalPath, long lineNumber)
        {
            string tempPath = finalPath + ".tmp";
            try { File.WriteAllText(tempPath, lineNumber.ToString()); File.Move(tempPath, finalPath, true); } catch { }
        }
        
        private bool CheckRgaHealth()
        {
            try { 
                var psi = new ProcessStartInfo(RGA_EXE, "--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using var p = Process.Start(psi);
                p?.WaitForExit();
                return true; 
            } catch { PrintError("RGA non trovato."); return false; }
        }

        public override void Help()
        {
             Console.WriteLine("swiss search <index.csv> <errors.csv> <chk.chk> <results.csv> <query> [regex_esclusione] <numero_thread_rga>");
        }
    }
}