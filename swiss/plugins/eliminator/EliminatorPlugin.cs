using System.Buffers;
using System.IO.Enumeration;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using lib.io;
using lib.utils;
using lib.console;
using lib.io.stack;

namespace plugins.eliminator
{
    class EliminatorPlugin : Plugin
    {
        public override string Name => "eliminator";
        public override string Description => "Tool avanzato per la cancellazione e l'archiviazione massiva dei file";

        private string GlobalTrashPath = "";
        private string DriveRoot = "C:\\";

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            if (args.Length < 1)
            {
                Help();
                return;
            }

            // PARSING
            string targetPath = args[0];
            if (targetPath == ".")
            {
                targetPath = Directory.GetCurrentDirectory();
            }
            else if (!Directory.Exists(targetPath))
            {
                Console.WriteLine($"Errore: il percorso \"{targetPath}\" non esiste");
                return;
            }

            ParseArguments(args, 1);

            // flag booleani
            bool isDebug = OptionsContains("--debug", "-d");
            bool isRecursive = OptionsContains("--recursive", "-r");
            // bool targetDirs = OptionsContains("--dirs");
            // filtri opzioni
            var filterOpts = new FileFilterFactory.FilterOptions(
                Pattern: GetOptionValue("--pattern", "-p"),
                MatchType: OptionsContains("--fixed", "-f") ? FilterFileNameMatchType.Fixed : FilterFileNameMatchType.Regex,
                IgnoreCase: OptionsContains("--ignore-case", "-i"),
                ModifiedBefore: GetOptionAge("--since", "-s"),
                ModifiedAfter: GetOptionAge("--older-than", "-o")
            );

            int threadNumber = Environment.ProcessorCount;

            var fileFilter = FileFilterFactory.CreateFilter(filterOpts);

            ConsolePlus.Write($"[Cyan]#[/] Avvio cancellazione ... {(isDebug ? "(DEBUG)" : "")}");

            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = isRecursive,
                BufferSize = 64 * 1024
            };
            // PRODUCER
            IEnumerable<StackFileInfo> itemsToScan = new FileSystemEnumerable<StackFileInfo>(
                targetPath,
                (ref FileSystemEntry entry) => new StackFileInfo(ref entry),
                enumOptions
            )
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    if (fileFilter != null)
                    {
                        return fileFilter(ref entry);
                    }
                    return false;
                }
            };

            var workChannel = Channel.CreateBounded<StackFileInfo>(new BoundedChannelOptions(50000)
            {
                SingleWriter = true,
                SingleReader = false
            });

            var producerTask = Task.Run(async () =>
            {
                try
                {
                    foreach (var item in itemsToScan)
                    {
                        ct.ThrowIfCancellationRequested();
                        await workChannel.Writer.WriteAsync(item, ct);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { PrintError($"\n[Errore I/O]: {ex.Message}"); }
                finally
                {
                    workChannel.Writer.Complete();
                }
            }, ct);
            // CONSUMER
            DriveRoot = Path.GetPathRoot(Path.GetFullPath(targetPath)) ?? "C:\\";
            GlobalTrashPath = Path.Combine(DriveRoot, $".swiss_trash_{Guid.NewGuid()}");
            

            var workers = new Task[threadNumber];
            var processedCountList = new int[threadNumber];
            var actionCountList = new int[threadNumber];
            var bytesSavedList = new int[threadNumber];

            for (int i = 0; i < threadNumber; i++)
            {
                workers[i] = Task.Run(async () =>
                {

                });
            }
            
            // REPORT FINALE
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n\nOperazione Conclusa.");
            Console.WriteLine($"- File analizzati : {processedCount}");
            Console.WriteLine($"- File colpiti    : {actionCount}");
            Console.WriteLine($"- Spazio coinvolto: {Formatter.Bytes(bytesSaved)}");
            Console.ResetColor();
        }

        public override void Help()
        {
            ConsolePlus.WriteHr();
            ConsolePlus.Write("[Cyan]#[/] Uso: [Yellow]swiss [Magenta]eliminator [DarkGray]<percorso> [opzioni]");
            ConsolePlus.Write("[Cyan]#[/] - percorso : usa . per la cartella corrente oppure definisci un percorso completo");
            ConsolePlus.Write("[Cyan]#[/] Opzioni:");
            ConsolePlus.Write("[Cyan]#[/]  --regex <pattern>     : Filtra i file in base a un'espressione regolare sul nome");
            ConsolePlus.Write("[Cyan]#[/]  --ignore-case, -i     : Rende case insensitive la regex");
            ConsolePlus.Write("[Cyan]#[/]  --older-than <giorni> : Colpisce solo i file più vecchi di X giorni");
            ConsolePlus.Write("[Cyan]#[/]  --date-type <m|c|a>   : Tipo di data per --older-than (m=Modifica, c=Creazione, a=Accesso). Default: m");
            ConsolePlus.Write("[Cyan]#[/]  --backup-path <path>  : Invece di eliminare, sposta i file in questa cartella e genera un log CSV");
            ConsolePlus.Write("[Cyan]#[/]  --rollback            : Ripristina i file dalle posizioni di un backup precedente");
            ConsolePlus.Write("[Cyan]#[/]  --debug, -d           : Simula l'operazione senza toccare i file sul disco");
            ConsolePlus.Write("[Cyan]#[/]  --recursive, -r       : Scansiona anche le sottocartelle");
            ConsolePlus.Write("[Cyan]#[/]  --force, -f, -y       : Procedi senza chiedere nessuna conferma di esecuzione");
            ConsolePlus.Write("[Cyan]#[/]  --dirs                : Applica i filtri e le operazioni alle CARTELLE anziché ai file");
            ConsolePlus.Write("[Cyan]#[/]  --parallel, -p        : Esegue l'operazione in multithreading");
            ConsolePlus.Write("[Cyan]#[/]  --threads, -t <num>   : Specifica il numero massimo di thread (default: numero di core della CPU)");
            ConsolePlus.WriteHr();
        }
    }
}