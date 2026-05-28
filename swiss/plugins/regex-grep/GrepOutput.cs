using System.Runtime.CompilerServices;
using lib.generator;
using lib.utils;
using lib.utils.span;

namespace plugins.regexgrep;

#region Match

[FastSerializable]
public readonly ref partial struct GrepMatchData
{
    public ReadOnlySpan<char> Path { get; }
    public ReadOnlySpan<char> PreMatch { get; }
    public ReadOnlySpan<char> Match { get; }
    public ReadOnlySpan<char> PostMatch { get; }
    public int LineNumber { get; }
    public int Column { get; }
    public int MatchLength { get; }

    public GrepMatchData(ReadOnlySpan<char> path, ReadOnlySpan<char> preMatch, ReadOnlySpan<char> match, ReadOnlySpan<char> postMatch, int lineNumber, int column, int matchLength)
    {
        Path = path; PreMatch = preMatch; Match = match; PostMatch = postMatch; LineNumber = lineNumber; Column = column; MatchLength = matchLength;
    }
}

#endregion
#region Count

// 2. Dati per il Count
[FastSerializable]
public readonly ref partial struct GrepCountData
{
    public ReadOnlySpan<char> Path { get; }
    public long Count { get; }

    public GrepCountData(ReadOnlySpan<char> path, long count)
    {
        Path = path; Count = count;
    }
}

#endregion

public static class GrepOutputFormatter
{
    #region Console Formatting

    /// <summary>
    /// Metodo per stampare a console l'output formattato del match
    /// </summary>
    /// <param name="data"></param>
    /// <param name="output"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FormatMatchConsole(ref GrepMatchData data, Span<char> output)
    {
        int pos = 0;
        "[Green]#[/] ".AsSpan().AppendTo(output, ref pos);
        Formatter.FormatFilePath(data.Path, output, ref pos);

        "[/]\n[Green]# [Yellow]".AsSpan().AppendTo(output, ref pos);
        data.LineNumber.AppendTo(output, ref pos);
        ":".AsSpan().AppendTo(output, ref pos);
        data.Column.AppendTo(output, ref pos);
        "[/] ".AsSpan().AppendTo(output, ref pos);

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

    /// <summary>
    /// Metodo per stampare a console l'output formattato del match count
    /// </summary>
    /// <param name="data"></param>
    /// <param name="output"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FormatCountConsole(ref GrepCountData data, Span<char> output)
    {
        int pos = 0;
        Formatter.FormatFilePath(data.Path, output, ref pos);
        "[/]: [Magenta]".AsSpan().AppendTo(output, ref pos);

        if (data.Count.TryFormat(output[pos..], out int countChars)) pos += countChars;

        "[/]".AsSpan().AppendTo(output, ref pos);
        return pos;
    }

    #endregion
}
