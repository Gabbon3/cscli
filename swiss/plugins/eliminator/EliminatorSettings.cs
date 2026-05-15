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

        [Option("silence|s", "Non mostrare la UI di progressione a video", "Comando")]
        public bool Silence { get; set; }

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

        [Option("hidden|H", "Includi file e cartelle nascoste nella ricerca", "Filtri")]
        public bool IncludeHidden { get; set; } = false;
        

        [Option("newer-than|n", "Colpisce solo i file modificati da questa data in poi", "Filtri")]
        public DateTime? Since { get; set; }

        [Option("older-than|o", "Colpisce solo i file più vecchi di questa data/età", "Filtri")]
        public DateTime? OlderThan { get; set; }
    }
}