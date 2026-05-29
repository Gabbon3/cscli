namespace plugins.mdconverter
{
    public class MdConverterSettings
    {
        // --- ARGOMENTI FISSI ---
        [Fixed(0, "percorso", "Percorso del file .md")]
        public string TargetPath { get; set; } = string.Empty;

        // --- OPZIONI BOOLEANE ---
        [Option("pdf|p", "Converti in pdf")]
        public bool Pdf { get; set; }

        [Option("keephtml|k", "Se converti in pdf e vuoi mantenere l'html")]
        public bool KeepHtml { get; set; }

        [Option("dark|d", "Genera il documento in dark mode")]
        public bool DarkMode { get; set; }

        [Option("generate-index|I", "Aggiunge in cima al documento una lista con i collegamenti a tutti i titoli suddivisi per livello")]
        public bool CreateIndex { get; set; }

        // --- OPZIONI CON VALORE ---
        [Option("destpath|dp", "Path di destinazione del file generato")]
        public string? DestPath { get; set; }

        [Option("mermaid-theme", "Definisci il tema che preferisci per i grafici")]
        public string? MermaidTheme { get; set; }
    }
}