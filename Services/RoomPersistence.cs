using Jurius.CollabEditing.Hubs;
using Jurius.CollabEditing.Model;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using StackExchange.Redis;
using Syncfusion.EJ2.DocumentEditor;

namespace Jurius.CollabEditing.Services
{
    /// <summary>
    /// CAMINHO ÚNICO de gravação do documento de uma sala no Nextcloud.
    ///
    /// Antes existiam dois caminhos parecidos (o corte por excesso de operações e a
    /// saída da última pessoa), cada um carregando a sua própria lista de operações
    /// pela fila. Isso abria duas falhas: a lista podia ser gravada duas vezes
    /// (texto duplicado) e as chaves do Redis eram APAGADAS inteiras no fim — o que
    /// jogava fora tudo que tivesse sido digitado durante a gravação.
    ///
    /// Aqui a gravação é sempre a mesma sequência, com trava por sala:
    ///   1. tira uma foto do que está pendente (versão + as duas filas);
    ///   2. baixa o .docx, aplica exatamente essas operações e envia de volta;
    ///   3. remove das filas EXATAMENTE a quantidade que foi aplicada.
    /// O passo 3 por quantidade (e não apagando a chave) é o que preserva o que
    /// chegou durante o passo 2.
    /// </summary>
    public interface IRoomPersistence
    {
        Task<SaveOutcome> PersistAsync(string roomName, string sourcePath, bool finalize, CancellationToken cancellationToken = default);
    }

    public class RoomPersistence : IRoomPersistence
    {
        /// <summary>Uma gravação por sala de cada vez. Duas em paralelo gravariam a mesma operação duas vezes.</summary>
        private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan LockPoll = TimeSpan.FromMilliseconds(120);

        private readonly IConnectionMultiplexer _redis;
        private readonly INextcloudStorage _storage;
        private readonly IActivityTracker _activity;
        private readonly IHubContext<DocumentEditorHub> _hub;
        private readonly ILogger<RoomPersistence> _logger;

        public RoomPersistence(
            IConnectionMultiplexer redis,
            INextcloudStorage storage,
            IActivityTracker activity,
            IHubContext<DocumentEditorHub> hub,
            ILogger<RoomPersistence> logger)
        {
            _redis = redis;
            _storage = storage;
            _activity = activity;
            _hub = hub;
            _logger = logger;
        }

        public async Task<SaveOutcome> PersistAsync(
            string roomName,
            string sourcePath,
            bool finalize,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(roomName)) throw new ArgumentException("Sala não informada.", nameof(roomName));

            IDatabase database = _redis.GetDatabase();
            string lockKey = roomName + CollaborativeEditingHelper.SaveLockSuffix;
            string lockToken = Guid.NewGuid().ToString("N");

            if (!await AcquireLockAsync(database, lockKey, lockToken, cancellationToken))
            {
                // Só chega aqui se outra gravação passou de LockWait. Recusar é melhor
                // do que gravar em paralelo: o botão Salvar mostra o erro em vez de
                // um "Salvo" que não aconteceu.
                throw new TimeoutException("Outra gravação desta sala ainda está em andamento.");
            }

            try
            {
                return await PersistInternalAsync(database, roomName, sourcePath, finalize, cancellationToken);
            }
            finally
            {
                await database.ScriptEvaluateAsync(
                    CollaborativeEditingHelper.ReleaseLock,
                    new RedisKey[] { lockKey },
                    new RedisValue[] { lockToken });
            }
        }

        private async Task<SaveOutcome> PersistInternalAsync(
            IDatabase database,
            string roomName,
            string sourcePath,
            bool finalize,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                sourcePath = await database.StringGetAsync(roomName + CollaborativeEditingHelper.SourceInfoSuffix);
            }

            // 1) Foto do pendente. Mesma leitura atômica do ImportFile.
            var snapshot = (RedisResult[])await database.ScriptEvaluateAsync(
                CollaborativeEditingHelper.ImportSnapshot,
                new RedisKey[]
                {
                    roomName + CollaborativeEditingHelper.VersionInfoSuffix,
                    roomName,
                    roomName + CollaborativeEditingHelper.ActionsToRemoveSuffix,
                },
                Array.Empty<RedisValue>());

            int version = int.Parse(snapshot[0].ToString());
            var processing = ((RedisResult[])snapshot[1])
                .Select(value => JsonConvert.DeserializeObject<ActionInfo>(value.ToString()))
                .ToList();
            var pending = ((RedisResult[])snapshot[2])
                .Select(value => JsonConvert.DeserializeObject<ActionInfo>(value.ToString()))
                .ToList();

            var actions = new List<ActionInfo>(processing.Count + pending.Count);
            actions.AddRange(processing);
            actions.AddRange(pending);

            var outcome = new SaveOutcome
            {
                Operations = actions.Count,
                Version = version,
                SavedAt = DateTime.UtcNow.ToString("o"),
            };

