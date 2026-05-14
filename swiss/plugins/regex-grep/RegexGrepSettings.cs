namespace plugins.regexgrep
{
    public class RegexGrepSettings
    {
        // --- ARGOMENTI FISSI ---
        [Fixed(0, "percorso", "La directory di partenza (usa '.' per la cartella corrente)")]
        public string TargetPath { get; set; } = string.Empty;

        [Fixed(1, "pattern", "Il pattern regex da ricercare")]
        public string Pattern { get; set; } = string.Empty;

        [Option("silence|s", "Se attivo non mostra i risultati a console")]
        public bool Silence { get; set; } = false;

        [Option("ignore-case|i", "Ricerca case insensitive (ASCII)")]
        public bool IgnoreCase { get; set; }

        [Option("count|c", "Restituisce il numero di match per ogni file")]
        public bool Count { get; set; }

        [Option("min-count|min", "Se --count attivo, mostra i risultati solo se trova almeno min-count match")]
        public int MinCount { get; set; } = 1;

        [Option("max-count|max", "Se --count attivo, mostra i risultati solo se trova al massimo max-count match")]
        public int MaxCount { get; set; } = -1;
        
        [Option("threads|t", "Numero di thread da usare per la ricerca (default: numero di core)")]
        public int Threads { get; set; } = Environment.ProcessorCount;

        [Option("exclude-dir|ex", "Aggiunge cartelle da escludere (separate da virgola)")]
        public string? ExcludeDirs { get; set; }

        [Option("include-dir|in", "Riabilita cartelle escluse di default (separate da virgola)")]
        public string? IncludeDirs { get; set; }

        [Option("glob|g", "Cerca solo nei file che corrispondono al pattern (es. *.cs,*.txt)")]
        public string? Glob { get; set; }

        [Option("output-file|o", "Indica il percorso del file dove scrivere i risultati del grep")]
        public string? OutputFile { get; set; }
    }
}