using System.Collections.Concurrent;

namespace plugins.tree
{
    // Record per l'albero visuale (contiene solo i dati da stampare)
    record DirectoryNode(string Name, long SizeBytes, long NumFiles, long NumSubDirs, List<DirectoryNode> Children);

    class TreePlugin : Plugin
    {
        public override string Name => "tree";
        public override string Description => "Mostra albero delle cartelle con dimensione > minsize (GB)";

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            if (args.Length < 2)
            {
                Help();
                return;
            }

            string rootPath = args[0];
            
            if (!double.TryParse(args[1], out double minSizeGb))
            {
                PrintError("Il valore minsize deve essere un numero valido.");
                return;
            }

            if (!Directory.Exists(rootPath))
            {
                PrintError($"Il percorso \"{rootPath}\" non esiste");
                return;
            }

            // Calcolo soglia in byte (usiamo long per precisione)
            long minSizeBytes = (long)(minSizeGb * 1024 * 1024 * 1024);

            Console.WriteLine($"Analisi di \"{rootPath}\" (Filtro: > {minSizeGb:N2} GB)...");
            
            // Avviamo la scansione
            // Nota: Non ci serve il Size totale qui fuori, ci serve solo il Nodo radice se valido
            var result = await ScanNodeAsync(new DirectoryInfo(rootPath), minSizeBytes, ct);

            if (result.Node != null)
            {
                Console.WriteLine();
                PrintTree(result.Node, "", true);
            }
            else
            {
                Console.WriteLine($"\nNessuna cartella supera la soglia di {minSizeGb} GB (Dimensione totale: {FormatSize(result.TotalSize)}).");
            }
        }

        private async Task<(long TotalSize, long TotalFiles, long TotalSubDirs, DirectoryNode? Node)> ScanNodeAsync(DirectoryInfo dirInfo, long threshold, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            long myFilesSize = 0;
            long myNumFiles = 0;
            long myNumSubDirs = 0;
            
            // 1. Calcolo dimensione file locali (veloce, thread corrente)
            try
            {
                foreach (var file in dirInfo.EnumerateFiles())
                {
                    myNumFiles++;
                    myFilesSize += file.Length;
                }
            }
            catch (UnauthorizedAccessException) { /* Ignoriamo errori di accesso */ }
            catch (Exception) { /* Ignoriamo altri errori file */ }

            long totalChildrenSize = 0;
            long totalChildFiles = 0;
            long totalChildSubDirs = 0;
            var validChildrenNodes = new List<DirectoryNode>();
            
            try
            {
                var subDirs = dirInfo.EnumerateDirectories();
                var tasks = new List<Task<(long, long, long, DirectoryNode?)>>();

                foreach (var subDir in subDirs)
                {
                    myNumSubDirs++;
                    tasks.Add(Task.Run(() => ScanNodeAsync(subDir, threshold, ct), ct));
                }

                var results = await Task.WhenAll(tasks);

                foreach (var (childSize, childFiles, childSubDirs, childNode) in results)
                {
                    totalChildrenSize += childSize;
                    totalChildFiles += childFiles;
                    totalChildSubDirs += childSubDirs;

                    if (childNode != null)
                    {
                        validChildrenNodes.Add(childNode);
                    }
                }
            }
            catch (UnauthorizedAccessException) { /* Accesso negato alla cartella */ }
            catch (Exception ex)
            {
                PrintError($"Errore lettura {dirInfo.Name}: {ex.Message}");
            }

            long totalSize = myFilesSize + totalChildrenSize;
            long totalFiles = myNumFiles + totalChildFiles;
            long totalSubDirs = myNumSubDirs + totalChildSubDirs;

            if (totalSize > threshold)
            {
                var sortedChildren = validChildrenNodes.OrderByDescending(x => x.SizeBytes).ToList();                
                var node = new DirectoryNode(dirInfo.Name, totalSize, totalFiles, totalSubDirs, sortedChildren);
                return (totalSize, totalFiles, totalSubDirs, node);
            }

            return (totalSize, totalFiles, totalSubDirs, null);
        }

        private void PrintTree(DirectoryNode node, string indent, bool isLast)
        {
            Console.Write(indent);
            Console.Write(isLast ? "└── " : "├── ");
            
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(node.Name);
            Console.ResetColor();
            
            Console.Write($" (F: {node.NumFiles:n0} - D: {node.NumSubDirs:n0} - ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write(FormatSize(node.SizeBytes));
            Console.ResetColor();
            Console.WriteLine(")");

            indent += isLast ? "    " : "│   ";

            for (int i = 0; i < node.Children.Count; i++)
            {
                PrintTree(node.Children[i], indent, i == node.Children.Count - 1);
            }
        }

        private string FormatSize(long bytes)
        {
            double gb = bytes / (1024.0 * 1024.0 * 1024.0);
            if (gb >= 1) return $"{gb:N2} GB";
            
            double mb = bytes / (1024.0 * 1024.0);
            return $"{mb:N0} MB";
        }

        public override void Help()
        {
            Console.WriteLine("------------------------------------------------");
            Console.WriteLine("Utilizzo comando tree:");
            Console.WriteLine("swiss tree <root_path> <min_size_gb>");
            Console.WriteLine("Esempio: swiss tree C:\\Users 1.5");
            Console.WriteLine("Mostra la struttura delle cartelle che superano 1.5 GB");
            Console.WriteLine("------------------------------------------------");
        }
    }
}