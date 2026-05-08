namespace plugins.grep
{
    public class GrepSettings
    {
        // --- ARGOMENTI FISSI ---
        [Fixed(0, "percorso", "La directory di partenza (usa '.' per la cartella corrente)")]
        public string TargetPath { get; set; } = string.Empty;

        [Fixed(1, "pattern", "Le parole da cercare, separate da '|' (es: 'error|warning|fail')")]
        public string Pattern { get; set; } = string.Empty;

        // --- OPZIONI BOOLEANE ---
        [Option("ignore-case|i", "Ricerca case insensitive (ASCII)")]
        public bool IgnoreCase { get; set; }

        // --- OPZIONI CON VALORE (Liste separate da virgola) ---
        [Option("threads|t", "Numero di thread da usare per la ricerca (default: numero di core)")]
        public int Threads { get; set; } = Environment.ProcessorCount;

        [Option("exclude-dir|ex", "Aggiunge cartelle da escludere (separate da virgola)")]
        public string? ExcludeDirs { get; set; }

        [Option("include-dir|in", "Riabilita cartelle escluse di default (separate da virgola)")]
        public string? IncludeDirs { get; set; }

        [Option("glob|g", "Cerca solo nei file che corrispondono al pattern (es. *.cs,*.txt)")]
        public string? Glob { get; set; }
    }
}