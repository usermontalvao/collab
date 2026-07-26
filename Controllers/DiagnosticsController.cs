using System.Diagnostics;
using Jurius.CollabEditing.Model;
using Jurius.CollabEditing.Services;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;
using Syncfusion.EJ2.DocumentEditor;

namespace Jurius.CollabEditing.Controllers
{
    /// <summary>
    /// Alimenta a página inicial de demonstração. É PÚBLICO de propósito (para
    /// conferir o serviço logo depois de subir), então não devolve nada sensível:
    /// só "funciona / não funciona", tempos e contagens. Nenhum caminho de arquivo,
    /// nome de cliente, credencial ou conteúdo de documento passa por aqui.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class DiagnosticsController : ControllerBase
    {
        private static readonly DateTime StartedAt = DateTime.UtcNow;

        private readonly IConnectionMultiplexer _redis;
        private readonly IConfiguration _config;
        private readonly INextcloudStorage _storage;
        private readonly IActivityTracker _activity;
        private readonly ILogger<DiagnosticsController> _logger;

        public DiagnosticsController(
            IConnectionMultiplexer redis,
            IConfiguration config,
            INextcloudStorage storage,
            IActivityTracker activity,
            ILogger<DiagnosticsController> logger)
        {
            _redis = redis;
            _config = config;
            _storage = storage;
            _activity = activity;
            _logger = logger;
        }

        public class RoomStatus
        {
            public string Room { get; set; } = string.Empty;
            public int People { get; set; }
            public long PendingOperations { get; set; }
            public long Version { get; set; }
        }

        public class CheckResult
        {
            public bool Ok { get; set; }
            public bool Configured { get; set; } = true;
            public long LatencyMs { get; set; }
            public string Detail { get; set; } = string.Empty;
        }

        public class SelfTestStep
        {
            public string Step { get; set; } = string.Empty;
            public bool Ok { get; set; }
            public string Detail { get; set; } = string.Empty;
        }

        private bool DemoEnabled =>
            !string.Equals(_config["Demo:Enabled"], "false", StringComparison.OrdinalIgnoreCase);

        [HttpGet]
        public async Task<IActionResult> Get()
        {
            CheckResult redis = await CheckRedis();
            CheckResult nextcloud = await CheckNextcloud();

            return Ok(new
            {
                status = redis.Ok ? "ok" : "degradado",
                startedAt = StartedAt.ToString("o"),
                uptimeSeconds = (int)(DateTime.UtcNow - StartedAt).TotalSeconds,
                syncfusionVersion = typeof(WordDocument).Assembly.GetName().Version?.ToString(),
                redis,
                nextcloud,
                auth = new
                {
                    required = !string.Equals(_config["Auth:Require"], "false", StringComparison.OrdinalIgnoreCase),
                    supabaseConfigured = !string.IsNullOrWhiteSpace(_config["Supabase:Url"]),
                },
                license = new
                {
                    configured = !string.IsNullOrWhiteSpace(_config["Syncfusion:LicenseKey"]),
                },
                demo = new { enabled = DemoEnabled },
            });
        }

        /// <summary>
        /// Atividade do serviço: salas abertas agora, quantas pessoas em cada uma,
        /// operações à espera de gravação e os últimos acontecimentos. Sem nome de
        /// cliente e sem nome completo de usuário (ver ActivityTracker).
        /// </summary>
        [HttpGet("activity")]
        public async Task<IActionResult> Activity()
        {
            var rooms = new List<RoomStatus>();
            try
            {
                IDatabase db = _redis.GetDatabase();
                var endpoint = _redis.GetEndPoints().FirstOrDefault();
                if (endpoint != null)
                {
                    IServer server = _redis.GetServer(endpoint);
                    foreach (var key in server.Keys(pattern: $"*{CollaborativeEditingHelper.UserInfoSuffix}", pageSize: 200).Take(200))
                    {
                        var full = key.ToString();
                        var room = full[..^CollaborativeEditingHelper.UserInfoSuffix.Length];
                        var people = (int)await db.HashLengthAsync(full);
                        if (people == 0) continue;

                        var version = await db.StringGetAsync(room + CollaborativeEditingHelper.VersionInfoSuffix);
                        rooms.Add(new RoomStatus
                        {
                            Room = ActivityTracker.MaskRoom(room),
                            People = people,
                            PendingOperations = await db.ListLengthAsync(room),
                            Version = version.HasValue && long.TryParse(version, out var parsed) ? parsed : 0,
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Não foi possível listar as salas ativas.");
            }

            var snapshot = _activity.Snapshot();

            return Ok(new
            {
                rooms = rooms.OrderByDescending(room => room.People).ToList(),
                totals = new
                {
                    rooms = rooms.Count,
                    people = rooms.Sum(room => room.People),
                    pendingOperations = rooms.Sum(room => room.PendingOperations),
                    operations = snapshot.Operations,
                    saves = snapshot.Saves,
                    savedOperations = snapshot.SavedOperations,
                    imports = snapshot.Imports,
                },
                lastOperationAt = snapshot.LastOperationAt,
                lastSaveAt = snapshot.LastSaveAt,
                events = snapshot.Events,
            });
        }

        private async Task<CheckResult> CheckRedis()
        {
            var watch = Stopwatch.StartNew();
            try
            {
                IDatabase db = _redis.GetDatabase();
                // Ida e volta de verdade: PING só diz que o socket está de pé, e o
                // serviço depende de escrever e ler chave.
                var key = $"diag_selftest_{Guid.NewGuid():N}";
                await db.StringSetAsync(key, "1", TimeSpan.FromSeconds(30));
                var value = await db.StringGetAsync(key);
                await db.KeyDeleteAsync(key);
                watch.Stop();

                var ok = value == "1";
                return new CheckResult
                {
                    Ok = ok,
                    LatencyMs = watch.ElapsedMilliseconds,
                    Detail = ok ? "gravou e leu uma chave" : "leitura não bateu com a gravação",
                };
            }
            catch (Exception ex)
            {
                watch.Stop();
                _logger.LogWarning(ex, "Diagnóstico: Redis indisponível.");
                return new CheckResult
                {
                    Ok = false,
                    LatencyMs = watch.ElapsedMilliseconds,
                    Detail = ex.GetType().Name,
                };
            }
        }

        private async Task<CheckResult> CheckNextcloud()
        {
            if (string.IsNullOrWhiteSpace(_config["Nextcloud:BaseUrl"]))
            {
                return new CheckResult { Ok = false, Configured = false, Detail = "sem Nextcloud__BaseUrl" };
            }

            var watch = Stopwatch.StartNew();
            try
            {
                // Pedir a raiz confirma URL + usuário + senha de uma vez só.
                await _storage.DownloadAsync(".", HttpContext.RequestAborted);
                watch.Stop();
                return new CheckResult { Ok = true, LatencyMs = watch.ElapsedMilliseconds, Detail = "credencial aceita" };
            }
            catch (Exception ex)
            {
                watch.Stop();
                var message = ex.Message ?? string.Empty;
                var authFailed = message.Contains("401") || message.Contains("403");
                return new CheckResult
                {
                    // 404/405 na raiz ainda prova que a credencial passou; 401/403 não.
                    Ok = !authFailed && (message.Contains("404") || message.Contains("405")),
                    LatencyMs = watch.ElapsedMilliseconds,
                    Detail = authFailed ? "credencial recusada pelo Nextcloud" : message,
                };
            }
        }

        /// <summary>
        /// Prova que o motor de documentos roda DENTRO do container: monta um .docx
        /// em memória, abre com o mesmo carregador usado na co-edição e serializa
        /// para SFDT. É o passo que quebra quando faltam as bibliotecas nativas de
        /// desenho na imagem — melhor descobrir aqui do que ao abrir uma petição.
        /// </summary>
        [HttpPost("selftest")]
        public IActionResult SelfTest()
        {
            var steps = new List<SelfTestStep>();
            var watch = Stopwatch.StartNew();

            try
            {
                const string marker = "Teste de co-edicao Jurius";

                using var docxStream = new MemoryStream();
                var authored = new Syncfusion.DocIO.DLS.WordDocument();
                authored.EnsureMinimal();
                authored.LastParagraph.AppendText(marker);
                authored.Save(docxStream, Syncfusion.DocIO.FormatType.Docx);
                authored.Dispose();
                steps.Add(new SelfTestStep
                {
                    Step = "Gerar um .docx em memória",
                    Ok = docxStream.Length > 0,
                    Detail = $"{docxStream.Length} bytes",
                });

                docxStream.Position = 0;
                WordDocument document = WordDocument.Load(docxStream, FormatType.Docx);
                steps.Add(new SelfTestStep
                {
                    Step = "Abrir o .docx com o carregador da co-edição",
                    Ok = true,
                    Detail = "documento carregado",
                });

                string sfdt = Newtonsoft.Json.JsonConvert.SerializeObject(document);
                document.Dispose();
                var carriesText = sfdt.Contains(marker, StringComparison.Ordinal);
                steps.Add(new SelfTestStep
                {
                    Step = "Converter para SFDT (o formato que vai ao navegador)",
                    Ok = carriesText,
                    Detail = carriesText
                        ? $"{sfdt.Length} caracteres, texto preservado"
                        : "o texto não sobreviveu à conversão",
                });

                watch.Stop();
                return Ok(new { ok = steps.All(step => step.Ok), elapsedMs = watch.ElapsedMilliseconds, steps });
            }
            catch (Exception ex)
            {
                watch.Stop();
                _logger.LogError(ex, "Autoteste do motor de documentos falhou.");
                steps.Add(new SelfTestStep
                {
                    Step = "Falhou",
                    Ok = false,
                    Detail = $"{ex.GetType().Name}: {ex.Message}",
                });
                return Ok(new { ok = false, elapsedMs = watch.ElapsedMilliseconds, steps });
            }
        }
    }
}
