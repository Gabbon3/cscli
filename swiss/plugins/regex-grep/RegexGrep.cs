using System.IO.Enumeration;
using System.Threading.Channels;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;
using lib.console;
using lib.console.fastprinter;
using lib.utils;
using System.Buffers;
using lib.io;
using System.Diagnostics;

namespace plugins.regexgrep
{
    class RegexGrepPlugin : Plugin
    {
        public override string Name => "rgrep";
        public override string Description => "Ricerca con espressioni regolari .NET (NonBacktracking, zero-alloc)";

        // # Stato condiviso tra i metodi
        private RegexGrepState State = new();

        // # Attributi di classe
        private bool IgnoreCase = false;
        private bool Silence;
        private bool CountOnly = false;
        private int MinMatchCount = 0;
        private int MaxMatchCount = -1;
        private Regex? RegexEngine;
        private FastPrinter? _fastPrinter;
        private static readonly int MaxContextSize = 50;
        private static readonly int FilesChannelBound = 8192;
        private long TotalMatchCount = 0;
        private long TotalSizeVisited = 0;
        private long TotalFileVisited = 0;
        private const int PathRentBytes = 2048;
        private const int ByteBufferSize = 65536;
        private const int CharBufferSize = 65536;
        private const int ByteOverlapSize = 4096; // ~1365 char nel worst case UTF-8

        #region Structs

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

        private readonly struct GrepFileEntry(ref FileSystemEntry entry)
        {
            public readonly string Path { get; } = entry.ToSpecifiedFullPath();
            public readonly long Size { get; } = entry.Length;
        }

        private class RegexGrepState
        {
            public string Root = string.Empty;
            public string Pattern = string.Empty;
            public HashSet<string> ExcludeDirs = new(StringComparer.OrdinalIgnoreCase);
            public Channel<string> FilesChannel = Channel.CreateBounded<string>(1);
        }

        #endregion
        #region RunAsync

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            var settings = ParseSettings<RegexGrepSettings>(args);
            if (args.Contains("--help") || string.IsNullOrEmpty(settings.TargetPath) || string.IsNullOrEmpty(settings.Pattern))
            {
                Help();
                return;
            }

            State = new RegexGrepState();
            
            if (!ParseAndValidateSettings(settings)) return;
            
            ValidateAndCompileRegex(settings.Pattern);
            ConfigureDirectoryFilters(settings);
            InitializeEngine();
            // inizializzo cronometro
            long startTimestamp = Stopwatch.GetTimestamp();
            
            ConsolePlus.Write($"[Cyan]#[/] Inizio la ricerca con regex...\n[DarkGray]*\n*[/]");
            
            try
            {
                _fastPrinter!.Run(ct);
                
                var producerTask = RunProducerAsync(settings, ct);
                var workerTasks = StartWorkers(settings, ct);
                
                await producerTask;
                await Task.WhenAll(workerTasks);
            }
            catch (OperationCanceledException) { }
            finally
            {
                await _fastPrinter!.Complete();
            }
            // # termine
            TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            double seconds = elapsed.TotalSeconds;
            double totalGB = TotalSizeVisited / 1_073_741_824.0; // 1024^3
            double gbSec = seconds > 0 ? totalGB / seconds : 0;
            // ---
            if (CountOnly) ConsolePlus.Write("[DarkGray]*\n*[/]");
            ConsolePlus.WriteBoxHeader($"Ricerca completata", 40);
            ConsolePlus.WriteList([
                $"Match totali: [Green]{TotalMatchCount:N0}[/]",
                $"File totali controllati: [Magenta]{TotalFileVisited:N0}[/]",
                $"Spazio totale controllato: [Blue]{Formatter.Bytes(TotalSizeVisited)}[/]",
                $"Throughput: [Cyan]{gbSec:N2} GB/sec[/]"
            ]);
            ConsolePlus.WriteHr(40);
        }

        #endregion
        #region Settings

