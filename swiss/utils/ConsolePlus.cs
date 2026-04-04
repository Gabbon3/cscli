namespace utils;

public static class ConsolePlus
{
    /// <summary>
    /// Stampa a console stringhe colorate, definisci i blocchi di testo all'interno di tag come questi:
    /// [Cyan] ... [/], [Green] ... [/]
    /// il colore indicato deve corrispondere ad uno valido (visionare ConsoleColor)
    /// </summary>
    /// <param name="text">testo da stampare a console</param>
    /// <param name="newLine">default true, stampa una nuova linea (come fa Console.WriteLine)</param>
    public static void Write(string text, bool newLine = true)
    {
        if (string.IsNullOrEmpty(text))
        {
            if (newLine) Console.WriteLine();
            return;
        }
        ReadOnlySpan<char> span = text.AsSpan();
        ConsoleColor defaultColor = Console.ForegroundColor;
        int length = span.Length;
        int lastPos = 0;
        for (int i = 0; i < length; i++)
        {
            if (!(span[i] == '[' && i + 2 < length)) continue;
            if (i > lastPos)
            {
                Console.Out.Write(span.Slice(lastPos, i - lastPos));
            }

            int closeBracket = -1;
            for (int j = i + 1; j < length; j++)
            {
                if (span[j] == ']')
                {
                    closeBracket = j;
                    break;
                }
            }

            if (closeBracket != -1)
            {
                ReadOnlySpan<char> tagContent = span.Slice(i + 1, closeBracket - i - 1);

                if (tagContent.SequenceEqual("/"))
                {
                    Console.ForegroundColor = defaultColor;
                }
                else if (Enum.TryParse<ConsoleColor>(tagContent, true, out var newColor))
                {
                    Console.ForegroundColor = newColor;
                }
                else
                {
                    Console.Out.Write(span.Slice(i, closeBracket - i + 1));
                }
                i = closeBracket;
                lastPos = i + 1;
            }
        }
        if (lastPos < length)
        {
            Console.Out.Write(span.Slice(lastPos));
        }
        Console.ForegroundColor = defaultColor;
        if (newLine) Console.WriteLine();
    }
}