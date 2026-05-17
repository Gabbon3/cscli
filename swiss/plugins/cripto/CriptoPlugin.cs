using lib.console;
using System.Security.Cryptography;
using System.Text;

namespace plugins.cripto
{
    internal class CriptoPlugin : Plugin
    {
        public override string Name => "cripto";
        public override string Description => "Crittografia ad alte prestazioni con streaming AES-GCM e DPAPI";

        // # Stato condiviso tra i metodi
        private CriptoState State = new();

        // # Costanti
        private const int ChunkSize = 1024 * 1024; // 1MB per chunk
        private static readonly byte[] MasterSalt = Encoding.UTF8.GetBytes("Gg_Master_Salt_2024_V1");

        // # Stato interno
        private class CriptoState
        {
            public string? KeyPath;
            public string? DpapiKeyName;
            public string? Password;
            public string? Target;
            public byte[] AesKey = [];
            public bool IsSetup;
            public bool IsEncrypt;
            public bool IsDecrypt;
        }

        // # ---------------------------------- #
        // RunAsync — diagramma di flusso
        // # ---------------------------------- #
        public override async Task RunAsync(string[] args, CancellationToken ct)
        {
            if (args.Length == 0 || args.Contains("--help"))
            {
                Help();
                return;
            }

            var settings = ParseSettings<CriptoSettings>(args);
            State = new CriptoState();
            // 1. parsing e validazione delle settings
            if (!ParseAndValidateSettings(settings)) return;
            // 2. fa il setup della chiave DPAPI se isSetup è attivo
            if (HandleSetupMode()) return;
            // 3. ottengo la password o da console oppure da DPAPI
            if (!RetrievePassword()) return;
            // 4. verifico che il file da cifrare/decifrare funzioni
            if (!ValidateTargetFile()) return;
            // 5. utilizzo PBKDF2 per generare una chiave crittografica pronta per AES
            DeriveAesKey();
            // 6. Cifro/Decifro il file
            await ExecuteCryptographicOperation();
            // 7. Pulisco manualmente dalla memoria i dati sensibili
            CleanupSensitiveData();
        }

        // # ---------------------------------- #
        // Metodi estratti
        // # ---------------------------------- #

        /// <summary>
        /// Valida le impostazioni e popola State con i parametri principali.
        /// Accede a: State.DpapiKeyName, State.KeyPath, State.IsSetup, State.IsEncrypt, State.IsDecrypt, State.Target
        /// </summary>
        private bool ParseAndValidateSettings(CriptoSettings settings)
        {
            State.DpapiKeyName = settings.KeyName;
            State.IsSetup = settings.Setup;
            State.IsEncrypt = settings.Enc;
            State.IsDecrypt = settings.Dec;
            State.Target = settings.Target;

            if (!string.IsNullOrEmpty(State.DpapiKeyName))
            {
                State.KeyPath = GetKeyPath(State.DpapiKeyName);
            }

            return true;
        }

        /// <summary>
        /// Gestisce la modalità setup: se attiva, crea la master key e termina l'esecuzione.
        /// Accede a: State.IsSetup, State.DpapiKeyName, State.KeyPath
        /// </summary>
        private bool HandleSetupMode()
        {
            if (!State.IsSetup) return false;

            if (string.IsNullOrEmpty(State.DpapiKeyName))
            {
                PrintError("Devi identificare la chiave che andrai a salvare utilizzando il comando --tpm | -k 'nome_chiave'");
                return true;
            }

            SetupMasterKey();
            return true;
        }

