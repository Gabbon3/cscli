using System.Runtime.InteropServices;

namespace utils
{
    public static class NativeIO
    {
        // import dell api nativa di windows
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteFileW(string lpFileName);

        public static bool DeleteFile(string filePath)
        {
            return DeleteFileW(filePath);
        }
    }
}