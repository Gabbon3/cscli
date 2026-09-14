namespace lib.io;

public readonly struct RelativeSize(long value)
{
    // Indica la dimensione in Bytes
    public readonly long Value { get; } = value;
    /// <summary>
    /// Fa il parse dell'oggetto relativo della dimensione
    /// Unità di misura accettate:
    ///  - vuoto -> bytes
    ///  - b -> bytes
    ///  - k -> KB
    ///  - m -> MB
    ///  - g -> GB
    ///  - t -> TB
    /// </summary>
    /// <param name="value">valore stringa ottenuto dagli args</param>
    /// <exception cref="ArgumentException">Lanciata se il valore in input non segue il formato richiesto</exception>
    /// <exception cref="InvalidOperationException">Lanciata se la conversione del numero della dimensione fallisce</exception>
    public static RelativeSize Parse(string value)
    {
        long resultValue = 0;
        // controlli
        if (string.IsNullOrEmpty(value)) throw new ArgumentException("La dimensione relativa passata non può essere vuota o nulla");
        // recupero l'ultimo carattere
        char unit = value[^1];
        string amountStr;
        // gestisco il default
        if (char.IsDigit(unit))
        {
            // byte
            unit = 'b';
            amountStr = value;
        }
        else
        {
            amountStr = value[..^1];
        }
        // provo a fare il parse del numero
        if (!long.TryParse(amountStr, out var amount))
        {
            throw new ArgumentException("Il valore della dimensione inserito non è valido");
        }

        resultValue = unit switch
        {
            'b' => amount,
            'k' => amount * 1024L,
            'm' => amount * 1024L * 1024L,
            'g' => amount * 1024L * 1024L * 1024L,
            't' => amount * 1024L * 1024L * 1024L * 1024L,
            _ => default
        };

        if (resultValue == default) throw new InvalidOperationException($"L'unità di misura utilizzata non è valida: '{unit}'");

        return new RelativeSize(resultValue);
    }
}