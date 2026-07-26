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
    /// A lógica de versão/transformação é a do exemplo oficial da Syncfusion. O que
    /// mudamos: o documento de origem vem do Nextcloud (e volta para lá).
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class CollaborativeEditingController : ControllerBase
    {
        private readonly IBackgroundTaskQueue _saveTaskQueue;
        private readonly IConnectionMultiplexer _redisConnection;
        private readonly IHubContext<DocumentEditorHub> _hubContext;
        private readonly INextcloudStorage _storage;
        private readonly IActivityTracker _activity;
        private readonly ILogger<CollaborativeEditingController> _logger;

        public CollaborativeEditingController(
            IHubContext<DocumentEditorHub> hubContext,
            IConnectionMultiplexer redisConnection,
            IBackgroundTaskQueue taskQueue,
            INextcloudStorage storage,
            IActivityTracker activity,
            ILogger<CollaborativeEditingController> logger)
        {
            _activity = activity;
            _hubContext = hubContext;
            _redisConnection = redisConnection;
            _saveTaskQueue = taskQueue;
            _storage = storage;
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

                return Content(JsonConvert.SerializeObject(content), "application/json");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao abrir {Path} para a sala {Room}.", param.filePath, param.roomName);
                return StatusCode(StatusCodes.Status500InternalServerError, "Não foi possível abrir o documento para co-edição.");
            }
        }

        [HttpPost]
        [Route("UpdateAction")]
        public async Task<ActionInfo> UpdateAction(ActionInfo param)
        {
            ActionInfo modifiedAction = await AddOperationsToCache(param);
            _activity.CountOperation();
            await _hubContext.Clients.Group(param.RoomName).SendAsync("dataReceived", "action", modifiedAction);
            return modifiedAction;
        }

        [HttpPost]
        [Route("GetActionsFromServer")]
        public async Task<string> GetActionsFromServer(ActionInfo param)
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

                return JsonConvert.SerializeObject(actions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao reenviar operações da sala {Room}.", param?.RoomName);
                return "{}";
            }
        }

        private async Task<ActionInfo> AddOperationsToCache(ActionInfo action)
        {
            int clientVersion = action.Version;

            IDatabase database = _redisConnection.GetDatabase();
            RedisKey[] keys = new RedisKey[]
            {
                action.RoomName + CollaborativeEditingHelper.VersionInfoSuffix,
                action.RoomName,
                action.RoomName + CollaborativeEditingHelper.RevisionInfoSuffix,
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
                RedisResult[] clearedOperation = (RedisResult[])results[2];
                var actions = clearedOperation
                    .Select(element => JsonConvert.DeserializeObject<ActionInfo>(element.ToString()))
                    .ToList();

                string sourcePath = await database.StringGetAsync(action.RoomName + CollaborativeEditingHelper.SourceInfoSuffix);
                var message = new SaveInfo
                {
                    Action = actions,
                    PartialSave = true,
                    RoomName = action.RoomName,
                    SourcePath = sourcePath,
                };
                _ = _saveTaskQueue.QueueBackgroundWorkItemAsync(message);
            }

            return action;
        }

        private async Task UpdateRecordToCache(int version, ActionInfo action, IDatabase database)
        {
            RedisKey[] keys = new RedisKey[]
            {
                action.RoomName,
                action.RoomName + CollaborativeEditingHelper.RevisionInfoSuffix,
            };

            RedisValue[] values = new RedisValue[]
            {
                JsonConvert.SerializeObject(action),
                (version - 1).ToString(),
                CollaborativeEditingHelper.SaveThreshold.ToString(),
            };

            await database.ScriptEvaluateAsync(CollaborativeEditingHelper.UpdateRecord, keys, values);
        }

        private async Task<List<ActionInfo>> GetEffectivePendingVersion(string roomName, int startIndex, IDatabase database)
        {
            RedisKey[] keys = new RedisKey[]
            {
                roomName,
                roomName + CollaborativeEditingHelper.RevisionInfoSuffix,
            };

            RedisValue[] values = new RedisValue[]
            {
                startIndex.ToString(),
                CollaborativeEditingHelper.SaveThreshold.ToString(),
            };

            RedisResult[] upcomingActions = (RedisResult[])await database.ScriptEvaluateAsync(
                CollaborativeEditingHelper.EffectivePendingOperations, keys, values);

            return upcomingActions
                .Select(value => JsonConvert.DeserializeObject<ActionInfo>(value.ToString()))
                .ToList();
        }
    }
}
