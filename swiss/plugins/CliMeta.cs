namespace plugins;

public static class CliMeta
{
    // --- FLAG E DESCRIZIONI: FILTRI ---
    public const string DateAfterFlag = "date-after|da";
    public const string DateAfterDesc = "Considera i file piu' recenti della data indicata (es: 60d, 2024-01-15, 12h:a, 30d:c). Campo: m modifica (default), c creazione, a accesso";

    public const string DateBeforeFlag = "date-before|db";
    public const string DateBeforeDesc = "Considera i file piu' vecchi della data indicata (es: 60d, 2024-01-15, 12h:a, 30d:c). Campo: m modifica (default), c creazione, a accesso";

    public const string FilePatternFlag = "pattern|p";
    public const string FilePatternDesc = "Filtra i file/cartelle in base al nome (regex)";

    // --- FLAG E DESCRIZIONI: CONFIGURAZIONE ---
    public const string ThreadsFlag = "threads|t";
    public const string ThreadsDesc = "Numero di thread da usare durante l'esecuzione (default: numero di core)";
    public const string SilenceFlag = "silence|s";
    public const string SilenceDesc = "Se attivo non mostra risultati di progessione a console";
    public const string HiddenFlag = "hidden|H";
    public const string HiddenDesc = "Se attivo include i file nascosti nell'enumerazione";
    public const string JustEnoughOutputFlag = "just-enough-output|jeo";
    public const string JustEnoughOutputDesc = "Se attivo mostra il minimo indispensabile di output a console";

    // --- FLAG E DESCRIZIONI: OUTPUT --- 
    public const string FormatFlag = "format|F";
    public const string FormatDesc = "Formato di output: console (default), csv, json";
    public const string OutputFileFlag = "output-file|o";
    public const string OutputFileDesc = "Indica il percorso del file dove scrivere i risultati";
}
