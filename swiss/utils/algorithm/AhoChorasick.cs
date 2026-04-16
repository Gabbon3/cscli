namespace utils.text;

// ─────────────────────────────────────────────────────────────────────────────
// Contratto per handler strutturato (JIT inline, zero virtual dispatch)
// ─────────────────────────────────────────────────────────────────────────────
public interface IMatchHandler
{
    void OnMatch(int startIndex, int endIndex, int patternIndex, int relativeLine);
}

// ─────────────────────────────────────────────────────────────────────────────
// Risultato singolo match — struct pura, layout sequenziale, cache-friendly
// ─────────────────────────────────────────────────────────────────────────────
public readonly struct MatchResult
{
    public readonly int StartIndex;
    public readonly int EndIndex;
    public readonly int PatternIndex;
    public readonly int Line;

    public MatchResult(int start, int end, int pattern, int line)
    {
        StartIndex   = start;
        EndIndex     = end;
        PatternIndex = pattern;
        Line         = line;
    }

    public int Length => EndIndex - StartIndex + 1;

    public override string ToString()
        => $"[{StartIndex}..{EndIndex}] pat={PatternIndex} line={Line}";
}

// ─────────────────────────────────────────────────────────────────────────────
// Handler interno per SearchAll: accumula match in un array che cresce
// per raddoppio (come List<T> ma senza boxing, senza IEnumerable overhead).
// È una struct → il JIT la inlina completamente dentro Search<THandler>.
// ─────────────────────────────────────────────────────────────────────────────
public struct MatchAccumulator : IMatchHandler
{
    private MatchResult[] _buffer;
    private int           _count;

    // Capacità iniziale: passa 0 per default (16).
    public MatchAccumulator(int initialCapacity)
    {
        _buffer = new MatchResult[initialCapacity > 0 ? initialCapacity : 16];
        _count  = 0;
    }

    public void OnMatch(int startIndex, int endIndex, int patternIndex, int line)
    {
        if (_count == _buffer.Length)
            Grow();

        _buffer[_count++] = new MatchResult(startIndex, endIndex, patternIndex, line);
    }

    /// <summary>
    /// Slice diretto sul buffer interno — zero copie.
    /// Valido finché questo MatchAccumulator è in scope e non viene modificato.
    /// </summary>
    public readonly ReadOnlySpan<MatchResult> AsSpan() => _buffer.AsSpan(0, _count);

    /// <summary>
    /// Copia i match nel buffer esterno fornito dal chiamante.
    /// Restituisce il numero di match copiati.
    /// </summary>
    public readonly int CopyTo(Span<MatchResult> destination)
    {
        AsSpan().CopyTo(destination);
        return _count;
    }

    /// <summary>
    /// Alloca e restituisce un array esatto. È la sola alloc del percorso SearchAll.
    /// </summary>
    public readonly MatchResult[] ToArray()
    {
        var result = new MatchResult[_count];
        _buffer.AsSpan(0, _count).CopyTo(result);
        return result;
    }

    public readonly int Count => _count;

