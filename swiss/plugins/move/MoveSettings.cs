namespace plugins.move
{
    public class MoveSettings
    {
        // --- ARGOMENTI FISSI ---
        
        [Fixed(0, "sorgente", "Il percorso di origine da cui avviare lo spostamento (usa '.' per la cartella corrente)")]
        public string SourcePath { get; set; } = string.Empty;

        [Fixed(1, "destinazione", "Il percorso di destinazione dove spostare i file (usa '.' per la cartella corrente)")]
        public string DestinationPath { get; set; } = string.Empty;

        // --- opzioni del comando ---

        [Option("debug|d", "Simula l'operazione senza toccare i file sul disco (Dry-run)", "Comando")]
        public bool Debug { get; set; }

        [Option("recursive|r", "Scansiona anche le sottocartelle e ricrea l'albero nella destinazione", "Comando")]
        public bool Recursive { get; set; }

        [Option("overwrite|ow", "Sovrascrive i file nella destinazione se esistono", "Comando")]
        public bool Overwrite { get; set; }

        [Option(CliMeta.SilenceFlag, CliMeta.SilenceDesc, "Comando")]
        public bool Silence { get; set; } = false;

        [Option("ignore-errors|ie", "Se attivo ignora gli errori di spostamento dei file", "Comando")]
        public bool IgnoreErrors { get; set; } = false;

        // --- opzioni di filtraggio ---

        [Option("fixed|f", "Usa il pattern come stringa esatta invece che come espressione regolare", "Filtri")]
        public bool FixedMatch { get; set; }

        [Option("ignore-case|i", "Rende la ricerca case-insensitive", "Filtri")]
        public bool IgnoreCase { get; set; }

        [Option(CliMeta.HiddenFlag, CliMeta.HiddenDesc, "Filtri")]
        public bool IncludeHidden { get; set; } = false;

        [Option("pattern|p", "Filtra i file in base a un'espressione regolare sul nome", "Filtri")]
        public string? Pattern { get; set; }

        [Option(CliMeta.SinceFlag, CliMeta.SinceDesc, "Filtri")]
        public DateTime? Since { get; set; } 

        [Option(CliMeta.OlderThanFlag, CliMeta.OlderThanDesc, "Filtri")]
        public DateTime? OlderThan { get; set; }
    }
}