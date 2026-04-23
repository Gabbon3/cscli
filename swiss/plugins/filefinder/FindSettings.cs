namespace plugins.filefinder
{
    public class FindSettings
    {
        // --- ARGOMENTI FISSI ---
        [Fixed(0, "percorso", "La directory di partenza (usa '.' per la cartella corrente)")]
        public string TargetPath { get; set; } = string.Empty;

        // Pattern è opzionale, ma essendo Fixed, la sua presenza dipende dalla lunghezza degli argomenti. 
        // Se non fornito, il nostro ParseSettings lo lascerà a string.Empty o null
        [Fixed(1, "pattern", "La stringa da usare per la ricerca (regex di default)")]
        public string? Pattern { get; set; }

        // --- OPZIONI RICERCA ---
        [Option("dirs|d", "Includi le cartelle nella ricerca", "Ricerca")]
        public bool Dirs { get; set; }

        [Option("ignore-case|i", "Rende case insensitive la ricerca", "Ricerca")]
        public bool IgnoreCase { get; set; }

        [Option("fixed|f", "Verifica se il pattern è contenuto nel nome (ignora regex)", "Ricerca")]
        public bool FixedMatch { get; set; }

        [Option("since|s", "Trova i file modificati da questa data in poi", "Ricerca")]
        public DateTime? Since { get; set; }

        [Option("older-than|o", "Trova i file più vecchi di questa data", "Ricerca")]
        public DateTime? OlderThan { get; set; }

        [Option("no-recurse|r", "Se attivo non ricerca anche nelle sottocartelle", "Ricerca")]
        public bool NoRecurseSubdirectories { get; set; } = false;

        // --- OPZIONI CLASSIFICA ---
        [Option("biggest|B", "Restituisce i file più grandi", "Classifica")]
        public bool Biggest { get; set; }

        [Option("smallest|S", "Restituisce i file più piccoli", "Classifica")]
        public bool Smallest { get; set; }

        [Option("newest|N", "Restituisce i file più recenti", "Classifica")]
        public bool Newest { get; set; }

        [Option("oldest|O", "Restituisce i file più vecchi", "Classifica")]
        public bool Oldest { get; set; }

        [Option("limit|l", "Limita il numero di risultati nella classifica (default 10)", "Classifica")]
        public int Limit { get; set; } = 10;
    }
}