using Jurius.CollabEditing.Model;
using StackExchange.Redis;
using Syncfusion.EJ2.DocumentEditor;

namespace Jurius.CollabEditing.Services
{
    /// <summary>
    /// Aplica as operações acumuladas ao .docx de origem e grava de volta no
    /// Nextcloud. Roda quando o cache passa do limite (gravação parcial) e quando
    /// a última pessoa sai da sala (gravação final).
    /// </summary>
    public class QueuedHostedService : BackgroundService
    {
        private readonly IConnectionMultiplexer _redisConnection;
        private readonly INextcloudStorage _storage;
        private readonly IActivityTracker _activity;
        private readonly ILogger<QueuedHostedService> _logger;

        public IBackgroundTaskQueue TaskQueue { get; }

        public QueuedHostedService(
            IBackgroundTaskQueue taskQueue,
            IConnectionMultiplexer redisConnection,
            INextcloudStorage storage,
            IActivityTracker activity,
            ILogger<QueuedHostedService> logger)
        {
            TaskQueue = taskQueue;
            _activity = activity;
            _redisConnection = redisConnection;
            _storage = storage;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                SaveInfo workItem;
                try
                {
                    workItem = await TaskQueue.DequeueAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    await ApplyOperationsToSourceDocument(workItem, stoppingToken);
                    await ClearRecordsFromRedisCache(workItem);
                }
                catch (Exception ex)
                {
                    // Derrubar o serviço aqui deixaria TODAS as salas sem gravação;
                    // registra e segue para o próximo item da fila.
                    _activity.Record("falha ao gravar", workItem.RoomName, null, ex.GetType().Name);
                    _logger.LogError(ex, "Falha ao gravar as operações da sala {Room} no documento de origem.", workItem.RoomName);
                }
            }
        }

        private async Task ClearRecordsFromRedisCache(SaveInfo workItem)
        {
            IDatabase database = _redisConnection.GetDatabase();
            if (!workItem.PartialSave)
            {
                await database.KeyDeleteAsync(workItem.RoomName);
                await database.KeyDeleteAsync(workItem.RoomName + CollaborativeEditingHelper.RevisionInfoSuffix);
                await database.KeyDeleteAsync(workItem.RoomName + CollaborativeEditingHelper.VersionInfoSuffix);
                await database.KeyDeleteAsync(workItem.RoomName + CollaborativeEditingHelper.SourceInfoSuffix);
            }
            await database.KeyDeleteAsync(workItem.RoomName + CollaborativeEditingHelper.ActionsToRemoveSuffix);
        }

        private async Task ApplyOperationsToSourceDocument(SaveInfo workItem, CancellationToken cancellationToken)
        {
            List<ActionInfo> actions = workItem.Action;
            if (actions == null || actions.Count == 0) return;

            var sourcePath = workItem.SourcePath;
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                IDatabase database = _redisConnection.GetDatabase();
                sourcePath = await database.StringGetAsync(workItem.RoomName + CollaborativeEditingHelper.SourceInfoSuffix);
            }

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                _logger.LogError("Sala {Room} sem caminho de origem no Nextcloud; nada foi gravado.", workItem.RoomName);
                return;
            }

            using Stream source = await _storage.DownloadAsync(sourcePath, cancellationToken);
            WordDocument document = WordDocument.Load(source, FormatType.Docx);
            CollaborativeEditingHandler handler = new CollaborativeEditingHandler(document);

            foreach (ActionInfo info in actions)
            {
                if (!info.IsTransformed)
                {
                    CollaborativeEditingHandler.TransformOperation(info, actions);
                }
            }

            for (int i = 0; i < actions.Count; i++)
            {
                handler.UpdateAction(actions[i]);
            }

            using var stream = new MemoryStream();
            Syncfusion.DocIO.DLS.WordDocument doc = WordDocument.Save(
                Newtonsoft.Json.JsonConvert.SerializeObject(handler.Document));
            doc.Save(stream, Syncfusion.DocIO.FormatType.Docx);
            doc.Dispose();
            document.Dispose();

            await _storage.UploadAsync(sourcePath, stream, cancellationToken);
            _activity.CountSave(actions.Count);
            _activity.Record("gravou no Nextcloud", workItem.RoomName, null,
                $"{actions.Count} operações · {(workItem.PartialSave ? "parcial" : "final")}");
            _logger.LogInformation("Sala {Room}: {Count} operações gravadas em {Path}.", workItem.RoomName, actions.Count, sourcePath);
        }
    }
}
