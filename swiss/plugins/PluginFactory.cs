namespace plugins;

public record PluginRegistration(string Name, string Description, Func<Plugin> Factory);