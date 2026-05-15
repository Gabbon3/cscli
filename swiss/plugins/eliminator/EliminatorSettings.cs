namespace plugins.eliminator
{
    public class EliminatorSettings
    {
        // --- ARGOMENTI FISSI ---
        [Fixed(0, "percorso", "Il percorso target da cui avviare la cancellazione (usa '.' per la cartella corrente)")]
        public string TargetPath { get; set; } = string.Empty;

        // --- OPZIONI BOOLEANE (Flag) ---

        [Option("debug|d", "Simula l'operazione senza toccare i file sul disco")]
        public bool Debug { get; set; }

        [Option("recursive|r", "Scansiona anche le sottocartelle")]
        public bool Recursive { get; set; }

        [Option("pattern|p", "Filtra i file in base a un'espressione regolare sul nome")]
        public string? Pattern { get; set; }

        [Option("fixed|f", "Usa il pattern come stringa esatta invece che come espressione regolare")]
        public bool FixedMatch { get; set; }

        [Option("ignore-case|i", "Rende la ricerca case-insensitive")]
        public bool IgnoreCase { get; set; }

        [Option("hidden|H", "Includi file e cartelle nascoste nella ricerca")]
        public bool IncludeHidden { get; set; } = false;
        
        [Option("drop-source|ds", "Cancella al termine anche la cartella target")]
        public bool DropSource { get; set; } = false;

        [Option("silence|s", "Non mostrare la UI di progressione a video")]
        public bool Silence { get; set; }

        [Option("ignore-errors|ie", "Se attivo ignora gli errori di cancellazione dei file")]
        public bool IgnoreErrors { get; set; } = false;

        [Option("threads|t", "Specifica il numero massimo di thread (default: numero di core della CPU)")]
        public int? Threads { get; set; }

        [Option("newer-than|n", "Colpisce solo i file modificati da questa data in poi")]
        public DateTime? Since { get; set; }

        [Option("older-than|o", "Colpisce solo i file più vecchi di questa data/età")]
        public DateTime? OlderThan { get; set; }
    }
}