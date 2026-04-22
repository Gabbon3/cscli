using System.Reflection;
using lib.console;

namespace plugins;

public abstract class Plugin
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract Task RunAsync(string[] args, CancellationToken ct);
    /// <summary>
    /// Parsa automaticamente gli args e restituisce l'oggetto Settings popolato
    /// </summary>
    protected static TSettings ParseSettings<TSettings>(string[] args) where TSettings : new()
    {
        var settings = new TSettings();
        var props = typeof(TSettings).GetProperties();

        // 1. Estrae i Fixed e le Opzioni
        var fixedProps = props
            .Select(p => new { Prop = p, Attr = p.GetCustomAttribute<FixedAttribute>() })
            .Where(x => x.Attr != null)
            .OrderBy(x => x.Attr!.Position)
            .ToList();

        var optionProps = props
            .Select(p => new { Prop = p, Attr = p.GetCustomAttribute<OptionAttribute>() })
            .Where(x => x.Attr != null)
            .ToList();

        int argIndex = 0;

        // 2. Parsa prima gli argomenti Fixed (Posizionali)
        foreach (var fp in fixedProps)
        {
            // Se finiscono gli argomenti, o se incontriamo improvvisamente un flag (es: -r), ci fermiamo
            if (argIndex >= args.Length || args[argIndex].StartsWith('-'))
                break;

            AssignValue(settings, fp.Prop, args[argIndex]);
            argIndex++;
        }

        // 3. Parsa il resto come Opzioni (Flag e Valori)
        while (argIndex < args.Length)
        {
            string currentArg = args[argIndex];

            if (currentArg.StartsWith('-'))
            {
                bool isLong = currentArg.StartsWith("--");
                string optName = isLong ? currentArg.Substring(2) : currentArg.Substring(1);

                // Cerca la proprietà corrispondente all'attributo
                var match = optionProps.FirstOrDefault(o =>
                    (isLong && o.Attr!.LongName == optName) ||
                    (!isLong && o.Attr!.ShortName?.ToString() == optName)
                );

                if (match != null)
                {
                    if (match.Prop.PropertyType == typeof(bool))
                    {
                        // Se è booleano, la sua semplice presenza significa TRUE (es: --recursive)
                        match.Prop.SetValue(settings, true);
                    }
                    else
                    {
                        // Se è un valore (es: --threads 4), prendiamo l'argomento successivo
                        if (argIndex + 1 < args.Length && !args[argIndex + 1].StartsWith('-'))
                        {
                            argIndex++;
                            AssignValue(settings, match.Prop, args[argIndex]);
                        }
                    }
                }
            }
            argIndex++;
        }

        return settings;
    }

    /// <summary>
    /// Fa il parsing dell'input path preso dalla CLI e controlla se esiste su richiesta
    /// </summary>
    /// <param name="inputPath">percorso preso dagli args</param>
    /// <param name="checkPath">se true controlla se il path esuste</param>
    /// <returns></returns>
    protected string? ParsePath(string inputPath, bool checkPath = false)
    {
        string path = inputPath;
        if (inputPath == ".")
        {
            path = Environment.CurrentDirectory;
        }
        else if (inputPath.StartsWith("./") || inputPath.StartsWith($".{Path.DirectorySeparatorChar}"))
        {
            path = Path.Combine(Environment.CurrentDirectory, inputPath[2..]);
        }
        if (checkPath && !Path.Exists(path))
        {
            PrintError($"Il percorso non esiste: {path}");
            return null;
        }
        return path;
    }

    /// <summary>
    /// Helper per convertire le stringhe nei tipi giusti (int, string, bool, ecc.)
    /// </summary>
    /// <param name="obj">Opzione delle settings da valorizzare</param>
    /// <param name="prop"></param>
    /// <param name="value">La stringa ottenuta dagli args</param>
    private static void AssignValue(object obj, PropertyInfo prop, string value)
    {
        // Ottengo il tipo di elemento (string, int, bool) della proprietà
        var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        // in base al tipo confronto e faccio il parsing
        // stringhe
        if (targetType == typeof(string))
            prop.SetValue(obj, value);
        // int
        else if (targetType == typeof(int) && int.TryParse(value, out int intVal))
            prop.SetValue(obj, intVal);
        // bool
        else if (targetType == typeof(bool) && bool.TryParse(value, out bool boolVal))
            prop.SetValue(obj, boolVal);
        // datetime
        else if (targetType == typeof(DateTime) && DateTime.TryParse(value, out DateTime datetimeVal))
            prop.SetValue(obj, datetimeVal);
        // double
        else if (targetType == typeof(double) && double.TryParse(value, out double doubleVal))
            prop.SetValue(obj, doubleVal);
        // TODO: aggiungere supporto per altri tipi
    }

    /// <summary>
    /// Stampa dell'Help standardizzata basata sulla struttura dei settings
    /// </summary>
    /// <typeparam name="TSettings"></typeparam>
    public void PrintHelp<TSettings>()
    {
        var properties = typeof(TSettings).GetProperties();

        // 1. Estrai e ordina gli argomenti Fixed
        var fixedArgs = properties
            .Select(p => new { Prop = p, Attr = p.GetCustomAttribute<FixedAttribute>() })
            .Where(x => x.Attr != null)
            .OrderBy(x => x.Attr!.Position)
            .ToList();

        // 2. Estrai le Opzioni
        var options = properties
            .Select(p => new { Prop = p, Attr = p.GetCustomAttribute<OptionAttribute>() })
            .Where(x => x.Attr != null)
            .ToList();

        // --- COMPOSIZIONE OUTPUT ---

        // Usage string: [Yellow]swiss [Blue]{Name} <fixed1> <fixed2> [opzioni]
        string usageLine = $"[Cyan]#[/] [Yellow]swiss[/] [Blue]{Name}[/]";
        foreach (var fa in fixedArgs)
        {
            usageLine += $" [DarkGray]<{fa.Attr!.Name}>[/]";
        }
        if (options.Any()) usageLine += " [DarkGray][opzioni][/]";

        ConsolePlus.WriteHr();
        ConsolePlus.Write(usageLine);

        // Stampa i Fixed (in ordine)
        if (fixedArgs.Count != 0)
        {
            foreach (var fa in fixedArgs)
            {
                string argName = $"<{fa.Attr!.Name}>".PadRight(20);
                ConsolePlus.Write($"  [White]{argName}[/] : {fa.Attr.Description}");
            }
            ConsolePlus.Write(""); // Riga vuota
        }

        // Stampa le Opzioni
        if (options.Count != 0)
        {
            ConsolePlus.Write("Opzioni:");
            foreach (var opt in options)
            {
                string shortFlag = opt.Attr!.ShortName.HasValue ? $", -{opt.Attr.ShortName}" : "";
                string flags = $"--{opt.Attr.LongName}{shortFlag}".PadRight(25);
                ConsolePlus.Write($"  [Cyan]{flags}[/] : {opt.Attr.Description}");
            }
        }
        ConsolePlus.WriteHr();
    }

    private readonly Lock _printErrorLock = new();
    public void PrintWarning(string message)
    {
        lock (_printErrorLock)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[ ! ] {Name}: {message}");
            Console.ResetColor();
        }
    }
    public void PrintError(string message)
    {
        lock (_printErrorLock)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[!!!] {Name}: {message}");
            Console.ResetColor();
        }
    }
}