    private void Grow()
    {
        var next = new MatchResult[_buffer.Length * 2];
        _buffer.AsSpan().CopyTo(next);
        _buffer = next;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Aho-Corasick DFA — struttura flat, zero heap in Search, O(n) garantito
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Aho-Corasick multi-pattern search.
/// Zero heap allocations dopo la build: niente classi nodo, niente List,
/// niente string — tutto in array flat pre-allocati.
///
/// Layout della trie (struttura a array paralleli):
///   _goto    [node * ALPHA + c]  → next node  (DFA completo, mai -1 dopo build)
///   _fail    [node]              → failure link
///   _output  [node]              → primo pattern che finisce qui (-1 = nessuno)
///   _dict    [node]              → dict-suffix link (-1 = nessuno)
///   _patLen  [patternIndex]      → lunghezza del pattern
/// </summary>
public sealed class AhoCorasick
{
    private const int ALPHA = 256;

    private readonly int[] _goto;
    private readonly int[] _fail;
    private readonly int[] _output;
    private readonly int[] _dict;
    private readonly int[] _patLen;

    private readonly int _nodeCount;
    private readonly int _patCount;

    // ─────────────────────────────────────────────────────────────────────────
    // BUILD
    // ─────────────────────────────────────────────────────────────────────────

    public AhoCorasick(ReadOnlySpan<ReadOnlyMemory<byte>> patterns)
    {
        _patCount = patterns.Length;
        _patLen   = new int[_patCount];

        int maxNodes = 1;
        for (int i = 0; i < _patCount; i++)
        {
            _patLen[i] = patterns[i].Length;
            maxNodes  += patterns[i].Length;
        }

        _goto   = new int[maxNodes * ALPHA];
        _fail   = new int[maxNodes];
        _output = new int[maxNodes];
        _dict   = new int[maxNodes];

        _goto.AsSpan().Fill(-1);
        _fail.AsSpan().Fill(0);
        _output.AsSpan().Fill(-1);
        _dict.AsSpan().Fill(-1);

        int usedNodes = 1;

        // Fase 1 — inserimento trie
        for (int pi = 0; pi < _patCount; pi++)
        {
            ReadOnlySpan<byte> pat = patterns[pi].Span;
            int cur = 0;

            for (int ci = 0; ci < pat.Length; ci++)
            {
                int gSlot = (cur << 8) + pat[ci];
                if (_goto[gSlot] == -1)
                    _goto[gSlot] = usedNodes++;
                cur = _goto[gSlot];
            }

            if (_output[cur] == -1)
                _output[cur] = pi;
        }

        // Fase 2 — failure links via BFS
        // stackalloc sotto soglia, heap sopra: nessun rischio stack overflow
        Span<int> queue = usedNodes <= 4096
            ? stackalloc int[usedNodes]
            : new int[usedNodes];

        int head = 0, tail = 0;

        for (int c = 0; c < ALPHA; c++)
        {
            if (_goto[c] == -1)
            {
                _goto[c] = 0;
            }
            else
            {
                int child = _goto[c];
                _fail[child] = 0;
                queue[tail++] = child;
            }
        }

        while (head < tail)
        {
            int r = queue[head++];

            for (int c = 0; c < ALPHA; c++)
            {
                int slot  = (r << 8) + c;
                int child = _goto[slot];

                if (child == -1)
                {
                    // DFA completo: propaga goto del failure → O(1) garantito in Search
                    _goto[slot] = _goto[(_fail[r] << 8) + c];
                }
                else
                {
                    _fail[child] = _goto[(_fail[r] << 8) + c];

                    int failNode = _fail[child];
                    _dict[child] = _output[failNode] != -1
                        ? failNode
                        : _dict[failNode];

                    queue[tail++] = child;
                }
            }
        }

        _nodeCount = usedNodes;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SEARCH — handler struct (JIT inline, zero virtual dispatch)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scorre il testo e notifica ogni match tramite <paramref name="handler"/>.
    /// Il constraint <c>where THandler : struct</c> permette al JIT di
    /// specializzare e inlinare completamente la chiamata — zero overhead
    /// rispetto a scrivere il loop a mano.
    /// </summary>
    public void Search<THandler>(ReadOnlySpan<byte> text, ref THandler handler)
        where THandler : struct, IMatchHandler
    {
        int state       = 0;
        int currentLine = 0;

        for (int i = 0; i < text.Length; i++)
        {
            int c = text[i];
            if (c == '\n') currentLine++;

            state = _goto[(state << 8) + c];

            // Fast-path: salta il while se non c'è output da emettere
            if (_output[state] == -1 && _dict[state] == -1) continue;

            int s = state;
            while (s != -1)
            {
                if (_output[s] != -1)
                {
                    int pi = _output[s];
                    handler.OnMatch(i - _patLen[pi] + 1, i, pi, currentLine);
                }
                s = _dict[s];
            }
        }
    }

    /// <summary>Overload per Span&lt;byte&gt; mutabile.</summary>
    public void Search<THandler>(Span<byte> text, ref THandler handler)
        where THandler : struct, IMatchHandler
        => Search((ReadOnlySpan<byte>)text, ref handler);

    // ─────────────────────────────────────────────────────────────────────────
    // SEARCH ALL — stile Regex.Matches
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Restituisce tutti i match come <c>MatchResult[]</c>.
    /// Internamente usa <see cref="MatchAccumulator"/> (struct) → il loop di
    /// raccolta è identico a <see cref="Search{THandler}"/>, zero virtual dispatch.
    /// L'unica allocazione heap è il <c>MatchResult[]</c> finale.
    /// </summary>
    /// <param name="text">Testo da scansionare.</param>
    /// <param name="initialCapacity">
    /// Stima del numero atteso di match (evita raddoppi dell'accumulatore).
    /// 0 = default (16).
    /// </param>
    public MatchResult[] SearchAll(ReadOnlySpan<byte> text, int initialCapacity = 0)
    {
        var acc = new MatchAccumulator(initialCapacity);
        Search(text, ref acc);
        return acc.ToArray();
    }

    /// <summary>
    /// Variante zero-alloc: scrive i match nel buffer esterno fornito dal chiamante.
    /// Restituisce il numero di match trovati.
    /// Nessuna allocazione heap se il buffer è abbastanza grande.
    /// </summary>
    public int SearchAll(ReadOnlySpan<byte> text, Span<MatchResult> destination)
    {
        // Usa la capacità del destination come hint per l'accumulatore interno
        var acc = new MatchAccumulator(destination.Length);
        Search(text, ref acc);
        return acc.CopyTo(destination);
    }

    /// <summary>
    /// Variante lazy: espone direttamente lo <see cref="ReadOnlySpan{MatchResult}"/>
    /// del buffer interno — nessuna copia finale.
    /// Utile per leggere i risultati sul posto senza tenerli in giro.
    /// ATTENZIONE: lo span è valido solo finché <paramref name="accumulator"/> è in scope.
    /// </summary>
    public ReadOnlySpan<MatchResult> SearchAll(
        ReadOnlySpan<byte>   text,
        out MatchAccumulator accumulator,
        int                  initialCapacity = 0)
    {
        accumulator = new MatchAccumulator(initialCapacity);
        Search(text, ref accumulator);
        return accumulator.AsSpan();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DIAGNOSTICS
    // ─────────────────────────────────────────────────────────────────────────

    public int NodeCount => _nodeCount;
    public int PatCount  => _patCount;
}
// PostScript: grazie Claude per l'aiuto dello sviluppo di questo componente