            if (actions.Count == 0)
            {
                _logger.LogInformation(
                    "Sala {Room}: nada pendente para gravar (versão {Version}, final {Final}).",
                    roomName, version, finalize);

                if (finalize) await ClearRoomAsync(database, roomName);
                outcome.StillPending = 0;
                await NotifyRoomSavedAsync(roomName, outcome, finalize, cancellationToken);
                return outcome;
            }

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                _logger.LogError("Sala {Room} sem caminho de origem no Nextcloud; nada foi gravado.", roomName);
                throw new InvalidOperationException("A sala não tem arquivo de origem registrado.");
            }

            // 2) Aplica no .docx e devolve ao Nextcloud.
            await ApplyAndUploadAsync(roomName, sourcePath, actions, cancellationToken);
            outcome.Uploaded = true;

            // 3) Remove das filas exatamente o que foi aplicado.
            if (finalize)
            {
                await ClearRoomAsync(database, roomName);
                outcome.StillPending = 0;
            }
            else
            {
                var trimmed = (RedisResult[])await database.ScriptEvaluateAsync(
                    CollaborativeEditingHelper.TrimPersistedOperations,
                    new RedisKey[]
                    {
                        roomName,
                        roomName + CollaborativeEditingHelper.ActionsToRemoveSuffix,
                        roomName + CollaborativeEditingHelper.OperationOffsetSuffix,
                    },
                    new RedisValue[] { processing.Count.ToString(), pending.Count.ToString() });

                outcome.StillPending = long.Parse(trimmed[0].ToString());
            }

            _activity.CountSave(actions.Count);
            _activity.Record("gravou no Nextcloud", roomName, null,
                $"{actions.Count} operações · {(finalize ? "final" : "sob demanda")}");
            _logger.LogInformation(
                "Sala {Room}: {Count} operações gravadas (versão {Version}, final {Final}, restaram {Pending}).",
                roomName, actions.Count, version, finalize, outcome.StillPending);

            await NotifyRoomSavedAsync(roomName, outcome, finalize, cancellationToken);
            return outcome;
        }

        /// <summary>
        /// Avisa a sala INTEIRA que o documento acabou de ser gravado no Nextcloud.
        /// É o que faz o "Alterações pendentes" do outro navegador virar "Salvo" na
        /// hora em que UMA pessoa salva: sem este aviso, quem não clicou continuava
        /// vendo pendência de um conteúdo que já estava gravado. Na gravação final
        /// (última pessoa saindo) a sala está vazia e não há quem avisar.
        /// </summary>
        private async Task NotifyRoomSavedAsync(
            string roomName,
            SaveOutcome outcome,
            bool finalize,
            CancellationToken cancellationToken)
        {
            if (finalize) return;

            try
            {
                await _hub.Clients.Group(roomName).SendAsync("dataReceived", "saved", new
                {
                    version = outcome.Version,
                    operations = outcome.Operations,
                    stillPending = outcome.StillPending,
                    uploaded = outcome.Uploaded,
                    savedAt = outcome.SavedAt,
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                // O documento JÁ está gravado; falhar o aviso não pode falhar a
                // gravação. O outro navegador atualiza no próximo salvamento.
                _logger.LogWarning(ex, "Sala {Room}: não foi possível avisar a sala da gravação.", roomName);
            }
        }

        private async Task ApplyAndUploadAsync(
            string roomName,
            string sourcePath,
            List<ActionInfo> actions,
            CancellationToken cancellationToken)
        {
            using Stream source = await _storage.DownloadAsync(sourcePath, cancellationToken);
            WordDocument document = WordDocument.Load(source, FormatType.Docx);
            var handler = new CollaborativeEditingHandler(document);

            foreach (ActionInfo info in actions)
            {
                if (!info.IsTransformed)
                {
                    CollaborativeEditingHandler.TransformOperation(info, actions);
                }
            }

            foreach (ActionInfo info in actions)
            {
                handler.UpdateAction(info);
            }

            using var stream = new MemoryStream();
            Syncfusion.DocIO.DLS.WordDocument doc = WordDocument.Save(JsonConvert.SerializeObject(handler.Document));
            doc.Save(stream, Syncfusion.DocIO.FormatType.Docx);
            doc.Dispose();
            document.Dispose();

            await _storage.UploadAsync(sourcePath, stream, cancellationToken);
            _logger.LogInformation("Sala {Room}: documento enviado ao Nextcloud.", roomName);
        }

        private static async Task ClearRoomAsync(IDatabase database, string roomName)
        {
            await database.KeyDeleteAsync(new RedisKey[]
            {
                roomName,
                roomName + CollaborativeEditingHelper.ActionsToRemoveSuffix,
                roomName + CollaborativeEditingHelper.OperationOffsetSuffix,
                roomName + CollaborativeEditingHelper.VersionInfoSuffix,
                roomName + CollaborativeEditingHelper.SourceInfoSuffix,
            });
        }

        private static async Task<bool> AcquireLockAsync(
            IDatabase database,
            string lockKey,
            string lockToken,
            CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + LockWait;
            while (true)
            {
                if (await database.StringSetAsync(lockKey, lockToken, LockTtl, When.NotExists)) return true;
                if (DateTime.UtcNow >= deadline) return false;
                await Task.Delay(LockPoll, cancellationToken);
            }
        }
    }
}
