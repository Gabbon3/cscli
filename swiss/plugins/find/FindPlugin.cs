using System.IO.Enumeration;
using lib.io;
using lib.io.stack;
using lib.console;
using lib.utils;

namespace plugins.find
{
    class FindPlugin : Plugin
    {
        public override string Name => "find";
        public override string Description => "Ricerca di file tramite regex o stringhe fisse, con supporto classifiche (ranking)";

        // # Stato condiviso tra i metodi
        private FindState State = new();

        // # Stato interno
        private class FindState
        {
            public string Root = string.Empty;
            public string? Pattern;
            public bool Recurse = true;
            public bool IsRanking = false;
            public int MatchCount = 0;

            public PriorityQueue<StackFileInfo, long>? PriorityQueue;
            public Action<StackFileInfo>? ProcessItemStrategy;
            public Func<StackFileInfo, long>? PrioritySelector;
            public FinderOptions Config = new();
            public FileSystemFilter? FileFilter;
        }

        private struct FinderOptions
        {
            public bool Oldest { get; set; }
            public bool Newest { get; set; }
            public bool Biggest { get; set; }
            public bool Smallest { get; set; }
            public int Limit { get; set; }
        }

        // # ---------------------------------- #
        // RunAsync — diagramma di flusso
        // # ---------------------------------- #
        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            // 1. ottengo i valori di settings
            var settings = ParseSettings<FindSettings>(args);
            if (args.Contains("--help") || string.IsNullOrEmpty(settings.TargetPath))
            {
                Help();
                return;
            }

            State = new FindState();
            // 2. valido e parsifico le settings
            if (!ParseAndValidateSettings(settings)) return;
            // 3. configuro le opzioni per il ranking (se richiesto)
            ConfigureRankingMode(settings);
            // 4. costruisco il filtro per cercare i file (il motore vero del plugin)
            if (!BuildFileFilter(settings)) return;
            // 5. creo la configurazione del FastWalker
            var walkerOptions = CreateWalkerOptions(settings);
            // 6. avvio il processo principale, inizio la ricerca
            await ProcessFilesAsync(walkerOptions, ct);
            // 7. stampo la top n (se richiesta)
            if (State.IsRanking) PrintRankingResults();
            // 8. statistiche finali
            PrintFinalSummary();
        }

        // # ---------------------------------- #
        // Metodi estratti
        // # ---------------------------------- #

        /// <summary>
        /// Valida le impostazioni e popola root, pattern e recurse in State.
        /// Accede a: State.Root, State.Pattern, State.Recurse
        /// </summary>
        private bool ParseAndValidateSettings(FindSettings settings)
        {
            string? root = ParsePath(settings.TargetPath, true);
            if (root is null)
            {
                PrintError("Il percorso specificato non è valido.");
                return false;
            }

            State.Root = root;
            State.Pattern = ParseMatchPattern(settings.Pattern);
            State.Recurse = !settings.NoRecurseSubdirectories;
            return true;
        }

        /// <summary>
        /// Configura la modalità ranking se richiesta, inizializzando PriorityQueue e i delegate.
        /// Accede a: State.IsRanking, State.PriorityQueue, State.Config, State.PrioritySelector, State.ProcessItemStrategy
        /// </summary>
        private void ConfigureRankingMode(FindSettings settings)
        {
            State.IsRanking = settings.Oldest || settings.Newest || settings.Biggest || settings.Smallest;

            if (State.IsRanking)
            {
                State.PriorityQueue = new PriorityQueue<StackFileInfo, long>();
                State.Config.Oldest = settings.Oldest;
                State.Config.Newest = settings.Newest;
                State.Config.Biggest = settings.Biggest;
                State.Config.Smallest = settings.Smallest;
                State.Config.Limit = settings.Limit;

                if (settings.Biggest)
                    State.PrioritySelector = item => item.Length;
                else if (settings.Smallest)
                    State.PrioritySelector = item => -item.Length;
                else if (settings.Oldest)
                    State.PrioritySelector = item => -item.LastWriteTime.Ticks;
                else if (settings.Newest)
                    State.PrioritySelector = item => item.LastWriteTime.Ticks;

                State.ProcessItemStrategy = RankItem;
            }
            else
            {
                State.ProcessItemStrategy = PrintSimpleMatch;
            }
        }