        private bool ParseAndValidateSettings(RegexGrepSettings settings)
        {
            string? root = ParsePath(settings.TargetPath, true);
            if (root is null)
            {
                PrintError("Il percorso specificato non è valido.");
                return false;
            }

            if (settings.Pattern.Length == 0)
            {
                PrintError("Il pattern di ricerca non può essere vuoto.");
                return false;
            }

            if (settings.Threads < 1 || settings.Threads > 50)
            {
                PrintError("Numero di thread non valido (1 <= threads <= 50)");
                return false;
            }

            // Setup output
            IFastOutput printerOutput = ConsoleOutput.Instance;
            bool hasOutputFile = !string.IsNullOrWhiteSpace(settings.OutputFile);
            
            if (hasOutputFile)
            {
                var fileOutput = new FileOutput(settings.OutputFile!);

                if (settings.Silence)
                {
                    printerOutput = fileOutput;
                }
                else
                {
                    printerOutput = new CompositeOutput(ConsoleOutput.Instance, fileOutput);
                }
            }
            else if (settings.Silence)
            {
                printerOutput = NullOutput.Instance;
            }

            var fastPrinterOptions = new FastPrinter.FastPrinterOptions(
                output: printerOutput,
                capacity: 10_000);

            _fastPrinter = new FastPrinter(fastPrinterOptions);

            State.Root = root;
            MinMatchCount = settings.MinCount > 0 ? settings.MinCount : 1;
            MaxMatchCount = settings.MaxCount > 0 ? settings.MaxCount : -1;
            
            if (MaxMatchCount > 0 && MaxMatchCount < MinMatchCount)
            {
                PrintError("Parametri non validi, --max-count non puo essere minore di --min-count");
                return false;
            }

            CountOnly = settings.Count;
            IgnoreCase = settings.IgnoreCase;
            Silence = settings.Silence;

            return true;
        }

        #endregion
        #region Pattern & Regex

        private void ValidateAndCompileRegex(string pattern)
        {
            State.Pattern = pattern;
            
            try
            {
                // Test compilation con timeout per evitare blocchi
                var testOptions = RegexOptions.None;
                if (IgnoreCase) testOptions |= RegexOptions.IgnoreCase;
                
                // Test del pattern prima di compilare
                _ = Regex.IsMatch("test", pattern, testOptions, TimeSpan.FromMilliseconds(100));
            }
            catch (RegexParseException ex)
            {
                PrintError($"Pattern regex non valido: {ex.Message}");
                throw;
            }
            catch (ArgumentException ex)
            {
                PrintError($"Errore nel pattern: {ex.Message}");
                throw;
            }
        }

        #endregion
        #region Filters

        private void ConfigureDirectoryFilters(RegexGrepSettings settings)
        {
            State.ExcludeDirs = new HashSet<string>(DefaultExcludeDirs, StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(settings.ExcludeDirs))
            {
                foreach (var dir in settings.ExcludeDirs.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    State.ExcludeDirs.Add(dir.Trim());
                }
            }

            if (!string.IsNullOrEmpty(settings.IncludeDirs))
            {
                foreach (var dir in settings.IncludeDirs.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    State.ExcludeDirs.Remove(dir.Trim());
                }
            }
        }

        #endregion
        #region Engine

        private void InitializeEngine()
        {
            // Configurazione Regex ottimizzata
            var options = RegexOptions.Compiled | RegexOptions.NonBacktracking;
            if (IgnoreCase) options |= RegexOptions.IgnoreCase;

            RegexEngine = new Regex(State.Pattern, options);
            
            State.FilesChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(FilesChannelBound)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        #endregion
        #region Producer

        private async Task RunProducerAsync(RegexGrepSettings settings, CancellationToken ct)
        {
            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false
            };
            
            FileSystemFilter? fileFilter = FileFilterFactory.CreateFilter(new FileFilterFactory.FilterOptions
            {
                Pattern = settings.Glob,
                MatchType = FilterFileNameMatchType.Glob,
            });
            
            var enumerable = new FileSystemEnumerable<GrepFileEntry>(
                State.Root,
                (ref FileSystemEntry entry) => new GrepFileEntry(ref entry),
                enumerationOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    if (entry.IsDirectory) return false;
                    if (fileFilter != null)
                    {
                        return fileFilter(ref entry);
                    }
                    return true;
                },
                ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                {
                    ReadOnlySpan<char> dirName = entry.FileName;
                    foreach (var excluded in State.ExcludeDirs)
                        if (dirName.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                            return false;
                    return true;
                }
            };
            
            try
            {
                foreach (var grepFileEntry in enumerable)
                {
                    if (ct.IsCancellationRequested) break;
                    TotalFileVisited++;
                    await State.FilesChannel.Writer.WriteAsync(grepFileEntry.Path, ct);
                }
            }
            finally
            {
                State.FilesChannel.Writer.Complete();
            }
        }

