namespace plugins.cripto
{
    public class CriptoSettings
    {
        [Option("setup|s", "Configura la Master Password legata a Windows Hello/Utente")]
        public bool Setup { get; set; }

        [Option("key|k", "Specifica il nome della chiave DPAPI da utilizzare")]
        public string? KeyName { get; set; }

        [Option("target|t", "Specifica il percorso del file target")]
        public string? Target { get; set; }

        [Option("enc", "Cifra il target")]
        public bool Enc { get; set; }

        [Option("dec", "Decifra il target")]
        public bool Dec { get; set; }
    }
}