using System.Net.Http.Headers;
using System.Text;

namespace Jurius.CollabEditing.Services
{
    /// <summary>
    /// Leitura/gravação dos .docx no Nextcloud por WebDAV. É a origem e o destino
    /// do documento da sala: o exemplo da Syncfusion usava um arquivo em wwwroot.
    /// </summary>
    public interface INextcloudStorage
    {
        Task<Stream> DownloadAsync(string relativePath, CancellationToken cancellationToken = default);
        Task UploadAsync(string relativePath, Stream content, CancellationToken cancellationToken = default);
    }

    public class NextcloudStorage : INextcloudStorage
    {
        private readonly HttpClient _http;
        private readonly ILogger<NextcloudStorage> _logger;
        private readonly string _baseUrl;

        public NextcloudStorage(HttpClient http, IConfiguration config, ILogger<NextcloudStorage> logger)
        {
            _logger = logger;

            var baseUrl = config["Nextcloud:BaseUrl"];
            var user = config["Nextcloud:User"];
            var password = config["Nextcloud:Password"];

            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException(
                    "Nextcloud não configurado. Defina Nextcloud__BaseUrl, Nextcloud__User e Nextcloud__Password.");
            }

            // Ex.: https://cloud.exemplo.com/remote.php/dav/files/usuario
            _baseUrl = baseUrl.TrimEnd('/');
            _http = http;
            _http.Timeout = TimeSpan.FromMinutes(3);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        }

        private string BuildUrl(string relativePath)
        {
            var clean = (relativePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
            if (clean.Length == 0) throw new ArgumentException("Caminho vazio.", nameof(relativePath));
            if (clean.Contains("..")) throw new ArgumentException("Caminho inválido.", nameof(relativePath));

            // Cada segmento é escapado separadamente: nomes de pastas de clientes
            // têm espaço e acento, mas as barras precisam continuar sendo barras.
            var encoded = string.Join('/', clean.Split('/').Select(Uri.EscapeDataString));
            return $"{_baseUrl}/{encoded}";
        }

        public async Task<Stream> DownloadAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            var url = BuildUrl(relativePath);
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Falha ao baixar {Path} do Nextcloud: {Status}", relativePath, response.StatusCode);
                throw new HttpRequestException($"Nextcloud retornou {(int)response.StatusCode} ao baixar o arquivo.");
            }

            // Cópia para memória: o WordDocument.Load precisa de um stream posicionável
            // e a resposta HTTP é descartada ao sair deste método.
            var buffer = new MemoryStream();
            await response.Content.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            return buffer;
        }

        public async Task UploadAsync(string relativePath, Stream content, CancellationToken cancellationToken = default)
        {
            var url = BuildUrl(relativePath);
            content.Position = 0;

            using var request = new HttpRequestMessage(HttpMethod.Put, url);
            request.Content = new StreamContent(content);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Falha ao gravar {Path} no Nextcloud: {Status}", relativePath, response.StatusCode);
                throw new HttpRequestException($"Nextcloud retornou {(int)response.StatusCode} ao gravar o arquivo.");
            }

            _logger.LogInformation("Documento gravado no Nextcloud: {Path}", relativePath);
        }
    }
}
