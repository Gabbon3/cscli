using System.IO.Enumeration;
using System.Threading.Channels;

namespace stack
{
    public delegate T TransformFileSystemEntry<T>(ref FileSystemEntry entry);

    public static class FastWalker
    {
        /// <summary>
        /// Questo metodo cammina il file system in parallelo in maniera ricorsiva
        /// </summary>
        /// <typeparam name="T">di default i dati vengono scritti sul channel in uscita come FileSystemEntry, qui puoi inserire un'altro oggetto a scelta dove memorizzare le informazioni di ogni file</typeparam>
        /// <param name="rootPath">percorso su cui iniziare il cammino</param>
        /// <param name="transform">metodo di trasformazione da FileSystemEntry a T (oggetto scelto per il channel in uscita)</param>
        /// <param name="options">opzioni di configurazione del FastWalker</param>
        /// <param name="ct">token di cancellazione dell'operazione</param>
        /// <returns>channel dove verranno lanciati tutti i risultati trovati nel cammino</returns>
        public static ChannelReader<T> Walk<T>(
            string rootPath,
            TransformFileSystemEntry<T> transform,
            FastWalkerOptions? options = null,
            CancellationToken ct = default)
        {
            options ??= new FastWalkerOptions();

            // # THREADS
            int threads = 0;
            threads = options.MaxDegreeOfParallelism > 0 ? options.MaxDegreeOfParallelism : Environment.ProcessorCount;
            // se non si vuole la ricorsione (dunque si analizza solo la cartella root)
            // utilizzo un solo thread
            if (options.RecurseSubdirectories == false)
            {
                threads = 1;
            }
            else
            {
                // se è definito il numero di threads uso quello se no uso il numero di processori del pc disponibili
                threads = options.MaxDegreeOfParallelism > 0
                    ? options.MaxDegreeOfParallelism
                    : Environment.ProcessorCount;
            }

            // # CANALE DIRECTORY
            // dirChannel rappresenta la coda delle cartelle "da esaminare"
            var dirChannel = Channel.CreateUnbounded<string>();
            // outputChannel è il canale dove coinfluiranno tutti i risultati "in uscita" pronti da far usare all'esterno
            var outputChannel = Channel.CreateBounded<T>(new BoundedChannelOptions(50000)
            {
                SingleWriter = false,
                SingleReader = options.SingleReader
            });
            // pending work rappresenta il WaitGroup che quando arriverà a 0 farà chiudere il canale
            int pendingWork = 1;
            // metto la root nel canale
            dirChannel.Writer.TryWrite(rootPath);

            // # ENUMOPTIONS LOCALI (del thread)
            // opzioni locali per l'enumerazione dei file delle singole cartelle
            var localOptions = new EnumerationOptions
            {
                IgnoreInaccessible = options.IgnoreInaccessible,
                RecurseSubdirectories = false, // ricorsione manuale gestita gia a livello superiore, sempre false per i thread
                BufferSize = options.BufferSize,
                AttributesToSkip = options.AttributesToSkip,
                MatchCasing = options.MatchCasing,
                MatchType = options.MatchType,
                ReturnSpecialDirectories = false
            };
            // avvio degli operai
            for (int i = 0; i < threads; i++)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        // pesco all'infinito finche il canale non viene chiuso
                        await foreach (var currentDir in dirChannel.Reader.ReadAllAsync(ct))
                        {
                            try
                            {
                                var enumerable = new FileSystemEnumerable<T>(
                                    currentDir,
                                    (ref FileSystemEntry entry) => transform(ref entry),
                                    localOptions
                                )
                                {
                                    ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                                    {
                                        if (entry.IsDirectory)
                                        {
                                            if (options.RecurseSubdirectories)
                                            {
                                                Interlocked.Increment(ref pendingWork);
                                                dirChannel.Writer.TryWrite(entry.ToFullPath());
                                            }
                                            return options.ReturnDirectoriesInOutput;
                                        }
                                        return true;
                                    }
                                };

                                // implemento a mano l'enumerazione per lavorare sulle scritture asincrone
                                using var enumerator = enumerable.GetEnumerator();
                                while (enumerator.MoveNext())
                                {
                                    ct.ThrowIfCancellationRequested();
                                    // scriviamo tutti i file nel channel output
                                    await outputChannel.Writer.WriteAsync(enumerator.Current, ct);
                                }
                            }
                            catch (UnauthorizedAccessException) { /* Ignoriamo cartelle senza permessi */ }
                            catch (DirectoryNotFoundException) { /* Cartella sparita nel mentre */ }
                            catch (Exception) { /* Gesti altri errori di I/O senza crashare */ }
                            finally
                            {
                                // l'operaio ha derminato e decrementa waitgroup, se = 0 chiude i channel
                                if (Interlocked.Decrement(ref pendingWork) == 0)
                                {
                                    dirChannel.Writer.TryComplete();
                                    outputChannel.Writer.TryComplete();
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                }, ct);
            }
            // restituisco appena avvio il channel
            return outputChannel.Reader;
        }
    }
}