        #endregion
        #region Workers

        private Task[] StartWorkers(RegexGrepSettings settings, CancellationToken ct)
        {
            var workers = new Task[settings.Threads];

            for (int i = 0; i < settings.Threads; i++)
            {
                workers[i] = Task.Run(async () =>
                {
                    byte[] byteBuffer = new byte[ByteBufferSize];
                    char[] charBuffer = new char[CharBufferSize];
                    long threadMatchCount = 0;
                    long totalByteSizeVisited = 0;

                    try
                    {
                        await foreach (var path in State.FilesChannel.Reader.ReadAllAsync(ct))
                        {
                            threadMatchCount += ProcessFile(path, byteBuffer, charBuffer, ref totalByteSizeVisited);
                        }
                    }
                    finally
                    {
                        Interlocked.Add(ref TotalMatchCount, threadMatchCount);
                        Interlocked.Add(ref TotalSizeVisited, totalByteSizeVisited);
                    }
                }, ct);
            }

            return workers;
        }

        #endregion
        #region ProcessFile

        /// <summary>
        /// Processa un file convertendo UTF-8 -> UTF-16 e usando Regex.EnumerateMatches (zero-alloc).
        /// </summary>
        private long ProcessFile(string path, byte[] byteBuffer, char[] charBuffer, ref long totalByteSizeVisited)
        {
            SafeFileHandle? handle = null;
            long matchCount = 0;
            
            try
            {
                handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.SequentialScan);
                long fileLength = RandomAccess.GetLength(handle);
                if (fileLength == 0) return 0;

                long fileOffset = 0;
                int leftoverBytes = 0;
                int totalLines = 1;
                bool isFirstChunk = true;
                int overlapChars = 0;

                while (fileOffset < fileLength)
                {
                    int bytesToRead = (int)Math.Min(byteBuffer.Length - leftoverBytes, fileLength - fileOffset);
                    int bytesRead = RandomAccess.Read(handle, byteBuffer.AsSpan(leftoverBytes, bytesToRead), fileOffset);
                    if (bytesRead == 0) break;

                    int currentByteLength = bytesRead + leftoverBytes;
                    ReadOnlySpan<byte> byteSpan = byteBuffer.AsSpan(0, currentByteLength);
                    totalByteSizeVisited += bytesRead;

                    // Binary file detection (solo primo chunk)
                    if (isFirstChunk)
                    {
                        if (byteSpan.Contains((byte)0)) return 0;
                        isFirstChunk = false;
                    }

                    // Conversione UTF-8 -> UTF-16 vettorializzata (SIMD)
                    OperationStatus status = Utf8.ToUtf16(byteSpan, charBuffer, out int bytesConsumed, out int charsWritten);
                    
                    if (status == OperationStatus.InvalidData)
                    {
                        // File non UTF-8 valido, saltiamo
                        return 0;
                    }

                    ReadOnlySpan<char> searchSpan = charBuffer.AsSpan(0, charsWritten);

                    // Calcola fino a dove processare (escludendo l'overlap che verrà riprocessato)
                    int searchEndIndex = isFirstChunk ? charsWritten : charsWritten - overlapChars;
                    if (searchEndIndex < 0) searchEndIndex = charsWritten;

                    // Esegui il matching con Regex
                    if (CountOnly)
                    {
                        matchCount += CountMatches(searchSpan, searchEndIndex);
                    }
                    else
                    {
                        matchCount += ProcessMatches(searchSpan, searchEndIndex, path, charBuffer, charsWritten, totalLines);
                    }

                    // Gestione leftover bytes (caratteri UTF-8 incompleti)
                    int unconsumedBytes = currentByteLength - bytesConsumed;
                    
                    // Gestione overlap: manteniamo ByteOverlapSize byte per evitare match spezzati
                    int overlapBytes = Math.Min(ByteOverlapSize, bytesConsumed);
                    leftoverBytes = unconsumedBytes + overlapBytes;
                    
                    // Copiamo i byte da mantenere all'inizio del buffer
                    byteSpan[(currentByteLength - leftoverBytes)..].CopyTo(byteBuffer);

                    // Calcola overlap in char per il prossimo chunk
                    // Stima conservativa: nel worst case UTF-8, 3 byte = 1 char
                    overlapChars = Math.Min(1024, overlapBytes / 3 + 1);

                    // Conta le linee processate (escluso overlap)
                    int charsProcessed = Math.Min(searchEndIndex, charsWritten);
                    totalLines += CountLines(searchSpan[..charsProcessed]);
                    
                    fileOffset += bytesRead;
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            finally
            {
                handle?.Dispose();
            }
            
            // Gestione count-only con min/max
            if (CountOnly)
            {
                bool satisfiesMin = matchCount >= MinMatchCount;
                bool satisfiesMax = MaxMatchCount == -1 || matchCount <= MaxMatchCount;
                
                if (satisfiesMin && satisfiesMax)
                {
                    PrintCountResult(path, matchCount);
                }
                else
                {
                    matchCount = 0;
                }
            }
            
            return matchCount;
        }

        #endregion
        #region Matching

        /// <summary>
        /// Conta i match usando EnumerateMatches (zero-alloc).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int CountMatches(ReadOnlySpan<char> span, int maxIndex)
        {
            int count = 0;
            foreach (var match in RegexEngine!.EnumerateMatches(span))
            {
                if (match.Index >= maxIndex)
                    break;
                count++;
            }
            return count;
        }

        /// <summary>
        /// Processa i match ed estrae il contesto per la stampa.
        /// </summary>
        private int ProcessMatches(ReadOnlySpan<char> span, int maxIndex, string path, char[] buffer, int totalChars, int chunkStartLine)
        {
            int count = 0;
            
            foreach (var match in RegexEngine!.EnumerateMatches(span))
            {
                if (match.Index >= maxIndex)
                    break;
                
                // Calcola il numero di riga del match
                int lineNumber = chunkStartLine + CountLines(span[..match.Index]);
                
                // Estrae e stampa il match con contesto
                ExtractAndPrintMatch(path, buffer, totalChars, match.Index, match.Length, lineNumber);
                count++;
            }
            
            return count;
        }

        #endregion
        #region Extract & Print

        /// <summary>
        /// Estrae il contesto del match e lo stampa tramite FastPrinter (zero-alloc).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ExtractAndPrintMatch(string path, char[] buffer, int totalChars, int matchIndex, int matchLength, int lineNumber)
        {
            ReadOnlySpan<char> dataSpan = buffer.AsSpan(0, totalChars);
            
            // Affittiamo spazio per costruire la stringa di output
            IMemoryOwner<char> memoryOwner = MemoryPool<char>.Shared.Rent(PathRentBytes);
            Span<char> outputSpan = memoryOwner.Memory.Span;
            int outputLength = 0;

            ReadOnlySpan<char> pathSpan = path.AsSpan();

            // Header: path e numero riga
            "[Green]#[/] [DarkGray]".AsSpan().AppendTo(outputSpan, ref outputLength);
            Path.GetDirectoryName(pathSpan).AppendTo(outputSpan, ref outputLength);
            Path.DirectorySeparatorChar.AppendTo(outputSpan, ref outputLength);
            "[Cyan]".AsSpan().AppendTo(outputSpan, ref outputLength);
            Path.GetFileName(pathSpan).AppendTo(outputSpan, ref outputLength);
            "[/]\n[Green]# [Yellow]".AsSpan().AppendTo(outputSpan, ref outputLength);
            
            if (lineNumber.TryFormat(outputSpan[outputLength..], out int charsWritten))
            {
                outputLength += charsWritten;
            }
            
            ":[/] ".AsSpan().AppendTo(outputSpan, ref outputLength);
            
            // Estrai il contesto del match
            int contextLen = ExtractMatchContext(
                dataSpan,
                matchIndex,
                matchLength,
                outputSpan[outputLength..]);

            outputLength += contextLen;
            
            // Footer
            "\n[DarkGray]*\n*[/]".AsSpan().AppendTo(outputSpan, ref outputLength);

            _fastPrinter!.Post(memoryOwner, outputLength);
        }

        /// <summary>
        /// Estrae il contesto attorno al match (lavora su char invece che byte).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ExtractMatchContext(
            ReadOnlySpan<char> span,
            int matchIndex,
            int matchLength,
            Span<char> output)
        {
            // Calcola le posizioni del match
            int start = Math.Max(0, matchIndex - MaxContextSize);
            int endMatch = matchIndex + matchLength;
            int end = Math.Min(endMatch + MaxContextSize, span.Length);
            
            // Trova i confini di riga a sinistra
            var leftSpan = span[start..matchIndex];
            int preNewLine = leftSpan.LastIndexOf('\n');
            int actualStart = preNewLine != -1 ? start + preNewLine + 1 : start;
            bool truncatedLeft = preNewLine == -1 && start > 0;
            
            // Trova i confini di riga a destra
            var rightSpan = span[endMatch..end];
            int postNewLine = rightSpan.IndexOf('\n');
            int actualEnd = postNewLine != -1 ? endMatch + postNewLine : end;
            if (actualEnd > 0 && span[actualEnd - 1] == '\r') actualEnd--;
            bool truncatedRight = postNewLine == -1 && end < span.Length;
            
            // Assegna le porzioni esatte
            var exactLeft = span[actualStart..matchIndex];
            var exactMatch = span[matchIndex..endMatch];
            var exactRight = span[endMatch..actualEnd];
            
            // Buffer per costruire l'output: 14 = 6 (... * 2) + 5 ([Red]) + 3 ([/])
            int maxSize = (actualEnd - actualStart) + 14;
            Span<char> buffer = maxSize <= 1024 ? stackalloc char[maxSize] : new char[maxSize];
            int pos = 0;
            
            // Contesto sinistro
            if (truncatedLeft)
            {
                "...".AsSpan().CopyTo(buffer[pos..]);
                pos += 3;
            }
            
            exactLeft.CopyTo(buffer[pos..]);
            pos += exactLeft.Length;
            
            // Match evidenziato in rosso
            "[Red]".AsSpan().CopyTo(buffer[pos..]);
            pos += 5;
            
            exactMatch.CopyTo(buffer[pos..]);
            pos += exactMatch.Length;
            
            "[/]".AsSpan().CopyTo(buffer[pos..]);
            pos += 3;
            
            // Contesto destro
            exactRight.CopyTo(buffer[pos..]);
            pos += exactRight.Length;
            
            if (truncatedRight)
            {
                "...".AsSpan().CopyTo(buffer[pos..]);
                pos += 3;
            }
            
            // Copia il buffer nell'output
            buffer[..pos].CopyTo(output);
            return pos;
        }

