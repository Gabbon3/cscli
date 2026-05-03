using System.Buffers;
using System.Threading.Channels;

namespace lib.console;

public readonly struct PrintPayload : IDisposable
{
    private readonly string? _text;
    private readonly IMemoryOwner<char>? _memoryOwner;
    private readonly int _length;
    // espone la memoria a prescindere dall'origine
    public ReadOnlyMemory<char> Memory => _text != null 
        ? _text.AsMemory() 
        : _memoryOwner!.Memory[.._length];
    // costruttore per le stringhe
    public PrintPayload(string text)
    {
        _text = text;
        _memoryOwner = null;
        _length = text.Length;
    }
    // costrutture zero allocazioni
    public PrintPayload(IMemoryOwner<char> memoryOwner, int length)
    {
        _text = null;
        _memoryOwner = memoryOwner;
        _length = length;
    }
    // implemento dispose
    public void Dispose()
    {
        _memoryOwner?.Dispose();
    }
}

/// <summary>
/// Classe utilizzata per stampare a Console ad alte prestazioni utilizzando un channel
/// </summary>
public class FastPrinter
{
    private readonly Channel<PrintPayload> _channel;
    private Task? _fastPrinterTask;

    /// <summary>
    /// Opzioni per FastPrinter
    /// </summary>
    /// <param name="singleWriter">Se false (default) indica che il Channel verrà scritto da più thread simultaneamente</param>
    /// <param name="capacity">Dimensioni massime del Channel utilizzato per incodare i messaggi da stampare (default 10000)</param>
    public readonly struct FastPrinterOptions(bool singleWriter = false, int capacity = 10000)
    {
        public bool SingleWriter { get; } = singleWriter;
        public int Capacity { get; } = capacity;
    }
    /// <summary>
    /// Definisci i parametri di stampa customizzati
    /// </summary>
    /// <param name="options">opzioni di stampa personalizzate</param>
    public FastPrinter(FastPrinterOptions options)
    {
        _channel = Channel.CreateBounded<PrintPayload>(new BoundedChannelOptions(options.Capacity)
        {
            SingleReader = true,
            SingleWriter = options.SingleWriter,
            FullMode = BoundedChannelFullMode.DropNewest
        });
    }
    /// <summary>
    /// Utilizza i parametri di default di FastPrinterOptions
    /// </summary>
    public FastPrinter() : this(new FastPrinterOptions(false, 10000)) { }
    /// <summary>
    /// Metodo principale che avvia il task di scrittura su console
    /// </summary>
    /// <param name="ct"></param>
    public void Run(CancellationToken ct)
    {
        _fastPrinterTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in _channel.Reader.ReadAllAsync())
                {
                    using (item)
                    {
                        ConsolePlus.Write(item.Memory);
                    }
                }
            }
            catch (OperationCanceledException) { /* operazione cancellata a mano dall'utente */ }
            catch (Exception) { /* non riuscita stampa */ }
        }, ct);
    }
    /// <summary>
    /// Posta il contenuto della console da stampare nel channel
    /// </summary>
    /// <param name="item">item T : IPrintable</param>
    /// <returns></returns>
    public ValueTask PostAsync(IMemoryOwner<char> owner, int length)
    {
        return _channel.Writer.WriteAsync(new PrintPayload(owner, length));
    }
    // Supporto per retrocompatibilità con le stringhe
    public ValueTask PostAsync(string item)
    {
        return _channel.Writer.WriteAsync(new PrintPayload(item));
    }
    /// <summary>
    /// Prova a postare il contenuto della console nel channel in maniera sincrona, quindi non viene atteso l'inserimento
    /// </summary>
    /// <param name="item">item T : IPrintable</param>
    /// <returns>Se false allora non è stato possibile scrivere nel channel</returns>
    public bool TryPost(IMemoryOwner<char> owner, int length)
    {
        return _channel.Writer.TryWrite(new PrintPayload(owner, length));
    }
    // Supporto per retrocompatibilità con le stringhe
    public bool TryPost(string item)
    {
        return _channel.Writer.TryWrite(new PrintPayload(item));
    }
    /// <summary>
    /// Chiudi il channel e attendo il completamento del task
    /// </summary>
    /// <returns></returns>
    public async Task Complete()
    {
        _channel.Writer.Complete();
        if (_fastPrinterTask != null) await _fastPrinterTask;
    }
}