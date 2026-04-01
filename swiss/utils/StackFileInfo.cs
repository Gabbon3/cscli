using System.Buffers;
using System.IO.Enumeration;

namespace utils
{
    public struct StackFileInfo
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
            // ... (il tuo costruttore rimane uguale) ...
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

        public readonly string GetFileName()
        {
            int start = PathLength - NameLength;
            return new string(PathBuffer, start, NameLength);
        }

        public string GetFullPath()
        {
            return new string(PathBuffer, 0, PathLength);
        }

        public readonly ReadOnlySpan<char> AsNameSpan()
        {
            return PathBuffer.AsSpan(PathLength - NameLength, NameLength);
        }

        public readonly ReadOnlySpan<char> AsPathSpan()
        {
            return PathBuffer.AsSpan(0, PathLength);
        }
    }
}