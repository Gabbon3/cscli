using System.IO.Enumeration;
using stack;
using utils;

namespace plugins.tree
{
    // Record per l'albero visuale (contiene solo i dati da stampare)
    record DirectoryNode(string Name, long SizeBytes, long NumFiles, long NumSubDirs, List<DirectoryNode> Children);

    class TreePlugin : Plugin
    {
        public override string Name => "tree";
        public override string Description => "Mostra albero delle cartelle con dimensione > minsize (GB)";

        // Classe di supporto interna per l'accumulo dei dati senza impattare record immutabili
        private class DirNode
        {
            public string Name { get; set; } = string.Empty;
            public long LocalSize { get; set; }
            public long LocalFilesCount { get; set; }

            public long TotalSize { get; set; }
            public long TotalFiles { get; set; }
            public long TotalDirs { get; set; }

            // Usiamo StringComparer.OrdinalIgnoreCase per i percorsi Windows
            public Dictionary<string, DirNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            if (args.Length < 2)
            {
                Help();
                return;
            }

            string rootPath = args[0];

            if (rootPath == ".")
            {
                rootPath = Directory.GetCurrentDirectory();
            }
            else if (!Directory.Exists(rootPath))
            {
                PrintError($"il percorso \"{rootPath}\" non esiste");
                return;
            }

            if (!double.TryParse(args[1], out double minSizeGb))
            {
                PrintError("Il valore minsize deve essere un numero valido.");
                return;
            }

            long minSizeBytes = (long)(minSizeGb * 1024 * 1024 * 1024);

            Console.WriteLine($"Analisi di \"{rootPath}\" (Filtro: > {minSizeGb:N2} GB)...");

            var result = await ScanSystemAsync(rootPath, minSizeBytes, ct);

            if (result.Node != null)
            {
                Console.WriteLine();
                PrintTree(result.Node, "", true);
            }
            else
            {
                Console.WriteLine($"\nNessuna cartella supera la soglia di {minSizeGb:N2} GB (Dimensione totale: {Formatter.Bytes(result.TotalSize)}).");
            }
        }

        private async Task<(long TotalSize, DirectoryNode? Node)> ScanSystemAsync(string rootPath, long thresholdBytes, CancellationToken ct)
        {
            // Normalizziamo il percorso radice per evitare discrepanze (es. slash finali mancanti o in eccesso)
            rootPath = TrimTrailingSeparator(Path.GetFullPath(rootPath));

            var nodes = new Dictionary<string, DirNode>(StringComparer.OrdinalIgnoreCase);
            var rootDirNode = GetOrAddNode(nodes, rootPath, rootPath);

            // Importante: RecurseSubdirectories deve essere true per innescare lo ShouldIncludePredicate in FastWalker
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            };

            var channel = FastWalker.Walk<StackFileInfo>(
                rootPath,
                options,
                (ref FileSystemEntry entry) => new StackFileInfo(ref entry),
                maxDegreeOfParallelism: -1,
                SingleReader: true,
                ct: ct
            );

            // Il Reader è singolo, quindi non abbiamo bisogno di lock o collezioni concorrenti sul dizionario
            await foreach (var item in channel.ReadAllAsync(ct))
            {
                try
                {
                    string dirPath = GetNormalizedPath(item, item.IsDirectory);
                    if (string.IsNullOrEmpty(dirPath)) continue;

                    if (item.IsDirectory)
                    {
                        // Registra la cartella per assicurarsi che i rami vuoti vengano tracciati
                        GetOrAddNode(nodes, dirPath, rootPath);
                    }
                    else
                    {
                        // È un file: aggiungiamo i dati al suo nodo genitore
                        var parentNode = GetOrAddNode(nodes, dirPath, rootPath);
                        parentNode.LocalSize += item.Length;
                        parentNode.LocalFilesCount++;
                    }
                }
                catch (Exception)
                {
                    /* Ignoriamo problemi su file specifici per non arrestare lo stream */
                }
                finally
                {
                    item.Dispose();
                }
            }

            // A scansione terminata, propaghiamo le dimensioni dal basso verso l'alto
            CalculateTotals(rootDirNode);

