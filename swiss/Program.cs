using System.Diagnostics;
using plugins;
using plugins.filefinder;
using plugins.indexer;
using plugins.searcher;
using plugins.tree;
using swiss.plugins.eqfile;
using swiss.plugins.findedge;
using swiss.plugins.eliminator;

// cancellation token
using var cts = new CancellationTokenSource();

const string version = "1.7.0";
const string versionDescription = "filewalker util - walker del file system parallelo";

Console.CancelKeyPress += (sender, e) =>
{
    e.Cancel = true;
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("\nRichiesta di annullamento ricevuta (Ctrl+C)...");
    Console.ResetColor();
    // invio del segnale di stop del processo in maniera safe
    cts.Cancel();
};

if (args.Length == 0)
{
    Console.WriteLine("swiss help per avere maggiori informazioni ...");
    Help(new List<Plugin>());
    return;
}

string pluginName = args[0].ToLower();

// registro dei plugin
List<Plugin> plugins = [
    new FileFinder(),
    new TreePlugin(),
    new IndexerPlugin(),
    new SearcherPlugin(),
    new EqFilePlugin(),
    new FindEdgePlugin(),
    new EliminatorPlugin()
];

if (pluginName == "help")
{
    Help(plugins);
    return;
}

if (pluginName == "version")
{
    VersionInfo();
    return;
}

string[] pluginArgs = [.. args[1..]];


Plugin? plugin = plugins.FirstOrDefault(p => p.Name == pluginName);

// setup per analisi performance processo
using Process currentProcess = Process.GetCurrentProcess();

if (plugin != null)
{
    if (pluginArgs[0] == "help")
    {
        plugin.Help();
        return;
    }
    // snapshot iniziale performance
    currentProcess.Refresh();
    long startTimestamp = Stopwatch.GetTimestamp();
    TimeSpan startCpuTime = currentProcess.TotalProcessorTime;
    long startGcMemory = GC.GetTotalMemory(false);

    try
    {
        await plugin.RunAsync(pluginArgs, cts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\nOperazione annullata dall'utente.");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Errore imprevisto esecuzione plugin: {ex.Message}");
        Console.ResetColor();
    }
    finally
    {
        // calcolo statistiche finali anche se crasha o viene annullato
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);

        currentProcess.Refresh();
        TimeSpan endCpuTime = currentProcess.TotalProcessorTime;
        TimeSpan cpuUsed = endCpuTime - startCpuTime;
        long peakMemory = currentProcess.PeakWorkingSet64;
        long endGcMemory = GC.GetTotalMemory(false);

        PrintStatistics(elapsed, cpuUsed, peakMemory, endGcMemory - startGcMemory);
    }
}
else
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Il comando \"{pluginName}\" non esiste.");
    Console.ResetColor();
    Help(plugins);
}

static void Help(List<Plugin> plugins)
{
    Console.WriteLine("------------------------");
    Console.WriteLine("Lista comandi supportati:");
    // per formattazione
    int maxNameLength = plugins.Count != 0 ? plugins.Max(p => p.Name.Length) : 0;
    foreach (var plugin in plugins)
    {
        Console.WriteLine($" - {plugin.Name.PadRight(maxNameLength)} -> {plugin.Description}");
    }
    Console.WriteLine("------------------------");
}

static void VersionInfo()
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"\n{version} - {versionDescription}\n");
    Console.ResetColor();
}

static void PrintStatistics(TimeSpan elapsed, TimeSpan cpuTime, long peakMemoryBytes, long gcMemoryDiff)
{
    Console.WriteLine();
    Console.WriteLine("------------------------------------------------");
    Console.WriteLine("STATISTICHE ESECUZIONE");
    Console.WriteLine("------------------------------------------------");

    // tempo reale (wall clock)
    Console.Write("Tempo Totale:      ");
    PrintColoredValue($"{elapsed.TotalSeconds:N4} s", ConsoleColor.Cyan);

    // tempo cpu (somma di tutti i core)
    Console.Write("Tempo CPU:         ");
    double cpuRatio = elapsed.TotalMilliseconds > 0 ? cpuTime.TotalMilliseconds / elapsed.TotalMilliseconds : 0;
    PrintColoredValue($"{cpuTime.TotalSeconds:N4} s (avg {cpuRatio:N1}x core)", ConsoleColor.Yellow);

    // memoria fisica (RAM)
    Console.Write("RAM Picco (Phys):  ");
    PrintColoredValue($"{peakMemoryBytes / 1024.0 / 1024.0:N2} MB", ConsoleColor.Magenta);

    // memoria managed (GC)
    Console.Write("GC Alloc (Delta):  ");
    string sign = gcMemoryDiff >= 0 ? "+" : "";
    PrintColoredValue($"{sign}{gcMemoryDiff / 1024.0 / 1024.0:N4} MB", ConsoleColor.Gray);

    Console.WriteLine("------------------------------------------------");
}

static void PrintColoredValue(string value, ConsoleColor color)
{
    Console.ForegroundColor = color;
    Console.WriteLine(value);
    Console.ResetColor();
}