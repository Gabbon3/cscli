using System.IO.Enumeration;
using System.Threading.Channels;
using System.Text;
using Microsoft.Win32.SafeHandles;
using utils.console;
using System.Runtime.CompilerServices;

namespace plugins.grep
{
    class GrepPlugin : Plugin
    {
        public override string Name => "grep";
        public override string Description => "Plugin che si bagna pensando a ripgrep";
        private byte[] Pattern = [];
        private bool IgnoreCase = false;
        // # ------------------------------- #
        // # Cartelle da ignorare di default #
        // # ------------------------------- #
        private static readonly HashSet<string> DefaultExcludeDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", ".git", ".svn", ".hg",
            "bin", "obj",                           // .NET
            ".vs", ".idea", ".vscode",              // IDE
            "__pycache__", ".pytest_cache",         // Python
            "dist", "build", ".next", ".nuxt",      // frontend build
            "vendor",                               // Go / PHP
            ".cargo",                               // Rust
        };
        // Record per gestire il match
        private readonly struct GrepMatch(string path, int lineNumber, string formattedContext) : IPrintable
        {
            public string ToFormattedString()
            {
                string? directory = Path.GetDirectoryName(path);
                string? fileName = Path.GetFileName(path);
                return $"[Green]#[/] [DarkGray]{directory}{Path.DirectorySeparatorChar}[/][Cyan]{fileName}[/]\n[Green]# [Yellow]{lineNumber}:[/] {formattedContext}\n[DarkGray]#[/]";
            }
        }
        private FastPrinter<GrepMatch> _fastPrinter = new();

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
            var options = ParseArguments(args, 2);

            if (pattern.Length == 0)
            {
                PrintError("Il pattern di ricerca non può essere vuoto.");
                return;
            }
            // # --------------------- #
            // # Parsing delle opzioni #
            // # --------------------- #
            IgnoreCase = options.ContainsKey("--ignore-case") || options.ContainsKey("-i");
            Pattern = Encoding.UTF8.GetBytes(IgnoreCase ? pattern.ToLowerInvariant() : pattern);
            // cartelle da escludere
            var excludeDirs = new HashSet<string>(DefaultExcludeDirs, StringComparer.OrdinalIgnoreCase);
            var excludeDirsOptions = options.TryGetValue("--exclude-dir", out string? ed1) ? ed1 : options.TryGetValue("-ex", out string? ed2) ? ed2 : null;
            if (!string.IsNullOrEmpty(excludeDirsOptions))
            {
                foreach (var dir in excludeDirsOptions.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    excludeDirs.Add(dir.Trim());
                }
            }
            // cartelle da includere rispetto a quelle di default
            var includeDirsOptions = options.TryGetValue("--include-dir", out string? id1) ? id1 : options.TryGetValue("-in", out string? id2) ? id2 : null;
            if (!string.IsNullOrEmpty(includeDirsOptions))
            {
                foreach (var dir in includeDirsOptions.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    excludeDirs.Remove(dir.Trim());
                }
            }
            // pattern glob per escludere files
            var includeGlobs = new List<string>();
            var GlobOptions = options.TryGetValue("--glob", out string? gl1) ? gl1 : options.TryGetValue("-g", out string? gl2) ? gl2 : null;
            if (!string.IsNullOrEmpty(GlobOptions))
            {
                foreach (var glob in GlobOptions.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    includeGlobs.Add(glob.Trim());
                }
            }

