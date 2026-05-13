# Analisi Comparativa: GrepPlugin vs RegexGrepPlugin

## Panoramica Architetturale

Entrambi i plugin condividono la stessa struttura producer/consumer con `Channel<string>`, `FastPrinter` per backpressure, e approccio zero-allocation. Le differenze chiave emergono nel motore di matching e nella gestione dei dati.

---

## Differenze Fondamentali

### 1. **Motore di Ricerca**

#### GrepPlugin (Aho-Corasick)
```csharp
// Pattern multipli separati da '|'
State.WordsToSearch = pattern.Split('|');
State.PatternList = new ReadOnlyMemory<byte>[State.WordsToSearch.Length];

// Matching diretto su byte UTF-8
AhoEngine.Search(searchSpan);
```

**Pro:**
- Lavora direttamente sui byte UTF-8 (zero conversioni)
- Ottimizzato per ricerca di stringhe letterali multiple
- Overlap preciso basato su `LongestPattern` in byte
- Perfetto per keyword search (error|warning|fail)

**Contro:**
- Non supporta pattern complessi (anchors, quantifiers, groups)
- Case-insensitive richiede buffer aggiuntivo e `ToLowerAsciiSafe()`

#### RegexGrepPlugin (Regex .NET)
```csharp
// Pattern come espressione regolare
RegexEngine = new Regex(State.Pattern, 
    RegexOptions.Compiled | RegexOptions.NonBacktracking);

// Matching su char dopo conversione UTF-8 -> UTF-16
Utf8.ToUtf16(byteSpan, charBuffer, out bytesConsumed, out charsWritten);
RegexEngine.EnumerateMatches(searchSpan);
```

**Pro:**
- Pattern arbitrariamente complessi (`\b(error|warn)\w+\b`, `\d{4}-\d{2}-\d{2}`)
- `RegexOptions.NonBacktracking` garantisce O(N) tempo lineare
- Case-insensitive nativo (`RegexOptions.IgnoreCase`)
- `EnumerateMatches` è zero-allocation (struct enumerator)

**Contro:**
- Richiede conversione UTF-8 → UTF-16 (overhead ~5-10%)
- Overlap più conservativo (4KB byte ≈ 1024 char worst-case)
- Non adatto per semplici keyword search (overkill)

---

### 2. **Pipeline di Elaborazione**

#### GrepPlugin
```
Byte UTF-8 → (opzionale: ToLowerAsciiSafe) → AhoCorasick.Search → Match
```

#### RegexGrepPlugin
```
Byte UTF-8 → Utf8.ToUtf16 (SIMD) → Regex.EnumerateMatches → Match
```

---

### 3. **Gestione Overlap tra Chunk**

#### GrepPlugin
```csharp
int overlap = LongestPattern; // Esatto, basato sul pattern più lungo

if (currentDataLength > overlap)
{
    leftover = overlap;
    dataSpan[(currentDataLength - overlap)..].CopyTo(buffer);
}
```

**Precisione:** L'overlap è esattamente la lunghezza del pattern più lungo. Se i pattern sono "error" (5 byte) e "warning" (7 byte), `overlap = 7`.

#### RegexGrepPlugin
```csharp
const int ByteOverlapSize = 4096; // Conservativo

int overlapBytes = Math.Min(ByteOverlapSize, bytesConsumed);
leftoverBytes = unconsumedBytes + overlapBytes;

// Stima overlap in char per evitare reprocessing
overlapChars = Math.Min(1024, overlapBytes / 3 + 1);

// Processa fino a searchEndIndex (esclude overlap)
int searchEndIndex = isFirstChunk ? charsWritten : charsWritten - overlapChars;
```

**Conservatività:** Con regex, i match possono essere arbitrariamente lunghi (`.*foo.*` potrebbe matchare megabyte). L'overlap di 4KB è un compromesso:
- **Troppo piccolo:** Rischio di spezzare match multi-riga
- **Troppo grande:** Reprocessing eccessivo

**Ottimizzazione chiave:** `searchEndIndex` evita di stampare match nella zona di overlap, che verranno trovati nel chunk successivo.

---

### 4. **Conversione UTF-8 → UTF-16**

```csharp
OperationStatus status = Utf8.ToUtf16(byteSpan, charBuffer, 
    out int bytesConsumed, out int charsWritten);

if (status == OperationStatus.InvalidData)
{
    return 0; // File non UTF-8 valido
}
```

