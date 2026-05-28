using System.Runtime.CompilerServices;

namespace lib.utils.span
{
    public static class SpanExtensions
    {
        #region APPENDS

        /// <summary>
        /// Copia il contenuto della sequenza corrente in una destinazione, aggiornando l'indice di posizione.
        /// </summary>
        /// <typeparam name="T">Il tipo di elementi contenuti nello span.</typeparam>
        /// <param name="source">Lo span di origine da copiare.</param>
        /// <param name="destination">Lo span di destinazione.</param>
        /// <param name="currentIndex">L'indice corrente nella destinazione, incrementato dopo la copia.</param>
        /// <exception cref="ArgumentOutOfRangeException">Lanciata se la destinazione non ha spazio sufficiente.</exception>
        public static void AppendTo<T>(this ReadOnlySpan<T> source, Span<T> destination, ref int currentIndex)
        {
            if (source.IsEmpty) return;
            // Utilizziamo lo slicing per definire l'area di scrittura
            // utilizzo lo slicing per definire l'area di scrittura
            Span<T> targetWindow = destination[currentIndex..];
            // copio i dati nella sezione di destinazione
            // ! lancia ArgumentException se source.Length > targetWindow.Length
            source.CopyTo(targetWindow);
            // aggiorno la posizione solo dopo il successo della copia
            currentIndex += source.Length;
        }
        /// <summary>
        /// Overload per char del metodo AppendTo
        /// </summary>
        /// <param name="source"></param>
        /// <param name="destination"></param>
        /// <param name="currentIndex"></param>
        public static void AppendTo(this char source, Span<char> destination, ref int currentIndex)
        {
            destination[currentIndex] = source;
            currentIndex++;
        }
        /// <summary>
        /// Copia il contenuto della sequenza in una destinazione e restituisce lo slice rimanente.
        /// Ottimo per concatenazioni a catena (fluent pattern) infinite con zero allocazioni.
        /// Spiegazione: da_copiare.AppendNext(destinazione) => [destinazione+da_copiare]
        /// Attenzione che destinazione deve avere abbastanza spazio 
        /// </summary>
        /// <typeparam name="T">Il tipo di elementi contenuti nello span.</typeparam>
        /// <param name="destination">Lo span di destinazione in cui scrivere.</param>
        /// <param name="source">Lo span di origine da copiare.</param>
        /// <returns>La porzione di span destinazione rimanente e libera.</returns>
        /// <exception cref="ArgumentException">Lanciata se la destinazione non ha spazio sufficiente.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Span<T> AppendNext<T>(this Span<T> destination, ReadOnlySpan<T> source)
        {
            if (source.IsEmpty) return destination;

            // copio i dati direttamente nell'area indicata
            source.CopyTo(destination);

            // restituisco solo l'area di memoria ancora non scritta
            return destination[source.Length..];
        }
        /// <summary>
        /// Overload per singolo char del metodo AppendNext.
        /// Ottimo per concatenare caratteri singoli nel flusso senza allocazioni.
        /// </summary>
        /// <param name="destination">Lo span di destinazione.</param>
        /// <param name="source">Il carattere da copiare.</param>
        /// <returns>La porzione di span destinazione rimanente e libera.</returns>
        /// <exception cref="IndexOutOfRangeException">Lanciata se la destinazione è vuota.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Span<char> AppendNext(this Span<char> destination, char source)
        {
            // Scrive sempre in prima posizione
            destination[0] = source;

            // Restituisce lo span "tagliando" il carattere appena scritto
            return destination[1..];
        }
        
        #endregion
        #region numeri

        /// <summary>
        /// AppendNext ma per gli int
        /// </summary>
        public static void AppendTo(this int value, Span<char> destination, ref int currentIndex)
        {
            if (value.TryFormat(destination[currentIndex..], out int charsWritten))
            {
                currentIndex += charsWritten;
            }
        }

        /// <summary>
        /// AppendNext ma per i long
        /// </summary>
        public static void AppendTo(this long value, Span<char> destination, ref int currentIndex)
        {
            if (value.TryFormat(destination[currentIndex..], out int charsWritten))
            {
                currentIndex += charsWritten;
            }
        }

        #endregion
        #region bool

        /// <summary>
        /// AppendTo per i booleani.
        /// Scrive sempre in minuscolo ("true" o "false") per garantire 
        /// la piena conformità con lo standard JSON.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AppendTo(this bool value, Span<char> destination, ref int currentIndex)
        {
            if (value)
            {
                // In C# "stringa" è convertibile implicitamente in ReadOnlySpan<char>
                "true".CopyTo(destination[currentIndex..]);
                currentIndex += 4; // lunghezza di "true"
            }
            else
            {
                "false".CopyTo(destination[currentIndex..]);
                currentIndex += 5; // lunghezza di "false"
            }
        }

        #endregion
        #region Fill

        /// <summary>
        /// Riempi una porzione di spazio con un carattere definito
        /// </summary>
        /// <param name="destination"></param>
        /// <param name="c">carattere per il fill</param>
        /// <param name="count"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Span<char> FillNext(this Span<char> destination, char c, int count)
        {
            destination[..count].Fill(c);
            return destination[count..];
        }
        #endregion
        #region path
        /// <summary>
        /// Combina due path passati come Span<char>
        /// </summary>
        /// <param name="source"></param>
        /// <param name="pathToCombine"></param>
        /// <param name="endWithSeparator">se true mette il separatore al fondo della stringa</param>
        /// <returns></returns>
        public static Span<char> PathCombine(this Span<char> source, ReadOnlySpan<char> pathToCombine, bool endWithSeparator = false)
        {
            if (pathToCombine.IsEmpty && !endWithSeparator)
                return source;

            // 1. Aggiungiamo il separatore PRIMA, ma solo se NON siamo all'inizio del buffer originale
            Span<char> current = source;
            // se pathToCombine inizia con un separatore, skippo
            if (!pathToCombine.IsEmpty && (pathToCombine[0] == Path.DirectorySeparatorChar || pathToCombine[0] == Path.AltDirectorySeparatorChar))
            {
                pathToCombine = pathToCombine[1..];
            }
            // aggiungo path
            current = current.AppendNext(pathToCombine);
            // separatore finale
            if (endWithSeparator)
            {
                // aggiungo solo se non ce già
                if (pathToCombine.IsEmpty || (pathToCombine[^1] != Path.DirectorySeparatorChar && pathToCombine[^1] != Path.AltDirectorySeparatorChar))
                {
                    current = current.AppendNext(Path.DirectorySeparatorChar);
                }
            }

            return current;
        }
        #endregion
    }
}