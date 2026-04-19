using System.IO.Enumeration;
using System.Threading.Channels;
using System.Text;
using Microsoft.Win32.SafeHandles;
using lib.console;
using System.Runtime.CompilerServices;
using lib.algorithm;

namespace plugins.grep
{
    class GrepPlugin : Plugin
    {
        public override string Name => "grep";
        public override string Description => "Plugin che si bagna pensando a ripgrep";
        // Lunghezza del pattern di ricerca piu lungo per gestire overlap
        private int LongestPattern = 0;
        // Lunghezza di tutti i pattern
        private int[] PatternLengths = [];
        private bool IgnoreCase = false;
        // motore di ricerca di grep (usato per ricerca di piu parole insieme)
        AhoCorasick? AhoEngine;
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
        private readonly struct AhoMatchHandler(string path, byte[] buffer, int currentDataLength, int chunkStartLine, int[] patternLengths, FastPrinter printer) : IMatchHandler
        {
            private readonly byte[] _buffer = buffer;
            private readonly int _currentDataLength = currentDataLength;
            private readonly int _chunkStartLine = chunkStartLine;
            private readonly int[] _patternLengths = patternLengths;
            private readonly FastPrinter _printer = printer;

            public void OnMatch(int startIndex, int endIndex, int patternIndex, int relativeLine)
            {
                int patLen = _patternLengths[patternIndex];
                ReadOnlySpan<byte> originalDataSpan = _buffer.AsSpan(0, _currentDataLength);
                int lineNumber = _chunkStartLine + relativeLine;
                string contextStr = ExtractMatchContext(originalDataSpan, startIndex, patLen, maxContext: 50);
                _printer.TryPost($"[Green]#[/] [DarkGray]{Path.GetDirectoryName(path)}{Path.DirectorySeparatorChar}[/][Cyan]{Path.GetFileName(path)}[/]\n[Green]# [Yellow]{lineNumber}:[/] {contextStr}\n[DarkGray]*\n*[/]");
            }
        }
        private FastPrinter _fastPrinter = new();

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
            // preparo il pattern per AhoChorasick
            string[] wordsToSearch = pattern.Split('|', StringSplitOptions.RemoveEmptyEntries);
            LongestPattern = wordsToSearch[0].Length;
            // # --------------------- #
            // # Parsing delle opzioni #
            // # --------------------- #
            IgnoreCase = OptionsContains("-i", "--ignore-case");
            var patternList = new ReadOnlyMemory<byte>[wordsToSearch.Length];
            PatternLengths = new int[wordsToSearch.Length]; // Inizializza l'array

            for (int i = 0; i < wordsToSearch.Length; i++)
            {
                PatternLengths[i] = wordsToSearch[i].Length; // Salva la lunghezza
                if (wordsToSearch[i].Length > LongestPattern) LongestPattern = wordsToSearch[i].Length;
                patternList[i] = Encoding.UTF8.GetBytes(IgnoreCase ? wordsToSearch[i].ToLowerInvariant() : wordsToSearch[i]);
            }

            // cartelle da escludere
            var excludeDirs = new HashSet<string>(DefaultExcludeDirs, StringComparer.OrdinalIgnoreCase);
            var excludeDirsOptions = GetOptionValue("--exclude-dir", "-ex");
            if (!string.IsNullOrEmpty(excludeDirsOptions))
            {
                foreach (var dir in excludeDirsOptions.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    excludeDirs.Add(dir.Trim());
                }
            }

            // cartelle da includere rispetto a quelle di default
            var includeDirsOptions = GetOptionValue("-in", "--include-dir");
            if (!string.IsNullOrEmpty(includeDirsOptions))
            {
                foreach (var dir in includeDirsOptions.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    excludeDirs.Remove(dir.Trim());
                }
            }

            // pattern glob per escludere files
            var includeGlobs = new List<string>();
            var GlobOptions = GetOptionValue("-g", "--glob");
            if (!string.IsNullOrEmpty(GlobOptions))
            {
                foreach (var glob in GlobOptions.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    includeGlobs.Add(glob.Trim());
                }
            }

