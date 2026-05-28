using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

#region Sanitize for

namespace lib.utils;

public static class TextSanitizer
{
    #region Json or Csv

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
}
#endregion