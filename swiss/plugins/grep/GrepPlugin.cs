using System.IO.Enumeration;
using System.Threading.Channels;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;
using lib.console;
using lib.console.fastprinter;
using lib.algorithm;
using lib.utils;
using System.Buffers;
using lib.io;
using System.Diagnostics;

namespace plugins.grep
{
    class GrepPlugin : Plugin
    {
        public override string Name => "grep";
        public override string Description => "Ricerca stringhe multiple con AhoCorasick (limitato ASCII - lavora con i byte grezzi)";

        // # Stato condiviso tra i metodi
        private GrepState State = new();

        // # Attributi di classe (non dipendono dallo stato)
        private int LongestPattern = 0;
        private int[] PatternLengths = [];
        private bool IgnoreCase = false;
        private bool Silence;
        private bool CountOnly = false;
        private int MinMatchCount = 0;
        private int MaxMatchCount = -1;
        private AhoCorasick? AhoEngine;
        private FastPrinter? _fastPrinter;
        private static readonly int MaxContextSize = 50;
        private static readonly int FilesChannelBound = 8192;
        private long TotalMatchCount = 0;
        private long TotalSizeVisited = 0;
        private long TotalFileVisited = 0;
        // numero di byte da affittare per i vari print dei path per fastprinter
        private const int PathRentBytes = 2048;

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

        // # Struct per leggere i file in input
        private readonly struct GrepFileEntry(ref FileSystemEntry entry)
        {
            public readonly string Path { get; } = entry.ToSpecifiedFullPath();
            public readonly long Size { get; } = entry.Length;
        }

        // # Stato interno
        private class GrepState
        {
            public string Root = string.Empty;
            public string[] WordsToSearch = [];
            public ReadOnlyMemory<byte>[] PatternList = [];
            public HashSet<string> ExcludeDirs = new(StringComparer.OrdinalIgnoreCase);
            public Channel<string> FilesChannel = Channel.CreateBounded<string>(1);
        }

        // # Handler
        private readonly struct AhoMatchHandler(string path, byte[] buffer, int currentDataLength, int chunkStartLine, int[] patternLengths, FastPrinter printer) : IMatchHandler
        {
            private readonly byte[] _buffer = buffer;
            private readonly int _currentDataLength = currentDataLength;
            private readonly int _chunkStartLine = chunkStartLine;
            private readonly int[] _patternLengths = patternLengths;
            private readonly FastPrinter _printer = printer;

            public void OnMatch(int startIndex, int endIndex, int patternIndex, int relativeLine)
            {
                int patternLength = _patternLengths[patternIndex];
                ReadOnlySpan<byte> originalDataSpan = _buffer.AsSpan(0, _currentDataLength);
                // affitto lo spazio per costruire la stringa di output
                IMemoryOwner<char> memoryOwner = MemoryPool<char>.Shared.Rent(PathRentBytes);
                Span<char> outputSpan = memoryOwner.Memory.Span;
                int matchLength = 0;

                int lineNumber = _chunkStartLine + relativeLine;
                ReadOnlySpan<char> pathSpan = path.AsSpan();

                // header
                "[Green]#[/] [DarkGray]".AsSpan().AppendTo(outputSpan, ref matchLength);
                Path.GetDirectoryName(pathSpan).AppendTo(outputSpan, ref matchLength);
                Path.DirectorySeparatorChar.AppendTo(outputSpan, ref matchLength);
                "[Cyan]".AsSpan().AppendTo(outputSpan, ref matchLength);
                Path.GetFileName(pathSpan).AppendTo(outputSpan, ref matchLength);
                "[/]\n[Green]# [Yellow]".AsSpan().AppendTo(outputSpan, ref matchLength);
                if (lineNumber.TryFormat(outputSpan[matchLength..], out int charsWritten))
                {
                    matchLength += charsWritten;
                }
                ":[/] ".AsSpan().AppendTo(outputSpan, ref matchLength);
                // estraggo il contesto del match
                int len = ExtractMatchContext(
                    originalDataSpan,
                    startIndex,
                    patternLength,
                    // passo al metodo solo la porzione successiva a quella che abbiamo
                    // precedentemente gia valorizzato quindi da matchLength in poi
                    outputSpan[matchLength..]);

                matchLength += len;
                // footer
                "\n[DarkGray]*\n*[/]".AsSpan().AppendTo(outputSpan, ref matchLength);

                _printer.Post(memoryOwner, matchLength);
            }
        }

        #endregion
        #region RunAsync

