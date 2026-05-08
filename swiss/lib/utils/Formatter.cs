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
    }
}