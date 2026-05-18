using System.Runtime.Intrinsics;
using System.Runtime.CompilerServices;
using lib.utils.span;

namespace plugins.regexgrep;

#region Match

// 1. Dati per un Match Classico
public readonly ref struct GrepMatchData
{
    public readonly ReadOnlySpan<char> Path;
    public readonly ReadOnlySpan<char> PreMatch;
    public readonly ReadOnlySpan<char> Match;
    public readonly ReadOnlySpan<char> PostMatch;
    public readonly int LineNumber;
    public readonly int Column;
    public readonly int MatchLength;

    public GrepMatchData(
        ReadOnlySpan<char> path,
        ReadOnlySpan<char> preMatch,
        ReadOnlySpan<char> match,
        ReadOnlySpan<char> postMatch,
        int lineNumber,
        int column,
        int matchLength)
    {
        Path = path;
        PreMatch = preMatch;
        Match = match;
        PostMatch = postMatch;
        LineNumber = lineNumber;
        Column = column;
        MatchLength = matchLength;
    }
}

#endregion
#region Count

// 2. Dati per il Count
public readonly ref struct GrepCountData
{
    public readonly ReadOnlySpan<char> Path;
    public readonly long Count;

    public GrepCountData(ReadOnlySpan<char> path, long count)
    {
        Path = path;
        Count = count;
    }
}

#endregion
#region Delegate

// 3. I Delegate custom (necessari perché Func/Action non accettano ref struct pre-C# 13)
// Restituiscono int: il numero di caratteri scritti nel buffer
public delegate int MatchFormatterDelegate(ref GrepMatchData data, Span<char> outputBuffer);
public delegate int CountFormatterDelegate(ref GrepCountData data, Span<char> outputBuffer);

public enum OutputFormat { Console, Csv, Json }

#endregion
#region GrepOutput

/// <summary>
/// Classe che gestisce interamente la formattazione dell output del grep
/// </summary>
public static class GrepOutput
{
    // Puntatori configurati all'avvio in base all'enum OutputFormat
    public static MatchFormatterDelegate FormatMatch = default!;
    public static CountFormatterDelegate FormatCount = default!;

    public static void Configure(OutputFormat format)
    {
        switch (format)
        {
            case OutputFormat.Json:
                FormatMatch = FormatMatchJson;
                FormatCount = FormatCountJson;
                break;
            case OutputFormat.Csv:
                FormatMatch = FormatMatchCsv;
                FormatCount = FormatCountCsv;
                break;
            case OutputFormat.Console:
            default:
                FormatMatch = FormatMatchConsole;
                FormatCount = FormatCountConsole;
                break;
        }
    }

    #region Sanitize

    /// <summary>
    /// Pulisce lo span IN-PLACE sostituendo '"' con '\'' e i char < 32 con ' '.
    /// Altamente ottimizzato con vettorizzazione SIMD.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SanitizeForDataFormat(Span<char> text)
    {
        if (text.IsEmpty) return;

        // In C# char = ushort (16 bit)
        if (Vector256.IsHardwareAccelerated && text.Length >= Vector256<ushort>.Count)
        {
            var quotes = Vector256.Create((ushort)'"');
            var singleQuotes = Vector256.Create((ushort)'\'');
            var space = Vector256.Create((ushort)' ');
            var limit32 = Vector256.Create((ushort)32);

            int i = 0;
            ref ushort ptr = ref Unsafe.As<char, ushort>(ref text[0]);
            int vectorSize = Vector256<ushort>.Count; // Di solito 16 char

            for (; i <= text.Length - vectorSize; i += vectorSize)
            {
                var v = Vector256.LoadUnsafe(ref ptr, (nuint)i);

                // 1. Sostituisci '"' con '\''
                var maskQuotes = Vector256.Equals(v, quotes);
                v = Vector256.ConditionalSelect(maskQuotes, singleQuotes, v);

                // 2. Sostituisci char < 32 con ' '
                var maskControl = Vector256.LessThan(v, limit32);
                v = Vector256.ConditionalSelect(maskControl, space, v);

                v.StoreUnsafe(ref ptr, (nuint)i);
            }

            // Fallback per i caratteri rimanenti (coda dello span)
            for (; i < text.Length; i++)
            {
                SanitizeChar(ref text[i]);
            }
        }
        else
        {
            // Fallback scalare veloce
            for (int i = 0; i < text.Length; i++)
            {
                SanitizeChar(ref text[i]);
            }
        }
    }

    /// <summary>
    /// Fallback lineare char per char
    /// </summary>
    /// <param name="c"></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SanitizeChar(ref char c)
    {
        if (c == '"') c = '\'';
        else if (c < 32) c = ' ';
    }

    #endregion

    // ==========================================
    // Formattatori per CSV JSON e Console
    // ==========================================