        /// <summary>
        /// Crea il FileSystemFilter in base alle opzioni fornite.
        /// Accede a: State.Pattern, State.FileFilter
        /// </summary>
        private bool BuildFileFilter(FindSettings settings)
        {
            var filterOpts = new FileFilterFactory.FilterOptions(
                Pattern: State.Pattern,
                MatchType: settings.FixedMatch ? FilterFileNameMatchType.Fixed : FilterFileNameMatchType.Regex,
                IgnoreCase: settings.IgnoreCase,
                ModifiedBefore: settings.OlderThan,
                ModifiedAfter: settings.Since
            );

            try
            {
                State.FileFilter = FileFilterFactory.CreateFilter(filterOpts);
                return true;
            }
            catch (ArgumentException ex)
            {
                PrintError("Il pattern fornito non è valido: " + ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                PrintError("Errore durante la creazione dei filtri per i file: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Crea le opzioni per FastWalker in base alle configurazioni in State.
        /// Accede a: State.FileFilter, State.Recurse
        /// </summary>
        private FastWalkerOptions CreateWalkerOptions(FindSettings settings)
        {
            return new FastWalkerOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = State.Recurse,
                Filter = State.FileFilter,
                BufferSize = 64 * 1024,
                SingleReader = true,
                ReturnDirectoriesInOutput = settings.Dirs
            };
        }

        /// <summary>
        /// Avvia il walker, legge i file e li processa tramite la strategy configurata.
        /// Accede a: State.Root, State.MatchCount, State.ProcessItemStrategy
        /// </summary>
        private async Task ProcessFilesAsync(FastWalkerOptions walkerOptions, CancellationToken ct)
        {
            var walkerReader = FastWalker.Walk<StackFileInfo>(
                State.Root,
                (ref FileSystemEntry entry) => new StackFileInfo(ref entry),
                walkerOptions,
                ct
            );

            await foreach (var item in walkerReader.ReadAllAsync(ct))
            {
                State.MatchCount++;
                State.ProcessItemStrategy!(item);
            }
        }

        /// <summary>
        /// Stampa i risultati della classifica estraendo gli elementi dalla PriorityQueue.
        /// Accede a: State.PriorityQueue, State.Config, State.MatchCount
        /// </summary>
        private void PrintRankingResults()
        {
            ConsolePlus.Write($"\n[Yellow]Risultati classifica (Top {Math.Min(State.Config.Limit, State.MatchCount)}):[/]");

            while (State.PriorityQueue!.Count > 0)
            {
                var item = State.PriorityQueue.Dequeue();
                try
                {
                    string info = State.Config.Biggest || State.Config.Smallest
                        ? $" ({Formatter.Bytes(item.Length)})"
                        : $" ({item.LastWriteTime.ToLocalTime():yyyy-MM-dd HH:mm:ss})";

                    ConsolePlus.Write($"[DarkGray]{item.AsDirectorySpan()}[Cyan]{item.AsNameSpan()}[/][Yellow]{info}[/]");
                }
                finally
                {
                    item.Dispose();
                }
            }
        }

        /// <summary>
        /// Stampa il riepilogo finale con il conteggio degli elementi trovati.
        /// Accede a: State.MatchCount
        /// </summary>
        private void PrintFinalSummary()
        {
            ConsolePlus.WriteHr();
            ConsolePlus.Write($"[Cyan]#[/] Ricerca conclusa");
            ConsolePlus.Write($"[Cyan]#[/] Elementi trovati: [Cyan]{State.MatchCount}[/]");
            ConsolePlus.WriteHr();
        }

        // # ---------------------------------- #
        // Metodi di supporto (invariati nella logica)
        // # ---------------------------------- #

        /// <summary>
        /// Stampa un match semplice (non-ranking) e dispose del StackFileInfo.
        /// Accede a: nessun accesso a State
        /// </summary>
        private void PrintSimpleMatch(StackFileInfo item)
        {
            if (item.IsDirectory)
            {
                ConsolePlus.Write($"[Cyan]{item.AsPathSpan()}[/]");
            }
            else
            {
                ConsolePlus.Write($"[DarkGray]{item.AsDirectorySpan()}[Cyan]{item.AsNameSpan()}[/]");
            }
            item.Dispose();
        }

        /// <summary>
        /// Inserisce un item nella PriorityQueue mantenendo solo i top N elementi.
        /// Accede a: State.PriorityQueue, State.PrioritySelector, State.Config.Limit
        /// </summary>
        private void RankItem(StackFileInfo item)
        {
            State.PriorityQueue!.Enqueue(item, State.PrioritySelector!(item));
            if (State.PriorityQueue.Count > State.Config.Limit)
            {
                State.PriorityQueue.Dequeue().Dispose();
            }
        }

        public override void Help() => PrintHelp<FindSettings>();
    }
}