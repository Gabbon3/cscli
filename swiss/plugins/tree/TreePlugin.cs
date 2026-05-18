using System.IO.Enumeration;
using lib.io;
using lib.io.stack;
using lib.utils;
using lib.console;

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
            var settings = ParseSettings<TreeSettings>(args);
            if (args.Contains("--help") || args.Length < 2)
            {
                Help();
                return;
            }

            string rootPath = ParsePath(settings.TargetPath, true)!;
            long minSizeBytes = (long)(settings.MinSizeGb * 1024 * 1024 * 1024);

            Console.WriteLine($"Analisi di \"{rootPath}\" (Filtro: > {settings.MinSizeGb:N2} GB)...");

            var result = await ScanSystemAsync(rootPath, minSizeBytes, ct);

            if (result.Node != null)
            {
                Console.WriteLine();
                PrintTree(result.Node, "", true);
            }
            else
            {
                Console.WriteLine($"\nNessuna cartella supera la soglia di {settings.MinSizeGb:N2} GB (Dimensione totale: {Formatter.Bytes(result.TotalSize)}).");
            }
        }

        private async Task<(long TotalSize, DirectoryNode? Node)> ScanSystemAsync(string rootPath, long thresholdBytes, CancellationToken ct)
        {
            // normalizzo il percorso radice convertendolo in Span, trimmandolo e riallocandolo (1 sola volta)
            rootPath = new string(TrimTrailingSeparator(Path.GetFullPath(rootPath).AsSpan()));
            ReadOnlySpan<char> rootPathSpan = rootPath.AsSpan();

            var nodes = new Dictionary<string, DirNode>(StringComparer.OrdinalIgnoreCase);

            // Ottengo l'AlternateLookup per cercare nel dizionario usando gli Span
            var lookup = nodes.GetAlternateLookup<ReadOnlySpan<char>>();

            // creo il nodo radice
            var rootDirNode = GetOrAddNode(lookup, rootPathSpan, rootPathSpan);

            var options = new FastWalkerOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            };

            var channel = FastWalker.Walk<StackFileInfo>(
                rootPath,
                (ref FileSystemEntry entry) => new StackFileInfo(ref entry),
                options,
                ct: ct
            );

            // Il Reader è singolo, quindi non abbiamo bisogno di lock o collezioni concorrenti sul dizionario
            await foreach (var item in channel.ReadAllAsync(ct))
            {
                try
                {
                    // Estraiamo la cartella direttamente dall'array char in memoria senza MAI creare una new string()
                    ReadOnlySpan<char> dirPathSpan = GetNormalizedPathSpan(item, item.IsDirectory);
                    if (dirPathSpan.IsEmpty) continue;

                    if (item.IsDirectory)
                    {
                        // Registra la cartella per assicurarsi che i rami vuoti vengano tracciati
                        GetOrAddNode(lookup, dirPathSpan, rootPath.AsSpan());
                    }
                    else
                    {
                        // È un file: cerchiamo o aggiungiamo il nodo genitore
                        var parentNode = GetOrAddNode(lookup, dirPathSpan, rootPath.AsSpan());
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
                    // Nota: Se modifichi StackFileInfo in futuro con ReleaseBuffer(), aggiorna questa chiamata.
                    item.Dispose();
                }
            }

            // A scansione terminata, propaghiamo le dimensioni dal basso verso l'alto
            CalculateTotals(rootDirNode);

            // Costruiamo e filtriamo l'albero visuale da ritornare
            var finalTree = BuildFilteredTree(rootDirNode, thresholdBytes);
            return (rootDirNode.TotalSize, finalTree);
        }

        // Metodo aggiornato per ricevere l'AlternateLookup e gli Span
        private DirNode GetOrAddNode(
            Dictionary<string, DirNode>.AlternateLookup<ReadOnlySpan<char>> lookup,
            ReadOnlySpan<char> pathSpan,
            ReadOnlySpan<char> rootPathSpan)
        {
            // FAST PATH: Se la cartella esiste già, O(1) e 0 allocazioni
            if (lookup.TryGetValue(pathSpan, out var node))
                return node;

            // SLOW PATH: Prima volta che vediamo la cartella.
            string pathStr = new string(pathSpan);

            // Otteniamo il nome del file o cartella tramite Span (100% safe, 0 alloc)
            ReadOnlySpan<char> nameSpan = Path.GetFileName(pathSpan);
            string name = nameSpan.IsEmpty ? pathStr : new string(nameSpan);

            node = new DirNode { Name = name };
            lookup.Dictionary[pathStr] = node; // Salviamo

            // Ricostruiamo la gerarchia verso l'alto
            if (!pathSpan.Equals(rootPathSpan, StringComparison.OrdinalIgnoreCase))
            {
                // Usiamo l'API nativa per estrarre la cartella padre (es. "C:\Windows" -> "C:\") 0 alloc!
                ReadOnlySpan<char> parentSpan = Path.GetDirectoryName(pathSpan);

                if (!parentSpan.IsEmpty)
                {
                    parentSpan = TrimTrailingSeparator(parentSpan);

                    var parentNode = GetOrAddNode(lookup, parentSpan, rootPathSpan);
                    parentNode.Children[pathStr] = node; // Agganciamo il figlio!
                }
            }

            return node;
        }

        private ReadOnlySpan<char> GetNormalizedPathSpan(StackFileInfo item, bool isDirectory)
        {
            // Estrae lo span grezzo, poi trimma lo slash finale senza creare stringhe intermedie
            ReadOnlySpan<char> pathSpan = isDirectory ? item.AsPathSpan() : item.AsDirectorySpan();
            return TrimTrailingSeparator(pathSpan);
        }

        private ReadOnlySpan<char> TrimTrailingSeparator(ReadOnlySpan<char> path)
        {
            // Rimuove lo slash finale in sicurezza preservando le root di sistema usando slicing veloce
            if (path.Length > 3 && (path[^1] == Path.DirectorySeparatorChar || path[^1] == Path.AltDirectorySeparatorChar))
            {
                return path[..^1];
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
            // 1. Disegno dell'albero con caratteri Unicode continui
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(indent);
            Console.Write(isLast ? "'-- " : "|-- ");

            // 2. Nome della cartella in evidenza
            ConsolePlus.Write($"[DarkGreen]{node.Name}[/] ", false);

            // 3. Formattazione pulita: Dimensione in risalto, file/dirs in secondo piano
            string size = Formatter.Bytes(node.SizeBytes);
            string fWord = node.NumFiles == 1 ? "file" : "files";
            string dWord = node.NumSubDirs == 1 ? "dir" : "dirs";

            // Esempio output riga: [ 84.37 GB ] (596 files, 12 dirs)
            ConsolePlus.Write($"[DarkGray][[/][DarkYellow]{size}[/][DarkGray]] ({node.NumFiles:n0} {fWord}, {node.NumSubDirs:n0} {dWord})[/]");

            // 4. Indentazione per i figli usando la barra dritta Unicode '│'
            indent += isLast ? "    " : "|   ";

            for (int i = 0; i < node.Children.Count; i++)
            {
                PrintTree(node.Children[i], indent, i == node.Children.Count - 1);
            }
        }

        public override void Help()
        {
            PrintHelp<TreeSettings>();
        }
    }
}