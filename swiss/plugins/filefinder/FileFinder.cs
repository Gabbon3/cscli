using System.IO.Enumeration;
using lib.io;
using lib.io.stack;
using lib.console;
using lib.utils;

namespace plugins.filefinder
{
    class FileFinder : Plugin
    {
        public override string Name => "find";
        public override string Description => "Ricerca di file tramite regex o stringhe fisse, con supporto classifiche (ranking)";

        private PriorityQueue<StackFileInfo, long>? PriorityQueue;
        // Delegate che deciderà cosa fare col file (Stampa o Inserimento in coda)
        private Action<StackFileInfo>? _processItemStrategy;
        // Delegate che calcolerà il punteggio al volo senza if
        private Func<StackFileInfo, long>? _prioritySelector;

        private struct FinderOptions
        {
            public bool Oldest { get; set; }
            public bool Newest { get; set; }
            public bool Biggest { get; set; }
            public bool Smallest { get; set; }
            public int Limit { get; set; }
        }

        private FinderOptions FinderOptionsConfig;

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            var settings = ParseSettings<FindSettings>(args);
            if (args.Contains("--help") || string.IsNullOrEmpty(settings.TargetPath))
            {
                Help();
                return;
            }
            
            string root = ParsePath(settings.TargetPath, true)!;
            string? pattern = ParseMatchPattern(settings.Pattern);
            // default true
            bool recurse = !settings.NoRecurseSubdirectories;

            // RANKING
            bool isRanking = settings.Oldest || settings.Newest || settings.Biggest || settings.Smallest;
            if (isRanking)
            {
                PriorityQueue = new PriorityQueue<StackFileInfo, long>();
                // --- LETTURA OPZIONI RANKING ---
                FinderOptionsConfig.Oldest = settings.Oldest;
                FinderOptionsConfig.Newest = settings.Newest;
                FinderOptionsConfig.Biggest = settings.Biggest;
                FinderOptionsConfig.Smallest = settings.Smallest;
                FinderOptionsConfig.Limit = settings.Limit;
                if (settings.Biggest) _prioritySelector = item => item.Length;
                else if (settings.Smallest) _prioritySelector = item => -item.Length;
                else if (settings.Oldest) _prioritySelector = item => -item.LastWriteTime.Ticks;
                else if (settings.Newest) _prioritySelector = item => item.LastWriteTime.Ticks;

                _processItemStrategy = RankItem;
            }
            else
            {
                _processItemStrategy = PrintSimpleMatch;
            }

            // FILTRI
            var filterOpts = new FileFilterFactory.FilterOptions(
                Pattern: pattern,
                MatchType: settings.FixedMatch ? FilterFileNameMatchType.Fixed : FilterFileNameMatchType.Regex,
                IgnoreCase: settings.IgnoreCase,
                ModifiedBefore: settings.Since,
                ModifiedAfter: settings.OlderThan
            );

            FileSystemFilter? fileFilter;
            try
            {
                fileFilter = FileFilterFactory.CreateFilter(filterOpts);
            }
            catch (ArgumentException ex)
            {
                PrintError("Il pattern fornito non è valido: " + ex.Message);
                return;
            }
            catch (Exception ex)
            {
                PrintError("Errore durante la creazione dei filtri per i file: " + ex.Message);
                return;
            }

            // FASTWALKER OPTIONS
            var fastWalkerOptions = new FastWalkerOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = recurse,
                Filter = fileFilter,
                BufferSize = 64 * 1024,
                SingleReader = true,
                ReturnDirectoriesInOutput = settings.Dirs
            };

            // avvio il walker
            var walkerReader = FastWalker.Walk<StackFileInfo>(
                root,
                (ref FileSystemEntry entry) => new StackFileInfo(ref entry),
                fastWalkerOptions,
                ct
            );

            int matchCount = 0;

            // leggo dal channel
            await foreach (var item in walkerReader.ReadAllAsync(ct))
            {
                matchCount++;
                _processItemStrategy!(item);
            }

            // se sto effettuando top n stampo i risultati finali
            if (isRanking)
            {
                ConsolePlus.Write($"\n[Yellow]Risultati classifica (Top {Math.Min(FinderOptionsConfig.Limit, matchCount)}):[/]");

                while (PriorityQueue!.Count > 0)
                {
                    var item = PriorityQueue.Dequeue();
                    try
                    {
                        string info = FinderOptionsConfig.Biggest || FinderOptionsConfig.Smallest
                            ? $" ({Formatter.Bytes(item.Length)})"
                            : $" ({item.LastWriteTime.ToLocalTime():yyyy-MM-dd HH:mm:ss})";

                        ConsolePlus.Write($"[DarkGray]{item.AsDirectorySpan()}[Cyan]{item.AsNameSpan()}[/][Yellow]{info}[/]");
                    }
                    finally
                    {
                        item.Dispose(); // smaltisco dall'arraypool i file nella coda prioritaria
                    }
                }
            }

            ConsolePlus.WriteHr();
            ConsolePlus.Write($"[Cyan]#[/] Ricerca conclusa");
            ConsolePlus.Write($"[Cyan]*[/] Elementi totali analizzati: [Cyan]{matchCount}[/]");
            ConsolePlus.WriteHr();
        }

        private void PrintSimpleMatch(StackFileInfo item)
        {
            ConsolePlus.Write($"[DarkGray]{item.AsDirectorySpan()}[Cyan]{item.AsNameSpan()}[/]");
            item.Dispose();
        }

        private void RankItem(StackFileInfo item)
        {
            PriorityQueue!.Enqueue(item, _prioritySelector!(item));
            if (PriorityQueue.Count > FinderOptionsConfig.Limit)
            {
                PriorityQueue.Dequeue().Dispose();
            }
        }

        public override void Help()
        {
            PrintHelp<FindSettings>();
        }
    }
}