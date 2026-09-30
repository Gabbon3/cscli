using System.Diagnostics;

namespace lib.io.log;

#pragma warning disable CA1416

public static class AuditLogger
{
    private const string Source = "Application";
    private const int EventId = 1000;

    public static void LogCommandExecution(string args)
    {
        try
        {
            string message = $"[Swiss Audit]{Environment.NewLine}"
                + $"User: {Environment.UserDomainName}\\{Environment.UserName}{Environment.NewLine}"
                + $"WorkDir: {Environment.CurrentDirectory}{Environment.NewLine}"
                + $"Command: swiss {args}";

            EventLog.WriteEntry(Source, message, EventLogEntryType.Information, EventId);
        }
        catch
        {
            // Ignora silenziosamente: l'audit non deve mai bloccare la CLI
        }
    }
}