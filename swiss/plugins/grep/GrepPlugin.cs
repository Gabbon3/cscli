using lib.console;
using lib.io;
using Microsoft.Win32.SafeHandles;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace plugins.grep
{
    class GrepPlugin : Plugin
    {
        public override string Name => "grep";
        public override string Description => "Plugin che si bagna pensando a ripgrep";

        private Regex? _searchRegex;
        private FastPrinter _fastPrinter = new();

        private static readonly HashSet<string> DefaultExcludeDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", ".git", ".svn", ".hg",
            "bin", "obj",
            ".vs", ".idea", ".vscode",
            "__pycache__", ".pytest_cache",
            "dist", "build", ".next", ".nuxt",
            "vendor",
            ".cargo",
        };

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            if (args.Length < 2)
            {
                Help();
                return;
            }

            string root = args[0] == "." ? Directory.GetCurrentDirectory() : args[0];
            if (!Directory.Exists(root))
            {
                PrintError($"il percorso \"{root}\" non esiste");
                return;
            }

            string pattern = args[1];
            ParseArguments(args, 2);

            if (pattern.Length == 0)
            {
                PrintError("Il pattern di ricerca non può essere vuoto.");
                return;
            }

            // Parsing opzioni
            bool ignoreCase = OptionsContains("-i", "--ignore-case");

            // Compilo la regex con opzioni ottimizzate per .NET 9
            var regexOptions = RegexOptions.Compiled | RegexOptions.NonBacktracking;
            if (ignoreCase) regexOptions |= RegexOptions.IgnoreCase;

            try
            {
                _searchRegex = new Regex(pattern, regexOptions);
            }
            catch (Exception ex)
            {
                PrintError($"Pattern regex non valido: {ex.Message}");
                return;
            }

            // Cartelle da escludere
            var excludeDirs = new HashSet<string>(DefaultExcludeDirs, StringComparer.OrdinalIgnoreCase);
            var excludeDirsOptions = GetOptionValue("--exclude-dir", "-ex");
            if (!string.IsNullOrEmpty(excludeDirsOptions))
            {
                foreach (var dir in excludeDirsOptions.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    excludeDirs.Add(dir.Trim());
                }
            }

            // Cartelle da includere
            var includeDirsOptions = GetOptionValue("-in", "--include-dir");
            if (!string.IsNullOrEmpty(includeDirsOptions))
            {
                foreach (var dir in includeDirsOptions.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    excludeDirs.Remove(dir.Trim());
                }
            }

            // Filtro file usando FileFilterFactory
            var globPattern = GetOptionValue("-g", "--glob");
            FileSystemFilter? fileFilter = null;

            if (!string.IsNullOrEmpty(globPattern))
            {
                var filterOpts = new FileFilterFactory.FilterOptions(
                    Pattern: globPattern,
                    MatchType: FilterFileNameMatchType.Glob,
                    IgnoreCase: true
                );

                try
                {
                    fileFilter = FileFilterFactory.CreateFilter(filterOpts);
                }
                catch (Exception ex)
                {
                    PrintError($"Errore nel filtro glob: {ex.Message}");
                    return;
                }
            }

            // Channel per i file
            var filesChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(50000)
            {
                SingleWriter = true,
                SingleReader = false
            });

            // Avvio FastPrinter
            _fastPrinter.Run(ct);

            // Workers paralleli
            int threads = Environment.ProcessorCount;
            var workers = new Task[threads];

            for (int i = 0; i < threads; i++)
            {
                workers[i] = Task.Run(async () =>
                {
                    await foreach (var path in filesChannel.Reader.ReadAllAsync(ct))
                    {
                        ProcessFile(path);
                    }
                }, ct);
            }

            // Producer: FileSystemEnumerable
            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false
            };

            var enumerable = new FileSystemEnumerable<string>(
                root,
                (ref FileSystemEntry entry) => entry.ToSpecifiedFullPath(),
                enumerationOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    if (entry.IsDirectory) return false;
                    if (fileFilter == null) return true;
                    return fileFilter(ref entry);
                },
                ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                {
                    ReadOnlySpan<char> dirName = entry.FileName;
                    foreach (var excluded in excludeDirs)
                    {
                        if (dirName.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }
                    }
                    return true;
                }
            };

            try
            {
                foreach (var fileInfo in enumerable)
                {
                    if (ct.IsCancellationRequested) break;
                    await filesChannel.Writer.WriteAsync(fileInfo, ct);
                }
            }
            finally
            {
                filesChannel.Writer.Complete();
            }

            await Task.WhenAll(workers);
            await _fastPrinter.Complete();
        }

        /// <summary>
        /// Processa un file cercando il pattern regex a chunk
        /// </summary>
        private void ProcessFile(string path)
        {
            SafeFileHandle? handle = null;
            try
            {
                handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    FileOptions.SequentialScan);
                long fileLength = RandomAccess.GetLength(handle);
                if (fileLength == 0) return;

                const int byteBufferSize = 65536; // 64KB byte buffer
                const int charBufferSize = byteBufferSize; // max char = max byte per UTF-8
                const int maxOverlap = 1000; // overlap in caratteri

                byte[] byteBuffer = new byte[byteBufferSize];
                char[] charBuffer = new char[charBufferSize + maxOverlap];

                long fileOffset = 0;
                int charLeftover = 0;
                int lineNumber = 1;
                int skipChars = 0;
                bool isFirstChunk = true;
                int incompleteBytesCount = 0; // byte UTF-8 incompleti dal chunk precedente

                while (fileOffset < fileLength)
                {
                    int bytesToRead = (int)Math.Min(byteBufferSize - incompleteBytesCount, fileLength - fileOffset);
                    int bytesRead = RandomAccess.Read(handle, byteBuffer.AsSpan(incompleteBytesCount, bytesToRead), fileOffset);
                    if (bytesRead == 0) break;

                    int totalBytes = bytesRead + incompleteBytesCount;
                    ReadOnlySpan<byte> byteSpan = byteBuffer.AsSpan(0, totalBytes);

                    // Controllo file binario (solo primo chunk)
                    if (isFirstChunk)
                    {
                        if (byteSpan.Contains((byte)0)) return;
                        isFirstChunk = false;
                    }

                    // Decodifica UTF-8: byte[] -> char[]
                    // Decodifico dopo il leftover di caratteri dal chunk precedente
                    int charsDecoded;
                    int bytesConsumed;
                    bool completed;

                    Encoding.UTF8.GetDecoder().Convert(
                        byteSpan,
                        charBuffer.AsSpan(charLeftover),
                        flush: fileOffset + bytesRead >= fileLength, // flush solo se è l'ultimo chunk
                        out bytesConsumed,
                        out charsDecoded,
                        out completed);

                    int totalChars = charLeftover + charsDecoded;
                    ReadOnlySpan<char> searchSpan = charBuffer.AsSpan(0, totalChars);

                    // Converto in string per Regex.EnumerateMatches
                    string searchText = new string(searchSpan);

                    // Cerco tutti i match
                    foreach (var match in _searchRegex!.EnumerateMatches(searchText))
                    {
                        if (match.Index < skipChars) continue;

                        int matchLineNumber = lineNumber + CountLines(searchText.AsSpan(0, match.Index));
                        string contextStr = ExtractMatchContext(searchText, match.Index, match.Length);

                        _fastPrinter.TryPost(
                            $"[Green]#[/] [DarkGray]{Path.GetDirectoryName(path)}{Path.DirectorySeparatorChar}[/]" +
                            $"[Cyan]{Path.GetFileName(path)}[/]\n" +
                            $"[Green]# [Yellow]{matchLineNumber}:[/] {contextStr}\n" +
                            $"[DarkGray]*\n*[/]");
                    }

                    // Gestione overlap
                    bool isLastChunk = fileOffset + bytesRead >= fileLength;

                    if (!isLastChunk && totalChars > maxOverlap)
                    {
                        // Salvo overlap caratteri
                        charLeftover = maxOverlap;
                        searchSpan[(totalChars - maxOverlap)..].CopyTo(charBuffer);
                        skipChars = charLeftover;

                        // Conta righe consumate
                        int consumedChars = totalChars - charLeftover;
                        lineNumber += CountLines(searchSpan[..consumedChars]);

                        // Gestione byte UTF-8 incompleti
                        // Se non ho consumato tutti i byte, significa che l'ultimo carattere era incompleto
                        incompleteBytesCount = totalBytes - bytesConsumed;
                        if (incompleteBytesCount > 0)
                        {
                            // Sposto byte incompleti all'inizio del buffer
                            byteSpan[(totalBytes - incompleteBytesCount)..].CopyTo(byteBuffer);
                        }
                    }
                    else
                    {
                        charLeftover = 0;
                        skipChars = 0;
                        incompleteBytesCount = 0;
                    }

                    fileOffset += bytesRead;
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            finally
            {
                handle?.Dispose();
            }
        }

        /// <summary>
        /// Estrae il contesto attorno al match evidenziando il testo trovato
        /// </summary>
        private static string ExtractMatchContext(string text, int matchIndex, int matchLength, int maxContext = 50)
        {
            int start = Math.Max(0, matchIndex - maxContext);
            int endMatch = matchIndex + matchLength;
            int end = Math.Min(endMatch + maxContext, text.Length);

            ReadOnlySpan<char> span = text.AsSpan();

            // Trova inizio/fine riga
            ReadOnlySpan<char> leftSpan = span[start..matchIndex];
            int preNewLine = leftSpan.LastIndexOf('\n');
            int actualStart = preNewLine != -1 ? start + preNewLine + 1 : start;
            bool truncatedLeft = preNewLine == -1 && start > 0;

            ReadOnlySpan<char> rightSpan = span[endMatch..end];
            int postNewLine = rightSpan.IndexOf('\n');
            int actualEnd = postNewLine != -1 ? endMatch + postNewLine : end;
            if (actualEnd > 0 && span[actualEnd - 1] == '\r') actualEnd--;
            bool truncatedRight = postNewLine == -1 && end < text.Length;

            // Compongo il risultato
            StringBuilder sb = new StringBuilder(actualEnd - actualStart + 20);

            if (truncatedLeft) sb.Append("...");
            sb.Append(span[actualStart..matchIndex]);
            sb.Append("[Red]");
            sb.Append(span[matchIndex..endMatch]);
            sb.Append("[/]");
            sb.Append(span[endMatch..actualEnd]);
            if (truncatedRight) sb.Append("...");

            return sb.ToString();
        }

        /// <summary>
        /// Conta il numero di newline in uno span
        /// </summary>
        private static int CountLines(ReadOnlySpan<char> span)
        {
            int count = 0;
            int index;
            while ((index = span.IndexOf('\n')) != -1)
            {
                count++;
                span = span[(index + 1)..];
            }
            return count;
        }

        public override void Help()
        {
            ConsolePlus.Write("[Cyan]#[DarkGray] ------------------------------------------------ [Cyan]#[/]");
            ConsolePlus.Write("[Cyan]#[/] Utilizzo: [Yellow]swiss [Magenta]grep [DarkGray]<percorso> <pattern> [opzioni]");
            ConsolePlus.Write("[Cyan]#[/] <pattern>                   : pattern regex (usa [Cyan]|[/] per alternative)");
            ConsolePlus.Write("[Cyan]#[/] Opzioni:");
            ConsolePlus.Write("[Cyan]#[/]   --ignore-case, -i         : Ricerca case insensitive");
            ConsolePlus.Write("[Cyan]#[/]   --exclude-dir <dir,...>   : Aggiunge cartelle da escludere");
            ConsolePlus.Write("[Cyan]#[/]   --include-dir <dir,...>   : Riabilita cartelle escluse di default");
            ConsolePlus.Write("[Cyan]#[/]   --glob <pattern>          : Filtra file per pattern glob (es. *.cs)");
            ConsolePlus.Write("[Cyan]#[DarkGray] ------------------------------------------------ [Cyan]#[/]");
        }
    }
}