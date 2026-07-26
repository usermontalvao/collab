using Jurius.CollabEditing.Hubs;
using Jurius.CollabEditing.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

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

var redisConnectionString = builder.Configuration["ConnectionStrings:RedisConnectionString"]
    ?? throw new InvalidOperationException("Defina ConnectionStrings__RedisConnectionString.");

// Backplane do SignalR: sem ele, dois containers da aplicação não entregam as
// operações um do outro.
builder.Services.AddSignalR().AddStackExchangeRedis(redisConnectionString, options =>
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

app.Run();
