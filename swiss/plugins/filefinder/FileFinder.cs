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
            if (args.Length < 2)
            {
                Help();
                return;
            }
            string root = args[0];
            string? pattern = args[1];

            if (root == ".")
            {
                root = Directory.GetCurrentDirectory();
            }
            else if (!Directory.Exists(root))
            {
                PrintError($"il percorso \"{root}\" non esiste");
                return;
            }

            if (string.IsNullOrEmpty(pattern))
            {
                pattern = null;
            }

            ParseArguments(args, 2);

            // --- LETTURA OPZIONI RANKING ---
            FinderOptionsConfig.Oldest = OptionsContains("--oldest", "-O");
            FinderOptionsConfig.Newest = OptionsContains("--newest", "-N");
            FinderOptionsConfig.Biggest = OptionsContains("--biggest", "-B");
            FinderOptionsConfig.Smallest = OptionsContains("--smallest", "-S");
            bool isRanking = FinderOptionsConfig.Oldest ||
                            FinderOptionsConfig.Newest ||
                            FinderOptionsConfig.Biggest ||
                            FinderOptionsConfig.Smallest;

            // parsing del limite, default 10
            FinderOptionsConfig.Limit = GetOptionInt("-l", "--limit") ?? 10;

            // pre calcolo delle strategie
            if (isRanking)
            {
                // alloco la coda solo se serve
                PriorityQueue = new PriorityQueue<StackFileInfo, long>();
                // Precalcolo come si estrae la priorità senza fare if nel ciclo
                if (FinderOptionsConfig.Biggest) _prioritySelector = item => item.Length;
                else if (FinderOptionsConfig.Smallest) _prioritySelector = item => -item.Length;
                else if (FinderOptionsConfig.Oldest) _prioritySelector = item => -item.LastWriteTime.Ticks;
                else if (FinderOptionsConfig.Newest) _prioritySelector = item => item.LastWriteTime.Ticks;
                // La strategia per ogni elemento sarà di usare la coda
                _processItemStrategy = RankItem;
            }
            else
            {
                // La strategia di base è la stampa immediata
                _processItemStrategy = PrintSimpleMatch;
            }

            var filterOpts = new FileFilterFactory.FilterOptions(
                Pattern: pattern,
                MatchType: OptionsContains("--fixed", "-f") ? FilterFileNameMatchType.Fixed : FilterFileNameMatchType.Regex,
                IgnoreCase: OptionsContains("--ignore-case", "-i"),
                ModifiedBefore: GetOptionAge("--since", "-s"),
                ModifiedAfter: GetOptionAge("--older-than", "-o")
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

            var fastWalkerOptions = new FastWalkerOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                Filter = fileFilter,
                BufferSize = 64 * 1024,
                SingleReader = true,
                ReturnDirectoriesInOutput = OptionsContains("--dirs", "-d")
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
            ConsolePlus.WriteHr();
            ConsolePlus.Write("[Cyan]#[/] Utilizzo: [Yellow]swiss [Magenta]find [DarkGray]<percorso> <pattern> [opzioni]");
            ConsolePlus.Write("[Cyan]#[/] - percorso: usa . per la cartella corrente oppure definisci un percorso completo");
            ConsolePlus.Write("[Cyan]#[/] - pattern: la stringa da usare per la ricerca, regex di default");
            ConsolePlus.Write("[Cyan]#[/] Opzioni Ricerca:");
            ConsolePlus.Write("[Cyan]#[/] --dirs, -d             : Includi le cartelle nella ricerca");
            ConsolePlus.Write("[Cyan]#[/] --ignore-case, -i      : Rende case insensitive la ricerca");
            ConsolePlus.Write("[Cyan]#[/] --fixed, -f            : non utilizza la regex ma verifica se il pattern è contenuto nel nome file (+ veloce)");
            ConsolePlus.Write("[Cyan]#[/] --since, -s <data>     : trova i file piu recenti di x (d giorni, h ore, m minuti) - es 12d - 12 giorni");
            ConsolePlus.Write("[Cyan]#[/] --older-than,-o <data>: trova i file piu vecchi di x (d giorni, h ore, m minuti) - es 5h - 5 ore");
            ConsolePlus.Write("[Cyan]#[/] Opzioni Classifica:");
            ConsolePlus.Write("[Cyan]#[/] --biggest, -B         : Restituisce i file più grandi");
            ConsolePlus.Write("[Cyan]#[/] --smallest, -S        : Restituisce i file più piccoli");
            ConsolePlus.Write("[Cyan]#[/] --newest, -N          : Restituisce i file più recenti");
            ConsolePlus.Write("[Cyan]#[/] --oldest, -O          : Restituisce i file più vecchi");
            ConsolePlus.Write("[Cyan]#[/] --limit, -l <num> : Limita il numero di risultati nella classifica (default 10)");
            ConsolePlus.Write("[Cyan]#[/] Esempi:");
            ConsolePlus.Write("[Cyan]#[/] - swiss find C:\\Users\\ \".*\\.pdf\"");
            ConsolePlus.Write("[Cyan]#[/] - swiss find . \"\" --biggest --limit 5");
            ConsolePlus.WriteHr();
        }
    }
}