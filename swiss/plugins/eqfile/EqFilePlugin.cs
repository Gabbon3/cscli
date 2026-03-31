using plugins;

namespace swiss.plugins.eqfile
{
    class EqFilePlugin : Plugin
    {
        public override string Name => "eqfile";
        public override string Description => "compara due file verificando se sono identici a livello di bit";

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            if (args.Length < 2)
            {
                Help();
                return;
            }
            // params
            string pathA = args[0];
            string pathB = args[1];
            // avvio il metodo
            bool filesAreEqual = AreFilesEqual(pathA, pathB);
            if (filesAreEqual)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("I file sono equivalenti");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("I file non sono equivalenti");
            }
            Console.ResetColor();
        }

        private bool AreFilesEqual(string pathA, string pathB)
        {
            if (!File.Exists(pathA) || !File.Exists(pathB))
                throw new FileNotFoundException();

            // controllo prima le dimensioni
            var infoA = new FileInfo(pathA);
            var infoB = new FileInfo(pathB);

            if (infoA.Length != infoB.Length)
            {
                return false;
            }

            // comparo a blocchi da 1 MB
            const int bufferSize = 1024 * 1024;

            byte[] bufferA = new byte[bufferSize];
            byte[] bufferB = new byte[bufferSize];

            using (FileStream fsA = new FileStream(pathA, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan))
            using (FileStream fsB = new FileStream(pathB, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan))
            {
                int bytesReadA;
                // itero solo su a perche la lunghezza dei due file è equivalente
                while ((bytesReadA = fsA.Read(bufferA, 0, bufferA.Length)) > 0)
                {
                    int bytesReadB = fsB.Read(bufferB, 0, bufferB.Length);
                    // controllo nuovamente dimensione buffer
                    if (bytesReadA != bytesReadB)
                        return false;

                    // otteniamo solamente lo span grezzo
                    ReadOnlySpan<byte> spanA = bufferA.AsSpan(0, bytesReadA);
                    ReadOnlySpan<byte> spanB = bufferB.AsSpan(0, bytesReadB);

                    // utilizziamo istruzioni SIMD per confrontare blocchi di byte per ciclo di clock
                    if (!spanA.SequenceEqual(spanB))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public override void Help()
        {
            Console.WriteLine("swiss eqfile <path_A> <path_B>");
        }
    }
}
