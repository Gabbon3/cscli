using System.IO.Enumeration;
using System.Threading.Channels;
using lib.io;
using lib.io.stack;

namespace lib.core.eliminator
{
    public class EliminatorCore
    {
        /// <summary>
        /// Esegue l'eliminazione in parallelo di files
        /// </summary>
        /// <param name="targetPath">percorso base da verificare per la cancellazione</param>
        /// <param name="threadNumber">numero di thread dedicati alla cancellazione</param>
        /// <param name="dropInstant">se true, cancella subito il file anziche usare il trick della move (utile su percorsi di rete)</param>
        /// <param name="isRecursive">se true, la cancellazione viene fatta su tutte le sottocartelle</param>
        /// <param name="filterOpts">opzioni per il filtraggio dei files da eliminare</param>
        /// <param name="progress">serve per la UI per ricevere un feedback continuo dal motore</param>
        /// <param name="ct">token di cancellazione per fermare correttamente l'elaborazione</param>
        /// <returns></returns>
        public async Task<EliminatorResult> RunAsync(
            string targetPath, 
            int threadNumber, 
            bool dropInstant, 
            bool isRecursive,
            FileFilterFactory.FilterOptions filterOpts,
            IProgress<EliminatorProgressReport>? progress, 
            CancellationToken ct)
        {
            // creo il filtro
            var fileFilter = FileFilterFactory.CreateFilter(filterOpts);
            var enumOptions = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = isRecursive, BufferSize = 64 * 1024 };
            // lavoro sullo stesso disco del target da pulire per massima velocità di eliminazioni e move
            string DriveRoot = Path.GetPathRoot(Path.GetFullPath(targetPath)) ?? "C:\\";
            string GlobalTrashPath = Path.Combine(DriveRoot, $".swiss_trash_{Guid.NewGuid()}");

            // preparazione cestino (Se fallisce, lancia l'eccezione alla UI)
            if (!dropInstant)
            {
                Directory.CreateDirectory(GlobalTrashPath);
            }

            // --- PRODUCER ---
            IEnumerable<StackFileInfo> itemsToScan = new FileSystemEnumerable<StackFileInfo>(
                targetPath,
                (ref FileSystemEntry entry) => new StackFileInfo(ref entry),
                enumOptions
            )
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    if (entry.IsDirectory) return false;
                    if (fileFilter != null) return fileFilter(ref entry);
                    return true;
                }
            };

            var workChannel = Channel.CreateBounded<StackFileInfo>(new BoundedChannelOptions(50000) { SingleWriter = true, SingleReader = false });

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
                finally { workChannel.Writer.Complete(); }
            }, ct);

            // --- CONSUMER ---
            var workers = new Task[threadNumber];
            long totalBytesSavedGlobal = 0;
            long totalFilesDroppedGlobal = 0;

            for (int i = 0; i < threadNumber; i++)
            {
                int workerId = i;
                workers[i] = Task.Run(async () =>
                {
                    List<Task> backgroundWorkerDrops = [];
                    int batchId = 0;
                    string workerRoot = Path.Combine(GlobalTrashPath, workerId.ToString());
                    string currentBatchPath = Path.Combine(workerRoot, batchId.ToString());
                    
                    if (!dropInstant)
                    {
                        Directory.CreateDirectory(workerRoot);
                        Directory.CreateDirectory(currentBatchPath);
                    }

                    int filesDroppedCounter = 0;
                    long bytesSavedWorker = 0;

                    try
                    {
                        await foreach (var item in workChannel.Reader.ReadAllAsync())
                        {
                            try
                            {
                                filesDroppedCounter++;
                                bytesSavedWorker += item.Length;

                                if (dropInstant)
                                {
                                    NativeIO.DeleteFile(item.GetFullPath());
                                }
                                else
                                {
                                    string destPath = $"{workerRoot}{Path.DirectorySeparatorChar}{filesDroppedCounter}.tmp";
                                    File.Move(item.GetFullPath(), destPath);
                                    
                                    if ((filesDroppedCounter & 4095) == 0)
                                    {
                                        string folderToDrop = currentBatchPath;
                                        backgroundWorkerDrops.Add(Task.Run(() => { try { Directory.Delete(folderToDrop, true); } catch { } }));
                                        batchId++;
                                        currentBatchPath = Path.Combine(workerRoot, batchId.ToString());
                                        Directory.CreateDirectory(currentBatchPath);
                                    }
                                }

                                // Segnala alla UI che abbiamo fatto progressi
                                progress?.Report(new EliminatorProgressReport { WorkerId = workerId, FilesDropped = filesDroppedCounter, BytesSaved = bytesSavedWorker });
                                ct.ThrowIfCancellationRequested();
                            }
                            catch (Exception ex)
                            {
                                // Segnala l'errore alla UI senza eplodere
                                progress?.Report(new EliminatorProgressReport { WorkerId = workerId, Error = ex, FailedFileName = item.AsNameSpan().ToString() });
                            }
                            finally
                            {
                                item.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        await Task.WhenAll(backgroundWorkerDrops);
                        // solo dopo l esecuzione di tutti i task di cancellazione delle working dir 
                        // cancello la working root se non è gia stata cancellata
                        if (Directory.Exists(workerRoot)) { try { Directory.Delete(workerRoot, true); } catch { } }
                        // aggiorno i dati alla fine dell'elaborazione del thread
                        Interlocked.Add(ref totalFilesDroppedGlobal, filesDroppedCounter);
                        Interlocked.Add(ref totalBytesSavedGlobal, bytesSavedWorker);
                    }
                });
            }
            // attendo l'esecuzione di tutti i task
            await Task.WhenAll(workers);
            await producerTask;

            if (Directory.Exists(GlobalTrashPath))
            {
                try { Directory.Delete(GlobalTrashPath, true); } catch { }
            }

            return new EliminatorResult(totalFilesDroppedGlobal, totalBytesSavedGlobal, true);
        }
    }
}