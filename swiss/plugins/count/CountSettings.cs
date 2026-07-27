using lib.io;

namespace plugins.count
{
    public class CountSettings
    {
        // --- ARGOMENTI FISSI ---
        [Fixed(0, "percorso", "La directory di partenza (usa '.' per la cartella corrente)")]
        public string TargetPath { get; set; } = string.Empty;

        // --- CONFIGURAZIONE ---
        [Option("directory|d", "Include anche le cartelle nel conteggio finale", "Configurazione")]
        public bool IncludeDirectory { get; set; }

        [Option(CliMeta.HiddenFlag, CliMeta.HiddenDesc, "Configurazione")]
        public bool IncludeHidden { get; set; } = false;

        [Option("recursive|r", "Scansiona e conta anche nelle sottocartelle", "Configurazione")]
        public bool Recursive { get; set; }

        [Option("fixed|f", "Usa il pattern come stringa esatta invece che come espressione regolare", "Configurazione")]
        public bool FixedMatch { get; set; }

        [Option("ignore-case|i", "Rende la ricerca del pattern case-insensitive", "Configurazione")]
        public bool IgnoreCase { get; set; }

        // --- FILTRI ---
        [Option(CliMeta.FilePatternFlag, CliMeta.FilePatternDesc, "Filtri")]
        public string? Pattern { get; set; }

        [Option(CliMeta.DateAfterFlag, CliMeta.DateAfterDesc, "Filtri")]
        public RelativeDateTime? DateAfter { get; set; }

        [Option(CliMeta.DateBeforeFlag, CliMeta.DateBeforeDesc, "Filtri")]
        public RelativeDateTime? DateBefore { get; set; }
    }
}