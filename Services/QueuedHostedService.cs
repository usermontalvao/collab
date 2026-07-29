using Jurius.CollabEditing.Model;

namespace Jurius.CollabEditing.Services
{
    /// <summary>
    /// Gravações que NÃO podem fazer o usuário esperar: o corte por excesso de
    /// operações e a saída da última pessoa da sala. O trabalho de verdade está em
    /// <see cref="IRoomPersistence"/> — o mesmo caminho usado pelo botão Salvar,
    /// que por sua vez é síncrono porque a tela precisa da confirmação.
    /// </summary>
    public class QueuedHostedService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IActivityTracker _activity;
        private readonly ILogger<QueuedHostedService> _logger;

        public IBackgroundTaskQueue TaskQueue { get; }

        public QueuedHostedService(
            IBackgroundTaskQueue taskQueue,
            IServiceScopeFactory scopeFactory,
            IActivityTracker activity,
            ILogger<QueuedHostedService> logger)
        {
            TaskQueue = taskQueue;
            _scopeFactory = scopeFactory;
            _activity = activity;
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
                    // Escopo próprio: o HttpClient do Nextcloud é registrado por
                    // escopo (AddHttpClient) e este serviço é singleton.
                    using var scope = _scopeFactory.CreateScope();
                    var persistence = scope.ServiceProvider.GetRequiredService<IRoomPersistence>();
                    await persistence.PersistAsync(
                        workItem.RoomName, workItem.SourcePath, workItem.Finalize, null, stoppingToken);
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
    }
}