        /// <summary>
        /// Recupera la password da DPAPI o tramite input manuale.
        /// Accede a: State.Password, State.DpapiKeyName
        /// </summary>
        private bool RetrievePassword()
        {
            State.Password = GetPassword(State.DpapiKeyName);

            if (string.IsNullOrEmpty(State.Password))
            {
                PrintError("Nessuna password fornita o configurata. Annullato.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Verifica che il file target esista.
        /// Accede a: State.Target
        /// </summary>
        private bool ValidateTargetFile()
        {
            if (string.IsNullOrEmpty(State.Target))
            {
                PrintError("Nessun target definito, utilizza --target | -t 'percorso file'");
                return false;
            }

            if (!File.Exists(State.Target))
            {
                PrintError($"Il file specificato non esiste: {State.Target}");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Deriva la chiave AES dalla password usando PBKDF2 e pulisce la password dalla memoria.
        /// Accede a: State.Password, State.AesKey, MasterSalt
        /// </summary>
        private void DeriveAesKey()
        {
            State.AesKey = DeriveKey(State.Password!, MasterSalt);
            State.Password = null; // Pulisce la password dalla memoria
        }

        /// <summary>
        /// Esegue l'operazione di cifratura o decifratura in base ai flag impostati.
        /// Accede a: State.IsEncrypt, State.IsDecrypt, State.Target, State.AesKey
        /// </summary>
        private async Task ExecuteCryptographicOperation()
        {
            try
            {
                if (State.IsEncrypt)
                {
                    await EncryptFileStreaming(State.Target!, State.AesKey);
                }
                else if (State.IsDecrypt)
                {
                    await DecryptFileStreaming(State.Target!, State.AesKey);
                }
                else
                {
                    PrintHelp<CriptoSettings>();
                }
            }
            catch (Exception ex)
            {
                PrintError($"Errore durante l'operazione crittografica: {ex.Message}");
            }
        }

        /// <summary>
        /// Pulisce la chiave AES dalla memoria al termine dell'operazione.
        /// Accede a: State.AesKey
        /// </summary>
        private void CleanupSensitiveData()
        {
            if (State.AesKey.Length > 0)
            {
                Array.Clear(State.AesKey, 0, State.AesKey.Length);
            }
        }

        // # ---------------------------------- #
        // Gestione input e DPAPI
        // # ---------------------------------- #

        /// <summary>
        /// Recupera la password da DPAPI se configurata, altrimenti chiede input manuale.
        /// Accede a: State.KeyPath
        /// </summary>
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

        /// <summary>
        /// Legge la password da console con asterischi nascosti.
        /// </summary>
        private string ReadPassword()
        {
            StringBuilder sb = new();
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
                        Console.Write("\b \b");
                    }
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                    Console.Write("*");
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Configura una nuova master key protetta con DPAPI.
        /// Accede a: State.KeyPath
        /// </summary>
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
            File.WriteAllBytes(State.KeyPath!, encryptedBytes);

            Array.Clear(secretBytes, 0, secretBytes.Length);
            ConsolePlus.Write("[Green]Successo:[/] Chiave protetta da DPAPI e salvata in modo sicuro.");
        }

        /// <summary>
        /// Recupera la master key salvata decifrandola con DPAPI.
        /// Accede a: State.KeyPath
        /// </summary>
        private string? GetMasterKey()
        {
            if (State.KeyPath == null || !File.Exists(State.KeyPath)) return null;
            try
            {
                byte[] encryptedBytes = File.ReadAllBytes(State.KeyPath);
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

        /// <summary>
        /// Costruisce il percorso del file chiave DPAPI.
        /// </summary>
        private string GetKeyPath(string keyName)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $".swiss_{keyName}.key");
        }

        // # ---------------------------------- #
        // Motore crittografico - Streaming chunks (invariati nella logica)
        // # ---------------------------------- #

        /// <summary>
        /// Deriva una chiave AES-256 dalla password usando PBKDF2 con 100k iterazioni.
        /// </summary>
        private byte[] DeriveKey(string password, byte[] salt)
        {
            using var kdf = new Rfc2898DeriveBytes(password, salt, 100000, HashAlgorithmName.SHA256);
            return kdf.GetBytes(32); // AES-256
        }

        /// <summary>
        /// Cifra un file usando AES-GCM in modalità streaming (chunk da 1MB).
        /// </summary>
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

                aes.Encrypt(nonce, buffer.AsSpan(0, bytesRead), ciphertext, tag);

                // Struttura chunk: [Lunghezza: 4 byte][Nonce: 12 byte][Tag: 16 byte][Ciphertext]
                await fsOut.WriteAsync(BitConverter.GetBytes(bytesRead));
                await fsOut.WriteAsync(nonce);
                await fsOut.WriteAsync(tag);
                await fsOut.WriteAsync(ciphertext);
            }
            ConsolePlus.Write($"[Green]Successo:[/] File salvato in [Yellow]{outPath}[/]");
        }

        /// <summary>
        /// Decifra un file usando AES-GCM in modalità streaming (chunk da 1MB).
        /// </summary>
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

                    aes.Decrypt(nonce, ciphertext, tag, plaintext);
                    await fsOut.WriteAsync(plaintext);
                }
                ConsolePlus.Write($"[Green]Successo:[/] File salvato in [Yellow]{outPath}[/]");
            }
            catch (CryptographicException)
            {
                PrintError("Errore critico: Password errata o file compromesso (Integrità blocco fallita).");
                fsOut.Close();
                File.Delete(outPath);
            }
        }

        public override void Help() => PrintHelp<CriptoSettings>();
    }
}