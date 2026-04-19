namespace plugins
{
    public abstract class Plugin : IPlugin
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        protected int ArgsStartIndex = 0;
        protected Dictionary<string, string> Options { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        protected readonly object _printErrorLock = new();

        public abstract Task RunAsync(string[] args, CancellationToken ct);

        /// <summary>
        /// Carica i valori degli argomenti di tipo "opzioni" in Plugin.Options
        /// </summary>
        /// <param name="args"></param>
        /// <param name="startIndex"></param>
        protected void ParseArguments(string[] args, int startIndex = 1)
        {
            Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = startIndex; i < args.Length; i++)
            {
                string current = args[i];

                if (current.StartsWith('-'))
                {
                    string key = current.ToLower();
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                    {
                        Options[key] = args[i + 1];
                        i++;
                    }
                    else
                    {
                        Options[key] = "true";
                    }
                }
                else
                {
                    PrintError($"Argomento posizionale non previsto o malformattato: {current}");
                }
            }
        }

        /// <summary>
        /// Restituisce il valore di un opzione
        /// Devi prima aver richiamato ParseArgument 
        /// </summary>
        /// <param name="flags"></param>
        /// <returns></returns>
        protected string? GetOptionValue(params string[] flags)
        {
            foreach (var flag in flags)
            {
                if (Options.TryGetValue(flag, out var value))
                {
                    return value;
                }
            }
            return null;
        }
        /// <summary>
        /// Restituisce il valore di un opzione come Datetime
        /// </summary>
        /// <param name="flags"></param>
        /// <returns></returns>
        protected DateTime? GetOptionDatetime(params string[] flags)
        {
            return DateTime.TryParse(GetOptionValue(flags), out var dt) ? dt : null;
        }
        /// <summary>
        /// Restituisce il valore di un opzione come int
        /// </summary>
        /// <param name="flags"></param>
        /// <returns></returns>
        protected int? GetOptionInt(params string[] flags)
        {
            return int.TryParse(GetOptionValue(flags), out var n) ? n : null;
        }

        /// <summary>
        /// Verifica se una chiave è presente o meno nelle Options
        /// </summary>
        /// <param name="flags"></param>
        /// <returns></returns>
        protected bool OptionsContains(params string[] flags)
        {
            foreach (var flag in flags)
            {
                if (!string.IsNullOrEmpty(flag) && Options.ContainsKey(flag))
                {
                    return true;
                }
            }
            return false;
        }

        public abstract void Help();

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
}