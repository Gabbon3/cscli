using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace plugins.filefinder
{
    class FileFinder : Plugin
    {
        public override string Name => "find";
        public override string Description => "ricerca uno o piu file tramite un pattern regex";
        private readonly Channel<string> _channel;
        // lock per la console per non mescolare colori della stampa MATCH
        private static readonly object _consoleLock = new();

        public FileFinder()
        {
            _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1000)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            if (args.Length < 2 || String.Equals(args[0], "help"))
            {
                Help();
                return;
            }

            string root = args[0];
            string pattern = args[1];

            if (!Directory.Exists(root))
            {
                Console.WriteLine($"Errore: il percorso \"{root}\" non esiste");
                return;
            }

            Console.WriteLine($"Ricerca di \"{pattern}\" in \"{root}\" ");

            // compilo la regex per maggiore efficienza
            var regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

            var producerTask = ProducerAsync(root, ct);

            int workerCount = Math.Max(1, Environment.ProcessorCount - 1);
            var workerTasks = new Task[workerCount];

            for (int i = 0; i < workerCount; i++)
            {
                workerTasks[i] = WorkerAsync(regex, ct);
            }

            await producerTask;

            await Task.WhenAll(workerTasks);
        }

        /// <summary>
        /// Producer scansiona il file system e pusha man mano le cartelle da far analizzare ai worker
        /// </summary>
        /// <param name="root">path da cui iniziare la scansione</param>
        private async Task ProducerAsync(string root, CancellationToken ct)
        {
            try
            {
                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    BufferSize = 65536
                };
                foreach (var file in Directory.EnumerateFiles(root, "*", options))
                {
                    if (ct.IsCancellationRequested) break;
                    await _channel.Writer.WriteAsync(file);
                }
            }
            catch (OperationCanceledException)
            {
                // Operazione annullata dall'utente
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Errore del producer: ${ex.Message}");
            }
            finally
            {
                _channel.Writer.Complete();
            }
        }

        private async Task WorkerAsync(Regex regex, CancellationToken ct)
        {
            try
            {
                await foreach (var file in _channel.Reader.ReadAllAsync(ct))
                {
                    if (regex.IsMatch(Path.GetFileName(file)))
                    {
                        PrintMatch(file);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // operazione annullata dall utente
            }
            catch (Exception ex)
            {
                PrintError(ex.Message);
            }
        }

        public override void Help()
        {
            Console.WriteLine("--------------------------------");
            Console.WriteLine("Come utilizzare il comando find:");
            Console.WriteLine("swiss find percorso nome_file");
            Console.WriteLine(" - percorso -> path da cui iniziare la ricerca ricorsiva");
            Console.WriteLine(" - nome_file -> pattern regex del nome file da ricercare");
            Console.WriteLine("--------------------------------");
        }
        private static void PrintMatch(string fileName)
        {
            lock (_consoleLock)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"{fileName}");
                Console.ResetColor();
            }
        }
    }
}