using Jurius.CollabEditing.Model;
using Jurius.CollabEditing.Services;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace Jurius.CollabEditing.Hubs
{
    /// <summary>
    /// Hub de tempo real da co-edição. Entrega as operações a todos da sala e
    /// mantém a lista de quem está no documento. Baseado no exemplo oficial da
    /// Syncfusion, com três acréscimos nossos:
    ///
    ///  - quem entra manda também id e foto (a lista de participantes é a fonte
    ///    ÚNICA de "quem está editando junto" — antes havia um segundo canal de
    ///    presença, que dizia "fulano digitando" mesmo sem nada sincronizando);
    ///  - existe um aviso de digitação (`Typing`) que só circula dentro da sala;
    ///  - ao sair o último participante, o documento é gravado no Nextcloud.
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

        public async Task JoinGroup(RoomMemberInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.RoomName))
            {
                _logger.LogWarning("JoinGroup sem sala; conexão {Connection} ignorada.", Mask(Context.ConnectionId));
                return;
            }

            info.ConnectionId = Context.ConnectionId;
            info.Typing = false;
            await Groups.AddToGroupAsync(Context.ConnectionId, info.RoomName);

            string usersKey = info.RoomName + CollaborativeEditingHelper.UserInfoSuffix;

            if (await _db.KeyExistsAsync(usersKey))
            {
                var allUsers = await _db.HashGetAllAsync(usersKey);
                var userList = allUsers
                    .Select(entry => JsonConvert.DeserializeObject<RoomMemberInfo>(entry.Value))
                    .Where(member => member != null)
                    .ToList();

                // Quem acabou de entrar recebe a lista de quem já estava.
                await Clients.Caller.SendAsync("dataReceived", "addUser", userList);
            }

            await _db.HashSetAsync(usersKey, Context.ConnectionId, JsonConvert.SerializeObject(info));
            await _db.HashSetAsync(CollaborativeEditingHelper.ConnectionIdRoomMappingKey, Context.ConnectionId, info.RoomName);

            _activity.Record("entrou", info.RoomName, info.CurrentUser);
            _logger.LogInformation(
                "Sala {Room}: conexão {Connection} entrou ({People} pessoas).",
                info.RoomName, Mask(Context.ConnectionId), await _db.HashLengthAsync(usersKey));

            await Clients.GroupExcept(info.RoomName, Context.ConnectionId).SendAsync("dataReceived", "addUser", info);
        }

        /// <summary>
        /// "Fulano está digitando". Circula SÓ dentro da sala e SÓ para os outros —
        /// com uma pessoa só na sala nada é enviado (o cliente nem chama).
        /// Não é prova de sincronia: é aviso de digitação, e só.
        /// </summary>
        public async Task Typing(string roomName, bool typing)
        {
            if (string.IsNullOrWhiteSpace(roomName)) return;

            string mappedRoom = await _db.HashGetAsync(CollaborativeEditingHelper.ConnectionIdRoomMappingKey, Context.ConnectionId);
            // Só quem está na sala pode avisar sobre ela.
            if (!string.Equals(mappedRoom, roomName, StringComparison.Ordinal)) return;

            await Clients.GroupExcept(roomName, Context.ConnectionId).SendAsync(
                "dataReceived", "typing", new { connectionId = Context.ConnectionId, typing });
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
            await _db.HashDeleteAsync(CollaborativeEditingHelper.ConnectionIdRoomMappingKey, Context.ConnectionId);

            long remaining = await _db.HashLengthAsync(roomName + CollaborativeEditingHelper.UserInfoSuffix);

            _activity.Record("saiu", roomName, null, remaining == 0 ? "sala ficou vazia" : $"{remaining} ainda editando");
            _logger.LogInformation(
                "Sala {Room}: conexão {Connection} saiu ({People} restantes).",
                roomName, Mask(Context.ConnectionId), remaining);

            if (remaining == 0)
            {
                // Saiu o último: grava o que estiver pendente no arquivo do Nextcloud
                // e libera as chaves da sala.
                string sourcePath = await _db.StringGetAsync(roomName + CollaborativeEditingHelper.SourceInfoSuffix);
                _ = _saveTaskQueue.QueueBackgroundWorkItemAsync(new SaveInfo
                {
                    RoomName = roomName,
                    SourcePath = sourcePath,
                    Finalize = true,
                });
            }
            else
            {
                await Clients.Group(roomName).SendAsync("dataReceived", "removeUser", Context.ConnectionId);
            }

            await base.OnDisconnectedAsync(e);
        }

        private static string Mask(string value) =>
            string.IsNullOrEmpty(value) ? "—" : (value.Length <= 6 ? value : value[..6] + "…");
    }
}
