namespace plugins.tree
{
    public class TreeSettings
    {
        [Fixed(0, "percorso", "La directory di partenza (usa '.' per la cartella corrente)")]
        public string TargetPath { get; set; } = string.Empty;

        [Fixed(1, "min_size_gb", "Dimensione minima in GB per mostrare la cartella (es. 1.5)")]
        public double MinSizeGb { get; set; }
    }
}