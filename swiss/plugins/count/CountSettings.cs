using plugins;

namespace plugins.count
{
    public class CountSettings
    {
        // --- ARGOMENTI FISSI ---
        [Fixed(0, "percorso", "La directory di partenza (usa '.' per la cartella corrente)")]
        public string TargetPath { get; set; } = string.Empty;

        // --- OPZIONI BOOLEANE ---
        [Option("directory|d", "Include anche le cartelle nel conteggio finale")]
        public bool IncludeDirectory { get; set; }

        [Option("hidden|H", "Includi file e cartelle nascoste nella ricerca")]
        public bool IncludeHidden { get; set; } = false;

        [Option("recursive|r", "Scansiona e conta anche nelle sottocartelle")]
        public bool Recursive { get; set; }

        [Option("fixed|f", "Usa il pattern come stringa esatta invece che come espressione regolare")]
        public bool FixedMatch { get; set; }

        [Option("ignore-case|i", "Rende la ricerca del pattern case-insensitive")]
        public bool IgnoreCase { get; set; }

        // --- OPZIONI CON VALORE ---
        [Option(CliMeta.FilePatternFlag, CliMeta.FilePatternDesc, "Filtri")]
        public string? Pattern { get; set; }

        [Option(CliMeta.SinceFlag, CliMeta.SinceDesc, "Filtri")]
        public DateTime? Since { get; set; }

        [Option(CliMeta.OlderThanFlag, CliMeta.OlderThanDesc, "Filtri")]
        public DateTime? OlderThan { get; set; }
    }
}