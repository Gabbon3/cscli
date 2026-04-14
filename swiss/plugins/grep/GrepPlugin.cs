using System.IO.Enumeration;
using System.Threading.Channels;
using System.Text;
using Microsoft.Win32.SafeHandles;
using utils;

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
        // lock per la stampa dei match a console
        private static readonly Lock _consoleLock = new();

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
            var excludeGlobs = new List<string>();
            var GlobOptions = options.TryGetValue("--glob", out string? gl1) ? gl1 : options.TryGetValue("-g", out string? gl2) ? gl2 : null;
            if (!string.IsNullOrEmpty(GlobOptions))
            {
                foreach (var glob in GlobOptions.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    excludeGlobs.Add(glob.Trim());
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
                    if (excludeGlobs.Count == 0) return true;

                    ReadOnlySpan<char> fileName = entry.FileName;
                    // per ogni regola di esclusione la verifico
                    foreach (var glob in excludeGlobs)
                    {
                        if (FileSystemName.MatchesSimpleExpression(glob, fileName, ignoreCase: true))
                        {
                            return false;
                        }
                    }
                    // il file passa a priori
                    return true;
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
                        PrintMatch(path);
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

        // Normalizzazione ASCII inline, zero heap
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static byte ToLowerAscii(byte b)
            => (b >= 65 && b <= 90) ? (byte)(b | 0x20) : b;

        private static void PrintMatch(string path)
        {
            lock (_consoleLock)
            {
                string dir = Path.GetDirectoryName(path) + Path.DirectorySeparatorChar;
                string file = Path.GetFileName(path);
                ConsolePlus.Write($"[Green]# [DarkGray]{dir}[/][Cyan]{file}[/]");
            }
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