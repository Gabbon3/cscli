using lib.console;
using System.Security.Cryptography;
using System.Text;

namespace plugins.cripto
{
    internal class CriptoPlugin : Plugin
    {
        public override string Name => "cripto";
        public override string Description => "Crittografia ad alte prestazioni con streaming AES-GCM e DPAPI";

        private string? KeyPath = null;
        private const int ChunkSize = 1024 * 1024; // 1MB per chunk

        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            ParseArguments(args);

            // NOME CHIAVE DPAPI
            string? dpapiKeyName = Options.TryGetValue("--tpm", out var tkn) ? tkn : Options.TryGetValue("-k", out var tk) ? tk : null;
            if (!string.IsNullOrEmpty(dpapiKeyName))
            {
                KeyPath = GetKeyPath(dpapiKeyName);
            }

            // SETUP
            bool isSetup = Options.ContainsKey("--setup") || Options.ContainsKey("-s");
            if (isSetup && string.IsNullOrEmpty(dpapiKeyName))
            {
                PrintError("Devi identificare la chiave che andrai a salvare utilizzando il comando --tpm | -k 'nome_chiave'");
                return;
            }
            if (isSetup)
            {
                SetupMasterKey();
                return;
            }

            // RECUPERO PASSWORD (da DPAPI o Manuale)
            string? password = GetPassword(dpapiKeyName);
            if (string.IsNullOrEmpty(password))
            {
                PrintError("Nessuna password fornita o configurata. Annullato.");
                return;
            }

            // TARGET CHECK
            var target = Options.TryGetValue("--target", out var trg) ? trg : Options.TryGetValue("-t", out var tr) ? tr : null;
            if (string.IsNullOrEmpty(target))
            {
                PrintError("Nessun target definito, utilizza --target | -t 'percorso file'");
                return;
            }

            if (!File.Exists(target))
            {
                PrintError($"Il file specificato non esiste: {target}");
                return;
            }

            // DERIVAZIONE CHIAVE AES (fatta una volta sola per massimizzare le performance)
            // Usiamo un salt fisso per la Master Key in modo che la stessa password generi la stessa chiave su PC diversi
            byte[] masterSalt = Encoding.UTF8.GetBytes("Swiss_Master_Salt_2024_V1");
            byte[] aesKey = DeriveKey(password, masterSalt);

            // Pulisce la password in chiaro dalla memoria il prima possibile
            password = null;

            // ESECUZIONE
            try
            {
                if (Options.ContainsKey("--enc"))
                {
                    await EncryptFileStreaming(target, aesKey);
                }
                else if (Options.ContainsKey("--dec"))
                {
                    await DecryptFileStreaming(target, aesKey);
                }
                else
                {
                    Help();
                }
            }
            finally
            {
                // Assicuriamoci di pulire sempre la chiave AES dalla RAM, anche se c'è un'eccezione
                Array.Clear(aesKey, 0, aesKey.Length);
            }
        }

        // ==========================================
        // GESTIONE INPUT E DPAPI
        // ==========================================

        private string? GetPassword(string? dpapiKeyName)
        {
            if (!string.IsNullOrEmpty(dpapiKeyName))
            {
                return GetMasterKey();
            }
            else
            {
                ConsolePlus.Write("[Cyan]* Inserisci la password: [/]");
                return ReadPassword();
            }
        }

