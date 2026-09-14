using System.Globalization;

namespace lib.io
{
    public enum RelativeDateTimeField
    {
        Modified,
        Created,
        Accessed
    }

    public readonly struct RelativeDateTime
    {
        public RelativeDateTimeField Field { get; }
        public DateTime ValueUtc { get; }

        public RelativeDateTime(RelativeDateTimeField field, DateTime valueUtc)
        {
            Field = field;
            ValueUtc = valueUtc;
        }

        /// <summary>
        /// Calcola la data relativa passando stringhe come "12d", "5h" o solo "12" (default giorni).
        /// Restituisce la DateTime corrispettiva sottraendo il valore indicato.
        /// </summary>
        public static bool TryParseRelativeDate(string value, out DateTime result)
        {
            result = default;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.Trim().ToLowerInvariant();

            char unit = value[^1];
            string amountStr;

            // Se l'ultimo carattere è un numero, assumiamo che l'utente non abbia messo l'unità.
            // Usiamo 'd' (giorni) di default e teniamo l'intera stringa come numero.
            if (char.IsDigit(unit))
            {
                unit = 'd';
                amountStr = value;
            }
            else
            {
                amountStr = value[..^1];
            }

            if (double.TryParse(amountStr, System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out double amount))
            {
                result = unit switch
                {
                    'd' => DateTime.UtcNow.AddDays(-amount),
                    'h' => DateTime.UtcNow.AddHours(-amount),
                    'm' => DateTime.UtcNow.AddMinutes(-amount),
                    's' => DateTime.UtcNow.AddSeconds(-amount),
                    _ => default
                };

                return result != default;
            }

            return false;
        }

        /// <summary>
        /// Calcola una data relativa/assoluta con selettore campo opzionale in coda:
        /// - 60d (default modifica)
        /// - 60d:c (creazione)
        /// - 12h:a (accesso)
        /// - 2024-01-15:m (modifica)
        /// </summary>
        public static bool TryParseRelativeDate(string value, out RelativeDateTime result)
        {
            result = default;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            string raw = value.Trim();
            RelativeDateTimeField field = RelativeDateTimeField.Modified;
            string temporalToken = raw;

            int separatorIndex = raw.LastIndexOf(':');
            if (separatorIndex > 0 && separatorIndex == raw.Length - 2)
            {
                char fieldToken = char.ToLowerInvariant(raw[^1]);
                if (TryParseDateField(fieldToken, out RelativeDateTimeField parsedField))
                {
                    field = parsedField;
                    temporalToken = raw[..separatorIndex];
                }
            }

            if (TryParseRelativeDate(temporalToken, out DateTime relativeUtc))
            {
                result = new RelativeDateTime(field, relativeUtc);
                return true;
            }

            if (DateTime.TryParse(temporalToken, out DateTime absoluteDate))
            {
                result = new RelativeDateTime(field, NormalizeToUtc(absoluteDate));
                return true;
            }

            return false;
        }

        public static bool TryParseDateField(char token, out RelativeDateTimeField field)
        {
            field = token switch
            {
                'm' => RelativeDateTimeField.Modified,
                'c' => RelativeDateTimeField.Created,
                'a' => RelativeDateTimeField.Accessed,
                _ => default
            };

            return token is 'm' or 'c' or 'a';
        }

        public static DateTime NormalizeToUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
                return value;

            if (value.Kind == DateTimeKind.Local)
                return value.ToUniversalTime();

            return DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime();
        }
    }
}
