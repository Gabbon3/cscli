namespace plugins.eliminator
{
    public class EliminatorSettings
    {
        // --- ARGOMENTI FISSI ---
        [Fixed(0, "percorso", "Il percorso target da cui avviare la cancellazione (usa '.' per la cartella corrente)")]
        public string TargetPath { get; set; } = string.Empty;

        // --- opzioni del comando ---

        [Option("debug|d", "Simula l'operazione senza toccare i file sul disco", "Comando")]
        public bool Debug { get; set; }

        [Option("recursive|r", "Scansiona anche le sottocartelle", "Comando")]
        public bool Recursive { get; set; }

        [Option("drop-source|ds", "Cancella al termine anche la cartella target", "Comando")]
        public bool DropSource { get; set; } = false;

        [Option(CliMeta.SilenceFlag, CliMeta.SilenceDesc, "Comando")]
        public bool Silence { get; set; } = false;

        [Option("ignore-errors|ie", "Se attivo ignora gli errori di cancellazione dei file", "Comando")]
        public bool IgnoreErrors { get; set; } = false;

        [Option("threads|t", "Specifica il numero massimo di thread (default: numero di core della CPU)", "Comando")]
        public int? Threads { get; set; }

        // --- opzioni di filtraggio ---

        [Option("pattern|p", "Filtra i file in base a un'espressione regolare sul nome", "Filtri")]
        public string? Pattern { get; set; }

        [Option("fixed|f", "Usa il pattern come stringa esatta invece che come espressione regolare", "Filtri")]
        public bool FixedMatch { get; set; }

        [Option("ignore-case|i", "Rende la ricerca case-insensitive", "Filtri")]
        public bool IgnoreCase { get; set; }

        [Option(CliMeta.HiddenFlag, CliMeta.HiddenDesc, "Filtri")]
        public bool IncludeHidden { get; set; } = false;
        

        [Option(CliMeta.SinceFlag, CliMeta.SinceDesc, "Filtri")]
        public DateTime? Since { get; set; }

        [Option(CliMeta.OlderThanFlag, CliMeta.OlderThanDesc, "Filtri")]
        public DateTime? OlderThan { get; set; }
    }
}