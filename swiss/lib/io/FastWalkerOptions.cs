using System.IO.Enumeration;

namespace lib.io
{
    public class FastWalkerOptions : EnumerationOptions
    {
        // Definisco il delegate per gestire il parametro 'ref'
        /// <summary>
        /// Determina se le directory esplorate devono essere restituite nel channel di output.
        /// Se false, verranno restituiti solo i file. Default: true.
        /// </summary>
        public bool ReturnDirectoriesInOutput { get; set; } = true;

        /// <summary>
        /// Numero massimo di thread. Se <= 0 usa Environment.ProcessorCount.
        /// </summary>
        public int MaxDegreeOfParallelism { get; set; } = -1;

        /// <summary>
        /// True se chi legge i risultati è un singolo thread (ottimizza le performance del channel).
        /// </summary>
        public bool SingleReader { get; set; } = true;

        /// <summary>
        /// Filtro personalizzato da applicare ai file. 
        /// Se restituisce false, il file viene scartato prima di generare allocazioni.
        /// </summary>
        public FileSystemFilter? Filter { get; set; }

        /// <summary>
        /// Filtro personalizzato da applicare alle cartelle da escludere durante la ricerca
        /// Se restituisce true, allora la directory verà ignorata per la scansione evitando ricorsione inutilmente
        /// Riutilizzo la stessa struttura dei file poiche FileSystemEntry tratta cartelle e file alla stessa maniera, sono distinguibili solamente grazie alla prop IsDirectory
        /// </summary>
        public FileSystemFilter? DirectoryExcludeFilter { get; set; }

        public FastWalkerOptions()
        {
            IgnoreInaccessible = true;
            RecurseSubdirectories = true;
            BufferSize = 64 * 1024; // 64 KB
        }
    }
}