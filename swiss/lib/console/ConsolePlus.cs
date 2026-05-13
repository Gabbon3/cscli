namespace lib.console;

public static class ConsolePlus
{
    private const string AnsiReset = "\x1b[0m";
    
    public static void WriteHr(int length = 32)
    {
        Write($"[Cyan]#[DarkGray] {new string('-', length)} [Cyan]#[/]");
    }
    
    /// <summary>
    /// Stampa a console stringhe colorate, definisci i blocchi di testo all'interno di tag come questi:
    /// [Cyan] ... [/], [Green] ... [/]
    /// il colore indicato deve corrispondere ad uno valido (visionare ConsoleColor)
    /// </summary>
    /// <param name="span">testo da stampare a console</param>
    /// <param name="newLine">default true, stampa una nuova linea (come fa Console.WriteLine)</param>
    public static void Write(ReadOnlySpan<char> span, bool newLine = true)
    {
        if (span.Length == 0)
        {
            if (newLine) Console.WriteLine();
            return;
        }
        
        int length = span.Length;
        int lastPos = 0;
        
        for (int i = 0; i < length; i++)
        {
            if (span[i] != '[') continue;
            
            // Stampa il testo accumulato finora
            if (i > lastPos)
            {
                Console.Out.Write(span[lastPos..i]);
            }
            
            // Cerca la parentesi di chiusura
            int closeBracket = -1;
            for (int j = i + 1; j < length; j++)
            {
                if (span[j] == ']')
                {
                    closeBracket = j;
                    break;
                }
            }
            
            // Se non c'è chiusura, è testo normale
            if (closeBracket == -1)
            {
                Console.Out.Write('[');
                lastPos = i + 1;
                continue;
            }
            
            ReadOnlySpan<char> tagContent = span.Slice(i + 1, closeBracket - i - 1);
            
            // Verifica se è un tag di reset
            if (tagContent.SequenceEqual("/"))
            {
                Console.Out.Write(AnsiReset);
                i = closeBracket;
                lastPos = i + 1;
            }
            // Verifica se è un colore valido
            else if (Enum.TryParse<ConsoleColor>(tagContent, true, out var color))
            {
                Console.Out.Write(GetAnsiCode(color));
                i = closeBracket;
                lastPos = i + 1;
            }
            // Non è un tag valido, stampa '[' come carattere normale
            else
            {
                Console.Out.Write('[');
                lastPos = i + 1;
            }
        }
        
        // Stampa il testo rimanente
        if (lastPos < length)
        {
            Console.Out.Write(span[lastPos..]);
        }
        
        Console.Out.Write(AnsiReset);
        if (newLine) Console.WriteLine();
    }
    
    /// <summary>
    /// Converte un ConsoleColor nel corrispondente codice ANSI
    /// </summary>
    private static string GetAnsiCode(ConsoleColor color)
    {
        return color switch
        {
            ConsoleColor.Black => "\x1b[30m",
            ConsoleColor.DarkRed => "\x1b[31m",
            ConsoleColor.DarkGreen => "\x1b[32m",
            ConsoleColor.DarkYellow => "\x1b[33m",
            ConsoleColor.DarkBlue => "\x1b[34m",
            ConsoleColor.DarkMagenta => "\x1b[35m",
            ConsoleColor.DarkCyan => "\x1b[36m",
            ConsoleColor.Gray => "\x1b[37m",
            ConsoleColor.DarkGray => "\x1b[90m",
            ConsoleColor.Red => "\x1b[91m",
            ConsoleColor.Green => "\x1b[92m",
            ConsoleColor.Yellow => "\x1b[93m",
            ConsoleColor.Blue => "\x1b[94m",
            ConsoleColor.Magenta => "\x1b[95m",
            ConsoleColor.Cyan => "\x1b[96m",
            ConsoleColor.White => "\x1b[97m",
            _ => AnsiReset
        };
    }

    /// <summary>
    /// Write a console passando ReadOnlyMemory anziche il ReadOnlySpan<char>
    /// </summary>
    public static void Write(ReadOnlyMemory<char> text, bool newLine = true)
    {
        Write(text.Span, newLine);
    }
    
    /// <summary>
    /// Write a console passando la String anziche il ReadOnlySpan<char>
    /// </summary>
    public static void Write(string text, bool newLine = true)
    {
        Write(text.AsSpan(), newLine);
    }
}