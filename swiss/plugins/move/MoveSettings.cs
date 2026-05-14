namespace plugins.move
{
    public class MoveSettings
    {
        // --- ARGOMENTI FISSI ---
        
        [Fixed(0, "sorgente", "Il percorso di origine da cui avviare lo spostamento (usa '.' per la cartella corrente)")]
        public string SourcePath { get; set; } = string.Empty;

        [Fixed(1, "destinazione", "Il percorso di destinazione dove spostare i file (usa '.' per la cartella corrente)")]
        public string DestinationPath { get; set; } = string.Empty;

        // --- OPZIONI BOOLEANE (Flag) ---

        [Option("debug|d", "Simula l'operazione senza toccare i file sul disco (Dry-run)")]
        public bool Debug { get; set; }

        [Option("recursive|r", "Scansiona anche le sottocartelle e ricrea l'albero nella destinazione")]
        public bool Recursive { get; set; }

        [Option("fixed|f", "Usa il pattern come stringa esatta invece che come espressione regolare")]
        public bool FixedMatch { get; set; }

        [Option("ignore-case|i", "Rende la ricerca case-insensitive")]
        public bool IgnoreCase { get; set; }

        [Option("hidden|H", "Includi file e cartelle nascoste nella ricerca")]
        public bool IncludeHidden { get; set; } = false;

        [Option("overwrite|ow", "Sovrascrive i file nella destinazione se esistono già")]
        public bool Overwrite { get; set; }

        [Option("silence|s", "Non mostrare la UI di progressione a video")]
        public bool Silence { get; set; }

        // --- OPZIONI CON VALORE ---

        [Option("pattern|p", "Filtra i file in base a un'espressione regolare sul nome")]
        public string? Pattern { get; set; }

        [Option("threads|t", "Specifica il numero massimo di thread (default: numero di core della CPU)")]
        public int? Threads { get; set; }

        [Option("newer-than|n", "Colpisce solo i file modificati da questa data in poi")]
        public DateTime? Since { get; set; } 

        [Option("older-than|o", "Colpisce solo i file più vecchi di questa data/età")]
        public DateTime? OlderThan { get; set; }
    }
}