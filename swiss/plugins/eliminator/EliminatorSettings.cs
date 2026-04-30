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

        [Option("fixed|f", "Usa il pattern come stringa esatta invece che come espressione regolare")]
        public bool FixedMatch { get; set; }

        [Option("ignore-case|i", "Rende la ricerca case-insensitive")]
        public bool IgnoreCase { get; set; }

        [Option("drop-instant|di", "Se attivo cancella subito appena lo trova (ottimo in percorsi di rete)")]
        public bool DropInstant { get; set; }

        // --- OPZIONI CON VALORE ---

        [Option("threads|t", "Specifica il numero massimo di thread (default: numero di core della CPU)")]
        public int? Threads { get; set; }

        [Option("pattern|p", "Filtra i file in base a un'espressione regolare sul nome")]
        public string? Pattern { get; set; }

        [Option("since|s", "Colpisce solo i file modificati da questa data in poi")]
        public DateTime? Since { get; set; } // Adatta il tipo (string/int) in base a cosa si aspetta GetOptionAge

        [Option("older-than|o", "Colpisce solo i file più vecchi di questa data/età")]
        public DateTime? OlderThan { get; set; }
    }
}