        // # ------------------------------ #
        // RunAsync — diagramma di flusso
        // # ------------------------------ #
        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            // 1. ottengo i valori di settings
            var settings = ParseSettings<GrepSettings>(args);
            if (args.Contains("--help") || string.IsNullOrEmpty(settings.TargetPath) || string.IsNullOrEmpty(settings.Pattern))
            {
                Help();
                return;
            }

            State = new GrepState();
            // 2. valido e parsifico le settings
            if (!ParseAndValidateSettings(settings)) return;
            // 3. costruisco la lista dei pattern da ricercare con AhoCorasick (split su '|')
            BuildPatternList(settings.Pattern);
            // 4. configuro le cartelle da escludere/includere nella ricerca
            ConfigureDirectoryFilters(settings);
            // 5. preparo AhoCorasick e il channel del producer e avvio il cronometro
            long startTimestamp = Stopwatch.GetTimestamp();
            // ---
            InitializeEngine();
            ConsolePlus.Write($"[Cyan]#[/] Inizio la ricerca...\n[DarkGray]*\n*[/]");
            try
            {
                // 6. avvio il task per il print a console multithread
                _fastPrinter!.Run(ct);
                // 7. avvio i task di producer e consumers
                var producerTask = RunProducerAsync(settings, ct);
                var workerTasks = StartWorkers(settings, ct);
                // 8. attendo il termine di tutti i worker
                await producerTask;
                await Task.WhenAll(workerTasks);
            }
            catch (OperationCanceledException) { /* Uscita pulita dall'esecuzione */ }
            finally
            {
                await _fastPrinter!.Complete();
            }
            // 9. termine
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
                $"Velocità media: [Cyan]{gbSec:N2} GB/sec[/]"
            ]);
            ConsolePlus.WriteHr(40);
        }

        #endregion
        #region Settings
        // # ------------------------------ #
        // Metodi principali
        // # ------------------------------ #

        /// <summary>
        /// Valida il percorso root e il pattern; popola State.Root.
        /// Accede a: State.Root, settings.TargetPath, settings.IgnoreCase, settings.Threads
        /// </summary>
        private bool ParseAndValidateSettings(GrepSettings settings)
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

            // # destinazione output fast-printer
            IFastOutput printerOutput = ConsoleOutput.Instance;
            bool hasOutputFile = !string.IsNullOrWhiteSpace(settings.OutputFile);
            // se è stato richiesto il file di output
            if (hasOutputFile)
            {
                // creo il file di output sovrascrivendo quello precedente
                var fileOutput = new FileOutput(settings.OutputFile!);

                if (settings.Silence)
                {
                    // Silenzioso ma con file
                    printerOutput = fileOutput;
                }
                else
                {
                    // Normale con file: scrive SIA su console SIA su file
                    printerOutput = new CompositeOutput(ConsoleOutput.Instance, fileOutput);
                }
            }
            else if (settings.Silence)
            {
                // Silenzio radio: i dati vengono droppati
                printerOutput = NullOutput.Instance;
            }

            // # inizializzo fastprinter
            var fastPrinterOptions = new FastPrinter.FastPrinterOptions(
                output: printerOutput,
                capacity: 10_000);

            _fastPrinter = new FastPrinter(fastPrinterOptions);

            // # state globale
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

            // Salviamo il flag Silence se ti serve in ProcessFile per usare l'overload veloce
            Silence = settings.Silence;

            return true;
        }

        #endregion
        #region Pattern
        /// <summary>
        /// Splitta il pattern su '|', calcola LongestPattern, PatternLengths e State.PatternList.
        /// Accede a: State.WordsToSearch, State.PatternList, LongestPattern, PatternLengths, IgnoreCase
        /// </summary>
        private void BuildPatternList(string pattern)
        {
            State.WordsToSearch = pattern.Split('|', StringSplitOptions.RemoveEmptyEntries);
            State.PatternList = new ReadOnlyMemory<byte>[State.WordsToSearch.Length];
            PatternLengths = new int[State.WordsToSearch.Length];
            LongestPattern = State.WordsToSearch[0].Length;

            for (int i = 0; i < State.WordsToSearch.Length; i++)
            {
                PatternLengths[i] = State.WordsToSearch[i].Length;
                if (State.WordsToSearch[i].Length > LongestPattern)
                    LongestPattern = State.WordsToSearch[i].Length;

                var wordBytes = Encoding.UTF8.GetBytes(State.WordsToSearch[i]);
                if (IgnoreCase) SpanExtensions.ToLowerAsciiSafe(wordBytes);
                State.PatternList[i] = wordBytes;
            }
        }

        #endregion
        #region Filters
        /// <summary>
        /// Costruisce State.ExcludeDirs partendo da DefaultExcludeDirs,
        /// aggiungendo le escluse e rimuovendo le incluse da settings.
        /// Accede a: State.ExcludeDirs, settings.ExcludeDirs, settings.IncludeDirs
        /// </summary>
        private void ConfigureDirectoryFilters(GrepSettings settings)
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
        /// <summary>
        /// Inizializza AhoEngine e State.FilesChannel.
        /// Accede a: AhoEngine, State.PatternList, State.FilesChannel
        /// </summary>
        private void InitializeEngine()
        {
            AhoEngine = new AhoCorasick(State.PatternList);
            State.FilesChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(FilesChannelBound)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        #endregion
        #region Producer
        /// <summary>
        /// Enumera i file nel filesystem e li scrive su State.FilesChannel.
        /// Accede a: State.Root, State.ExcludeDirs, State.IncludeGlobs, State.FilesChannel
        /// </summary>
        private async Task RunProducerAsync(GrepSettings settings, CancellationToken ct)
        {
            // preparo le opzioni di enumerazione
            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false
            };
            // genero la funzione di filtraggio dei file
            FileSystemFilter? fileFilter = FileFilterFactory.CreateFilter(new FileFilterFactory.FilterOptions
            {
                Pattern = settings.Glob,
                MatchType = FilterFileNameMatchType.Glob,
            });
            // preparo il motore di enumerazione
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
            // avvio
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
        /// <summary>
        /// Avvia un task worker per ogni core disponibile e li restituisce.
        /// Accede a: State.FilesChannel, AhoEngine, LongestPattern, PatternLengths, IgnoreCase, _fastPrinter
        /// </summary>
        private Task[] StartWorkers(GrepSettings settings, CancellationToken ct)
        {
            var workers = new Task[settings.Threads];

            for (int i = 0; i < settings.Threads; i++)
            {
                workers[i] = Task.Run(async () =>
                {
                    byte[] workerBuffer = new byte[65536];
                    byte[] lowerBuffer = IgnoreCase ? new byte[65536] : [];
                    long threadMatchCount = 0;
                    long totalByteSizeVisited = 0;

                    try
                    {
                        await foreach (var path in State.FilesChannel.Reader.ReadAllAsync(ct))
                        {
                            threadMatchCount += ProcessFile(path, workerBuffer, lowerBuffer, LongestPattern, ref totalByteSizeVisited);
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

        // # ------------------------------ #
        // Metodi di elaborazione
        // # ------------------------------ #
        #endregion
        #region ProcessFile
        /// <summary>
        /// Processa un file cercando il pattern con AhoCorasick a blocchi da 64 KB.
        /// Accede a: AhoEngine, PatternLengths, IgnoreCase, _fastPrinter
        /// </summary>
        private long ProcessFile(string path, byte[] buffer, byte[] lowerBuffer, int overlap, ref long totalByteSizeVisited)
        {
            SafeFileHandle? handle = null;
            long matchCount = 0;
            try
            {
                handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.SequentialScan);
                long fileLength = RandomAccess.GetLength(handle);
                if (fileLength == 0) return 0;

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
                    totalByteSizeVisited += bytesRead;

                    if (isFirstChunk)
                    {
                        if (dataSpan.Contains((byte)0)) return 0;
                        isFirstChunk = false;
                    }

                    ReadOnlySpan<byte> searchSpan;
                    if (IgnoreCase)
                    {
                        var destSpan = lowerBuffer.AsSpan(0, currentDataLength);
                        dataSpan.CopyTo(destSpan);
                        destSpan.ToLowerAsciiSafe();
                        searchSpan = destSpan;
                    }
                    else
                    {
                        searchSpan = dataSpan;
                    }
                    // # Avvio il motore AhoCorasick
                    // se devo solo contare uso l'overload specifico per contare e basta
                    if (CountOnly)
                    {
                        matchCount += AhoEngine!.Search(searchSpan);
                    }
                    else // altrimenti uso match handler per poi stampare a video il contesto
                    {
                        var handler = new AhoMatchHandler(path, buffer, currentDataLength, totalLines, PatternLengths, _fastPrinter!);
                        matchCount += AhoEngine!.Search(searchSpan, ref handler);
                    }

                    if (currentDataLength > overlap)
                    {
                        leftover = overlap;
                        dataSpan[(currentDataLength - overlap)..].CopyTo(buffer);
                    }
                    else
                    {
                        leftover = currentDataLength;
                    }

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
            // # Se solo conteggio stampo il match con il percorso del file e il numero di match trovati
            if (CountOnly)
            {
                // verifico il limite inferiore
                bool satisfiesMin = matchCount >= MinMatchCount;
                // verifico il limite superiore (solo se non è -1)
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
        #region Extract
        /// <summary>
        /// Restituisce la stringa con i colori applicati attorno al match trovato.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ExtractMatchContext(
            ReadOnlySpan<byte> span,
            int matchIndex,
            int patternLength,
            Span<char> output)
        {
            // calcolo le posizioni dei match
            int start = Math.Max(0, matchIndex - MaxContextSize);
            int endMatch = matchIndex + patternLength;
            int end = Math.Min(endMatch + MaxContextSize, span.Length);
            // sinistra del match
            var leftSpan = span[start..matchIndex];
            int preNewLine = leftSpan.LastIndexOf((byte)'\n');
            int actualStart = preNewLine != -1 ? start + preNewLine + 1 : start;
            bool truncatedLeft = preNewLine == -1 && start > 0;
            // destra del match
            var rightSpan = span[endMatch..end];
            int postNewLine = rightSpan.IndexOf((byte)'\n');
            int actualEnd = postNewLine != -1 ? endMatch + postNewLine : end;
            if (actualEnd > 0 && span[actualEnd - 1] == '\r') actualEnd--;
            bool truncatedRight = postNewLine == -1 && end < span.Length;
            // assegno le porzioni esatte calcolate
            var exactLeft = span[actualStart..matchIndex];
            var exactMatch = span[matchIndex..endMatch];
            var exactRight = span[endMatch..actualEnd];
            // 14 = 6 (... * 2) + 5 ([Red]) + 3 ([/]) vedi sotto infatti le allocazioni
            Span<char> buffer = stackalloc char[actualEnd - actualStart + 14];
            int pos = 0;
            // se troncato a sinistra aggiungo ...
            if (truncatedLeft) { "...".AsSpan().CopyTo(buffer[pos..]); pos += 3; }
            // assumo che tutto sia UFT-8 (zero sbatta)
            // estraggo e copio nel buffer la parte a sinistra del match
            int leftChars = Encoding.UTF8.GetChars(exactLeft, buffer[pos..]); pos += leftChars;
            "[Red]".AsSpan().CopyTo(buffer[pos..]); pos += 5;
            // estraggo e copio nel buffer il match inglobandolo nel testo rosso
            int matchChars = Encoding.UTF8.GetChars(exactMatch, buffer[pos..]); pos += matchChars;
            "[/]".AsSpan().CopyTo(buffer[pos..]); pos += 3;
            // estraggo e copio nel buffer la parte a destra del match
            int rightChars = Encoding.UTF8.GetChars(exactRight, buffer[pos..]); pos += rightChars;
            // se troncato a destra aggiungo ...
            if (truncatedRight) { "...".AsSpan().CopyTo(buffer[pos..]); pos += 3; }
            // copio il buffer finale nell'output
            buffer[..pos].CopyTo(output);
            return pos;
        }

        #endregion
        #region Other
        /// <summary>
        /// Stampa a console il conteggio delle occorrenze di un file usando il MemoryPool.
        /// Viene chiamato solo in modalità CountOnly.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PrintCountResult(string path, long fileMatchCount)
        {
            // Affittiamo spazio per stampare il percorso
            IMemoryOwner<char> memoryOwner = MemoryPool<char>.Shared.Rent(PathRentBytes);
            Span<char> outputSpan = memoryOwner.Memory.Span;
            int matchLength = 0;

            ReadOnlySpan<char> pathSpan = path.AsSpan();

            // # Costruzione path no allocazioni
            "[Green]#[/] [DarkGray]".AsSpan().AppendTo(outputSpan, ref matchLength);
            Path.GetDirectoryName(pathSpan).AppendTo(outputSpan, ref matchLength);
            Path.DirectorySeparatorChar.AppendTo(outputSpan, ref matchLength);
            "[Cyan]".AsSpan().AppendTo(outputSpan, ref matchLength);
            Path.GetFileName(pathSpan).AppendTo(outputSpan, ref matchLength);
            "[/]: [Magenta]".AsSpan().AppendTo(outputSpan, ref matchLength);

            // # formattazione
            if (fileMatchCount.TryFormat(outputSpan[matchLength..], out int charsWritten))
            {
                matchLength += charsWritten;
            }

            " match[/]".AsSpan().AppendTo(outputSpan, ref matchLength);

            // # invio a fastprinter per la stampa
            _fastPrinter!.Post(memoryOwner, matchLength);
        }

        /// <summary>
        /// Conta il numero di newline presenti nello span (usato per il conteggio righe).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CountLines(ReadOnlySpan<byte> span)
        {
            return span.Count((byte)'\n');
        }

        public override void Help() => PrintHelp<GrepSettings>();
    }
    #endregion
}