            // # ---------------------- #
            // # 1. Preparo il producer #
            // # ---------------------- #
            var filesChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(50000)
            {
                SingleWriter = true,
                SingleReader = false
            });

            // avvio il printer ad alte prestazioni sulla console
            _fastPrinter.Run(ct);

            // # --------------------- #
            // # 2. Preparo gli operai #
            // # --------------------- #
            int threads = Environment.ProcessorCount;
            var workers = new Task[threads];
            int overlap = Pattern.Length - 1;

            for (int i = 0; i < threads; i++)
            {
                workers[i] = Task.Run(async () =>
                {
                    byte[] workerBuffer = new byte[65536];
                    byte[] lowerBuffer = IgnoreCase ? new byte[65536] : [];

                    await foreach (var path in filesChannel.Reader.ReadAllAsync(ct))
                    {
                        ProcessFile(path, workerBuffer, lowerBuffer, overlap);
                    }
                }, ct);
            }

            // # --------------------------------- #
            // # 3. Producer: FileSystemEnumerable #
            // # --------------------------------- #
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
                // logica di esclusione dei file
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    if (entry.IsDirectory) return false;
                    // se non ci sono filtri passo tutto
                    if (includeGlobs.Count == 0) return true;

                    ReadOnlySpan<char> fileName = entry.FileName;
                    // per ogni regola di esclusione la verifico
                    foreach (var glob in includeGlobs)
                    {
                        if (FileSystemName.MatchesSimpleExpression(glob, fileName, ignoreCase: true))
                        {
                            return true;
                        }
                    }
                    // qui il file non ha superato nessun match
                    return false;
                },
                // qui filtriamo le cartelle da includere nella ricorsione
                ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                {
                    ReadOnlySpan<char> dirName = entry.FileName; // qui file name => nome cartella
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

        private void ProcessFile(string path, byte[] buffer, byte[] lowerBuffer, int overlap)
        {
            SafeFileHandle? handle = null;
            try
            {
                // API OS-Level: bypassa FileStream. Apre un handle sicuro al file.
                // SequentialScan dice al Kernel di caricare i blocchi successivi in cache hardware.
                handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.SequentialScan);

                long fileLength = RandomAccess.GetLength(handle);
                if (fileLength == 0) return;

                long fileOffset = 0;
                int leftover = 0;

                // controllo se questo è un file binario
                bool isFirstChunk = true;

                while (fileOffset < fileLength)
                {
                    // leggo la porzione di file calcolando lo spazio libero nel buffer
                    int bytesToRead = (int)Math.Min(buffer.Length - leftover, fileLength - fileOffset);
                    // RandomAccess legge direttamente tramite handle OS dello span
                    int bytesRead = RandomAccess.Read(handle, buffer.AsSpan(leftover, bytesToRead), fileOffset);
                    if (bytesRead == 0) break;

                    int currentDataLength = bytesRead + leftover;
                    ReadOnlySpan<byte> dataSpan = buffer.AsSpan(0, currentDataLength);
                    // Se è il primo blocco e contiene un byte 0, allora è un binario/video/exe
                    if (isFirstChunk)
                    {
                        if (dataSpan.Contains((byte)0)) return;
                        isFirstChunk = false;
                    }

                    ReadOnlySpan<byte> searchSpan;

                    if (IgnoreCase)
                    {
                        var sourceSpan = buffer.AsSpan(0, currentDataLength);
                        var destSpan = lowerBuffer.AsSpan(0, currentDataLength);
                        // uso le istruzioni SIMD
                        Ascii.ToLower(sourceSpan, destSpan, out _);
                        searchSpan = destSpan;
                    }
                    else
                    {
                        searchSpan = buffer.AsSpan(0, currentDataLength);
                    }

                    int matchIndex = searchSpan.IndexOf(Pattern);

                    if (matchIndex != -1)
                    {
                        int lineNumber = CountLines(searchSpan[..matchIndex]);
                        ReadOnlySpan<byte> originalDataSpan = buffer.AsSpan(0, currentDataLength);
                        string contextStr = ExtractMatchContext(originalDataSpan, matchIndex, Pattern.Length, 50);
                        _fastPrinter.TryPost(new GrepMatch(path, lineNumber, contextStr));
                        break;
                    }

                    if (currentDataLength > overlap)
                    {
                        leftover = overlap;
                        searchSpan[(currentDataLength - overlap)..].CopyTo(buffer);
                    }
                    else
                    {
                        leftover = currentDataLength;
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
        /// Restituisce la stringa gia con i colori del match trovato
        /// </summary>
        /// <param name="span"></param>
        /// <param name="matchIndex"></param>
        /// <param name="patternLength"></param>
        /// <param name="maxContext"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string ExtractMatchContext(ReadOnlySpan<byte> span, int matchIndex, int patternLength, int maxContext = 50)
        {
            int start = Math.Max(0, matchIndex - maxContext);
            int endMatch = matchIndex + patternLength;
            int end = Math.Min(endMatch + maxContext, span.Length);

            var leftSpan = span[start..matchIndex];
            int preNewLine = leftSpan.LastIndexOf((byte)'\n');
            int actualStart = preNewLine != -1 ? start + preNewLine + 1 : start;
            bool truncatedLeft = preNewLine == -1 && start > 0;

            var rightSpan = span[endMatch..end];
            int postNewLine = rightSpan.IndexOf((byte)'\n');
            int actualEnd = postNewLine != -1 ? endMatch + postNewLine : end;
            if (actualEnd > 0 && span[actualEnd - 1] == '\r') actualEnd--;
            bool truncatedRight = postNewLine == -1 && end < span.Length;

            var exactLeft = span[actualStart..matchIndex];
            var exactMatch = span[matchIndex..endMatch];
            var exactRight = span[endMatch..actualEnd];

            // +14 per i ... (6) e i tag colore (8)
            Span<char> buffer = stackalloc char[actualEnd - actualStart + 14];
            int pos = 0;

            // prefisso
            if (truncatedLeft)
            {
                "...".AsSpan().CopyTo(buffer[pos..]);
                pos += 3;
            }

            // decodifico sinistra
            int leftChars = Encoding.UTF8.GetChars(exactLeft, buffer[pos..]);
            pos += leftChars;

            // tag rosso e match
            "[Red]".AsSpan().CopyTo(buffer[pos..]);
            pos += 5;

            int matchChars = Encoding.UTF8.GetChars(exactMatch, buffer[pos..]);
            pos += matchChars;

            "[/]".AsSpan().CopyTo(buffer[pos..]);
            pos += 3;

            // decodifico destra
            int rightChars = Encoding.UTF8.GetChars(exactRight, buffer[pos..]);
            pos += rightChars;

            // suffisso
            if (truncatedRight)
            {
                "...".AsSpan().CopyTo(buffer[pos..]);
                pos += 3;
            }

            return new string(buffer[..pos]);
        }

        /// <summary>
        /// Restituisce il numero di riga del match
        /// </summary>
        /// <param name="span"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CountLines(ReadOnlySpan<byte> span)
        {
            int count = 0;
            int index;
            while ((index = span.IndexOf((byte)'\n')) != -1)
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
            ConsolePlus.Write("[Cyan]#[/] Opzioni:");
            ConsolePlus.Write("[Cyan]#[/]   --ignore-case, -i         : Ricerca case insensitive (ASCII)");
            ConsolePlus.Write("[Cyan]#[/]   --exclude-dir <dir,...>   : Aggiunge cartelle da escludere");
            ConsolePlus.Write("[Cyan]#[/]   --include-dir <dir,...>   : Riabilita cartelle escluse di default");
            ConsolePlus.Write("[Cyan]#[/]   --glob <pattern,...>      : Esclude file per pattern (es. *.min.js)");
            ConsolePlus.Write("[Cyan]#[DarkGray] ------------------------------------------------ [Cyan]#[/]");
        }
    }
}