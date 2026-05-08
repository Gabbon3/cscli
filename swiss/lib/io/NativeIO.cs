using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace lib.io;

/// <summary>
/// Fornisce accesso alle API native di Windows per operazioni su file e directory.
/// Progettato per operazioni massive con zero allocazioni GC sul path di successo.
/// Sul path di errore le allocazioni sono accettabili (eccezioni).
/// </summary>
public static class NativeIO
{
    // --- Costanti per CreateFileW ---
    private const uint GENERIC_WRITE         = 0x40000000;
    private const uint OPEN_ALWAYS           = 4;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    // --- Costanti per MoveFileExW ---
    private const uint MOVEFILE_REPLACE_EXISTING = 0x00000001;
    private const uint MOVEFILE_COPY_ALLOWED     = 0x00000002;

    // --- Import delle API native (char* per zero allocazioni) ---

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool DeleteFileW(char* lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool CreateDirectoryW(char* lpPathName, IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern unsafe SafeFileHandle CreateFileW(
        char* lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool MoveFileExW(char* lpExistingFileName, char* lpNewFileName, uint dwFlags);

    // --- Metodi Pubblici ---

    /// <summary>
    /// Elimina un file dal disco.
    /// Zero allocazioni GC se <paramref name="filePath"/> termina già con '\0'.
    /// </summary>
    /// <param name="filePath">Percorso del file da eliminare.</param>
    /// <returns>true se l'operazione ha successo.</returns>
    /// <exception cref="NativeIOException">
    /// Lanciata quando:
    /// - ERROR_FILE_NOT_FOUND (2): Il file non esiste.
    /// - ERROR_PATH_NOT_FOUND (3): La directory nel percorso non esiste.
    /// - ERROR_ACCESS_DENIED (5): Permessi insufficienti o file in uso.
    /// </exception>
    public static unsafe bool DeleteFile(ReadOnlySpan<char> filePath)
    {
        char[]? rented = null;
        try
        {
            ReadOnlySpan<char> span;
            if (filePath.Length > 0 && filePath[^1] == '\0')
            {
                span = filePath;
            }
            else
            {
                rented = ArrayPool<char>.Shared.Rent(filePath.Length + 1);
                filePath.CopyTo(rented);
                rented[filePath.Length] = '\0';
                span = rented.AsSpan(0, filePath.Length + 1);
            }

            fixed (char* ptr = span)
            {
                if (!DeleteFileW(ptr))
                    ThrowNativeError(Marshal.GetLastWin32Error(), filePath, default, "eliminazione");
            }
            return true;
        }
        finally
        {
            if (rented != null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <inheritdoc cref="DeleteFile(ReadOnlySpan{char})"/>
    public static bool DeleteFile(string filePath)
        => DeleteFile(filePath.AsSpan());

    /// <summary>
    /// Crea una directory. Se la directory esiste già, l'operazione viene considerata un successo.
    /// Zero allocazioni GC se <paramref name="path"/> termina già con '\0'.
    /// </summary>
    /// <param name="path">Percorso della directory da creare.</param>
    /// <returns>true se la directory è stata creata o esiste già.</returns>
    /// <exception cref="NativeIOException">
    /// Lanciata quando:
    /// - ERROR_PATH_NOT_FOUND (3): La directory padre non esiste.
    /// - ERROR_ACCESS_DENIED (5): Permessi insufficienti.
    /// - ERROR_INVALID_PARAMETER (87): Il percorso contiene caratteri non validi.
    /// </exception>
    public static unsafe bool CreateDirectory(ReadOnlySpan<char> path)
    {
        char[]? rented = null;
        try
        {
            ReadOnlySpan<char> span;
            if (path.Length > 0 && path[^1] == '\0')
            {
                span = path;
            }
            else
            {
                rented = ArrayPool<char>.Shared.Rent(path.Length + 1);
                path.CopyTo(rented);
                rented[path.Length] = '\0';
                span = rented.AsSpan(0, path.Length + 1);
            }

            fixed (char* ptr = span)
            {
                if (!CreateDirectoryW(ptr, IntPtr.Zero))
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    if (errorCode == 183) // ERROR_ALREADY_EXISTS
                        return true;

                    ThrowNativeError(errorCode, path, default, "creazione directory");
                }
            }
            return true;
        }
        finally
        {
            if (rented != null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <inheritdoc cref="CreateDirectory(ReadOnlySpan{char})"/>
    public static bool CreateDirectory(string path)
        => CreateDirectory(path.AsSpan());

    /// <summary>
    /// Crea un nuovo file o apre uno esistente con accesso in scrittura.
    /// Zero allocazioni GC se <paramref name="filePath"/> termina già con '\0'.
    /// </summary>
    /// <param name="filePath">Percorso del file da creare.</param>
    /// <returns>true se il file è stato creato o aperto con successo.</returns>
    /// <exception cref="NativeIOException">
    /// Lanciata quando:
    /// - ERROR_PATH_NOT_FOUND (3): La directory nel percorso non esiste.
    /// - ERROR_ACCESS_DENIED (5): Permessi insufficienti o file bloccato.
    /// - ERROR_INVALID_PARAMETER (87): Flags o attributi non validi.
    /// </exception>
    public static unsafe bool CreateFile(ReadOnlySpan<char> filePath)
    {
        char[]? rented = null;
        try
        {
            ReadOnlySpan<char> span;
            if (filePath.Length > 0 && filePath[^1] == '\0')
            {
                span = filePath;
            }
            else
            {
                rented = ArrayPool<char>.Shared.Rent(filePath.Length + 1);
                filePath.CopyTo(rented);
                rented[filePath.Length] = '\0';
                span = rented.AsSpan(0, filePath.Length + 1);
            }

            fixed (char* ptr = span)
            {
                using SafeFileHandle handle = CreateFileW(
                    ptr,
                    GENERIC_WRITE,
                    0,
                    IntPtr.Zero,
                    OPEN_ALWAYS,
                    FILE_ATTRIBUTE_NORMAL,
                    IntPtr.Zero);

                if (handle.IsInvalid)
                    ThrowNativeError(Marshal.GetLastWin32Error(), filePath, default, "creazione file");
            }
            return true;
        }
        finally
        {
            if (rented != null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <inheritdoc cref="CreateFile(ReadOnlySpan{char})"/>
    public static bool CreateFile(string filePath)
        => CreateFile(filePath.AsSpan());

    /// <summary>
    /// Sposta un file da sorgente a destinazione, opzionalmente sovrascrivendo.
    /// Zero allocazioni GC se entrambi i percorsi terminano già con '\0'.
    /// </summary>
    /// <param name="sourcePath">Percorso del file sorgente.</param>
    /// <param name="destinationPath">Percorso del file di destinazione.</param>
    /// <param name="overwrite">Se true, sovrascrive il file di destinazione se esiste. Default: true.</param>
    /// <returns>true se lo spostamento ha successo.</returns>
    /// <exception cref="NativeIOException">
    /// Lanciata quando:
    /// - ERROR_FILE_NOT_FOUND (2): File sorgente non trovato.
    /// - ERROR_PATH_NOT_FOUND (3): Directory di destinazione non esistente.
    /// - ERROR_ACCESS_DENIED (5): Permessi insufficienti o file in uso.
    /// - ERROR_ALREADY_EXISTS (183): Destinazione esiste e <paramref name="overwrite"/> è false.
    /// </exception>
    public static unsafe bool Move(ReadOnlySpan<char> sourcePath, ReadOnlySpan<char> destinationPath, bool overwrite = true)
    {
        uint flags = MOVEFILE_COPY_ALLOWED;
        if (overwrite) flags |= MOVEFILE_REPLACE_EXISTING;

        char[]? srcRented = null;
        char[]? dstRented = null;
        try
        {
            ReadOnlySpan<char> srcSpan;
            if (sourcePath.Length > 0 && sourcePath[^1] == '\0')
            {
                srcSpan = sourcePath;
            }
            else
            {
                srcRented = ArrayPool<char>.Shared.Rent(sourcePath.Length + 1);
                sourcePath.CopyTo(srcRented);
                srcRented[sourcePath.Length] = '\0';
                srcSpan = srcRented.AsSpan(0, sourcePath.Length + 1);
            }

            ReadOnlySpan<char> dstSpan;
            if (destinationPath.Length > 0 && destinationPath[^1] == '\0')
            {
                dstSpan = destinationPath;
            }
            else
            {
                dstRented = ArrayPool<char>.Shared.Rent(destinationPath.Length + 1);
                destinationPath.CopyTo(dstRented);
                dstRented[destinationPath.Length] = '\0';
                dstSpan = dstRented.AsSpan(0, destinationPath.Length + 1);
            }

            fixed (char* pSrc = srcSpan)
            fixed (char* pDst = dstSpan)
            {
                if (!MoveFileExW(pSrc, pDst, flags))
                    ThrowNativeError(Marshal.GetLastWin32Error(), sourcePath, destinationPath, "spostamento");
            }
            return true;
        }
        finally
        {
            if (srcRented != null) ArrayPool<char>.Shared.Return(srcRented);
            if (dstRented != null) ArrayPool<char>.Shared.Return(dstRented);
        }
    }

    /// <inheritdoc cref="Move(ReadOnlySpan{char}, ReadOnlySpan{char}, bool)"/>
    public static bool Move(string sourcePath, string destinationPath, bool overwrite = true)
        => Move(sourcePath.AsSpan(), destinationPath.AsSpan(), overwrite);

    // --- Helper errori (NoInlining per permettere al JIT di inlinare i metodi caldi) ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNativeError(int errorCode, ReadOnlySpan<char> path1, ReadOnlySpan<char> path2, string operation)
    {
        // Rimuove il terminatore nullo per la visualizzazione
        if (path1.Length > 0 && path1[^1] == '\0') path1 = path1[..^1];
        if (path2.Length > 0 && path2[^1] == '\0') path2 = path2[..^1];

        string p1 = new(path1);
        string p2 = path2.IsEmpty ? "" : new(path2);

        throw errorCode switch
        {
            2   => new NativeIOException($"File non trovato durante l'{operation}: '{p1}'", errorCode, "ERROR_FILE_NOT_FOUND"),
            3   => new NativeIOException($"Percorso non trovato durante l'{operation}: '{p1}'{(p2.Length > 0 ? $" -> '{p2}'" : "")}", errorCode, "ERROR_PATH_NOT_FOUND"),
            5   => new NativeIOException($"Accesso negato durante l'{operation} di '{p1}'.", errorCode, "ERROR_ACCESS_DENIED"),
            87  => new NativeIOException($"Parametri non validi durante l'{operation} di '{p1}'.", errorCode, "ERROR_INVALID_PARAMETER"),
            183 => new NativeIOException($"Il file di destinazione esiste già: '{p2}'.", errorCode, "ERROR_ALREADY_EXISTS"),
            _   => new NativeIOException($"Errore {errorCode} durante l'{operation} di '{p1}'.", errorCode, "UNKNOWN_ERROR")
        };
    }
}

/// <summary>
/// Eccezione custom per operazioni native di I/O su Windows.
/// Fornisce il codice numerico e il nome simbolico dell'errore Win32.
/// </summary>
public sealed class NativeIOException : Exception
{
    /// <summary>Codice di errore Win32 (da GetLastError).</summary>
    public int ErrorCode { get; }

    /// <summary>Nome simbolico dell'errore (es: "ERROR_ACCESS_DENIED").</summary>
    public string ErrorName { get; }

    /// <param name="message">Messaggio descrittivo dell'errore.</param>
    /// <param name="errorCode">Codice Win32 dell'errore.</param>
    /// <param name="errorName">Nome simbolico dell'errore.</param>
    public NativeIOException(string message, int errorCode, string errorName)
        : base($"{message} [ErrorCode: {errorCode}, ErrorName: {errorName}]")
    {
        ErrorCode = errorCode;
        ErrorName = errorName;
    }

    public override string ToString()
        => $"NativeIOException: {Message}\n  ErrorCode: {ErrorCode}\n  ErrorName: {ErrorName}";
}