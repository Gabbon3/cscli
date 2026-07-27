using System.IO.Enumeration;
using lib.io;
using lib.utils;
using lib.console;

namespace plugins.count
{
    internal class CountPlugin : Plugin
    {
        private readonly struct CountEntry(bool isDirectory, long size)
        {
            public readonly bool IsDirectory { get; } = isDirectory;
            public readonly long Size { get; } = size;
        }
        public override string Name => "count";
        public override string Description => "Conta il numero di file e/o cartelle";

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            var settings = ParseSettings<CountSettings>(args);

            // gestione Help o argomenti mancanti
            if (args.Length < 1 || args.Contains("--help") || string.IsNullOrEmpty(settings.TargetPath))
            {
                Help();
                return;
            }

            // setup del percorso root
            string root = settings.TargetPath == "." ? Directory.GetCurrentDirectory() : settings.TargetPath;

            if (!Directory.Exists(root))
            {
                PrintError($"Il percorso \"{root}\" non esiste");
                return;
            }

            // creazione del filtro
            var filterOpts = new FileFilterFactory.FilterOptions(
                Pattern: ParseMatchPattern(settings.Pattern),
                MatchType: settings.FixedMatch ? FilterFileNameMatchType.Fixed : FilterFileNameMatchType.Regex,
                IgnoreCase: settings.IgnoreCase,
                DateBefore: settings.DateBefore,
                DateAfter: settings.DateAfter
            );

            FileSystemFilter? fileFilter;
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
            FileAttributes attributesToSkip = FileAttributes.None;
            if (!settings.IncludeHidden) attributesToSkip = FileAttributes.Hidden;

            var fastWalkerOptions = new FastWalkerOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = settings.Recursive,
                BufferSize = 64 * 1024,
                ReturnDirectoriesInOutput = settings.IncludeDirectory,
                Filter = fileFilter,
                SingleReader = true,
                AttributesToSkip = attributesToSkip
            };

            FastWalker.CountResult result = await FastWalker.CountAsync(
                root,
                fastWalkerOptions,
                ct
            );

            ConsolePlus.Write($"\n[Cyan]#[/] Conteggio completato:");
            ConsolePlus.Write($"[Cyan]*[/] Files: [Yellow]{result.Files:N0}[/]");
            ConsolePlus.Write($"[Cyan]*[/] Dimensione: [Green]{Formatter.Bytes(result.Bytes)}[/]");
            if (settings.IncludeDirectory)
            {
                ConsolePlus.Write($"[Cyan]*[/] Cartelle: [Blue]{result.Directories:N0}[/]");
            }
            ConsolePlus.Write($"[Cyan]=[/] Totale: [Magenta]{result.Files + result.Directories:N0}[/]");
        }

        public override void Help()
        {
            PrintHelp<CountSettings>();
        }
    }
}