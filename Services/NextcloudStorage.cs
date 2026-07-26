using System.Net.Http.Headers;
using System.Text;

namespace Jurius.CollabEditing.Services
{
    /// <summary>
    /// Leitura/gravação dos .docx no Nextcloud por WebDAV. É a origem e o destino
    /// do documento da sala: o exemplo da Syncfusion usava um arquivo em wwwroot.
    ///
    /// Sem configuração o serviço NÃO deixa de subir: só as operações de documento
    /// falham, com mensagem clara, e a página inicial mostra o que está faltando.
    /// Validar no construtor derrubava o processo no start e deixava o container
    /// em loop de reinício, sem página nenhuma para explicar o motivo.
    /// </summary>
    public interface INextcloudStorage
    {
        bool IsConfigured { get; }
        Task<Stream> DownloadAsync(string relativePath, CancellationToken cancellationToken = default);
        Task UploadAsync(string relativePath, Stream content, CancellationToken cancellationToken = default);
    }

    public class NextcloudStorage : INextcloudStorage
    {
        private readonly HttpClient _http;
        private readonly ILogger<NextcloudStorage> _logger;
        private readonly string _baseUrl;
        private readonly string _user;
        private readonly string _password;

        public NextcloudStorage(HttpClient http, IConfiguration config, ILogger<NextcloudStorage> logger)
        {
            _logger = logger;
            _http = http;
            _http.Timeout = TimeSpan.FromMinutes(3);

            _user = config["Nextcloud:User"] ?? string.Empty;

            // Aceita as duas grafias: o CRM (Edge Function nextcloud-proxy) já usa
            // NEXTCLOUD_APP_PASSWORD, então dá para copiar o mesmo valor sem
            // renomear nada.
            _password = Primeiro(config["Nextcloud:Password"], config["Nextcloud:AppPassword"]);

            // Aceita tanto a raiz do Nextcloud (https://nextcloud.exemplo.com)
            // quanto a raiz WebDAV completa (.../remote.php/dav/files/usuario).
            // O proxy do CRM guarda só a raiz — mesma coisa aqui.
            var configured = Primeiro(config["Nextcloud:BaseUrl"], config["Nextcloud:Url"]).TrimEnd('/');
            _baseUrl = configured.Length == 0 || configured.Contains("/remote.php/dav/", StringComparison.OrdinalIgnoreCase)
                ? configured
                : $"{configured}/remote.php/dav/files/{Uri.EscapeDataString(_user)}";
        }

        private static string Primeiro(params string[] valores) =>
            valores.FirstOrDefault(valor => !string.IsNullOrWhiteSpace(valor)) ?? string.Empty;

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_baseUrl) &&
            !string.IsNullOrWhiteSpace(_user) &&
            !string.IsNullOrWhiteSpace(_password);

        private void EnsureConfigured()
        {
            if (IsConfigured) return;
            throw new InvalidOperationException(
                "Nextcloud não configurado. Defina NEXTCLOUD_URL, NEXTCLOUD_USER e NEXTCLOUD_APP_PASSWORD.");
        }

        // O cabeçalho vai em cada requisição, não no HttpClient: a fábrica entrega
        // instâncias compartilhadas e mexer no DefaultRequestHeaders daria corrida.
        private AuthenticationHeaderValue BuildAuth() =>
            new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_user}:{_password}")));

        private string BuildUrl(string relativePath)
        {
            var clean = (relativePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
            if (clean.Contains("..")) throw new ArgumentException("Caminho inválido.", nameof(relativePath));

            // "." é a própria raiz — usado só pelo diagnóstico da página inicial.
            if (clean.Length == 0 || clean == ".") return _baseUrl;

            // Cada segmento é escapado separadamente: nomes de pastas de clientes
            // têm espaço e acento, mas as barras precisam continuar sendo barras.
            var encoded = string.Join('/', clean.Split('/').Select(Uri.EscapeDataString));
            return $"{_baseUrl}/{encoded}";
        }

        public async Task<Stream> DownloadAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            EnsureConfigured();

            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(relativePath));
            request.Headers.Authorization = BuildAuth();

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
            EnsureConfigured();
            content.Position = 0;

            using var request = new HttpRequestMessage(HttpMethod.Put, BuildUrl(relativePath));
            request.Headers.Authorization = BuildAuth();
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
