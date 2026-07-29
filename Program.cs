using Jurius.CollabEditing.Hubs;
using Jurius.CollabEditing.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// O botão Salvar envia o SFDT completo que está visível no editor. Documentos
// com imagens podem ultrapassar o limite padrão de 30 MB do Kestrel.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 96 * 1024 * 1024;
});

// Licença do Syncfusion (mesma chave do servidor de documentos).
var licenseKey = builder.Configuration["Syncfusion:LicenseKey"];
if (!string.IsNullOrWhiteSpace(licenseKey))
{
    Syncfusion.Licensing.SyncfusionLicenseProvider.RegisterLicense(licenseKey);
}

builder.Services.AddControllers();

// Origens permitidas — nada de AllowAnyOrigin: o serviço abre e grava documentos
// de clientes. Configure em Cors__AllowedOrigins (separado por vírgula).
var allowedOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? "https://jurius.com.br")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

// Cai no serviço do compose quando não vier configurado — o serviço sobe e a
// página inicial mostra se o Redis respondeu ou não.
var redisConnectionString = builder.Configuration["ConnectionStrings:RedisConnectionString"];
if (string.IsNullOrWhiteSpace(redisConnectionString)) redisConnectionString = "redis:6379";

// Backplane do SignalR: sem ele, dois containers da aplicação não entregam as
// operações um do outro.
builder.Services.AddSignalR(options =>
{
    // O padrão do SignalR é 32 KB por mensagem — pequeno demais para este uso.
    // Colar uma imagem, uma tabela grande ou um trecho longo vira UMA operação
    // do Document Editor bem maior que isso, e a mensagem que estoura o limite
    // não é rejeitada: o servidor DERRUBA a conexão. O sintoma na tela é a
    // co-edição morrer do nada no meio da edição.
    options.MaximumReceiveMessageSize = 32 * 1024 * 1024;
}).AddStackExchangeRedis(redisConnectionString, options =>
{
    options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("docedit");
});

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var configuration = ConfigurationOptions.Parse(redisConnectionString, true);
    configuration.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(configuration);
});

// Painel da página inicial: contadores e últimos acontecimentos (em memória).
builder.Services.AddSingleton<IActivityTracker, ActivityTracker>();

builder.Services.AddSingleton<IBackgroundTaskQueue>(_ => new BackgroundTaskQueue(200));
builder.Services.AddHostedService<QueuedHostedService>();

builder.Services.AddHttpClient<INextcloudStorage, NextcloudStorage>();
builder.Services.AddHttpClient<ISupabaseTokenValidator, SupabaseTokenValidator>();

// Caminho único de gravação no Nextcloud: usado pelo botão Salvar (síncrono) e
// pela fila de background (corte por excesso de operações / sala vazia).
builder.Services.AddScoped<IRoomPersistence, RoomPersistence>();

var app = builder.Build();

// Página inicial de demonstração (wwwroot/index.html).
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseCors();

// Exige token do Supabase nas rotas de documento. A página inicial, o
// diagnóstico e o hub de demonstração ficam de fora — nenhum deles toca em
// documento (ver SupabaseAuthMiddleware).
app.UseMiddleware<SupabaseAuthMiddleware>();

app.MapHub<DocumentEditorHub>("/documenteditorhub");

// Hub da demonstração: só existe para a página inicial provar que o WebSocket
// atravessa o túnel da Cloudflare. Desligue com Demo__Enabled=false quando não
// precisar mais dele.
var demoEnabled = !string.Equals(app.Configuration["Demo:Enabled"], "false", StringComparison.OrdinalIgnoreCase);
if (demoEnabled)
{
    app.MapHub<DemoHub>("/demohub");
}

app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Resumo da configuração no start: com isto o `docker logs` já diz o que falta,
// em vez de deixar o problema aparecer só na hora de abrir um documento.
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Configuração");
    var cfg = app.Configuration;
    string Estado(params string[] keys) =>
        keys.Any(key => !string.IsNullOrWhiteSpace(cfg[key])) ? "ok" : "FALTANDO";

    log.LogInformation(
        "Co-edição Jurius iniciando · Redis: {Redis} · Nextcloud: Url {NcUrl}, User {NcUser}, Senha {NcPass} · " +
        "Supabase: Url {SbUrl}, AnonKey {SbKey} · Licença Syncfusion: {Lic} · Login exigido: {Auth} · Demonstração: {Demo}",
        redisConnectionString,
        Estado("Nextcloud:Url", "Nextcloud:BaseUrl"), Estado("Nextcloud:User"), Estado("Nextcloud:AppPassword", "Nextcloud:Password"),
        Estado("Supabase:Url"), Estado("Supabase:AnonKey"),
        Estado("Syncfusion:LicenseKey"),
        !string.Equals(cfg["Auth:Require"], "false", StringComparison.OrdinalIgnoreCase),
        demoEnabled);

    if (string.IsNullOrWhiteSpace(cfg["Nextcloud:Url"]) && string.IsNullOrWhiteSpace(cfg["Nextcloud:BaseUrl"]))
    {
        log.LogWarning(
            "Sem Nextcloud configurado o serviço sobe, mas não consegue abrir nem gravar documento. " +
            "Confira se o arquivo .env existe ao lado do docker-compose.yml e se as variáveis chegaram ao container " +
            "(docker compose config mostra os valores já resolvidos).");
    }
}

app.Run();

/// <summary>
/// Torna a classe gerada pelos top-level statements visível para os testes
/// (WebApplicationFactory&lt;Program&gt;). Não muda nada em produção.
/// </summary>
public partial class Program { }
