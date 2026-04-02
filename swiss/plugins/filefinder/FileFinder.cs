using System.Text.RegularExpressions;
using System.Buffers;
using utils;
using System.IO.Compression;

namespace plugins.filefinder
{
    class FileFinder : Plugin
    {
        public override string Name => "find";
        public override string Description => "Ricerca tramite Regex di file";

        private static readonly object _consoleLock = new();

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            if (args.Length < 2 || string.Equals(args[0], "help"))
            {
                Help();
                return;
            }

            string root = args[0];
            string pattern = args[1];

            var options = ParseArguments(args, 2);

            var isPatternFixed = options.ContainsKey("--fixed") || options.ContainsKey("-f");
            var ignoreCase = options.ContainsKey("--ignore-case") || options.ContainsKey("-i");

            if (root == ".")
            {
                root = Directory.GetCurrentDirectory();
            }
            else if (!Directory.Exists(root))
            {
                Console.WriteLine($"Errore: il percorso \"{root}\" non esiste");
                return;
            }

            Regex? regex = null;
            if (!string.IsNullOrEmpty(pattern) && !isPatternFixed)
            {
                try
                {
                    var regexOptions = RegexOptions.Compiled | RegexOptions.NonBacktracking;
                    if (ignoreCase) regexOptions |= RegexOptions.IgnoreCase;
                    regex = new Regex(pattern, regexOptions);
                }
                catch (Exception)
                {
                    PrintError("la regex inserita non è valida");
                    return;
                }
            }
            // Funzione di filtraggio dei file
            Func<ReadOnlySpan<char>, bool> filterFunction;
            if (isPatternFixed)
            {
                // uso indexOf diretto - no regex
                StringComparison indexOfOptions = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                filterFunction = (span) => span.IndexOf(pattern.AsSpan(), indexOfOptions) >= 0;
            }
            else
            {
                // utilizzo le regex
                filterFunction = regex == null ? (span) => true : (span) => regex == null || regex.IsMatch(span);
            }

            var enumerationOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                BufferSize = 64 * 1024
            };
            // avvio il walker
            var walkerReader = FastWalker.Walk<StackFileInfo>(
                root,
                enumerationOptions,
                (ref System.IO.Enumeration.FileSystemEntry entry) => new StackFileInfo(ref entry),
                maxDegreeOfParallelism: Environment.ProcessorCount,
                SingleReader: false,
                ct
            );
            // avvio i consumer
            int matchCount = 0;
            await Parallel.ForEachAsync(
                walkerReader.ReadAllAsync(ct),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = ct
                },
                async (item, token) =>
                {
                    try
                    {
                        if (filterFunction(item.AsNameSpan()))
                        {
                            Interlocked.Increment(ref matchCount);
                            PrintMatch(item.GetFullPath());
                        }
                    }
                    finally
                    {
                        // libero l'ArrayPool
                        if (item.PathBuffer != null)
                            ArrayPool<char>.Shared.Return(item.PathBuffer, clearArray: false);
                    }
                }
            );
        }

        private static void PrintMatch(string path)
        {
            int lastSeparator = path.LastIndexOf(Path.DirectorySeparatorChar);
            if (lastSeparator == -1)
            {
                Console.WriteLine(path);
                return;
            }

            string directory = path[..(lastSeparator + 1)];
            string fileName = path[(lastSeparator + 1)..];

            lock (_consoleLock)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(directory);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(fileName);
                Console.ResetColor();
            }
        }

        public override void Help()
        {
            Console.WriteLine("--------------------------------");
            Console.WriteLine("Utilizzo: swiss find <percorso> <regex>");
            Console.WriteLine(" - swiss find C:\\Users\\ \".*\\.pdf\"");
            Console.WriteLine(" - swiss find . \"\"");
            Console.WriteLine("--------------------------------");
        }
    }
}