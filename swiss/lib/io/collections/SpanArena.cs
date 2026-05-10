using System.Buffers;
using System.Runtime.CompilerServices;

namespace lib.io.collections;

/// <summary>
/// Pool dinamico per gestire sequenze struct non gestite dall'heap (char, byte, int, long...)
/// Come Lista o come Stack
/// i dati vengono disposti in sequenza in un array unico, questo rende impossibile l'ordinamento
/// ottimo per operazioni dove serve lavorare con Stack o Liste classiche
/// Caratteristiche:
/// - non ordinata
/// - contigua in memoria, ottima per cache e cpu
/// - addio stringhe in hot path, usa char e span solo piu
/// </summary>
sealed class SpanArena<T> : IDisposable where T : unmanaged
{
    // array effettivo che conterrà i dati
    private T[] _buffer;
    // memorizza il punto esatto in cui siamo arrivati dentro il buffer
    private int _bufferEnd;
    // indice che memorizza la posizione di ogni elemento nel buffer principale
    private int[] _index;
    // numero degli item salvati effettivamente in index
    private int _indexEnd;
    public int Count => _indexEnd;
    public SpanArena(int initialItems = 1024, int initialSize = 65536)
    {
        _index = ArrayPool<int>.Shared.Rent(initialItems);
        _buffer = ArrayPool<T>.Shared.Rent(initialSize);
    }

    /// <summary>
    /// Pusha un nuovo item
    /// </summary>
    /// <param name="data"></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(ReadOnlySpan<T> data)
    {
        // devo verificare se supero la dimensione, in tal caso devo affittare piu spazio
        int requiredSpace = data.Length;
        // # 1. verifico se i nuovi dati fittano nel pool attuale
        // se la dimensione richiesta per salvare i nuovi dati è < dello spazio attuale:
        // ci tocca affittare un nuovo pool piu grande
        if (_bufferEnd + requiredSpace > _buffer.Length)
        {
            // affitto uno spazio abbastanza grande che equivalga almeno il doppio di quello attuale
            // o di piu se la dimensione dello spazio richiesto è maggiore
            var newBuffer = ArrayPool<T>.Shared.Rent(Math.Max(_buffer.Length * 2, _bufferEnd + requiredSpace));
            // copio il buffer attuale in quello nuovo
            _buffer.AsSpan(0, _bufferEnd).CopyTo(newBuffer);
            // restituisco l'arraypool precedente
            ArrayPool<T>.Shared.Return(_buffer);
            _buffer = newBuffer;
        }

        // # 2. resize degli offset anche
        if (_indexEnd >= _index.Length)
        {
            // affitto il nuovo spazio
            var newIndex = ArrayPool<int>.Shared.Rent(_index.Length * 2);
            // copio da 0 all'elemento corrente sul nuovo indice
            _index.AsSpan(0, _indexEnd).CopyTo(newIndex);
            // restituisco il pool precedente
            ArrayPool<int>.Shared.Return(_index);
            _index = newIndex;
        }

        // # 3. ora posso copiare i nuovi dati finalmente
        _index[_indexEnd] = _bufferEnd;
        _indexEnd++;
        // copio i dati dopo l'ultima posizione 
        data.CopyTo(_buffer.AsSpan(_bufferEnd));
        _bufferEnd += requiredSpace;
    }

    /// <summary>
    /// Estrai e rimuovi l'ultimo elemento
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPop(out ReadOnlySpan<T> data)
    {
        if (_indexEnd == 0)
        {
            data = default;
            return false;
        }

        _indexEnd--;
        int start = _index[_indexEnd];
        int end = _bufferEnd;
        // TODO: implementare logica per diminuire la dimensione del _buffer se abbiamo dimezzato lo spazio occupato
        // diminuisco logicamente lo spazio senza rimuoverlo effettivamente perche sarebbe uno spreco
        _bufferEnd = start;
        // restituisco lo span
        data = _buffer.AsSpan(start, end - start);
        return true;
    }

    /// <summary>
    /// Restituisce l'elemento i-esimo senza rimuoverlo
    /// </summary>
    /// <param name="index"></param>
    /// <returns></returns>
    /// <exception cref="IndexOutOfRangeException"></exception>
    public ReadOnlySpan<T> this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (index < 0 || index >= _indexEnd) throw new IndexOutOfRangeException();

            int start = _index[index];
            int end = index == _indexEnd - 1 ? _bufferEnd : _index[index + 1];

            return _buffer.AsSpan(start, end - start);
        }
    }

    /// <summary>
    /// Restituisce al pool gli array affittati
    /// </summary>
    public void Dispose()
    {
        if (_buffer != null)
        {
            ArrayPool<T>.Shared.Return(_buffer);
            _buffer = null!;
        }
        if (_index != null)
        {
            ArrayPool<int>.Shared.Return(_index);
            _index = null!;
        }
    }
}