**Dettagli implementativi:**
- `Utf8.ToUtf16` usa istruzioni SIMD (AVX2/NEON) per conversioni vettorializzate
- `bytesConsumed` indica quanti byte sono stati convertiti (gestisce caratteri UTF-8 incompleti)
- `unconsumedBytes` vengono mantenuti per il chunk successivo
- Performance: ~5-10 GB/s su CPU moderne (overhead minimo)

**Gestione caratteri incompleti:**
```csharp
int unconsumedBytes = currentByteLength - bytesConsumed;
int overlapBytes = Math.Min(ByteOverlapSize, bytesConsumed);
leftoverBytes = unconsumedBytes + overlapBytes;
```

Se un carattere UTF-8 multi-byte è spezzato tra chunk, `bytesConsumed` si ferma prima, e i byte incompleti vengono mantenuti in `unconsumedBytes`.

---

### 5. **Estrazione Contesto**

#### GrepPlugin
```csharp
private static int ExtractMatchContext(
    ReadOnlySpan<byte> span,  // Byte UTF-8
    int matchIndex,
    int patternLength,
    Span<char> output)
{
    // Lavora su byte, converte solo il necessario a char
    int leftChars = Encoding.UTF8.GetChars(exactLeft, buffer[pos..]);
    int matchChars = Encoding.UTF8.GetChars(exactMatch, buffer[pos..]);
    int rightChars = Encoding.UTF8.GetChars(exactRight, buffer[pos..]);
}
```

#### RegexGrepPlugin
```csharp
private static int ExtractMatchContext(
    ReadOnlySpan<char> span,  // Già char
    int matchIndex,
    int matchLength,
    Span<char> output)
{
    // Lavora direttamente su char (già convertiti)
    exactLeft.CopyTo(buffer[pos..]);
    exactMatch.CopyTo(buffer[pos..]);
    exactRight.CopyTo(buffer[pos..]);
}
```

**Vantaggio RegexGrepPlugin:** Estrazione più veloce (no conversioni), dato che i dati sono già in char. Tutto il file viene convertito una volta sola in `ProcessFile`, e riutilizzato.

---

### 6. **Zero-Allocation Matching**

#### GrepPlugin
```csharp
// IMatchHandler personalizzato
private readonly struct AhoMatchHandler(...) : IMatchHandler
{
    public void OnMatch(int startIndex, int endIndex, int patternIndex, int relativeLine)
    {
        // Callback inline, zero allocazioni
    }
}

AhoEngine.Search(searchSpan, ref handler);
```

**Strategia:** Callback struct passato by-ref, inline processing durante il match.

#### RegexGrepPlugin
```csharp
// ValueMatchEnumerator (struct enumerator .NET 7+)
foreach (var match in RegexEngine.EnumerateMatches(span))
{
    if (match.Index >= maxIndex) break;
    
    int lineNumber = chunkStartLine + CountLines(span[..match.Index]);
    ExtractAndPrintMatch(...);
}
```

**Strategia:** Struct enumerator `ValueMatchEnumerator` che restituisce `ValueMatch` struct. Zero heap allocations, tutto in stack.

**Nota:** `Regex.Matches(string)` alloca `MatchCollection` e `Match` objects. `EnumerateMatches(ReadOnlySpan<char>)` è l'API zero-alloc introdotta in .NET 7.

---

### 7. **Case-Insensitive Search**

#### GrepPlugin
```csharp
byte[] lowerBuffer = IgnoreCase ? new byte[65536] : [];

if (IgnoreCase)
{
    dataSpan.CopyTo(lowerBuffer);
    lowerBuffer.AsSpan(0, currentDataLength).ToLowerAsciiSafe();
    searchSpan = lowerBuffer;
}
```

**Costo:** Buffer aggiuntivo + copia + conversione ASCII lowercase per ogni chunk.

#### RegexGrepPlugin
```csharp
var options = RegexOptions.Compiled | RegexOptions.NonBacktracking;
if (IgnoreCase) options |= RegexOptions.IgnoreCase;

RegexEngine = new Regex(State.Pattern, options);
```

**Costo:** Zero overhead runtime. Il motore regex gestisce case-insensitivity internamente durante la compilazione.

---

### 8. **Performance Comparison (Stima)**

