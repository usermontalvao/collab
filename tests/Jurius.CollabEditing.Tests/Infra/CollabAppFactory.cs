using Jurius.CollabEditing.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jurius.CollabEditing.Tests.Infra;

/// <summary>
/// Sobe o serviço inteiro (controllers + hub + fila de gravação) contra o Redis
/// do <see cref="RedisFixture"/>, trocando só o Nextcloud por um dublê em memória.
/// Tudo o mais é o código de produção.
/// </summary>
public sealed class CollabAppFactory : WebApplicationFactory<Program>
{
    private readonly string _redisEndpoint;
    private readonly bool _requireAuth;

    public FakeNextcloudStorage Storage { get; } = new();

    public CollabAppFactory(string redisEndpoint, bool requireAuth = false)
    {
        _redisEndpoint = redisEndpoint;
        _requireAuth = requireAuth;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:RedisConnectionString", _redisEndpoint);
        builder.UseSetting("Auth:Require", _requireAuth ? "true" : "false");
        builder.UseSetting("Demo:Enabled", "false");
        builder.UseSetting("Cors:AllowedOrigins", "https://exemplo.test");
        // Com auth ligada o validador precisa estar "configurado", senão ele
        // recusa tudo antes mesmo de olhar o token — o que esconderia o que o
        // teste quer provar (que SEM token é 401 e COM token passa).
        builder.UseSetting("Supabase:Url", "https://projeto.supabase.test");
        builder.UseSetting("Supabase:AnonKey", "anon-de-teste");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<INextcloudStorage>();
            services.AddSingleton<INextcloudStorage>(Storage);

            if (_requireAuth)
            {
                services.RemoveAll<ISupabaseTokenValidator>();
                services.AddSingleton<ISupabaseTokenValidator>(new StubTokenValidator());
            }
        });
    }

    /// <summary>Conexão SignalR real contra o servidor de teste (transporte de long polling).</summary>
    public async Task<HubConnection> ConnectHubAsync(string accessToken = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, "documenteditorhub"), options =>
            {
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (accessToken is not null) options.AccessTokenProvider = () => Task.FromResult(accessToken);
            })
            .Build();

        await connection.StartAsync();
        return connection;
    }

    /// <summary>Aceita o token "valido" e recusa o resto — sem ida de rede ao Supabase.</summary>
    private sealed class StubTokenValidator : ISupabaseTokenValidator
    {
        public Task<bool> IsValidAsync(string accessToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(accessToken == "token-valido");
    }
}
