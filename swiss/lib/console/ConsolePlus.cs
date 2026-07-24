using lib.utils.span;

namespace lib.console;

public static class ConsolePlus
{
    private const string AnsiReset = "\x1b[0m";
    private const string AnsiBold = "\x1b[1m";

    /// <summary>
    /// Stampa una linea # --- #
    /// </summary>
    /// <param name="totalLength">lunghezza totale linea</param>
    public static void WriteHr(int totalLength = 33)
    {
        int dashesCount = totalLength - 4;
        // ---
        ReadOnlySpan<char> prefix = "[Cyan]#[DarkGray] ".AsSpan();
        ReadOnlySpan<char> suffix = " [Cyan]#[/]".AsSpan();
        // ---
        Span<char> buffer = stackalloc char[prefix.Length + dashesCount + suffix.Length];
        Span<char> rest = buffer.AppendNext(prefix);
        // aggiungo -
        rest[..dashesCount].Fill('-');
        rest[dashesCount..].AppendNext(suffix);
        // termino la copia
        Write(buffer);
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
            char c = span[i];

            if (c == '\n')
            {
                // se la stringa passata ha gia \r\n, escludo il \r per non stampare \r\r\n
                int chunkEnd = (i > lastPos && span[i - 1] == '\r') ? i - 1 : i;

                if (chunkEnd > lastPos)
                {
                    Console.Out.Write(span[lastPos..chunkEnd]);
                }

                // scrivo a console il newline nativo dell'OS (risolvendo il problema degli a capo a scala su vecchi server)
                Console.Out.Write(Environment.NewLine);
                lastPos = i + 1;
                continue;
            }

            if (c != '[') continue;

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
            else if (tagContent.Equals("b", StringComparison.OrdinalIgnoreCase) || 
                     tagContent.Equals("bold", StringComparison.OrdinalIgnoreCase))
            {
                Console.Out.Write(AnsiBold);
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

    /// <summary>
    /// Crea l'header di una box in stile hacker: # -- Titolo --------- #
    /// Zero allocazioni, zero numeri magici.
    /// </summary>
    public static void WriteBoxHeader(ReadOnlySpan<char> title, int totalWidth = 40, ConsoleColor titleColor = ConsoleColor.Cyan)
    {
        // Calcolo dashes: # (1) + spazio (1) + -- (2) + spazio (1) + titolo + spazio (1) + # (1) + spazio (1)
        int dashesCount = Math.Max(2, totalWidth - title.Length - 8);
        // definisco i pezzi
        string titleColorString = titleColor.ToString();
        ReadOnlySpan<char> p1 = $"[Cyan]#[/] [DarkGray]-- [/][{titleColorString}]".AsSpan();
        ReadOnlySpan<char> p2 = "[/] [DarkGray]".AsSpan();
        ReadOnlySpan<char> p3 = " [/][Cyan]#[/]".AsSpan();
        // allocazione stack precisa basata sulle lunghezze reali
        Span<char> buffer = stackalloc char[p1.Length + title.Length + p2.Length + dashesCount + p3.Length];
        Span<char> rest = buffer;
        // 1. Prefisso (# -- ) e inizio colore titolo
        rest = rest.AppendNext(p1);
        // 2. Il titolo
        rest = rest.AppendNext(title);
        // 3. Reset colore titolo e inizio colore trattini
        rest = rest.AppendNext(p2);
        // 4. Trattini
        rest[..dashesCount].Fill('-');
        rest = rest[dashesCount..];
        // 5. Chiusura box
        rest = rest.AppendNext(p3);
        // calcolo quanto abbiamo scritto effettivamente e stampo
        int totalWritten = buffer.Length - rest.Length;
        Write(buffer[..totalWritten], newLine: true);
    }

    /// <summary>
    /// Stampa un singolo elemento di una lista con indentazione e marker colorato.
    /// </summary>
    /// <param name="text">Testo (può contenere tag di colore)</param>
    /// <param name="level">Livello di annidamento (0 = base)</param>
    /// <param name="marker">Il carattere bullet (default '*')</param>
    /// <param name="tabSize">Spazi per ogni livello di indentazione</param>
    public static void WriteListItem(ReadOnlySpan<char> text, int level = 0, char marker = '*', int tabSize = 2, ConsoleColor markerColor = ConsoleColor.Cyan)
    {
        int indent = level * tabSize;
        string colorName = markerColor.ToString();
        // affitto sullo stack
        int size = indent + colorName.Length + text.Length + 16;
        Span<char> buffer = stackalloc char[size];
        Span<char> rest = buffer;
        // 1. indentazione: Fill diretto sullo slice iniziale
        if (indent > 0)
        {
            rest[..indent].Fill(' ');
            rest = rest[indent..];
        }
        // 2. costruisco tag e marker usando AppendNext
        rest = rest.AppendNext('[');
        rest = rest.AppendNext(colorName.AsSpan());
        rest = rest.AppendNext(']');
        rest = rest.AppendNext(marker);
        rest = rest.AppendNext("[/] ".AsSpan());
        // 3. testo completo
        rest = rest.AppendNext(text);
        // calcolo dimensione effettiva
        int written = buffer.Length - rest.Length;
        Write(buffer[..written]);
    }

    /// <summary>
    /// Stampa un'intera collezione come lista puntata.
    /// </summary>
    public static void WriteList(IEnumerable<string> items, int level = 0, char marker = '*', int tabSize = 2, ConsoleColor markerColor = ConsoleColor.Cyan)
    {
        foreach (var item in items)
        {
            WriteListItem(item.AsSpan(), level, marker, tabSize, markerColor);
        }
    }
}