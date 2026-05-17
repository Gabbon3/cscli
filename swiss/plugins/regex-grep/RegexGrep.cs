using System.IO.Enumeration;
using System.Threading.Channels;
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
        public override string Name => "grep";
        public override string Description => "Ricerca con espressioni regolari .NET (NonBacktracking, zero-alloc)";

        // # stato condiviso tra i metodi
        private RegexGrepState State = new();

        // # attributi
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
        private long TotalFileFounded = 0;
        private long TotalFileProcessed = 0;
        private const int PathRentBytes = 2048;
        private const int ByteBufferSize = 65536;
        private const int CharBufferSize = 65536;
        private const int ByteOverlapSize = 4096; // circa 1365 char circa nel peggiore dei casi in UTF-8 (1 char max 3 byte)
        
        // altre statistiche
        // -- statistiche sulle scritture del channel
        private long ChannelWriteSync = 0;
        private long ChannelWriteAsync = 0;

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

        /// <summary>
        /// Struttura usata per l'enumerazione dei file
        /// </summary>
        /// <param name="entry"></param>
        private readonly struct GrepFileEntry(ref FileSystemEntry entry)
        {
            public readonly string Path { get; } = entry.ToSpecifiedFullPath();
            public readonly long Size { get; } = entry.Length;
        }

        /// <summary>
        /// classe di stato condiviso tra le classi
        /// </summary>
        private class RegexGrepState
        {
            public string Root = string.Empty;
            public string Pattern = string.Empty;
            public HashSet<string> ExcludeDirs = new(StringComparer.OrdinalIgnoreCase);
            public Channel<GrepFileEntry> FilesChannel = Channel.CreateBounded<GrepFileEntry>(1);
        }

        #endregion
        #region RunAsync

        /// <summary>
        /// Esecuzione comando principale
        /// </summary>
        /// <returns></returns>
        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            // 1. parsing degli argomenti da linea di comando
            var settings = ParseSettings<RegexGrepSettings>(args);
            if (args.Contains("--help") || string.IsNullOrEmpty(settings.TargetPath) || string.IsNullOrEmpty(settings.Pattern))
            {
                Help();
                return;
            }

            State = new RegexGrepState();
            // 2. valido i configuro stati e attributi della classe
            if (!ParseAndValidateSettings(settings)) return;
            // 3. valido e compilo la regex
            ValidateAndCompileRegex(settings.Pattern);
            // 4. configuro i filtri delle directory
            ConfigureDirectoryFilters(settings);
            // 5. inizializzo il motore regex e il channel
            InitializeEngine();
            // 6. inizializzo cronometro per tracciare il tempo di esecuzione effettivo
            long startTimestamp = Stopwatch.GetTimestamp();
            // ---
            ConsolePlus.Write($"[Cyan]#[/] Inizio la ricerca con regex...\n[DarkGray]*\n*[/]");
            // ---
            try
            {
                // 7. avvio il fastprinter per la stampa a console concorrente
                _fastPrinter!.Run(ct);
                // 8. avvio il produttore e i consumatori
                var producerTask = RunProducerAsync(settings, ct);
                var workerTasks = StartWorkers(settings, ct);
                // 9. attendo l'esecuzione di entrambi
                await producerTask;
                await Task.WhenAll(workerTasks);
            }
            catch (OperationCanceledException) { }
            finally
            {
                // chiudo il channel del fastprinter
                await _fastPrinter!.Complete();
            }
            // 10. termine esecuzione, calcolo statistiche finali
            TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            // ---
            if (CountOnly) ConsolePlus.Write("[DarkGray]*\n*[/]");
            ConsolePlus.WriteBoxHeader($"Ricerca completata", 40);
            ConsolePlus.WriteList([
                $"Match totali: [Green]{TotalMatchCount:N0}[/]",
                $"File totali trovati: [DarkGray]{TotalFileFounded:N0}[/]",
                $"File totali processati: [Magenta]{TotalFileProcessed:N0}[/]",
                $"Spazio totale controllato: [Blue]{Formatter.Bytes(TotalSizeVisited)}[/]",
                $"Throughput: [Cyan]{Formatter.Throughput(TotalSizeVisited, elapsed.TotalSeconds)}[/]",
                $"Scritture Channel: [DarkGray]sync {ChannelWriteSync} - async {ChannelWriteAsync}[/]"
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
            // configurazione regex ottimizzata:
            // - Compiled: la regex viene compilata in codice macchina
            // - NonBacktracking: la regex utilizza un DFA lineare
            var options = RegexOptions.Compiled | RegexOptions.NonBacktracking;
            if (IgnoreCase) options |= RegexOptions.IgnoreCase;

            RegexEngine = new Regex(State.Pattern, options);

            // configuro il channel
            State.FilesChannel = Channel.CreateBounded<GrepFileEntry>(new BoundedChannelOptions(FilesChannelBound)
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
                    TotalFileFounded++;
                    ChannelWriteSync++;
                    if (!State.FilesChannel.Writer.TryWrite(grepFileEntry))
                    {
                        await State.FilesChannel.Writer.WriteAsync(grepFileEntry, ct);
                        ChannelWriteSync--;
                        ChannelWriteAsync++;
                    }
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
                    long localTotalFileProcessed = 0;
                    long localTotalByteSizeVisited = 0;

                    try
                    {
                        await foreach (var entry in State.FilesChannel.Reader.ReadAllAsync(ct))
                        {
                            threadMatchCount += ProcessFile(
                                entry.Path, 
                                entry.Size, 
                                byteBuffer, 
                                charBuffer, 
                                ref localTotalByteSizeVisited, 
                                ref localTotalFileProcessed
                            );
                        }
                    }
                    finally
                    {
                        Interlocked.Add(ref TotalMatchCount, threadMatchCount);
                        Interlocked.Add(ref TotalFileProcessed, localTotalFileProcessed);
                        Interlocked.Add(ref TotalSizeVisited, localTotalByteSizeVisited);
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
        private long ProcessFile(
            string path, 
            long fileLength, 
            byte[] byteBuffer, 
            char[] charBuffer, 
            ref long localTotalByteSizeVisited,
            ref long localTotalFileProcessed
        )
        {
            SafeFileHandle? handle = null;
            long matchCount = 0;

            try
            {
                // apro l'handle a basso livello per bypassare l'overhead dell'I/O standard di .NET
                handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.SequentialScan);
                
                // stato del lettore a chunk
                long fileOffset = 0;
                int leftoverBytes = 0; // byte residui (orfani + overlap) da iniettare nel ciclo successivo
                int totalLines = 1;    // tracciamento globale delle righe '\n'
                bool isFirstChunk = true;
                int overlapChars = 0;  // char da ignorare a fine chunk per evitare match tagliati a metà

                while (fileOffset < fileLength)
                {
                    // calcolo quanti byte pescare dal disco, tenendo conto del leftover già presente in testa al buffer
                    int bytesToRead = (int)Math.Min(byteBuffer.Length - leftoverBytes, fileLength - fileOffset);
                    int bytesRead = RandomAccess.Read(handle, byteBuffer.AsSpan(leftoverBytes, bytesToRead), fileOffset);
                    if (bytesRead == 0) break;

                    int currentByteLength = bytesRead + leftoverBytes;
                    ReadOnlySpan<byte> byteSpan = byteBuffer.AsSpan(0, currentByteLength);
                    localTotalByteSizeVisited += bytesRead; // telemetria: sommo solo i byte freschi appena estratti dal disco

                    // euristica sui file binari: se rilevo un byte nullo (0x00) nel primo blocco, scarto l'intero file
                    if (isFirstChunk)
                    {
                        if (byteSpan.Contains((byte)0)) return 0;
                        isFirstChunk = false;
                        // se ho superato il primo chunk e non contiene byte nulli allora processero l'intero file
                        localTotalFileProcessed++;
                    }

                    // conversione vettorializzata UTF-8 -> UTF-16 (SIMD, zero-alloc)
                    OperationStatus status = Utf8.ToUtf16(byteSpan, charBuffer, out int bytesConsumed, out int charsWritten);

                    if (status == OperationStatus.InvalidData)
                    {
                        // il file contiene byte non conformi allo standard UTF-8 (es binari/compressi anomali), annullo
                        return 0;
                    }

                    ReadOnlySpan<char> searchSpan = charBuffer.AsSpan(0, charsWritten);

                    // definisco la "safe-zone" per la regex: escludo la coda del buffer che verrà ri-analizzata nel chunk successivo
                    int searchEndIndex = isFirstChunk ? charsWritten : charsWritten - overlapChars;
                    if (searchEndIndex < 0) searchEndIndex = charsWritten;

                    // avvio lo scan sul testo normalizzato in memoria
                    if (CountOnly)
                    {
                        matchCount += CountMatches(searchSpan, searchEndIndex);
                    }
                    else
                    {
                        matchCount += ProcessMatches(searchSpan, searchEndIndex, path, charBuffer, charsWritten, totalLines);
                    }

                    // recupero i byte "orfani" (codifiche UTF-8 multi-byte spezzate dal taglio del chunk)
                    int unconsumedBytes = currentByteLength - bytesConsumed;

                    // estrazione dell'overlap: mantengo un margine di sicurezza (ByteOverlapSize) per evitare di frammentare i match
                    int overlapBytes = Math.Min(ByteOverlapSize, bytesConsumed);
                    leftoverBytes = unconsumedBytes + overlapBytes;

                    // memory shift: sposto il blocco di byte da conservare all'inizio del buffer per il prossimo giro
                    byteSpan[(currentByteLength - leftoverBytes)..].CopyTo(byteBuffer);

                    // stima conservativa dell'overlap in char per calcolare la safe-zone del prossimo chunk.
                    // nel worst-case UTF-8 un carattere occupa 3 byte
                    overlapChars = Math.Min(1024, overlapBytes / 3 + 1);

                    // aggiorno il contatore delle righe calcolando i '\n' esclusivamente sulla porzione di testo consumata
                    int charsProcessed = Math.Min(searchEndIndex, charsWritten);
                    totalLines += CountLines(searchSpan[..charsProcessed]);

                    fileOffset += bytesRead;
                }
            }
            catch (UnauthorizedAccessException) { /* ignoro silenziosamente i file di sistema bloccati */ }
            catch (IOException) { /* ignoro i file aperti in uso esclusivo da altri processi */ }
            finally
            {
                handle?.Dispose();
            }

            // validazione dei constraint numerici per la modalità count-only
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
                    matchCount = 0; // i limiti non sono rispettati, invalido il conteggio per questo file
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

            // affitto spazio per costruire la stringa di output
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

            // estraggo il contesto del match
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
        /// Conta le newline nello span di char (SIMD)
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