        private string ReadPassword()
        {
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    break;
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0)
                    {
                        sb.Length--;
                        Console.Write("\b \b"); // Cancella l'asterisco dallo schermo
                    }
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                    Console.Write("*"); // Mostra l'asterisco
                }
            }
            return sb.ToString();
        }

        private void SetupMasterKey()
        {
            ConsolePlus.Write("[Cyan]Inserisci la Master Password da legare al tuo utente Windows: [/]");
            string password = ReadPassword();

            if (string.IsNullOrEmpty(password))
            {
                PrintError("Non hai inserito nessuna password");
                return;
            }

            byte[] secretBytes = Encoding.UTF8.GetBytes(password);
            byte[] encryptedBytes = ProtectedData.Protect(secretBytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(KeyPath!, encryptedBytes);

            Array.Clear(secretBytes, 0, secretBytes.Length);
            ConsolePlus.Write("[Green]Successo:[/] Chiave protetta da DPAPI e salvata in modo sicuro.");
        }

        private string? GetMasterKey()
        {
            if (KeyPath == null || !File.Exists(KeyPath)) return null;
            try
            {
                byte[] encryptedBytes = File.ReadAllBytes(KeyPath);
                byte[] secretBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                string password = Encoding.UTF8.GetString(secretBytes);

                Array.Clear(secretBytes, 0, secretBytes.Length);
                return password;
            }
            catch (CryptographicException)
            {
                PrintError("Accesso negato: Questa chiave appartiene a un altro utente o PC.");
                return null;
            }
        }

        private string GetKeyPath(string keyName)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $".swiss_{keyName}.key");
        }

        // ==========================================
        // MOTORE CRITTOGRAFICO (STREAMING CHUNKS)
        // ==========================================

        private byte[] DeriveKey(string password, byte[] salt)
        {
            using var kdf = new Rfc2898DeriveBytes(password, salt, 100000, HashAlgorithmName.SHA256);
            return kdf.GetBytes(32); // AES-256
        }

        private async Task EncryptFileStreaming(string filePath, byte[] key)
        {
            string outPath = filePath + ".enc";
            using var fsIn = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using var fsOut = new FileStream(outPath, FileMode.Create, FileAccess.Write);
            using var aes = new AesGcm(key, 16);

            byte[] buffer = new byte[ChunkSize];
            int bytesRead;

            ConsolePlus.Write($"[Gray]Cifratura streaming in corso:[/] {Path.GetFileName(filePath)}...");

            while ((bytesRead = await fsIn.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                byte[] nonce = RandomNumberGenerator.GetBytes(12);
                byte[] tag = new byte[16];
                byte[] ciphertext = new byte[bytesRead];

                // Cifra il singolo chunk
                aes.Encrypt(nonce, buffer.AsSpan(0, bytesRead), ciphertext, tag);

                // Struttura chunk: [Lunghezza: 4 byte][Nonce: 12 byte][Tag: 16 byte][Ciphertext]
                await fsOut.WriteAsync(BitConverter.GetBytes(bytesRead));
                await fsOut.WriteAsync(nonce);
                await fsOut.WriteAsync(tag);
                await fsOut.WriteAsync(ciphertext);
            }
            ConsolePlus.Write($"[Green]Successo:[/] File salvato in [Yellow]{outPath}[/]");
        }

        private async Task DecryptFileStreaming(string filePath, byte[] key)
        {
            string outPath = filePath.EndsWith(".enc") ? filePath[..^4] : filePath + ".dec";
            if (File.Exists(outPath))
            {
                outPath = Path.Combine(Path.GetDirectoryName(outPath) ?? "", "dec_" + Path.GetFileName(outPath));
            }

            using var fsIn = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using var fsOut = new FileStream(outPath, FileMode.Create, FileAccess.Write);
            using var aes = new AesGcm(key, 16);

            ConsolePlus.Write($"[Gray]Decifratura streaming in corso:[/] {Path.GetFileName(filePath)}...");

            byte[] sizeBuffer = new byte[4];
            try
            {
                while (await fsIn.ReadAsync(sizeBuffer, 0, 4) == 4)
                {
                    int chunkSize = BitConverter.ToInt32(sizeBuffer);

                    byte[] nonce = new byte[12];
                    byte[] tag = new byte[16];
                    byte[] ciphertext = new byte[chunkSize];
                    byte[] plaintext = new byte[chunkSize];

                    await fsIn.ReadAsync(nonce);
                    await fsIn.ReadAsync(tag);
                    await fsIn.ReadAsync(ciphertext);

                    // Decifra il singolo chunk (Lancia eccezione se Tag non combacia)
                    aes.Decrypt(nonce, ciphertext, tag, plaintext);
                    await fsOut.WriteAsync(plaintext);
                }
                ConsolePlus.Write($"[Green]Successo:[/] File salvato in [Yellow]{outPath}[/]");
            }
            catch (CryptographicException)
            {
                PrintError("Errore critico: Password errata o file compromesso (Integrità blocco fallita).");
                // Se fallisce, cancelliamo il file mezzo-decifrato per evitare di lasciare spazzatura
                fsOut.Close();
                File.Delete(outPath);
            }
        }

        public override void Help()
        {
            ConsolePlus.WriteHr();
            ConsolePlus.Write("[Cyan]#[/] Utilizzo: [Yellow]swiss [Magenta]cripto [DarkGray][opzioni]");
            ConsolePlus.WriteHr();
            ConsolePlus.Write("[Cyan]#[/] Opzioni:");
            ConsolePlus.Write("[Cyan]#[/]   [Yellow]--setup | -s[/]      Configura la Master Password legata a Windows Hello/Utente");
            ConsolePlus.Write("[Cyan]#[/]   [Yellow]-k[/]                Specifica il nome della chiave DPAPI da utilizzare");
            ConsolePlus.Write("[Cyan]#[/]   [Yellow]--target | -t[/]     Specifica il percorso del file");
            ConsolePlus.Write("[Cyan]#[/]   [Yellow]--enc[/]             Cifra il target");
            ConsolePlus.Write("[Cyan]#[/]   [Yellow]--dec[/]             Decifra il target");
            ConsolePlus.WriteHr();
            ConsolePlus.Write("[Cyan]#[/] Esempio Setup:   [Gray]swiss cripto --setup -k Lavoro[/]");
            ConsolePlus.Write("[Cyan]#[/] Esempio Cifra:   [Gray]swiss cripto --enc -t appunti.md -k Lavoro[/]");
        }
    }
}