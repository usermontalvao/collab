using Jurius.CollabEditing.Model;
using Jurius.CollabEditing.Services;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using StackExchange.Redis;
using Syncfusion.EJ2.DocumentEditor;

namespace Jurius.CollabEditing.Hubs
{
    /// <summary>
    /// Hub de tempo real da co-edição. Entrega as operações a todos da sala e
    /// mantém a lista de quem está no documento. Baseado no exemplo oficial da
    /// Syncfusion; acréscimo nosso: ao sair o último participante, o caminho do
    /// arquivo no Nextcloud vai junto para a fila de gravação.
    /// </summary>
    public class DocumentEditorHub : Hub
    {
        private readonly IDatabase _db;
        private readonly IBackgroundTaskQueue _saveTaskQueue;
        private readonly IActivityTracker _activity;
        private readonly ILogger<DocumentEditorHub> _logger;

        public DocumentEditorHub(
            IConnectionMultiplexer redisConnection,
            IBackgroundTaskQueue taskQueue,
            IActivityTracker activity,
            ILogger<DocumentEditorHub> logger)
        {
            _db = redisConnection.GetDatabase();
            _saveTaskQueue = taskQueue;
            _activity = activity;
            _logger = logger;
        }

        public override Task OnConnectedAsync()
        {
            Clients.Caller.SendAsync("dataReceived", "connectionId", Context.ConnectionId);
            return base.OnConnectedAsync();
        }

        public async Task JoinGroup(ActionInfo info)
        {
            info.ConnectionId = Context.ConnectionId;
            await Groups.AddToGroupAsync(Context.ConnectionId, info.RoomName);

            bool roomExists = await _db.KeyExistsAsync(info.RoomName + CollaborativeEditingHelper.UserInfoSuffix);
            if (roomExists)
            {
                var allUsers = await _db.HashGetAllAsync(info.RoomName + CollaborativeEditingHelper.UserInfoSuffix);
                var userList = allUsers.Select(u => JsonConvert.DeserializeObject<ActionInfo>(u.Value)).ToList();

                // Quem acabou de entrar recebe a lista de quem já estava.
                await Clients.Caller.SendAsync("dataReceived", "addUser", userList);
            }

            await _db.HashSetAsync(info.RoomName + CollaborativeEditingHelper.UserInfoSuffix, Context.ConnectionId, JsonConvert.SerializeObject(info));
            await _db.HashSetAsync(CollaborativeEditingHelper.ConnectionIdRoomMappingKey, Context.ConnectionId, info.RoomName);

            _activity.Record("entrou", info.RoomName, info.CurrentUser);

            await Clients.GroupExcept(info.RoomName, Context.ConnectionId).SendAsync("dataReceived", "addUser", info);
        }

        public override async Task OnDisconnectedAsync(Exception e)
        {
            string roomName = await _db.HashGetAsync(CollaborativeEditingHelper.ConnectionIdRoomMappingKey, Context.ConnectionId);
            if (string.IsNullOrEmpty(roomName))
            {
                await base.OnDisconnectedAsync(e);
                return;
            }

            await _db.HashDeleteAsync(roomName + CollaborativeEditingHelper.UserInfoSuffix, Context.ConnectionId);

            var allUsers = await _db.HashGetAllAsync(roomName + CollaborativeEditingHelper.UserInfoSuffix);
            var userList = allUsers.Select(u => JsonConvert.DeserializeObject<ActionInfo>(u.Value)).ToList();

            await _db.HashDeleteAsync(CollaborativeEditingHelper.ConnectionIdRoomMappingKey, Context.ConnectionId);

            _activity.Record("saiu", roomName, null, userList.Count == 0 ? "sala ficou vazia" : $"{userList.Count} ainda editando");

            if (userList.Count == 0)
            {
                // Saiu o último: grava o que estiver pendente no arquivo do Nextcloud.
                RedisValue[] pendingOps = await _db.ListRangeAsync(roomName, 0, -1);
                string sourcePath = await _db.StringGetAsync(roomName + CollaborativeEditingHelper.SourceInfoSuffix);

                if (pendingOps.Length > 0)
                {
                    List<ActionInfo> actions = pendingOps
                        .Select(element => JsonConvert.DeserializeObject<ActionInfo>(element.ToString()))
                        .ToList();

                    var message = new SaveInfo
                    {
                        Action = actions,
                        PartialSave = false,
                        RoomName = roomName,
                        SourcePath = sourcePath,
                    };
                    _ = _saveTaskQueue.QueueBackgroundWorkItemAsync(message);
                    _logger.LogInformation("Sala {Room} vazia: {Count} operações enfileiradas para gravação.", roomName, actions.Count);
                }
            }
            else
            {
                await Clients.Group(roomName).SendAsync("dataReceived", "removeUser", Context.ConnectionId);
            }

            await base.OnDisconnectedAsync(e);
        }
    }
}