    #region CSV
    private static int FormatMatchCsv(ref GrepMatchData data, Span<char> output)
    {
        int pos = 0;

        data.Path.AppendTo(output, ref pos); ';'.AppendTo(output, ref pos);
        data.LineNumber.AppendTo(output, ref pos); ';'.AppendTo(output, ref pos);

        // NUOVI CAMPI
        data.Column.AppendTo(output, ref pos); ';'.AppendTo(output, ref pos);
        data.MatchLength.AppendTo(output, ref pos); ';'.AppendTo(output, ref pos);

        // Contesto (se il match era troppo lungo, questi span saranno vuoti e non scriveranno nulla)
        int prePos = pos; data.PreMatch.AppendTo(output, ref pos); SanitizeForDataFormat(output[prePos..pos]);
        ';'.AppendTo(output, ref pos);

        int matchPos = pos; data.Match.AppendTo(output, ref pos); SanitizeForDataFormat(output[matchPos..pos]);
        ';'.AppendTo(output, ref pos);

        int postPos = pos; data.PostMatch.AppendTo(output, ref pos); SanitizeForDataFormat(output[postPos..pos]);

        '\n'.AppendTo(output, ref pos);
        return pos;
    }

    private static int FormatCountCsv(ref GrepCountData data, Span<char> output)
    {
        int pos = 0;
        data.Path.AppendTo(output, ref pos);
        ';'.AppendTo(output, ref pos);

        if (data.Count.TryFormat(output[pos..], out int countChars)) pos += countChars;
        '\n'.AppendTo(output, ref pos);

        return pos;
    }

    #endregion
    #region JSON
    private static int FormatMatchJson(ref GrepMatchData data, Span<char> output)
    {
        int pos = 0;
        "{\"path\":\"".AsSpan().AppendTo(output, ref pos); data.Path.AppendTo(output, ref pos);

        "\",\"line\":".AsSpan().AppendTo(output, ref pos); data.LineNumber.AppendTo(output, ref pos);
        ",\"col\":".AsSpan().AppendTo(output, ref pos); data.Column.AppendTo(output, ref pos);
        ",\"len\":".AsSpan().AppendTo(output, ref pos); data.MatchLength.AppendTo(output, ref pos);

        ",\"pre\":\"".AsSpan().AppendTo(output, ref pos);
        int prePos = pos; data.PreMatch.AppendTo(output, ref pos); SanitizeForDataFormat(output[prePos..pos]);

        "\",\"match\":\"".AsSpan().AppendTo(output, ref pos);
        int matchPos = pos; data.Match.AppendTo(output, ref pos); SanitizeForDataFormat(output[matchPos..pos]);

        "\",\"post\":\"".AsSpan().AppendTo(output, ref pos);
        int postPos = pos; data.PostMatch.AppendTo(output, ref pos); SanitizeForDataFormat(output[postPos..pos]);

        "\"}\n".AsSpan().AppendTo(output, ref pos);
        return pos;
    }

    private static int FormatCountJson(ref GrepCountData data, Span<char> output)
    {
        int pos = 0;
        "{\"path\":\"".AsSpan().AppendTo(output, ref pos);
        data.Path.AppendTo(output, ref pos);

        "\",\"count\":".AsSpan().AppendTo(output, ref pos);
        if (data.Count.TryFormat(output[pos..], out int countChars)) pos += countChars;

        "}\n".AsSpan().AppendTo(output, ref pos);
        return pos;
    }
    #endregion
    #region Console
    private static int FormatMatchConsole(ref GrepMatchData data, Span<char> output)
    {
        int pos = 0;

        "[Green]#[/] [DarkGray]".AsSpan().AppendTo(output, ref pos);
        data.Path.AppendTo(output, ref pos);

        "[/]\n[Green]# [Yellow]".AsSpan().AppendTo(output, ref pos);
        data.LineNumber.AppendTo(output, ref pos);
        ":".AsSpan().AppendTo(output, ref pos);
        data.Column.AppendTo(output, ref pos);
        "[/] ".AsSpan().AppendTo(output, ref pos);

        // Se il match è vuoto (cioè era troppo lungo e l'abbiamo scartato)
        if (data.Match.IsEmpty && data.MatchLength > 0)
        {
            "[DarkGray]<Match di ".AsSpan().AppendTo(output, ref pos);
            data.MatchLength.AppendTo(output, ref pos);
            " caratteri omesso>[/]".AsSpan().AppendTo(output, ref pos);
        }
        else
        {
            data.PreMatch.AppendTo(output, ref pos);
            "[Red]".AsSpan().AppendTo(output, ref pos);
            data.Match.AppendTo(output, ref pos);
            "[/]".AsSpan().AppendTo(output, ref pos);
            data.PostMatch.AppendTo(output, ref pos);
        }

        "\n[DarkGray]*\n*[/]".AsSpan().AppendTo(output, ref pos);
        return pos;
    }

    private static int FormatCountConsole(ref GrepCountData data, Span<char> output)
    {
        int pos = 0;

        "[Green]#[/] [Cyan]".AsSpan().AppendTo(output, ref pos);
        data.Path.AppendTo(output, ref pos);
        "[/]: [Magenta]".AsSpan().AppendTo(output, ref pos);

        if (data.Count.TryFormat(output[pos..], out int countChars)) pos += countChars;

        " match[/]\n".AsSpan().AppendTo(output, ref pos);

        return pos;
    }
    #endregion
}
#endregion