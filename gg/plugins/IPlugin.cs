interface IPlugin
{
    string Name { get; }
    string Description { get; }
    Task RunAsync(string[] args, CancellationToken ct);
    void PrintHelp();
    void Help();
    void PrintError(string message);
}