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
            // 1. Il parsing magico in una riga
            var settings = ParseSettings<CountSettings>(args);

            // 2. Gestione Help o argomenti mancanti
            if (args.Length < 1 || args.Contains("--help") || string.IsNullOrEmpty(settings.TargetPath))
            {
                Help();
                return;
            }

            // 3. Setup del percorso root
            string root = settings.TargetPath == "." ? Directory.GetCurrentDirectory() : settings.TargetPath;

            if (!Directory.Exists(root))
            {
                PrintError($"Il percorso \"{root}\" non esiste"); // Assumo tu abbia un PrintError nella classe base o altrove
                return;
            }

            // 4. Creazione del filtro usando l'oggetto strongly-typed
            var filterOpts = new FileFilterFactory.FilterOptions(
                Pattern: ParseMatchPattern(settings.Pattern),
                MatchType: settings.FixedMatch ? FilterFileNameMatchType.Fixed : FilterFileNameMatchType.Regex,
                IgnoreCase: settings.IgnoreCase,
                ModifiedBefore: settings.Since,
                ModifiedAfter: settings.OlderThan
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
                RecurseSubdirectories = settings.Recursive,
                BufferSize = 64 * 1024,
                ReturnDirectoriesInOutput = settings.IncludeDirectory,
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
            if (settings.IncludeDirectory)
            {
                ConsolePlus.Write($"[Cyan]*[/] Cartelle: [Blue]{dirsCount:N0}[/]");
            }
            ConsolePlus.Write($"[Cyan]=[/] Totale: [Magenta]{(filesCount + dirsCount):N0}[/]");
        }

        public override void Help()
        {
            PrintHelp<CountSettings>();
        }
    }
}