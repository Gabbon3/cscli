using System.Text.RegularExpressions;
using stack;
using utils.console;

namespace plugins.filefinder
{
    class FileFinder : Plugin
    {
        public override string Name => "find";
        public override string Description => "Ricerca di file tramite regex o stringhe fisse";

        private static readonly Lock _consoleLock = new();

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            if (args.Length < 2)
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
                PrintError($"il percorso \"{root}\" non esiste");
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

            var fastWalkerOptions = new FastWalkerOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                BufferSize = 64 * 1024
            };
            // avvio il walker
            var walkerReader = FastWalker.Walk<StackFileInfo>(
                root,
                (ref System.IO.Enumeration.FileSystemEntry entry) => new StackFileInfo(ref entry),
                fastWalkerOptions,
                ct
            );
            // avvio i consumer
            // int matchCount = 0; // INFO: al momento non serve
            await Parallel.ForEachAsync(
                walkerReader.ReadAllAsync(ct),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = ct
                },
                (item, token) =>
                {
                    using (item)
                    {
                        if (filterFunction(item.AsNameSpan()))
                        {
                            // Interlocked.Increment(ref matchCount);
                            PrintMatch(item.AsDirectorySpan(), item.AsNameSpan());
                        }
                    }
                    return ValueTask.CompletedTask;
                }
            );
        }

        private static void PrintMatch(ReadOnlySpan<char> directorySpan, ReadOnlySpan<char> fileNameSpan)
        {
            lock (_consoleLock)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Out.Write(directorySpan);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Out.WriteLine(fileNameSpan);
                Console.ResetColor();
            }
        }

        public override void Help()
        {
            ConsolePlus.Write("[Cyan]#[DarkGray] -------------------------------- [Cyan]#[/]");
            ConsolePlus.Write("[Cyan]#[/] Utilizzo: [Yellow]swiss [Magenta]find [DarkGray]<percorso> <pattern> [opzioni]");
            ConsolePlus.Write("[Cyan]#[/] - percorso: usa . per la cartella corrente oppure definisci un percorso completo");
            ConsolePlus.Write("[Cyan]#[/] - pattern: la stringa da usare per la ricerca, regex di default");
            ConsolePlus.Write("[Cyan]#[/] Opzioni:");
            ConsolePlus.Write("[Cyan]#[/] --ignore-case, -i : Rende case insensitive la ricerca");
            ConsolePlus.Write("[Cyan]#[/] --fixed, -f       : non utilizza la regex ma verifica se il pattern è contenuto nel nome file (+ veloce)");
            ConsolePlus.Write("[Cyan]#[/] Esempi:");
            ConsolePlus.Write("[Cyan]#[/] - swiss find C:\\Users\\ \".*\\.pdf\"");
            ConsolePlus.Write("[Cyan]#[/] - swiss find . \"\"");
            ConsolePlus.Write("[Cyan]#[DarkGray] -------------------------------- [Cyan]#[/]");
        }
    }
}