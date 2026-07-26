using System.Collections.Concurrent;
using System.Net.Http.Headers;

namespace Jurius.CollabEditing.Services
{
    /// <summary>
    /// O serviço fica exposto na internet e movimenta documentos de clientes, então
    /// toda chamada precisa apresentar um token válido do Supabase (o mesmo da
    /// sessão do CRM). A validação é delegada ao próprio Supabase (/auth/v1/user) e
    /// o resultado fica em cache curto para não pagar uma ida de rede por operação
    /// de digitação.
    /// </summary>
    public interface ISupabaseTokenValidator
    {
        Task<bool> IsValidAsync(string accessToken, CancellationToken cancellationToken = default);
    }

    public class SupabaseTokenValidator : ISupabaseTokenValidator
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

        private readonly HttpClient _http;
        private readonly ILogger<SupabaseTokenValidator> _logger;
        private readonly string _supabaseUrl;
        private readonly string _anonKey;
        private readonly bool _required;
        private readonly ConcurrentDictionary<string, DateTimeOffset> _cache = new();

        public SupabaseTokenValidator(HttpClient http, IConfiguration config, ILogger<SupabaseTokenValidator> logger)
        {
            _http = http;
            _logger = logger;
            _supabaseUrl = (config["Supabase:Url"] ?? string.Empty).TrimEnd('/');
            _anonKey = config["Supabase:AnonKey"] ?? string.Empty;
            _required = !string.Equals(config["Auth:Require"], "false", StringComparison.OrdinalIgnoreCase);

            if (_required && !IsConfigured)
            {
                // Falha FECHADA, mas sem derrubar o processo: o serviço sobe, recusa
                // as rotas de documento e a página inicial mostra o que falta. Antes
                // isso estourava no construtor e o container só reiniciava em loop.
                _logger.LogError(
                    "Autenticação exigida mas Supabase__Url/Supabase__AnonKey não foram configurados. " +
                    "Enquanto isso, toda chamada de documento será recusada com 401.");
            }
        }

        private bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_supabaseUrl) && !string.IsNullOrWhiteSpace(_anonKey);

        public async Task<bool> IsValidAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            if (!_required) return true;
            if (!IsConfigured) return false;
            if (string.IsNullOrWhiteSpace(accessToken)) return false;

            if (_cache.TryGetValue(accessToken, out var expiresAt))
            {
                if (expiresAt > DateTimeOffset.UtcNow) return true;
                _cache.TryRemove(accessToken, out _);
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{_supabaseUrl}/auth/v1/user");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Headers.TryAddWithoutValidation("apikey", _anonKey);

                using var response = await _http.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) return false;

                _cache[accessToken] = DateTimeOffset.UtcNow.Add(CacheTtl);
                // Limpeza preguiçosa: sem isso o dicionário cresce com tokens antigos.
                if (_cache.Count > 500)
                {
                    var now = DateTimeOffset.UtcNow;
                    foreach (var entry in _cache.Where(pair => pair.Value <= now).ToList())
                    {
                        _cache.TryRemove(entry.Key, out _);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Não foi possível validar o token no Supabase.");
                return false;
            }
        }
    }

    /// <summary>
    /// Exige token válido nas rotas de co-edição e no hub. O SignalR em WebSocket
    /// não manda cabeçalho Authorization — manda `?access_token=` —, por isso as
    /// duas formas são aceitas.
    /// </summary>
    public class SupabaseAuthMiddleware
    {
        private readonly RequestDelegate _next;

        public SupabaseAuthMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, ISupabaseTokenValidator validator)
        {
            var path = context.Request.Path.Value ?? string.Empty;

            // Protegido = só o que chega perto de documento. A página inicial, o
            // /api/diagnostics (booleanos e tempos, sem nada de cliente) e o
            // /demohub (eco puro) ficam abertos, para dar para conferir o serviço
            // logo depois de subir, sem token nenhum.
            var isProtected =
                path.StartsWith("/api/CollaborativeEditing", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/documenteditorhub", StringComparison.OrdinalIgnoreCase);

            // O preflight do CORS não carrega credencial por definição.
            if (!isProtected || HttpMethods.IsOptions(context.Request.Method))
            {
                await _next(context);
                return;
            }

            var token = string.Empty;
            var header = context.Request.Headers.Authorization.ToString();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = header.Substring("Bearer ".Length).Trim();
            }
            else if (context.Request.Query.TryGetValue("access_token", out var queryToken))
            {
                token = queryToken.ToString();
            }

            if (!await validator.IsValidAsync(token, context.RequestAborted))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Token do Supabase ausente ou inválido.");
                return;
            }

            await _next(context);
        }
    }
}
