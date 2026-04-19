using System.IO.Enumeration;
using lib.io;
using lib.console;

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

            string root = args[0] == "." ? Directory.GetCurrentDirectory() : args[0];

            if (!Directory.Exists(root))
            {
                PrintError($"il percorso \"{root}\" non esiste");
                return;
            }

            ParseArguments(args, 1);

            var includeDirectory = OptionsContains("--directory", "-d");
            var recurse = OptionsContains("--recursive", "-r");

            // creazione dei filtro per il conteggio
            var filterOpts = new FileFilterFactory.FilterOptions(
                Pattern: GetOptionValue("-p", "--pattern"),
                MatchType: OptionsContains("-f", "--fixed") ? FilterFileNameMatchType.Fixed : FilterFileNameMatchType.Regex,
                IgnoreCase: OptionsContains("-i", "--ignore-case"),
                ModifiedAfter: GetOptionDatetime("-a", "--after"),
                ModifiedBefore: GetOptionDatetime("-b", "--before")
            );

            FileSystemFilter? fileFilter = null;
            try
            {
                fileFilter = FileFilterFactory.CreateFilter(filterOpts);
            }
            catch (Exception ex)
            {
                PrintError("Errore filtro: " + ex.Message);
                return;
            }

            // configurazione walker
            var fastWalkerOptions = new FastWalkerOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = recurse,
                BufferSize = 64 * 1024,
                ReturnDirectoriesInOutput = includeDirectory,
                Filter = fileFilter,
                SingleReader = true
            };

            // walker restituisce solo il bool, la logica di filtro ce l'abbiamo gia a monte
            var walkerReader = FastWalker.Walk<bool>(
                root,
                (ref FileSystemEntry entry) => entry.IsDirectory, // transform crazy
                fastWalkerOptions,
                ct
            );

            long filesCount = 0;
            long dirsCount = 0;

            // Lettura dal channel
            await foreach (bool isDirectory in walkerReader.ReadAllAsync(ct))
            {
                if (isDirectory)
                {
                    dirsCount++;
                }
                else
                {
                    filesCount++;
                }
            }

            ConsolePlus.Write($"\n[Cyan]#[/] Conteggio completato:");
            ConsolePlus.Write($"[Cyan]*[/] Files: [Yellow]{filesCount:N0}[/]");
            if (includeDirectory)
            {
                ConsolePlus.Write($"[Cyan]*[/] Cartelle: [Blue]{dirsCount:N0}[/]");
            }
            ConsolePlus.Write($"[Cyan]=[/] Totale: [Magenta]{(filesCount + dirsCount):N0}[/]");
        }

        public override void Help()
        {
            ConsolePlus.WriteHr();
            ConsolePlus.Write("[Cyan]#[/] Utilizzo: [Yellow]swiss [Magenta]count [DarkGray]<percorso> [opzioni]");
            ConsolePlus.Write("[Cyan]#[/] - percorso: usa . per la cartella corrente oppure definisci un percorso completo");
            ConsolePlus.Write("[Cyan]#[/] Opzioni Ricerca:");
            ConsolePlus.Write("[Cyan]#[/] --directory, -d   : Includi le cartelle nel conteggio");
            ConsolePlus.Write("[Cyan]#[/] --recursive, -r   : Includi nel conteggio tutte le sotto cartelle");
            ConsolePlus.Write("[Cyan]#[/] --pattern, -p     : Pattern per filtrare i file da contare");
            ConsolePlus.Write("[Cyan]#[/] --ignore-case, -i : Rende case insensitive la ricerca");
            ConsolePlus.Write("[Cyan]#[/] --fixed, -f       : non utilizza la regex ma verifica se il pattern è contenuto nel nome file (+ veloce)");
            ConsolePlus.Write("[Cyan]#[/] --after, -a <data>: Trova i file modificati DOPO questa data (YYYY-MM-DD)");
            ConsolePlus.Write("[Cyan]#[/] --before,-b <data>: Trova i file modificati PRIMA di questa data (YYYY-MM-DD)");
            ConsolePlus.Write("[Cyan]#[/] Esempi:");
            ConsolePlus.Write("[Cyan]#[/] - swiss count .");
            ConsolePlus.Write("[Cyan]#[/] - swiss count . -d -p \"*.txt\" -f");
            ConsolePlus.WriteHr();
        }
    }
}