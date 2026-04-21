using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace lib.utils
{
    public static class SpanExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ToLowerAsciiSafe(this Span<byte> span)
        {
            int i = 0;
            // se la cpu supporta AVX2 e abbiamo almeno 32 byte da processare
            if (Avx2.IsSupported && span.Length >= Vector256<byte>.Count)
            {
                // Carico i limiti nei registri (costanti per tutto il ciclo)
                var upperA = Vector256.Create((byte)'A');
                var upperZ = Vector256.Create((byte)'Z');
                var shift = Vector256.Create((byte)32);
                // Processiamo 32 byte alla volta
                for (; i <= span.Length - Vector256<byte>.Count; i += Vector256<byte>.Count)
                {
                    // 1. Carica 32 byte in un registro a 256 bit
                    var data = Vector256.LoadUnsafe(ref span[i]);
                    // 2. Range Check: (data >= 'A' AND data <= 'Z')
                    // Nota: GreaterThanOrEqual e LessThanOrEqual creano una maschera di bit (0xFF dove vero, 0x00 dove falso)
                    var mask = Vector256.BitwiseAnd(
                        Vector256.GreaterThanOrEqual(data, upperA),
                        Vector256.LessThanOrEqual(data, upperZ)
                    );
                    // 3. Applica lo shift (32) solo dove la maschera è attiva (BitwiseAnd) e sommalo ai dati originali
                    var result = Vector256.Add(data, Vector256.BitwiseAnd(mask, shift));
                    // 4. Salva il risultato nel buffer
                    result.StoreUnsafe(ref span[i]);
                }
            }

            // Fallback Lineare classico senza SIMD
            // gestisce i byte rimanenti (se il file non è multiplo di 32)
            // o se la CPU è vecchia e non supporta AVX2
            for (; i < span.Length; i++)
            {
                byte b = span[i];
                if (b >= 65 && b <= 90)
                {
                    span[i] = (byte)(b + 32);
                }
            }
        }
    }
}