using System.Diagnostics;
using plugins;
using lib.console;
// lista plugins
using plugins.filefinder;
using plugins.tree;
using plugins.eliminator;
using plugins.count;
using plugins.mdconverter;
using plugins.cripto;
using plugins.grep;
// # ----------------------- #
// # CONFIGURAZIONE INIZIALE #
// # ----------------------- #
// info sulla versione
const string version = "1.9.0";
const string versionDescription = "Librerie interne ottimizzate";
const string author = "Gabbon3";
// cancellation token
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (sender, e) =>
{
    e.Cancel = true;
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("\nRichiesta di annullamento ricevuta (Ctrl+C)...");
    Console.ResetColor();
    // invio del segnale di stop del processo in maniera safe
    cts.Cancel();
};
// registro dei plugin
List<PluginRegistration> plugins = [
    new("find", "Cerca file nel file system", () => new FileFinder()),
    new("tree", "Mostra l'albero delle directory", () => new TreePlugin()),
    new("eliminator", "Elimina file o cartelle in modo sicuro", () => new EliminatorPlugin()),
    new("count", "Conta il numero di file e/o cartelle", () => new CountPlugin()),
    new("mdconverter", "Converte un file md in html (default) e pdf", () => new MdConverterPlugin()),
    new("cripto", "Effettua la crittografia su file o cartelle legata all'utente Windows", () => new CriptoPlugin()),
    new("grep", "Ricerca stringhe multiple con AhoCorasick (limitato ASCII - lavora con i byte grezzi)", () => new GrepPlugin()),
];
// # ----------------------- #

// # --------------------------------- #
// # RECUPERO INFORMAZIONI PRELIMINARI #
// # --------------------------------- #
if (args.Length == 0)
{
    ConsolePlus.Write("[Yellow][ ! ] Nessun comando inserito[/]\n[ i ] [Cyan]swiss --help[/] oppure [Cyan]swiss -h[/] per ottenere maggiori informazioni");
    //Help(plugins);
    return;
}

string pluginName = args[0].ToLower();

if (pluginName == "--help" || pluginName == "-h")
{
    Help(plugins);
    return;
}

if (pluginName == "--version" || pluginName == "-v")
{
    VersionInfo();
    return;
}

bool printStats = args[^1] == "--stats";
string[] pluginArgs = [];
if (printStats)
{
    pluginArgs = args.Length > 1 ? [.. args[1..^1]] : [];
}
else
{
    pluginArgs = [.. args[1..]];
}

PluginRegistration? pluginMeta = plugins.FirstOrDefault(p => p.Name == pluginName);
// # --------------------------------- #

// # ---------------------- #
// # -- AVVIO DEL PLUGIN -- #
// # ---------------------- #

// setup per analisi performance processo
using Process currentProcess = Process.GetCurrentProcess();

if (pluginMeta != null)
{
    Plugin plugin = pluginMeta.Factory();

    if (pluginArgs.Length > 0 && (pluginArgs[0] == "--help" || pluginArgs[0] == "-h"))
    {
        plugin.PrintHelp();
        return;
    }
    // # STATS
    long startTimestamp = 0;
    TimeSpan startCpuTime = TimeSpan.Zero;
    long startGcMemory = 0;
    if (printStats)
    {
        currentProcess.Refresh();
        startTimestamp = Stopwatch.GetTimestamp();
        startCpuTime = currentProcess.TotalProcessorTime;
        startGcMemory = GC.GetTotalMemory(false);
    }
    // # -----
    try
    {
        // # ESECUZIONE PLUGIN
        await plugin.RunAsync(pluginArgs, cts.Token);
        // # -----------------
    }
    catch (OperationCanceledException)
    {
        ConsolePlus.Write("\n[Yellow]Operazione annullata dall'utente.[/]");
    }
    catch (Exception ex)
    {
        ConsolePlus.Write($"\n[Red]Errore imprevisto esecuzione plugin: {ex.Message}[/]");
    }
    finally
    {
        // # STATS
        if (printStats)
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            currentProcess.Refresh();
            TimeSpan endCpuTime = currentProcess.TotalProcessorTime;
            TimeSpan cpuUsed = endCpuTime - startCpuTime;
            long peakMemory = currentProcess.PeakWorkingSet64;
            long endGcMemory = GC.GetTotalMemory(false);
            PrintStatistics(elapsed, cpuUsed, peakMemory, endGcMemory - startGcMemory);
        }
        // # -----
    }
}
else
{
    ConsolePlus.Write($"[Yellow][ ! ] Il comando \"[Magenta]{pluginName}[Yellow]\" non esiste.[/]");
}
// # ---------------------- #

// # -------------------- #
// # METODI BASE DEL MAIN #
// # -------------------- #
static void Help(List<PluginRegistration> plugins)
{
    ConsolePlus.WriteHr();
    ConsolePlus.Write("[Cyan]#[/] Lista comandi supportati:");
    // per formattazione
    int maxNameLength = plugins.Count != 0 ? plugins.Max(p => p.Name.Length) : 0;
    foreach (var plugin in plugins)
    {
        ConsolePlus.Write($"[Cyan]* [Green]{plugin.Name.PadRight(maxNameLength)}[/] -> [Yellow]{plugin.Description}[/]");
    }
    ConsolePlus.Write($"[Cyan]* [Green]{"--stats".PadRight(maxNameLength)}[/] -> [DarkGray]Inseriscilo come ultimo argomento per stampare le statistiche di esecuzione[/]");
    ConsolePlus.WriteHr();
}

static void VersionInfo()
{
    ConsolePlus.WriteHr();
    ConsolePlus.Write($"[Cyan]#[/] [Green]{version}[/] - {versionDescription}");
    ConsolePlus.Write($"[Cyan]#[/] Author: [Green]{author}");
    ConsolePlus.WriteHr();
}

static void PrintStatistics(TimeSpan elapsed, TimeSpan cpuTime, long peakMemoryBytes, long gcMemoryDiff)
{
    ConsolePlus.WriteHr();
    ConsolePlus.Write("[Cyan]#[/] Statistiche esecuzione:");

    // tempo reale (wall clock)
    ConsolePlus.Write($"[Cyan]*[/] Tempo Totale:      [Cyan]{elapsed.TotalSeconds:N4} s[/]");

    // tempo cpu (somma di tutti i core)
    double cpuRatio = elapsed.TotalMilliseconds > 0 ? cpuTime.TotalMilliseconds / elapsed.TotalMilliseconds : 0;
    ConsolePlus.Write($"[Cyan]*[/] Tempo CPU:         [Yellow]{cpuTime.TotalSeconds:N4} s (avg {cpuRatio:N1}x core)[/]");

    // memoria fisica (RAM)
    ConsolePlus.Write($"[Cyan]*[/] RAM Picco (Phys):  [Magenta]{peakMemoryBytes / 1024.0 / 1024.0:N2} MB[/]");

    // memoria managed (GC)
    string sign = gcMemoryDiff >= 0 ? "+" : "";
    ConsolePlus.Write($"[Cyan]*[/] GC Alloc (Delta):  [Gray]{sign}{gcMemoryDiff / 1024.0 / 1024.0:N4} MB[/]");
    ConsolePlus.WriteHr();
}
// # -------------------------- #
// dotnet build /t:PublishRelease
// # -------------------------- #