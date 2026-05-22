namespace plugins;

public static class CliMeta
{
    // --- FLAG E DESCRIZIONI: FILTRI ---
    public const string SinceFlag = "modified-after|ma";
    public const string SinceDesc = "Considera i file piu' recenti di 30d (d->giorni, h->ore, m->minuti)";

    public const string OlderThanFlag = "modified-before|mb";
    public const string OlderThanDesc = "Considera i file piu' vecchi di 30d (d->giorni, h->ore, m->minuti)";

    public const string FilePatternFlag = "pattern|p";
    public const string FilePatternDesc = "Filtra i file/cartelle in base al nome (regex)";

    // --- FLAG E DESCRIZIONI: CONFIGURAZIONE ---
    public const string ThreadsFlag = "threads|t";
    public const string ThreadsDesc = "Numero di thread da usare durante l'esecuzione (default: numero di core)";
}
