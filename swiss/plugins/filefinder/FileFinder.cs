using System.Text.RegularExpressions;
using System.Buffers;
using utils;

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
            if (!string.IsNullOrEmpty(pattern))
            {
                try
                {
                    regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                }
                catch (Exception)
                {
                    PrintError("la regex inserita non è valida");
                    return;
                }
            }

            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                BufferSize = 64 * 1024
            };
            // avvio il walker
            var walkerReader = FastWalker.Walk<StackFileInfo>(
                root,
                options,
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
                        if (regex == null || regex.IsMatch(item.AsNameSpan()))
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