        #endregion
        #region Count Result

        /// <summary>
        /// Stampa il conteggio match per file (modalità count-only).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PrintCountResult(string path, long fileMatchCount)
        {
            IMemoryOwner<char> memoryOwner = MemoryPool<char>.Shared.Rent(PathRentBytes);
            Span<char> outputSpan = memoryOwner.Memory.Span;
            int matchLength = 0;

            ReadOnlySpan<char> pathSpan = path.AsSpan();

            "[Green]#[/] [DarkGray]".AsSpan().AppendTo(outputSpan, ref matchLength);
            Path.GetDirectoryName(pathSpan).AppendTo(outputSpan, ref matchLength);
            Path.DirectorySeparatorChar.AppendTo(outputSpan, ref matchLength);
            "[Cyan]".AsSpan().AppendTo(outputSpan, ref matchLength);
            Path.GetFileName(pathSpan).AppendTo(outputSpan, ref matchLength);
            "[/]: [Magenta]".AsSpan().AppendTo(outputSpan, ref matchLength);

            if (fileMatchCount.TryFormat(outputSpan[matchLength..], out int charsWritten))
            {
                matchLength += charsWritten;
            }

            " match[/]".AsSpan().AppendTo(outputSpan, ref matchLength);

            _fastPrinter!.Post(memoryOwner, matchLength);
        }

        #endregion
        #region Utilities

        /// <summary>
        /// Conta le newline nello span di char.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CountLines(ReadOnlySpan<char> span)
        {
            return span.Count('\n');
        }

        public override void Help() => PrintHelp<RegexGrepSettings>();

        #endregion
    }
}