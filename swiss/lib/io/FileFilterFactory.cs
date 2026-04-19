using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace lib.io
{
    public delegate bool FileSystemFilter(ref FileSystemEntry entry);
    public enum FilterFileNameMatchType
    {
        Regex, // usa regex compilata non backtracking
        Fixed, // usa indexOf
        Glob // pattern glob semplice, simile a regex ma piu veloce
    }
    public static class FileFilterFactory
    {
        // enum per definire il tipo di ricerca
        // Record che raggruppa tutti i filtri
        public record FilterOptions(
            string? Pattern =  null,
            FilterFileNameMatchType MatchType = FilterFileNameMatchType.Regex, // default regex per semplicita
            bool IgnoreCase = true,
            // modifica
            DateTime? ModifiedAfter = null,
            DateTime? ModifiedBefore = null,
            // creazione
            DateTime? CreatedAfter = null,
            DateTime? CreatedBefore = null,
            // accesso
            DateTime? AccessedAfter = null,
            DateTime? AccessedBefore = null
        );

        /// <summary>
        /// Fonde due filtri. Se il primo fallisce, il secondo non viene nemmeno eseguito.
        /// </summary>
        private static FileSystemFilter Combine(FileSystemFilter a, FileSystemFilter b)
        {
            return (ref FileSystemEntry entry) => a(ref entry) && b(ref entry);
        }

        /// <summary>
        /// Genera il delegate ad alte prestazioni per il filtraggio dei file
        /// </summary>
        public static FileSystemFilter? CreateFilter(FilterOptions options)
        {
            // parto da filtro nullo cosi se non sono stati richiesti volo
            FileSystemFilter? finalFilter = null;
            // helper per aggiungere un filtro alla catena
            void AddFilter(FileSystemFilter newFilter)
            {
                if (finalFilter == null)
                    finalFilter = newFilter;
                else
                    finalFilter = Combine(finalFilter, newFilter);
            }
            // --- FILTRI SULLE DATE ---
            // creazione
            if (options.CreatedAfter.HasValue)
            {
                var date = options.CreatedAfter.Value;
                AddFilter((ref FileSystemEntry entry) => entry.CreationTimeUtc >= date);
            }
            if (options.CreatedBefore.HasValue)
            {
                var date = options.CreatedBefore.Value;
                AddFilter((ref FileSystemEntry entry) => entry.CreationTimeUtc <= date);
            }
            // modifica
            if (options.ModifiedAfter.HasValue)
            {
                var date = options.ModifiedAfter.Value;
                AddFilter((ref FileSystemEntry entry) => entry.LastWriteTimeUtc >= date);
            }
            if (options.ModifiedBefore.HasValue)
            {
                var date = options.ModifiedBefore.Value;
                AddFilter((ref FileSystemEntry entry) => entry.LastWriteTimeUtc <= date);
            }
            // accesso
            if (options.AccessedAfter.HasValue)
            {
                var date = options.AccessedAfter.Value;
                AddFilter((ref FileSystemEntry entry) => entry.LastAccessTimeUtc >= date);
            }
            if (options.AccessedBefore.HasValue)
            {
                var date = options.AccessedBefore.Value;
                AddFilter((ref FileSystemEntry entry) => entry.LastAccessTimeUtc <= date);
            }
            // --- FILTRO SUL NOME (regex o indexof semplice) ---
            if (!string.IsNullOrEmpty(options.Pattern))
            {
                if (options.MatchType == FilterFileNameMatchType.Fixed)
                {
                    // Fixed: pura ricerca di sottostringa (IndexOf)
                    StringComparison comp = options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    string pattern = options.Pattern;

                    AddFilter((ref FileSystemEntry entry) => entry.FileName.IndexOf(pattern.AsSpan(), comp) >= 0);
                }
                else if (options.MatchType == FilterFileNameMatchType.Glob)
                {
                    // Glob
                    string pattern = options.Pattern;
                    bool ignoreCase = options.IgnoreCase;

                    AddFilter((ref FileSystemEntry entry) => FileSystemName.MatchesSimpleExpression(pattern.AsSpan(), entry.FileName, ignoreCase));
                }
                else
                {
                    // Regex
                    var regexOptions = RegexOptions.Compiled | RegexOptions.NonBacktracking;
                    if (options.IgnoreCase) regexOptions |= RegexOptions.IgnoreCase;
                    var regex = new Regex(options.Pattern, regexOptions);

                    AddFilter((ref FileSystemEntry entry) => regex.IsMatch(entry.FileName));
                }
            }
            return finalFilter;
        }
    }
}