            // Costruiamo e filtriamo l'albero visuale da ritornare
            var finalTree = BuildFilteredTree(rootDirNode, thresholdBytes);
            return (rootDirNode.TotalSize, finalTree);
        }

        private DirNode GetOrAddNode(Dictionary<string, DirNode> dict, string path, string rootPath)
        {
            // Se esiste già, lo restituiamo all'istante
            if (dict.TryGetValue(path, out var node))
                return node;

            node = new DirNode { Name = Path.GetFileName(path) };
            if (string.IsNullOrEmpty(node.Name)) node.Name = path; // Fallback per drive root (es. "C:\")

            dict[path] = node;

            // Ricostruiamo la gerarchia verso l'alto fino a collegarci alla radice.
            // Gestisce perfettamente l'arrivo fuori ordine dai thread paralleli.
            if (!path.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
            {
                string? parentPath = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parentPath))
                {
                    parentPath = TrimTrailingSeparator(parentPath);
                    var parentNode = GetOrAddNode(dict, parentPath, rootPath);
                    parentNode.Children[path] = node;
                }
            }

            return node;
        }

        private string GetNormalizedPath(StackFileInfo item, bool isDirectory)
        {
            if (isDirectory)
            {
                return TrimTrailingSeparator(item.GetFullPath());
            }
            else
            {
                // Estrarre solo la cartella genitrice di un file minimizza enormemente le allocazioni di stringhe
                int dirLen = item.PathLength - item.NameLength - 1;
                if (dirLen <= 0) return string.Empty;

                string parentDir = new string(item.PathBuffer, 0, dirLen);
                return TrimTrailingSeparator(parentDir);
            }
        }

        private string TrimTrailingSeparator(string path)
        {
            // Rimuove lo slash finale in sicurezza preservando le root di sistema (C:\, /)
            if (path.Length > 3 && (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)))
            {
                return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            return path;
        }

        private void CalculateTotals(DirNode node)
        {
            node.TotalSize = node.LocalSize;
            node.TotalFiles = node.LocalFilesCount;
            node.TotalDirs = node.Children.Count; // Directory locali

            foreach (var child in node.Children.Values)
            {
                CalculateTotals(child); // Attraversamento in profondità

                node.TotalSize += child.TotalSize;
                node.TotalFiles += child.TotalFiles;
                node.TotalDirs += child.TotalDirs; // Directory figlie ricorsive
            }
        }

        private DirectoryNode? BuildFilteredTree(DirNode node, long thresholdBytes)
        {
            if (node.TotalSize <= thresholdBytes)
                return null; // Taglia l'intero ramo se sotto la soglia

            var validChildren = new List<DirectoryNode>();
            foreach (var child in node.Children.Values)
            {
                var childRecord = BuildFilteredTree(child, thresholdBytes);
                if (childRecord != null)
                {
                    validChildren.Add(childRecord);
                }
            }

            validChildren = validChildren.OrderByDescending(c => c.SizeBytes).ToList();

            return new DirectoryNode(
                node.Name,
                node.TotalSize,
                node.TotalFiles,
                node.TotalDirs,
                validChildren);
        }

        private void PrintTree(DirectoryNode node, string indent, bool isLast)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(indent);
            Console.Write(isLast ? "└── " : "├── ");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(node.Name);
            Console.ResetColor();

            ConsolePlus.Write($" ([Yellow]{node.NumFiles:n0}[/] - [Blue]{node.NumSubDirs:n0}[/] - [Magenta]{Formatter.Bytes(node.SizeBytes)}[/])");

            indent += isLast ? "    " : "│   ";

            for (int i = 0; i < node.Children.Count; i++)
            {
                PrintTree(node.Children[i], indent, i == node.Children.Count - 1);
            }
        }

        public override void Help()
        {
            Console.WriteLine("------------------------------------------------");
            Console.WriteLine("Utilizzo comando tree:");
            Console.WriteLine("swiss tree <root_path> <min_size_gb>");
            Console.WriteLine("Esempio: swiss tree C:\\Users 1.5");
            Console.WriteLine("Mostra la struttura delle cartelle che superano 1.5 GB");
            Console.WriteLine("Ogni record contiene il nome della cartella seguito da (numero files, numero cartelle, peso)");
            Console.WriteLine("------------------------------------------------");
        }
    }
}