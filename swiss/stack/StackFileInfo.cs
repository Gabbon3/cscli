using System.Buffers;
using System.IO.Enumeration;

namespace stack
{
    /// <summary>
    /// StackFileInfo è una struct che memorizza le informazioni di un file/cartella utilizzando strutture che vivono
    /// solo sullo stack, è pensata infatti per operazioni massive ad alte prestazioni dove si vuole evitare l'utilizzo dell'HEAP
    /// Recupera tutte le sue informazioni da FileSystemEntry, memorizza le stringhe come 
    /// </summary>
    public struct StackFileInfo : IDisposable
    {
        public char[] PathBuffer;
        public int PathLength;
        public int NameLength;
        public DateTime CreationTime { get; }
        public DateTime LastAccessTime { get; }
        public DateTime LastWriteTime { get; }
        public long Length { get; }
        public bool IsDirectory { get; }

        public StackFileInfo(ref FileSystemEntry entry)
        {
            CreationTime = entry.CreationTimeUtc.LocalDateTime;
            LastAccessTime = entry.LastAccessTimeUtc.LocalDateTime;
            LastWriteTime = entry.LastWriteTimeUtc.LocalDateTime;
            Length = entry.Length;
            IsDirectory = entry.IsDirectory;

            PathLength = entry.Directory.Length + 1 + entry.FileName.Length;
            NameLength = entry.FileName.Length;

            PathBuffer = ArrayPool<char>.Shared.Rent(PathLength);

            entry.Directory.CopyTo(PathBuffer);
            PathBuffer[entry.Directory.Length] = Path.DirectorySeparatorChar;
            entry.FileName.CopyTo(PathBuffer.AsSpan(entry.Directory.Length + 1));
        }

        /// <summary>
        /// Restituisco il Buffer prenotato all'ArrayPool
        /// </summary>
        public void Dispose()
        {
            if (PathBuffer != null)
            {
                ArrayPool<char>.Shared.Return(PathBuffer, clearArray: false);
                // forzo il buffer a null per sicurezza
                PathBuffer = null!;
            }
        }

        /// ---------
        /// To String
        /// ---------

        public readonly string GetFileName()
        {
            int start = PathLength - NameLength;
            return new string(PathBuffer, start, NameLength);
        }

        public string GetFullPath()
        {
            return new string(PathBuffer, 0, PathLength);
        }

        /// ---------
        ///  To Span
        /// ---------

        public readonly ReadOnlySpan<char> AsNameSpan()
        {
            return PathBuffer.AsSpan(PathLength - NameLength, NameLength);
        }

        public readonly ReadOnlySpan<char> AsPathSpan()
        {
            return PathBuffer.AsSpan(0, PathLength);
        }

        public readonly ReadOnlySpan<char> AsDirectorySpan()
        {
            return PathBuffer.AsSpan(0, PathLength - NameLength);
        }
    }
}