using Microsoft.AspNetCore.SignalR;

namespace Jurius.CollabEditing.Hubs
{
    /// <summary>
    /// Hub SÓ da página de demonstração. Existe para provar, da própria página
    /// inicial, que o WebSocket atravessa o túnel da Cloudflare — que é o que mais
    /// costuma quebrar nesse tipo de publicação.
    ///
    /// Ele é deliberadamente burro: devolve a mensagem recebida e nada mais. Não
    /// enxerga Redis, salas nem documentos, então liberá-lo sem token não expõe
    /// conteúdo de cliente nenhum. Pode ser desligado com Demo__Enabled=false.
    /// </summary>
    public class DemoHub : Hub
    {
        public Task Ping(string message)
        {
            return Clients.Caller.SendAsync("pong", new
            {
                message,
                connectionId = Context.ConnectionId,
                serverTime = DateTime.UtcNow.ToString("o"),
            });
        }
    }
}