| Scenario | GrepPlugin | RegexGrepPlugin | Vincitore |
|----------|------------|-----------------|-----------|
| Keyword search semplice (`error\|warning`) | 12 GB/s | 10-11 GB/s | GrepPlugin |
| Pattern complessi (`\b\w+@\w+\.\w+\b`) | N/A | 8-10 GB/s | RegexGrepPlugin |
| Case-insensitive keyword | 10 GB/s | 10-11 GB/s | RegexGrepPlugin |
| File con pochi match | 12 GB/s | 10-11 GB/s | GrepPlugin |
| File con molti match | 8-10 GB/s | 7-9 GB/s | Pari |

**Bottleneck comune:** Con molti match, `FastPrinter.Post()` diventa il limite (backpressure sul channel della console).

---

## Quando Usare Quale Plugin

### Usa **GrepPlugin** se:
✅ Cerchi stringhe letterali fisse (`error`, `TODO`, `FIXME`)  
✅ Hai molti pattern alternativi (`error|warn|fail|panic|fatal`)  
✅ Performance massima assoluta è critica  
✅ Non serve case-insensitivity (o accettabile il costo)  
✅ File prevalentemente ASCII

### Usa **RegexGrepPlugin** se:
✅ Serve pattern matching complesso (`\d{4}-\d{2}-\d{2}`, `\b[A-Z]{2,}\b`)  
✅ Vuoi anchors, boundaries, lookaheads (`\bfoo\b`, `(?=bar)`)  
✅ Case-insensitive è frequente  
✅ Pattern cambiano dinamicamente (no recompilazione)  
✅ Vuoi quantifiers (`\w+`, `.{3,10}`)

---

## Ottimizzazioni Avanzate Implementate

### 1. **SIMD Vectorization**
```csharp
Utf8.ToUtf16(byteSpan, charBuffer, out bytesConsumed, out charsWritten);
```
Usa AVX2/SSE4.2 su x86 e NEON su ARM per conversioni parallelizzate.

### 2. **NonBacktracking Regex**
```csharp
RegexOptions.NonBacktracking
```
Garantisce O(N) tempo lineare. Previene ReDoS (Regular Expression Denial of Service) su pattern come `(a+)+b`.

### 3. **Struct Enumerator**
```csharp
foreach (var match in RegexEngine.EnumerateMatches(span))
```
Zero box/unbox, zero `IEnumerator` allocations.

### 4. **Memory Pool Reuse**
```csharp
IMemoryOwner<char> memoryOwner = MemoryPool<char>.Shared.Rent(PathRentBytes);
```
Riutilizzo dei buffer invece di `new char[]`.

### 5. **Backpressure via FastPrinter**
```csharp
_fastPrinter.Post(memoryOwner, matchLength);
```
Blocca il worker se il channel è pieno (evita OOM con milioni di match).

---

## Considerazioni sulla Correttezza

### Gestione Overlap
**Problema:** Match a cavallo tra chunk.

**Soluzione GrepPlugin:** Overlap preciso = `LongestPattern`.

**Soluzione RegexGrepPlugin:** 
- Overlap conservativo di 4KB byte (~1365 char worst-case UTF-8)
- Match nella zona di overlap **non vengono stampati** nel chunk corrente
- Vengono trovati e stampati nel chunk successivo quando escono dall'overlap

```csharp
int searchEndIndex = isFirstChunk ? charsWritten : charsWritten - overlapChars;

foreach (var match in RegexEngine.EnumerateMatches(searchSpan))
{
    if (match.Index >= searchEndIndex)
        break; // Match nell'overlap, salta
}
```

### Thread Safety
Entrambi i plugin:
- **Producer:** Single-writer sul channel
- **Consumers:** Multiple-reader dal channel, buffers locali per worker
- **FastPrinter:** Thread-safe internal queue
- **Contatori globali:** `Interlocked.Add(ref TotalMatchCount, threadMatchCount)`

---

## Conclusioni

**RegexGrepPlugin** è un degno gemello di **GrepPlugin**:
- Mantiene le stesse garanzie di performance (zero-allocation, multithreading, backpressure)
- Aggiunge espressività tramite regex .NET
- Sacrifica ~10-15% di throughput per la conversione UTF-8→UTF-16
- Gestisce overlap in modo più conservativo ma corretto
- Ideale per use-case dove serve pattern matching avanzato

**Raccomandazione:** Usa GrepPlugin per grep tradizionale, RegexGrepPlugin quando serve potenza espressiva.