            // inizializzo l'automa di AhoCorasick solo dopo aver validato tutto
            AhoEngine = new AhoCorasick(patternList);

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

            for (int i = 0; i < threads; i++)
            {
                workers[i] = Task.Run(async () =>
                {
                    byte[] workerBuffer = new byte[65536];
                    byte[] lowerBuffer = IgnoreCase ? new byte[65536] : [];

                    await foreach (var path in filesChannel.Reader.ReadAllAsync(ct))
                    {
                        ProcessFile(path, workerBuffer, lowerBuffer, LongestPattern);
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

        /// <summary>
        /// Processa un file andando a cercare il pattern definito all'inizio con AhoCorasick a blocchi di 64KB
        /// </summary>
        /// <param name="path"></param>
        /// <param name="buffer">Definito in precedenza: byte[] workerBuffer = new byte[65536];</param>
        /// <param name="lowerBuffer"></param>
        /// <param name="overlap"></param>
        private void ProcessFile(string path, byte[] buffer, byte[] lowerBuffer, int overlap)
        {
            SafeFileHandle? handle = null;
            try
            {
                handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.SequentialScan);
                long fileLength = RandomAccess.GetLength(handle);
                if (fileLength == 0) return;

                long fileOffset = 0;
                int leftover = 0;
                int totalLines = 1;
                bool isFirstChunk = true;

                while (fileOffset < fileLength)
                {
                    int bytesToRead = (int)Math.Min(buffer.Length - leftover, fileLength - fileOffset);
                    int bytesRead = RandomAccess.Read(handle, buffer.AsSpan(leftover, bytesToRead), fileOffset);
                    if (bytesRead == 0) break;

                    int currentDataLength = bytesRead + leftover;
                    ReadOnlySpan<byte> dataSpan = buffer.AsSpan(0, currentDataLength);

                    // controllo se si tratta di un file binario
                    if (isFirstChunk)
                    {
                        if (dataSpan.Contains((byte)0)) return;
                        isFirstChunk = false;
                    }

                    ReadOnlySpan<byte> searchSpan;
                    if (IgnoreCase)
                    {
                        var destSpan = lowerBuffer.AsSpan(0, currentDataLength);
                        Ascii.ToLower(dataSpan, destSpan, out _);
                        searchSpan = destSpan;
                    }
                    else
                    {
                        searchSpan = dataSpan;
                    }

                    // creo l'handler passando tutto il necessario
                    var handler = new AhoMatchHandler(path, buffer, currentDataLength, totalLines, PatternLengths, _fastPrinter);

                    // inizio la ricerca con AhoCorasick e itero su tutti i byte
                    AhoEngine!.Search(searchSpan, ref handler);

                    // gestione leftover
                    if (currentDataLength > overlap)
                    {
                        leftover = overlap;
                        dataSpan[(currentDataLength - overlap)..].CopyTo(buffer);
                    }
                    else
                    {
                        leftover = currentDataLength;
                    }

                    // conto il numero di righe presenti in questo blocco di ricerca
                    int consumedLength = currentDataLength - leftover;
                    totalLines += CountLines(dataSpan[..consumedLength]);

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
            ConsolePlus.Write("[Cyan]#[/] <pattern>                   : cerca più parole contemporaneamente separandole con [Cyan]|[/]");
            ConsolePlus.Write("[Cyan]#[/] Opzioni:");
            ConsolePlus.Write("[Cyan]#[/]   --ignore-case, -i         : Ricerca case insensitive (ASCII)");
            ConsolePlus.Write("[Cyan]#[/]   --exclude-dir <dir,...>   : Aggiunge cartelle da escludere");
            ConsolePlus.Write("[Cyan]#[/]   --include-dir <dir,...>   : Riabilita cartelle escluse di default");
            ConsolePlus.Write("[Cyan]#[/]   --glob <pattern,...>      : Esclude file per pattern (es. *.min.js)");
            ConsolePlus.Write("[Cyan]#[DarkGray] ------------------------------------------------ [Cyan]#[/]");
        }
    }
}