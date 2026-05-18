namespace plugins.regexgrep
{
    public class RegexGrepSettings
    {
        // --- ARGOMENTI FISSI ---
        [Fixed(0, "percorso", "La directory di partenza (usa '.' per la cartella corrente)")]
        public string TargetPath { get; set; } = string.Empty;

        [Fixed(1, "pattern", "Il pattern regex da ricercare")]
        public string Pattern { get; set; } = string.Empty;

        // --- OPZIONI ---

        [Option("silence|s", "Se attivo non mostra i risultati a console", "Configurazione")]
        public bool Silence { get; set; } = false;

        [Option("ignore-case|i", "Regex case insensitive", "Configurazione")]
        public bool IgnoreCase { get; set; }

        [Option("count|c", "Restituisce il numero di match per ogni file", "Configurazione")]
        public bool Count { get; set; }

        [Option("count-min|min", "Se --count attivo, mostra i risultati solo se trova almeno n (valore in input) match", "Configurazione")]
        public int MinCount { get; set; } = 1;

        [Option("count-max|max", "Se --count attivo, mostra i risultati solo se trova al massimo n (valore in input) match", "Configurazione")]
        public int MaxCount { get; set; } = -1;

        [Option("threads|t", "Numero di thread da usare per la ricerca (default: numero di core)", "Configurazione")]
        public int Threads { get; set; } = Environment.ProcessorCount;

        [Option("dir-exclude|ex", "Aggiunge cartelle da escludere (separate da virgola)", "Filtri")]
        public string? ExcludeDirs { get; set; }

        [Option("dir-include|in", "Riabilita cartelle escluse di default (separate da virgola)", "Filtri")]
        public string? IncludeDirs { get; set; }

        [Option("glob|g", "Cerca solo nei file che corrispondono al pattern (es. *.cs,*.txt)", "Filtri")]
        public string? Glob { get; set; }

        [Option("output-file|o", "Indica il percorso del file dove scrivere i risultati del grep", "Output")]
        public string? OutputFile { get; set; }

        [Option("format|f", "Formato di output: console (default), csv, json", "Output")]
        public string? Format { get; set; }
    }
}