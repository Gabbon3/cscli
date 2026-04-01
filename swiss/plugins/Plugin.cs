namespace plugins
{
    public abstract class Plugin : IPlugin
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        protected readonly object _printErrorLock = new();

        public abstract Task RunAsync(string[] args, CancellationToken ct);

        protected Dictionary<string, string> ParseArguments(string[] args, int startIndex = 1)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = startIndex; i < args.Length; i++)
            {
                string current = args[i];

                if (current.StartsWith('-'))
                {
                    string key = current.ToLower();
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        dict[key] = args[i + 1];
                        i++;
                    }
                    else
                    {
                        dict[key] = "true";
                    }
                }
                else
                {
                    PrintError($"Argomento posizionale non previsto o malformattato: {current}");
                }
            }

            return dict;
        }

        public abstract void Help();

        public void PrintWarning(string message)
        {
            lock (_printErrorLock)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"{Name} WARNING: {message}");
                Console.ResetColor();
            }
        }

        public void PrintError(string message)
        {
            lock (_printErrorLock)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"{Name} ERROR: {message}");
                Console.ResetColor();
            }
        }
    }
}