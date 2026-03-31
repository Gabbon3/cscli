using System.Text;
using System.Text.Json;

namespace utils
{
    public class AlertClient
    {
        private static readonly HttpClient _client = new HttpClient();
        private readonly string _baseUrl = "https://edialertproxy.vercel.app";
        private readonly string _apiKey;

        public AlertClient(string apiKey)
        {
            _apiKey = apiKey;
        }

        /// <summary>
        /// Invia un messaggio testuale (HTML supportato)
        /// </summary>
        public async Task SendAlertAsync(string subject, string message, string level = "info")
        {
            var payload = new { subject, message, level };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/send");
            request.Headers.Add("x-api-key", _apiKey);

            string json = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var response = await _client.SendAsync(request);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Alert Error] Messaggio non inviato: {ex.Message}");
            }
        }

        /// <summary>
        /// Invia un file (es. log o report) al bot
        /// </summary>
        public async Task SendFileAsync(string filePath, string caption = "")
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"[Alert Error] File non trovato: {filePath}");
                return;
            }

            using var form = new MultipartFormDataContent();
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using var streamContent = new StreamContent(fileStream);

            // Aggiungiamo il file al form
            form.Add(streamContent, "file", Path.GetFileName(filePath));

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/send-file?caption={Uri.EscapeDataString(caption)}");
            request.Headers.Add("x-api-key", _apiKey);
            request.Content = form;

            try
            {
                var response = await _client.SendAsync(request);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Alert Error] File non inviato: {ex.Message}");
            }
        }
    }
}