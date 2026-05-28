using System.Runtime.CompilerServices;
using lib.utils.span;

namespace lib.utils
{
    class Formatter
    {
        static public string Bytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
        /// <summary>
        /// Restituisce il Throughput basandosi sul tempo in secondi e sui bytes totali
        /// </summary>
        /// <param name="totalBytes"></param>
        /// <param name="totalSeconds"></param>
        /// <returns></returns>
        public static string Throughput(long totalBytes, double totalSeconds)
        {
            // protezione dalla divisione per 0
            if (totalBytes <= 0 || totalSeconds <= 0)
            {
                return "0 B/s";
            }
            // bytes al secondo
            double bytesPerSecond = totalBytes / totalSeconds;
            // suffissi
            string[] suffixes = { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };
            int order = 0;
            // scalo dinamicamente finche il valore non è minore di 1024
            while (bytesPerSecond >= 1024 && order < suffixes.Length - 1)
            {
                order++;
                bytesPerSecond /= 1024;
            }
            // restituisco la stringa formattata (es: "740.12 MB/s" o "0.63 GB/s")
            return $"{bytesPerSecond:0.##} {suffixes[order]}";
        }
        public static string Milliseconds(long totalMilliseconds)
        {
            var ts = TimeSpan.FromMilliseconds(totalMilliseconds);

            if (ts.TotalSeconds < 1)
                return $"{ts.Milliseconds} ms";

            if (ts.TotalMinutes < 1)
                return $"{ts.Seconds}s {ts.Milliseconds:D3}ms"; // Es: 12s 045ms

            if (ts.TotalHours < 1)
                return $"{ts.Minutes}m {ts.Seconds}s"; // Es: 1m 20s

            return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s"; // Es: 1h 5m 20s
        }

        /// <summary>
        /// Formatta il path di un file con il classico pattern
        /// [grigio]percorso[ciano]file
        /// </summary>
        /// <param name="path">percorso da formattare</param>
        /// <param name="output">buffer su cui scrivere il path colorato</param>
        /// <param name="pos"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FormatFilePath(ReadOnlySpan<char> path, Span<char> output, ref int pos)
        {
            "[DarkGray]".AsSpan().AppendTo(output, ref pos);
            Path.GetDirectoryName(path).AppendTo(output, ref pos);
            Path.DirectorySeparatorChar.AppendTo(output, ref pos);
            "[Cyan]".AsSpan().AppendTo(output, ref pos);
            Path.GetFileName(path).AppendTo(output, ref pos);
        }
    }
}