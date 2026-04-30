namespace lib.core.eliminator
{
    // Il report che ogni worker invia alla UI
    public readonly struct EliminatorProgressReport
    {
        public int WorkerId { get; init; }
        public int FilesDropped { get; init; }
        public long BytesSaved { get; init; }
        
        // Se ce un errore su un file specifico
        public Exception? Error { get; init; }
        public string? FailedFileName { get; init; }
    }

    // Il risultato finale restituito alla fine dell'operazione
    public record EliminatorResult(
        long TotalFilesDropped, 
        long TotalBytesSaved, 
        bool CompletedSuccessfully
    );
}