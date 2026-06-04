using System.IO.Enumeration;
using lib.io;
using lib.io.stack;
using lib.utils;
using lib.console;

namespace plugins.tree
{
    #region DTO

    /// <summary>
    /// Plugin per mostrare l'albero delle cartelle filtrato per dimensione minima.
    /// </summary>
    class TreePlugin : Plugin
    {
        public override string Name => "tree";
        public override string Description => "Mostra albero delle cartelle con dimensione > minsize (GB)";

        /// <summary>
        /// Classe di accumulo dei dati. Usa allocazione lazy per i figli e viene passata direttamente alla stampa, senza record intermedi.
        /// </summary>
        private class DirNode
        {
            public string Name { get; set; } = string.Empty;
            public long LocalSize { get; set; }
            public long LocalFilesCount { get; set; }

            public long TotalSize { get; set; }
            public long TotalFiles { get; set; }
            public long TotalDirs { get; set; }

            // uso l'allocazione lazy per abbattere la ram ed evito dizionari interni
            public List<DirNode>? Children { get; set; }
        }

        #endregion

        #region RunAsync

        /// <summary>
        /// Entry point del plugin. Effettua il parsing e avvia la scansione.
        /// </summary>
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

            if (result.Node != null && result.TotalSize > minSizeBytes)
            {
                Console.WriteLine();
                // passo direttamente il nodo grezzo alla stampa, applicando il filtro al volo
                PrintTree(result.Node, "", true, minSizeBytes);
            }
            else
            {
                Console.WriteLine($"\nNessuna cartella supera la soglia di {settings.MinSizeGb:N2} GB (Dimensione totale: {Formatter.Bytes(result.TotalSize)}).");
            }
        }

        #endregion

        #region IO

        /// <summary>
        /// Scansiona l'intero file system in modo asincrono usando FastWalker per massimizzare la lettura disco parallela.
        /// </summary>
        private async Task<(long TotalSize, DirNode? Node)> ScanSystemAsync(string rootPath, long thresholdBytes, CancellationToken ct)
        {
            // normalizzo il percorso radice convertendolo in span, trimmandolo e riallocandolo una sola volta
            rootPath = new string(TrimTrailingSeparator(Path.GetFullPath(rootPath).AsSpan()));
            ReadOnlySpan<char> rootPathSpan = rootPath.AsSpan();

            var nodes = new Dictionary<string, DirNode>(StringComparer.OrdinalIgnoreCase);

            // ottengo l'alternatelookup per cercare nel dizionario usando gli span senza allocare stringhe
            var lookup = nodes.GetAlternateLookup<ReadOnlySpan<char>>();

            // creo il nodo radice
            var rootDirNode = GetOrAddNode(lookup, rootPathSpan, rootPathSpan);

            var options = new FastWalkerOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.None, // leggo tutto quanto
            };

            var channel = FastWalker.Walk<StackFileInfo>(
                rootPath,
                (ref FileSystemEntry entry) => new StackFileInfo(ref entry),
                options,
                ct: ct
            );

            // reader singolo asincrono
            await foreach (var item in channel.ReadAllAsync(ct))
            {
                try
                {
                    // estraggo la cartella direttamente dall'array char in memoria senza mai creare una new string()
                    ReadOnlySpan<char> dirPathSpan = GetNormalizedPathSpan(item, item.IsDirectory);
                    if (dirPathSpan.IsEmpty) continue;

                    if (item.IsDirectory)
                    {
                        // registro la cartella per assicurarmi che i rami vuoti vengano tracciati
                        GetOrAddNode(lookup, dirPathSpan, rootPath.AsSpan());
                    }
                    else
                    {
                        // è un file: cerco o aggiungo il nodo genitore e sommo i parziali
                        var parentNode = GetOrAddNode(lookup, dirPathSpan, rootPath.AsSpan());
                        parentNode.LocalSize += item.Length;
                        parentNode.LocalFilesCount++;
                    }
                }
                catch (Exception)
                {
                    // ignoro spudoratamente gli errori
                }
                finally
                {
                    // rilascio l'arraypool preso da stackfileinfo
                    item.Dispose(); 
                }
            }

            // quando termino la scansione inizio a fare i conti dal basso
            CalculateTotals(rootDirNode);

