using stack;
using utils.console;

namespace plugins.count
{
    internal class CountPlugin : Plugin
    {
        public override string Name => "count";
        public override string Description => "Conta il numero di file e/o cartelle";

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            if (args.Length < 1)
            {
                Help();
                return;
            }

            string root = args[0];

            var options = ParseArguments(args, 1);

            if (root == ".")
            {
                root = Directory.GetCurrentDirectory();
            }
            else if (!Directory.Exists(root))
            {
                PrintError($"il percorso \"{root}\" non esiste");
                return;
            }

            var includeDirectory = options.ContainsKey("--directory") || options.ContainsKey("-d");
            var recurse = options.ContainsKey("--recursive") || options.ContainsKey("-r");

            var fastWalkerOptions = new FastWalkerOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = recurse,
                BufferSize = 64 * 1024,
                ReturnDirectoriesInOutput = includeDirectory
            };
            var walkerReader = FastWalker.Walk<StackFileInfo>(
                root,
                (ref System.IO.Enumeration.FileSystemEntry entry) => new StackFileInfo(ref entry),
                fastWalkerOptions,
                ct
            );
            long filesCount = 0;
            long dirsCount = 0;
            await foreach (var entry in walkerReader.ReadAllAsync(ct))
            {
                try
                {
                    if (entry.IsDirectory)
                    {
                        dirsCount++;
                    }
                    else
                    {
                        filesCount++;
                    }
                }
                finally
                {
                    entry.Dispose();
                }
            }
            ConsolePlus.Write($"\n[Cyan]#[/] Conteggio completato:");
            ConsolePlus.Write($"[Cyan]*[/] Files: [Yellow]{filesCount:N0}[/]");
            ConsolePlus.Write($"[Cyan]*[/] Cartelle: [Blue]{dirsCount:N0}[/]");
            ConsolePlus.Write($"[Cyan]=[/] Totale: [Magenta]{(filesCount + dirsCount):N0}[/]");
        }

        public override void Help()
        {
            ConsolePlus.Write("[Cyan]#[DarkGray] -------------------------------- [Cyan]#[/]");
            ConsolePlus.Write("[Cyan]#[/] Utilizzo: [Yellow]swiss [Magenta]count [DarkGray]<percorso> [opzioni]");
            ConsolePlus.Write("[Cyan]#[/] - percorso: usa . per la cartella corrente oppure definisci un percorso completo");
            ConsolePlus.Write("[Cyan]#[/] Opzioni:");
            ConsolePlus.Write("[Cyan]#[/] --directory, -d : Includi le cartelle nel conteggio");
            ConsolePlus.Write("[Cyan]#[/] --recursive, -r : Includi nel conteggio tutte le sotto cartelle");
            ConsolePlus.Write("[Cyan]#[/] Esempi:");
            ConsolePlus.Write("[Cyan]#[/] - swiss count .");
            ConsolePlus.Write("[Cyan]#[/] - swiss count . -d");
            ConsolePlus.Write("[Cyan]#[DarkGray] -------------------------------- [Cyan]#[/]");
        }
    }
}
