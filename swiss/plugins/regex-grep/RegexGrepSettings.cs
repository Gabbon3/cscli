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

        // configurazione

        [Option(CliMeta.SilenceFlag, CliMeta.SilenceDesc, "Configurazione")]
        public bool Silence { get; set; } = false;

        [Option("ignore-case|i", "Regex case insensitive", "Configurazione")]
        public bool IgnoreCase { get; set; }

        [Option("recurse|r", "Se attivo ricerca anche nelle sottocartelle", "Configurazione")]
        public bool RecurseSubdirectories { get; set; } = false;
        [Option(CliMeta.HiddenFlag, CliMeta.HiddenDesc, "Configurazione")]
        public bool IncludeHidden { get; set; } = false;

        [Option("fixed|f", "Disabilita la regex, cerca direttamente la stringa", "Configurazione")]
        public bool FixedPattern { get; set; }

        [Option("count|c", "Restituisce il numero di match per ogni file", "Configurazione")]
        public bool Count { get; set; }

        [Option("count-min|min", "Se --count attivo, mostra i risultati solo se trova almeno n (valore in input) match", "Configurazione")]
        public int MinCount { get; set; } = 1;

        [Option("count-max|max", "Se --count attivo, mostra i risultati solo se trova al massimo n (valore in input) match", "Configurazione")]
        public int MaxCount { get; set; } = -1;

        [Option(CliMeta.ThreadsFlag, CliMeta.ThreadsDesc, "Configurazione")]
        public int Threads { get; set; } = Environment.ProcessorCount;

        // filtri

        [Option("dir-exclude|ex", "Aggiunge cartelle da escludere (separate da virgola)", "Filtri")]
        public string? ExcludeDirs { get; set; }

        [Option("dir-include|in", "Riabilita cartelle escluse di default (separate da virgola)", "Filtri")]
        public string? IncludeDirs { get; set; }

        [Option("glob|g", "Cerca solo nei file che corrispondono al pattern (es. *.cs,*.txt)", "Filtri")]
        public string? Glob { get; set; }

        [Option(CliMeta.SinceFlag, CliMeta.SinceDesc, "Filtri")]
        public DateTime? Since { get; set; } 

        [Option(CliMeta.OlderThanFlag, CliMeta.OlderThanDesc, "Filtri")]
        public DateTime? OlderThan { get; set; }

        // output

        [Option(CliMeta.FormatFlag, CliMeta.FormatDesc, "Output")]
        public string? Format { get; set; }

        [Option(CliMeta.OutputFileFlag, CliMeta.OutputFileDesc, "Output")]
        public string? OutputFile { get; set; }
    }
}