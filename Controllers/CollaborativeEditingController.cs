using Jurius.CollabEditing.Hubs;
using Jurius.CollabEditing.Model;
using Jurius.CollabEditing.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using StackExchange.Redis;
using Syncfusion.EJ2.DocumentEditor;

namespace Jurius.CollabEditing.Controllers
{
    /// <summary>
    /// Endpoints que o Document Editor chama sozinho quando
    /// `enableCollaborativeEditing` está ligado:
    ///   ImportFile            — abre o documento e diz em que versão ele está
    ///   UpdateAction          — recebe uma operação, versiona, transforma e distribui
    ///   GetActionsFromServer  — reenvia operações que o cliente perdeu
    ///
    /// E um endpoint NOSSO, que o Document Editor não tem:
    ///   SaveToSource          — grava agora o que está pendente (o botão Salvar)
    ///
    /// A lógica de versão/transformação é a do exemplo oficial da Syncfusion. O que
    /// mudamos: o documento de origem vem do Nextcloud (e volta para lá) e a
    /// gravação pode ser pedida a qualquer momento, não só quando a sala esvazia.
    ///
    /// TODAS as rotas exigem token do Supabase (ver SupabaseAuthMiddleware). O
    /// Document Editor manda `UpdateAction`/`GetActionsFromServer` por XMLHttpRequest
    /// próprio, com os cabeçalhos de `documentEditor.headers` — é lá, no front, que o
    /// `Authorization` é colocado.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class CollaborativeEditingController : ControllerBase
    {
        private readonly IBackgroundTaskQueue _saveTaskQueue;
        private readonly IConnectionMultiplexer _redisConnection;
        private readonly IHubContext<DocumentEditorHub> _hubContext;
        private readonly INextcloudStorage _storage;
        private readonly IRoomPersistence _persistence;
        private readonly IActivityTracker _activity;
        private readonly ILogger<CollaborativeEditingController> _logger;

        public CollaborativeEditingController(
            IHubContext<DocumentEditorHub> hubContext,
            IConnectionMultiplexer redisConnection,
            IBackgroundTaskQueue taskQueue,
            INextcloudStorage storage,
            IRoomPersistence persistence,
            IActivityTracker activity,
            ILogger<CollaborativeEditingController> logger)
        {
            _activity = activity;
            _hubContext = hubContext;
            _redisConnection = redisConnection;
            _saveTaskQueue = taskQueue;
            _storage = storage;
            _persistence = persistence;
            _logger = logger;
        }

        /// <summary>
        /// Abre o .docx do Nextcloud, aplica as operações que ainda não foram
        /// gravadas no arquivo e devolve o SFDT + a versão correspondente.
        /// </summary>
        [HttpPost]
        [Route("ImportFile")]
        public async Task<IActionResult> ImportFile([FromBody] ImportFileInfo param)
        {
            if (param == null || string.IsNullOrWhiteSpace(param.roomName) || string.IsNullOrWhiteSpace(param.filePath))
            {
                return BadRequest("roomName e filePath são obrigatórios.");
            }

            try
            {
                IDatabase database = _redisConnection.GetDatabase();

                // Guarda o arquivo de origem da sala: o salvamento em background só
                // conhece o nome da sala.
                await database.StringSetAsync(param.roomName + CollaborativeEditingHelper.SourceInfoSuffix, param.filePath);

                // Versão e operações pendentes na MESMA leitura — ver ImportSnapshot.
                var snapshot = (RedisResult[])await database.ScriptEvaluateAsync(
                    CollaborativeEditingHelper.ImportSnapshot,
                    new RedisKey[]
                    {
                        param.roomName + CollaborativeEditingHelper.VersionInfoSuffix,
                        param.roomName,
                        param.roomName + CollaborativeEditingHelper.ActionsToRemoveSuffix,
                    },
                    Array.Empty<RedisValue>());

                int version = int.Parse(snapshot[0].ToString());
                var actions = new List<ActionInfo>();
                actions.AddRange(((RedisResult[])snapshot[1])
                    .Select(value => JsonConvert.DeserializeObject<ActionInfo>(value.ToString())));
                actions.AddRange(((RedisResult[])snapshot[2])
                    .Select(value => JsonConvert.DeserializeObject<ActionInfo>(value.ToString())));

                using Stream source = await _storage.DownloadAsync(param.filePath, HttpContext.RequestAborted);
                WordDocument document = WordDocument.Load(source, FormatType.Docx);

                if (actions.Count > 0)
                {
                    document.UpdateActions(actions);
                }

                var content = new DocumentContent
                {
                    // A versão TEM de ser a corrente do servidor. Devolver 0 aqui faria
                    // quem entra depois pedir de novo operações que já estão aplicadas
                    // no documento — texto duplicado.
                    version = version,
                    sfdt = JsonConvert.SerializeObject(document),
                };
                document.Dispose();

                _activity.CountImport();
                _activity.Record("abriu documento", param.roomName, null, $"versão {version}");
                _logger.LogInformation(
                    "Sala {Room}: documento aberto na versão {Version} ({Pending} operações pendentes aplicadas).",
                    param.roomName, version, actions.Count);

                return Content(JsonConvert.SerializeObject(content), "application/json");
            }
            catch (Exception ex)
            {
                // Sem o caminho: ele carrega nome de cliente/processo.
                _logger.LogError(ex, "Falha ao abrir o documento da sala {Room}.", param.roomName);
                return StatusCode(StatusCodes.Status500InternalServerError, "Não foi possível abrir o documento para co-edição.");
            }
        }

        [HttpPost]
        [Route("UpdateAction")]
        public async Task<IActionResult> UpdateAction([FromBody] ActionInfo param)
        {
            if (param == null || string.IsNullOrWhiteSpace(param.RoomName))
            {
                return BadRequest("roomName é obrigatório.");
            }

            try
            {
                ActionInfo modifiedAction = await AddOperationsToCache(param);
                _activity.CountOperation();

                // Volta para a sala INTEIRA, inclusive quem enviou: é assim que o
                // Document Editor confirma a própria operação (ele descarta pelo
                // connectionId) e é o que faz o texto aparecer na tela dos outros.
                await _hubContext.Clients.Group(param.RoomName).SendAsync("dataReceived", "action", modifiedAction);

                _logger.LogDebug(
                    "Sala {Room}: operação da conexão {Connection} aceita na versão {Version} ({Count} operações no lote).",
                    param.RoomName, Mask(param.ConnectionId), modifiedAction.Version, param.Operations?.Count ?? 0);

                return new JsonResult(modifiedAction);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao registrar a operação da sala {Room}.", param.RoomName);
                return StatusCode(StatusCodes.Status500InternalServerError, "Não foi possível registrar a edição.");
            }
        }

        [HttpPost]
        [Route("GetActionsFromServer")]
        public async Task<string> GetActionsFromServer([FromBody] ActionInfo param)
        {
            try
            {
                string roomName = param.RoomName;
                int lastSyncedVersion = param.Version;
                int clientVersion = param.Version;

                IDatabase database = _redisConnection.GetDatabase();

                List<ActionInfo> actions = await GetEffectivePendingVersion(roomName, lastSyncedVersion, database);

                actions.ForEach(action => action.Version = ++clientVersion);

                actions = actions.Where(action => action.Version > lastSyncedVersion).ToList();

                actions.Where(action => !action.IsTransformed).ToList()
                    .ForEach(action => CollaborativeEditingHandler.TransformOperation(action, actions));

                _logger.LogInformation(
                    "Sala {Room}: reenviando {Count} operações a partir da versão {Version}.",
                    roomName, actions.Count, lastSyncedVersion);

                return JsonConvert.SerializeObject(actions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao reenviar operações da sala {Room}.", param?.RoomName);
                return "[]";
            }
        }

        /// <summary>
        /// ACRÉSCIMO NOSSO — o botão Salvar do editor.
        ///
        /// Aplica AGORA, no .docx do Nextcloud, tudo o que está pendente da sala e
        /// só responde depois que o Nextcloud confirmou a gravação. É o que permite
        /// à tela dizer "Salvo" sem mentir: se aqui falhar, o usuário vê o erro.
        ///
        /// A sala continua viva e as operações que chegarem durante a gravação NÃO
        /// são perdidas nem gravadas duas vezes (ver <see cref="RoomPersistence"/>).
        /// </summary>
        [HttpPost]
        [Route("SaveToSource")]
        public async Task<IActionResult> SaveToSource([FromBody] SaveToSourceInfo param)
        {
            if (param == null || string.IsNullOrWhiteSpace(param.roomName))
            {
                return BadRequest("roomName é obrigatório.");
            }

            // O editor novo manda o documento inteiro e a versão que ele já
            // incorporou. Sem esses campos (front antigo ainda no ar durante a
            // implantação) a gravação segue pela reaplicação da fila no servidor —
            // mais frágil, mas honesta: se falhar, responde erro em vez de "Salvo".
            DocumentSaveSnapshot snapshot = null;
            if (!string.IsNullOrWhiteSpace(param.sfdt) && param.version.HasValue)
            {
                snapshot = new DocumentSaveSnapshot
                {
                    Sfdt = param.sfdt,
                    Version = param.version.Value,
                };
            }

            try
            {
                SaveOutcome outcome = await _persistence.PersistAsync(
                    param.roomName,
                    param.filePath,
                    finalize: false,
                    snapshot: snapshot,
                    cancellationToken: HttpContext.RequestAborted);

                _activity.Record("gravação pedida pelo editor", param.roomName, null,
                    $"{outcome.Operations} operações");

                return Ok(outcome);
            }
            catch (RoomVersionConflictException ex)
            {
                _logger.LogInformation(
                    "Sala {Room}: snapshot de gravação na versão {Requested}, sala na {Current}.",
                    param.roomName, ex.RequestedVersion, ex.CurrentVersion);
                return StatusCode(
                    StatusCodes.Status409Conflict,
                    "Chegaram novas edições enquanto o documento era preparado. Sincronize e salve novamente.");
            }
            catch (UnmaterializedSnapshotException ex)
            {
                // O documento chegou com edições ainda penduradas: convertê-lo
                // gravaria o texto anterior. A fila fica intacta.
                _logger.LogError(ex, "Sala {Room}: documento recebido sem as edições aplicadas.", param.roomName);
                return StatusCode(
                    StatusCodes.Status422UnprocessableEntity,
                    "O documento chegou sem as edições aplicadas e não foi gravado. Reabra o documento e salve de novo.");
            }
            catch (OperationReplayException ex)
            {
                _logger.LogError(ex, "Sala {Room}: falha ao reaplicar a fila de operações.", param.roomName);
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    "Não foi possível montar o documento para gravar. Nada foi perdido: as edições continuam na sala.");
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(ex, "Sala {Room}: gravação sob demanda não conseguiu a trava.", param.roomName);
                return StatusCode(StatusCodes.Status409Conflict, "Outra gravação desta sala está em andamento. Tente de novo em instantes.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sala {Room}: falha na gravação sob demanda.", param.roomName);
                _activity.Record("falha ao gravar", param.roomName, null, ex.GetType().Name);
                return StatusCode(StatusCodes.Status500InternalServerError, "Não foi possível gravar o documento no Nextcloud.");
            }
        }

        /// <summary>Nome da conexão nunca sai inteiro no log.</summary>
        private static string Mask(string value) =>
            string.IsNullOrEmpty(value) ? "—" : (value.Length <= 6 ? value : value[..6] + "…");

        private async Task<ActionInfo> AddOperationsToCache(ActionInfo action)
        {
            int clientVersion = action.Version;

            IDatabase database = _redisConnection.GetDatabase();
            RedisKey[] keys = new RedisKey[]
            {
                action.RoomName + CollaborativeEditingHelper.VersionInfoSuffix,
                action.RoomName,
                action.RoomName + CollaborativeEditingHelper.OperationOffsetSuffix,
                action.RoomName + CollaborativeEditingHelper.ActionsToRemoveSuffix,
            };
            RedisValue[] values = new RedisValue[]
            {
                JsonConvert.SerializeObject(action),
                clientVersion.ToString(),
                CollaborativeEditingHelper.SaveThreshold.ToString(),
            };

            RedisResult[] results = (RedisResult[])await database.ScriptEvaluateAsync(CollaborativeEditingHelper.InsertScript, keys, values);

            int version = int.Parse(results[0].ToString());
            List<ActionInfo> previousOperations = ((RedisResult[])results[1])
                .Select(value => JsonConvert.DeserializeObject<ActionInfo>(value.ToString()))
                .ToList();

            previousOperations.ForEach(op => op.Version = ++clientVersion);

            if (previousOperations.Count > 1)
            {
                action = previousOperations.Last();
                previousOperations.Where(op => !op.IsTransformed).ToList()
                    .ForEach(op => CollaborativeEditingHandler.TransformOperation(op, previousOperations));
            }

            action.Version = version;
            action.IsTransformed = true;
            await UpdateRecordToCache(version, action, database);

            if (results.Length > 2 && !results[2].IsNull)
            {
                // O cache passou do limite: as operações mais antigas já foram para a
                // fila de gravação. Aqui só pedimos a gravação — quem decide o que
                // gravar é o RoomPersistence, lendo o Redis na hora.
                string sourcePath = await database.StringGetAsync(action.RoomName + CollaborativeEditingHelper.SourceInfoSuffix);
                _ = _saveTaskQueue.QueueBackgroundWorkItemAsync(new SaveInfo
                {
                    RoomName = action.RoomName,
                    SourcePath = sourcePath,
                    Finalize = false,
                });
            }

            return action;
        }

        private async Task UpdateRecordToCache(int version, ActionInfo action, IDatabase database)
        {
            RedisKey[] keys = new RedisKey[]
            {
                action.RoomName,
                action.RoomName + CollaborativeEditingHelper.OperationOffsetSuffix,
            };

            RedisValue[] values = new RedisValue[]
            {
                JsonConvert.SerializeObject(action),
                (version - 1).ToString(),
            };

            await database.ScriptEvaluateAsync(CollaborativeEditingHelper.UpdateRecord, keys, values);
        }

        private async Task<List<ActionInfo>> GetEffectivePendingVersion(string roomName, int startIndex, IDatabase database)
        {
            RedisKey[] keys = new RedisKey[]
            {
                roomName,
                roomName + CollaborativeEditingHelper.OperationOffsetSuffix,
            };

            RedisValue[] values = new RedisValue[]
            {
                startIndex.ToString(),
            };

            RedisResult[] upcomingActions = (RedisResult[])await database.ScriptEvaluateAsync(
                CollaborativeEditingHelper.EffectivePendingOperations, keys, values);

            return upcomingActions
                .Select(value => JsonConvert.DeserializeObject<ActionInfo>(value.ToString()))
                .ToList();
        }
    }
}
