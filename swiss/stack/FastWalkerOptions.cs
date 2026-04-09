namespace stack
{
    public class FastWalkerOptions : EnumerationOptions
    {
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

        public FastWalkerOptions()
        {
            IgnoreInaccessible = true;
            RecurseSubdirectories = true;
            BufferSize = 64 * 1024; // 64 KB
        }
    }
}