using System.Diagnostics;
using plugins;
using utils;
// lista plugins
using plugins.filefinder;
using plugins.tree;
//using plugins.indexer;
//using plugins.searcher;
using plugins.eqfile;
using plugins.findedge;
using plugins.eliminator;
using plugins.count;
using plugins.mdconverter;
// # ----------------------- #
// # CONFIGURAZIONE INIZIALE #
// # ----------------------- #
// info sulla versione
const string version = "1.8.5";
const string versionDescription = "MdConverter Plugin - KateX - ottimizzazioni";
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
    //new("indexer", "Indicizza i contenuti per ricerche veloci", () => new IndexerPlugin()),
    //new("searcher", "Cerca testo all'interno dei file", () => new SearcherPlugin()),
    new("eqfile", "Confronta file o trova duplicati", () => new EqFilePlugin()),
    new("findedge", "Trova file con caratteristiche limite", () => new FindEdgePlugin()),
    new("eliminator", "Elimina file o cartelle in modo sicuro", () => new EliminatorPlugin()),
    new("count", "Conta il numero di file e/o cartelle", () => new CountPlugin()),
    new("mdconverter", "Converte un file md in html (default) e pdf", () => new MdConverterPlugin()),
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
        plugin.Help();
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
    ConsolePlus.Write("# [DarkGray]-----------------------[/] #");
    ConsolePlus.Write("# Lista comandi supportati:");
    // per formattazione
    int maxNameLength = plugins.Count != 0 ? plugins.Max(p => p.Name.Length) : 0;
    foreach (var plugin in plugins)
    {
        ConsolePlus.Write($"* [Cyan]{plugin.Name.PadRight(maxNameLength)}[/] -> [Yellow]{plugin.Description}[/]");
    }
    ConsolePlus.Write($"* [Magenta]{"--stats".PadRight(maxNameLength)}[/] -> [DarkGray]Stampa le statistiche di esecuzione[/]");
    ConsolePlus.Write("# [DarkGray]-----------------------[/] #");
}

static void VersionInfo()
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"{version} - {versionDescription}\n");
    Console.WriteLine($"Author - {author}\n");
    Console.ResetColor();
}

static void PrintStatistics(TimeSpan elapsed, TimeSpan cpuTime, long peakMemoryBytes, long gcMemoryDiff)
{
    ConsolePlus.Write("# [DarkGray]-----------------------[/] #");
    Console.WriteLine("# Statistiche esecuzione:");

    // tempo reale (wall clock)
    Console.Write("* Tempo Totale:      ");
    ConsolePlus.Write($"[Cyan]{elapsed.TotalSeconds:N4} s[/]");

    // tempo cpu (somma di tutti i core)
    Console.Write("* Tempo CPU:         ");
    double cpuRatio = elapsed.TotalMilliseconds > 0 ? cpuTime.TotalMilliseconds / elapsed.TotalMilliseconds : 0;
    ConsolePlus.Write($"[Yellow]{cpuTime.TotalSeconds:N4} s (avg {cpuRatio:N1}x core)[/]");

    // memoria fisica (RAM)
    Console.Write("* RAM Picco (Phys):  ");
    ConsolePlus.Write($"[Magenta]{peakMemoryBytes / 1024.0 / 1024.0:N2} MB[/]");

    // memoria managed (GC)
    Console.Write("* GC Alloc (Delta):  ");
    string sign = gcMemoryDiff >= 0 ? "+" : "";
    ConsolePlus.Write($"[Gray]{sign}{gcMemoryDiff / 1024.0 / 1024.0:N4} MB[/]");
    ConsolePlus.Write("# [DarkGray]-----------------------[/] #");
}
// # -------------------------------- #
// dotnet publish -c Release -r win-x64
// # -------------------------------- #