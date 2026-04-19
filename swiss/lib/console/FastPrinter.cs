using System.Threading.Channels;

namespace lib.console;

/// <summary>
/// Classe utilizzata per stampare a Console ad alte prestazioni utilizzando un channel
/// </summary>
public class FastPrinter
{
    private readonly Channel<string> _channel;
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
        _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions()
        {
            SingleReader = true,
            SingleWriter = options.SingleWriter
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
                    ConsolePlus.Write(item);
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
    public async ValueTask PostAsync(string item) => await _channel.Writer.WriteAsync(item);
    /// <summary>
    /// Prova a posta il contenuto della console nel channel in maniera sincrona, quindi non viene atteso l'inserimento
    /// </summary>
    /// <param name="item">item T : IPrintable</param>
    /// <returns>Se false allora non è stato possibile scrivere nel channel</returns>
    public bool TryPost(string item) => _channel.Writer.TryWrite(item);
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