            // ritorno direttamente l'albero grezzo, senza duplicarlo in record visuali
            return (rootDirNode.TotalSize, rootDirNode);
        }

        #endregion

        #region Calcolo e Stampa

        /// <summary>
        /// Calcola ricorsivamente la dimensione totale dal basso verso l'alto (Bottom-Up).
        /// </summary>
        private void CalculateTotals(DirNode node)
        {
            node.TotalSize = node.LocalSize;
            node.TotalFiles = node.LocalFilesCount;
            node.TotalDirs = node.Children == null ? 0 : node.Children.Count; // directory locali

            if (node.TotalDirs == 0) return;

            foreach (var child in node.Children!)
            {
                // attraversamento in profondità dfs
                CalculateTotals(child); 

                node.TotalSize += child.TotalSize;
                node.TotalFiles += child.TotalFiles;
                node.TotalDirs += child.TotalDirs; // directory figlie ricorsive
            }
        }

        /// <summary>
        /// Stampa l'albero a schermo ed esegue il pruning dei rami in tempo reale ignorando l'ordinamento.
        /// </summary>
        private void PrintTree(DirNode node, string indent, bool isLast, long thresholdBytes)
        {
            // poto il ramo sul nascere se è sotto la soglia, risparmiando cicli cpu
            if (node.TotalSize <= thresholdBytes) return;

            // 1. disegno dell'albero con caratteri unicode continui
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(indent);
            Console.Write(isLast ? "'-- " : "|-- ");

            // 2. nome della cartella in evidenza
            ConsolePlus.Write($"[Green]{node.Name}[/] ", false);

            // 3. formattazione pulita: dimensione in risalto, file/dirs in secondo piano
            string size = Formatter.Bytes(node.TotalSize);
            string fWord = node.TotalFiles == 1 ? "file" : "files";
            string dWord = node.TotalDirs == 1 ? "dir" : "dirs";

            ConsolePlus.Write($"[DarkGray][[/][DarkYellow]{size}[/][DarkGray]] ({node.TotalFiles:n0} {fWord}, {node.TotalDirs:n0} {dWord})[/]");

            // 4. indentazione per i figli usando la barra dritta unicode '│'
            indent += isLast ? "    " : "|   ";

            if (node.Children != null)
            {
                // me ne frego dell'ordinamento per non allocare memoria extra.
                // raccolgo solo i figli validi in una listina per gestire la grafica (saper qual è l'ultimo)
                List<DirNode> validChildren = [];
                for (int i = 0; i < node.Children.Count; i++)
                {
                    if (node.Children[i].TotalSize > thresholdBytes)
                    {
                        validChildren.Add(node.Children[i]);
                    }
                }

                for (int i = 0; i < validChildren.Count; i++)
                {
                    PrintTree(validChildren[i], indent, i == validChildren.Count - 1, thresholdBytes);
                }
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Ottiene un nodo dal dizionario tramite span, oppure lo crea e lo aggancia all'albero allocando stringhe solo in questo caso.
        /// </summary>
        private DirNode GetOrAddNode(
            Dictionary<string, DirNode>.AlternateLookup<ReadOnlySpan<char>> lookup,
            ReadOnlySpan<char> pathSpan,
            ReadOnlySpan<char> rootPathSpan)
        {
            // fast path: se la cartella esiste già ritorno subito, zero allocazioni
            if (lookup.TryGetValue(pathSpan, out var node))
                return node;

            // slow path: prima volta che vedo la cartella, alloco
            string pathStr = new(pathSpan);

            // ottengo il nome del file o cartella tramite span per evitare un'altra stringa
            ReadOnlySpan<char> nameSpan = Path.GetFileName(pathSpan);
            string name = nameSpan.IsEmpty ? pathStr : new string(nameSpan);

            node = new DirNode { Name = name };
            lookup.Dictionary[pathStr] = node; // salvo nel dizionario

            // ricostruisco la gerarchia verso l'alto
            // 1. mi fermo se sono arrivato alla radice della scansione
            if (!pathSpan.Equals(rootPathSpan, StringComparison.OrdinalIgnoreCase))
            {
                // 2. estraggo il percorso padre usando l'api nativa per evitare un loop infinito
                ReadOnlySpan<char> parentSpan = Path.GetDirectoryName(pathSpan);

                if (!parentSpan.IsEmpty)
                {
                    parentSpan = TrimTrailingSeparator(parentSpan);
                    var parentNode = GetOrAddNode(lookup, parentSpan, rootPathSpan);

                    // inizializzo la lista in modo lazy solo se sto aggiungendo il primo figlio
                    parentNode.Children ??= [];
                    parentNode.Children.Add(node);
                }
            }

            return node;
        }

        /// <summary>
        /// Estrae in modo sicuro il path dal record strutturato per passarlo ai metodi.
        /// </summary>
        private ReadOnlySpan<char> GetNormalizedPathSpan(StackFileInfo item, bool isDirectory)
        {
            // estraggo lo span grezzo, poi trimmo lo slash finale senza creare stringhe intermedie
            ReadOnlySpan<char> pathSpan = isDirectory ? item.AsPathSpan() : item.AsDirectorySpan();
            return TrimTrailingSeparator(pathSpan);
        }

        /// <summary>
        /// Rimuove i separatori finali (es. slash) preservando i percorsi di root (es. C:\).
        /// </summary>
        private ReadOnlySpan<char> TrimTrailingSeparator(ReadOnlySpan<char> path)
        {
            // rimuovo lo slash finale in sicurezza preservando le root di sistema usando slicing veloce
            if (path.Length > 3 && (path[^1] == Path.DirectorySeparatorChar || path[^1] == Path.AltDirectorySeparatorChar))
            {
                return path[..^1];
            }
            return path;
        }

        public override void Help()
        {
            PrintHelp<TreeSettings>();
        }

        #endregion
    }
}