using System.Diagnostics;
using plugins;
using lib.console;
using Spectre.Console;
// lista plugins
using plugins.find;
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
const string version = "1.9.6";
const string versionDescription = "Refactoring Grep";
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
    new("find", "Cerca file nel file system", () => new FindPlugin()),
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
    ConsolePlus.Write("[Yellow][ ! ] Nessun comando inserito[/]\n[ i ] [Cyan]gg --help[/] oppure [Cyan]gg -h[/] per ottenere maggiori informazioni");
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
    double cpuRatio = elapsed.TotalMilliseconds > 0 ? cpuTime.TotalMilliseconds / elapsed.TotalMilliseconds : 0;
    string sign = gcMemoryDiff >= 0 ? "+" : "";

    // 1. Creiamo una griglia per allineare i dati in due colonne
    var grid = new Grid()
        .AddColumn(new GridColumn().NoWrap().PadRight(4)) // Colonna Etichette
        .AddColumn(new GridColumn().NoWrap());            // Colonna Valori

    // 2. Aggiungiamo i dati riga per riga
    grid.AddRow("[cyan]Tempo Totale:[/]", $"[cyan]{elapsed.TotalSeconds:N4} s[/]");
    grid.AddRow("[cyan]Tempo CPU:[/]", $"[yellow]{cpuTime.TotalSeconds:N4} s[/] [grey](avg {cpuRatio:N1}x core)[/]");
    grid.AddRow("[cyan]RAM Picco (Phys):[/]", $"[magenta]{peakMemoryBytes / 1024.0 / 1024.0:N2} MB[/]");
    grid.AddRow("[cyan]GC Alloc (Delta):[/]", $"[grey]{sign}{gcMemoryDiff / 1024.0 / 1024.0:N4} MB[/]");

    // 3. Avvolgiamo la griglia in un pannello decorato
    var panel = new Panel(grid)
        .Header("[bold cyan]Statistiche di Esecuzione[/]")
        .Border(BoxBorder.Rounded)
        .BorderColor(Color.DarkCyan)
        .Padding(2, 1, 2, 1); // Padding interno (sinistra, sopra, destra, sotto)

    // Aggiungiamo uno spazio vuoto prima del pannello per distanziarlo dall'output del plugin
    AnsiConsole.WriteLine();
    
    // Stampiamo il capolavoro
    AnsiConsole.Write(panel);
}
// # -------------------------- #
// dotnet build /t:PublishRelease
// # -------------------------- #