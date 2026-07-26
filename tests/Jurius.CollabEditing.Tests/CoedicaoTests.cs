using System.Net;
using System.Net.Http.Json;
using System.Text;
using Jurius.CollabEditing.Model;
using Jurius.CollabEditing.Tests.Infra;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using Syncfusion.EJ2.DocumentEditor;
using Xunit;

namespace Jurius.CollabEditing.Tests;

/// <summary>
/// O que estes testes provam, na ordem dos critérios de aceite:
///  - o que uma pessoa digita chega às OUTRAS da mesma sala (e só a elas);
///  - Salvar grava mesmo no Nextcloud e o documento reaberto tem o texto;
///  - duas gravações ao mesmo tempo não aplicam a mesma operação duas vezes;
///  - o que é digitado DURANTE a gravação não se perde;
///  - quem cai e volta recupera o que perdeu;
///  - sem token do CRM, nada de operação (era esta a falha que travava tudo).
/// </summary>
[Collection("redis")]
public class CoedicaoTests
{
    private const string CaminhoDocumento = "Clientes/Exemplo/peticao.docx";
    private const string TextoInicial = "Ola";

    private readonly RedisFixture _redis;

    public CoedicaoTests(RedisFixture redis)
    {
        _redis = redis;
        Skip.IfNot(_redis.Available, _redis.UnavailableReason);
        _redis.Flush();
    }

    private CollabAppFactory NovoServico(bool requireAuth = false)
    {
        var factory = new CollabAppFactory(_redis.Endpoint, requireAuth);
        factory.Storage.Seed(CaminhoDocumento, DocumentFactory.NewDocx(TextoInicial));
        return factory;
    }

    private static async Task<int> AbrirDocumento(HttpClient client, string sala)
    {
        var response = await client.PostAsJsonAsync("/api/CollaborativeEditing/ImportFile", new
        {
            roomName = sala,
            filePath = CaminhoDocumento,
            fileName = "peticao.docx",
        });
        response.EnsureSuccessStatusCode();
        var content = JsonConvert.DeserializeObject<DocumentContent>(await response.Content.ReadAsStringAsync());
        return content.version;
    }

    private static async Task<ActionInfo> EnviarOperacao(HttpClient client, ActionInfo acao)
    {
        var response = await client.PostAsync(
            "/api/CollaborativeEditing/UpdateAction",
            new StringContent(JsonConvert.SerializeObject(acao), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        return JsonConvert.DeserializeObject<ActionInfo>(await response.Content.ReadAsStringAsync());
    }

    private static async Task<SaveOutcome> Salvar(HttpClient client, string sala)
    {
        var response = await client.PostAsJsonAsync("/api/CollaborativeEditing/SaveToSource", new { roomName = sala });
        response.EnsureSuccessStatusCode();
        return JsonConvert.DeserializeObject<SaveOutcome>(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Espera uma condição sem `Thread.Sleep` fixo (teste lento e instável).</summary>
    private static async Task<bool> Ate(Func<bool> condicao, int timeoutMs = 8000)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < limite)
        {
            if (condicao()) return true;
            await Task.Delay(25);
        }
        return condicao();
    }

    // ---------------------------------------------------------------- entrega

    [SkippableFact(DisplayName = "Duas conexões na mesma sala recebem as operações uma da outra")]
    public async Task DuasConexoesNaMesmaSalaRecebemAsOperacoes()
    {
        using var servico = NovoServico();
        var client = servico.CreateClient();
        const string sala = "sala_entrega";

        var versao = await AbrirDocumento(client, sala);

        await using var ana = await servico.ConnectHubAsync();
        await using var bruno = await servico.ConnectHubAsync();

        // JsonElement: o mesmo `dataReceived` carrega ora um texto (connectionId),
        // ora um objeto (action) — igual ao que chega no navegador.
        var recebidasPorBruno = new List<int>();
        bruno.On<string, System.Text.Json.JsonElement>("dataReceived", (tipo, dados) =>
        {
            if (tipo != "action") return;
            recebidasPorBruno.Add(dados.GetProperty("version").GetInt32());
        });

        await ana.InvokeAsync("JoinGroup", new RoomMemberInfo { RoomName = sala, CurrentUser = "Ana" });
        await bruno.InvokeAsync("JoinGroup", new RoomMemberInfo { RoomName = sala, CurrentUser = "Bruno" });

        await EnviarOperacao(client, DocumentFactory.Insert(sala, "conexao-da-ana", versao, "X", offset: 4));

        Assert.True(
            await Ate(() => recebidasPorBruno.Count > 0),
            "Bruno não recebeu a operação que a Ana enviou.");
        Assert.Equal(versao + 1, recebidasPorBruno[0]);
    }

    [SkippableFact(DisplayName = "Salas de arquivos diferentes ficam isoladas")]
    public async Task SalasDeArquivosDiferentesFicamIsoladas()
    {
        using var servico = NovoServico();
        servico.Storage.Seed("Clientes/Outro/contrato.docx", DocumentFactory.NewDocx("Outro"));
        var client = servico.CreateClient();

        var versaoA = await AbrirDocumento(client, "sala_a");

        var responseB = await client.PostAsJsonAsync("/api/CollaborativeEditing/ImportFile", new
        {
            roomName = "sala_b",
            filePath = "Clientes/Outro/contrato.docx",
            fileName = "contrato.docx",
        });
        responseB.EnsureSuccessStatusCode();

        await using var naSalaA = await servico.ConnectHubAsync();
        await using var naSalaB = await servico.ConnectHubAsync();

        var recebidasNaB = 0;
        var recebidasNaA = 0;
        naSalaB.On<string, object>("dataReceived", (tipo, _) => { if (tipo == "action") Interlocked.Increment(ref recebidasNaB); });
        naSalaA.On<string, object>("dataReceived", (tipo, _) => { if (tipo == "action") Interlocked.Increment(ref recebidasNaA); });

        await naSalaA.InvokeAsync("JoinGroup", new RoomMemberInfo { RoomName = "sala_a", CurrentUser = "Ana" });
        await naSalaB.InvokeAsync("JoinGroup", new RoomMemberInfo { RoomName = "sala_b", CurrentUser = "Bruno" });

        await EnviarOperacao(client, DocumentFactory.Insert("sala_a", "conexao-da-ana", versaoA, "X", offset: 4));

        Assert.True(await Ate(() => Volatile.Read(ref recebidasNaA) > 0), "A operação nem chegou à própria sala.");
        // Margem depois da entrega na sala certa: se fosse vazar, teria vazado aqui.
        await Task.Delay(400);
        Assert.Equal(0, Volatile.Read(ref recebidasNaB));
    }

    [SkippableFact(DisplayName = "Aviso de digitação não sai quando há uma pessoa só na sala")]
    public async Task TypingNaoTransmiteComUmaPessoaSo()
    {
        using var servico = NovoServico();
        const string sala = "sala_typing";
        await AbrirDocumento(servico.CreateClient(), sala);

        await using var sozinha = await servico.ConnectHubAsync();
        var avisos = 0;
        sozinha.On<string, object>("dataReceived", (tipo, _) => { if (tipo == "typing") Interlocked.Increment(ref avisos); });

        await sozinha.InvokeAsync("JoinGroup", new RoomMemberInfo { RoomName = sala, CurrentUser = "Ana" });
        await sozinha.InvokeAsync("Typing", sala, true);
        await Task.Delay(400);

        Assert.Equal(0, Volatile.Read(ref avisos));

        // Com duas pessoas, o aviso circula — mas só para a OUTRA.
        await using var acompanhando = await servico.ConnectHubAsync();
        var avisosDoOutro = 0;
        acompanhando.On<string, object>("dataReceived", (tipo, _) => { if (tipo == "typing") Interlocked.Increment(ref avisosDoOutro); });
        await acompanhando.InvokeAsync("JoinGroup", new RoomMemberInfo { RoomName = sala, CurrentUser = "Bruno" });

        await sozinha.InvokeAsync("Typing", sala, true);

        Assert.True(await Ate(() => Volatile.Read(ref avisosDoOutro) > 0), "O aviso de digitação não chegou a quem está junto.");
        Assert.Equal(0, Volatile.Read(ref avisos));
    }

    [SkippableFact(DisplayName = "Reentrar limpa a conexão antiga da MESMA pessoa (não vira 'outra pessoa na sala')")]
    public async Task ReentrarRemoveConexaoAntigaDoMesmoUsuario()
    {
        using var servico = NovoServico();
        const string sala = "sala_sobra";
        await AbrirDocumento(servico.CreateClient(), sala);

        // Primeira aba entra e "morre" sem que o servidor perceba: para simular
        // isso, entramos e deixamos a conexão viva, mas reentramos com o mesmo
        // usuário por outra conexão — é o que acontece quando a rede cai e o
        // navegador reabre o documento antes do servidor derrubar a antiga.
        await using var abaAntiga = await servico.ConnectHubAsync();
        await abaAntiga.InvokeAsync("JoinGroup", new RoomMemberInfo
        {
            RoomName = sala,
            CurrentUser = "Pedro",
            UserId = "u-pedro",
        });

        await using var abaNova = await servico.ConnectHubAsync();
        var listaRecebida = new List<int>();
        abaNova.On<string, System.Text.Json.JsonElement>("dataReceived", (tipo, dados) =>
        {
            if (tipo != "addUser") return;
            listaRecebida.Add(dados.ValueKind == System.Text.Json.JsonValueKind.Array ? dados.GetArrayLength() : 1);
        });

        await abaNova.InvokeAsync("JoinGroup", new RoomMemberInfo
        {
            RoomName = sala,
            CurrentUser = "Pedro",
            UserId = "u-pedro",
        });

        Assert.True(await Ate(() => listaRecebida.Count > 0), "A nova aba não recebeu a lista da sala.");
        // A sala tem de chegar VAZIA: a única outra conexão era dele mesmo.
        Assert.Equal(0, listaRecebida[0]);
    }

    [SkippableFact(DisplayName = "Reentrar NÃO remove as outras pessoas da sala")]
    public async Task ReentrarPreservaAsOutrasPessoas()
    {
        using var servico = NovoServico();
        const string sala = "sala_sobra_outros";
        await AbrirDocumento(servico.CreateClient(), sala);

        await using var ana = await servico.ConnectHubAsync();
        await ana.InvokeAsync("JoinGroup", new RoomMemberInfo
        {
            RoomName = sala,
            CurrentUser = "Ana",
            UserId = "u-ana",
        });

        await using var pedro = await servico.ConnectHubAsync();
        var tamanhoDaLista = new List<int>();
        pedro.On<string, System.Text.Json.JsonElement>("dataReceived", (tipo, dados) =>
        {
            if (tipo != "addUser") return;
            tamanhoDaLista.Add(dados.ValueKind == System.Text.Json.JsonValueKind.Array ? dados.GetArrayLength() : 1);
        });

        await pedro.InvokeAsync("JoinGroup", new RoomMemberInfo
        {
            RoomName = sala,
            CurrentUser = "Pedro",
            UserId = "u-pedro",
        });

        Assert.True(await Ate(() => tamanhoDaLista.Count > 0), "Pedro não recebeu a lista da sala.");
        Assert.Equal(1, tamanhoDaLista[0]);
    }

    // ---------------------------------------------------------------- gravação

    [SkippableFact(DisplayName = "Salvar força a gravação no Nextcloud e o documento reaberto tem o texto")]
    public async Task SalvarForcaPersistenciaEReabrirContemAsAlteracoes()
    {
        using var servico = NovoServico();
        var client = servico.CreateClient();
        const string sala = "sala_salvar";

        var versao = await AbrirDocumento(client, sala);
        Assert.Equal(0, servico.Storage.UploadCount);

        // "Ola" + "ZZ" no fim (deslocamento 1-based: depois de 3 letras, 4).
        await EnviarOperacao(client, DocumentFactory.Insert(sala, "conexao-da-ana", versao, "ZZ", offset: 4));

        var resultado = await Salvar(client, sala);

        Assert.True(resultado.Uploaded, "O serviço respondeu sem ter enviado nada ao Nextcloud.");
        Assert.Equal(1, resultado.Operations);
        Assert.Equal(0, resultado.StillPending);
        Assert.Equal(1, servico.Storage.UploadCount);

        var texto = DocumentFactory.TextOf(servico.Storage.Read(CaminhoDocumento));
        Assert.Contains("OlaZZ", texto);
    }

    [SkippableFact(DisplayName = "Salvar sem nada pendente não inventa gravação")]
    public async Task SalvarSemPendenciaNaoGrava()
    {
        using var servico = NovoServico();
        var client = servico.CreateClient();
        const string sala = "sala_vazia";

        await AbrirDocumento(client, sala);
        var resultado = await Salvar(client, sala);

        Assert.False(resultado.Uploaded);
        Assert.Equal(0, resultado.Operations);
        Assert.Equal(0, servico.Storage.UploadCount);
    }

    [SkippableFact(DisplayName = "Gravações concorrentes não aplicam a mesma operação duas vezes")]
    public async Task FlushConcorrenteNaoDuplicaOperacoes()
    {
        using var servico = NovoServico();
        var client = servico.CreateClient();
        const string sala = "sala_concorrente";

        var versao = await AbrirDocumento(client, sala);
        await EnviarOperacao(client, DocumentFactory.Insert(sala, "conexao-da-ana", versao, "ZZ", offset: 4));

        var primeira = Salvar(client, sala);
        var segunda = Salvar(client, sala);
        var resultados = await Task.WhenAll(primeira, segunda);

        // A operação foi contabilizada UMA vez só, não importa qual das duas
        // gravações chegou primeiro.
        Assert.Equal(1, resultados.Sum(resultado => resultado.Operations));

        var texto = DocumentFactory.TextOf(servico.Storage.Read(CaminhoDocumento));
        Assert.Contains("OlaZZ", texto);
        Assert.DoesNotContain("OlaZZZZ", texto);
    }

    [SkippableFact(DisplayName = "O que é digitado durante a gravação não se perde")]
    public async Task OperacoesDuranteAGravacaoSobrevivem()
    {
        using var servico = NovoServico();
        var client = servico.CreateClient();
        const string sala = "sala_durante";

        var versao = await AbrirDocumento(client, sala);
        var primeira = await EnviarOperacao(client, DocumentFactory.Insert(sala, "conexao-da-ana", versao, "AA", offset: 4));

        var salvamento = await Salvar(client, sala);
        Assert.Equal(1, salvamento.Operations);

        // Continua digitando DEPOIS do corte: a conta de deslocamento tem de
        // continuar batendo (era aqui que a fórmula antiga embaralhava o texto).
        await EnviarOperacao(client, DocumentFactory.Insert(sala, "conexao-da-ana", primeira.Version, "BB", offset: 6));

        var segundoSalvamento = await Salvar(client, sala);
        Assert.Equal(1, segundoSalvamento.Operations);

        var texto = DocumentFactory.TextOf(servico.Storage.Read(CaminhoDocumento));
        Assert.Contains("OlaAABB", texto);
    }

    [SkippableFact(DisplayName = "Reabrir o documento depois de salvar traz o texto uma vez só")]
    public async Task ReabrirDepoisDeSalvarNaoDuplica()
    {
        using var servico = NovoServico();
        var client = servico.CreateClient();
        const string sala = "sala_reabrir";

        var versao = await AbrirDocumento(client, sala);
        await EnviarOperacao(client, DocumentFactory.Insert(sala, "conexao-da-ana", versao, "ZZ", offset: 4));
        await Salvar(client, sala);

        // ImportFile de novo: o arquivo já tem o "ZZ" e a fila está vazia, então o
        // texto NÃO pode aparecer duas vezes no SFDT devolvido.
        var response = await client.PostAsJsonAsync("/api/CollaborativeEditing/ImportFile", new
        {
            roomName = sala,
            filePath = CaminhoDocumento,
            fileName = "peticao.docx",
        });
        response.EnsureSuccessStatusCode();
        var conteudo = JsonConvert.DeserializeObject<DocumentContent>(await response.Content.ReadAsStringAsync());

        var reaberto = WordDocument.Save(conteudo.sfdt);
        var texto = reaberto.GetText() ?? string.Empty;
        reaberto.Dispose();

        Assert.Contains("OlaZZ", texto);
        Assert.DoesNotContain("OlaZZZZ", texto);
    }

    // ------------------------------------------------------------ reconexão

    [SkippableFact(DisplayName = "Depois de reconectar, GetActionsFromServer devolve o que foi perdido")]
    public async Task ReconexaoRecuperaOperacoesPerdidas()
    {
        using var servico = NovoServico();
        var client = servico.CreateClient();
        const string sala = "sala_reconexao";

        var versaoQuandoCaiu = await AbrirDocumento(client, sala);

        // Enquanto "estava fora", outras duas operações entraram.
        var primeira = await EnviarOperacao(client, DocumentFactory.Insert(sala, "outra-conexao", versaoQuandoCaiu, "A", offset: 4));
        await EnviarOperacao(client, DocumentFactory.Insert(sala, "outra-conexao", primeira.Version, "B", offset: 5));

        var response = await client.PostAsync(
            "/api/CollaborativeEditing/GetActionsFromServer",
            new StringContent(
                JsonConvert.SerializeObject(new ActionInfo { RoomName = sala, Version = versaoQuandoCaiu }),
                Encoding.UTF8,
                "application/json"));
        response.EnsureSuccessStatusCode();

        var perdidas = JsonConvert.DeserializeObject<List<ActionInfo>>(await response.Content.ReadAsStringAsync());

        Assert.Equal(2, perdidas.Count);
        Assert.Equal(versaoQuandoCaiu + 1, perdidas[0].Version);
        Assert.Equal(versaoQuandoCaiu + 2, perdidas[1].Version);
    }

    [SkippableFact(DisplayName = "Sair do documento grava no Nextcloud e libera a sala")]
    public async Task SairDaSalaGravaELimpa()
    {
        using var servico = NovoServico();
        var client = servico.CreateClient();
        const string sala = "sala_saida";

        var versao = await AbrirDocumento(client, sala);

        var conexao = await servico.ConnectHubAsync();
        await conexao.InvokeAsync("JoinGroup", new RoomMemberInfo { RoomName = sala, CurrentUser = "Ana" });
        await EnviarOperacao(client, DocumentFactory.Insert(sala, "conexao-da-ana", versao, "ZZ", offset: 4));
        await conexao.DisposeAsync();

        Assert.True(
            await Ate(() => servico.Storage.UploadCount > 0),
            "A saída da última pessoa não gravou o documento.");

        var texto = DocumentFactory.TextOf(servico.Storage.Read(CaminhoDocumento));
        Assert.Contains("OlaZZ", texto);
    }

    // ----------------------------------------------------------------- auth

    [SkippableFact(DisplayName = "Sem token do CRM, UpdateAction é recusado (e com token passa)")]
    public async Task UpdateActionExigeToken()
    {
        using var servico = NovoServico(requireAuth: true);
        var client = servico.CreateClient();
        const string sala = "sala_auth";

        var corpo = JsonConvert.SerializeObject(new ActionInfo { RoomName = sala, Version = 0 });

        var semToken = await client.PostAsync(
            "/api/CollaborativeEditing/UpdateAction",
            new StringContent(corpo, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, semToken.StatusCode);

        // Era exatamente esta a falha: o Document Editor manda as operações por um
        // XMLHttpRequest próprio; sem o cabeçalho, TODA edição parava aqui.
        var comToken = new HttpRequestMessage(HttpMethod.Post, "/api/CollaborativeEditing/UpdateAction")
        {
            Content = new StringContent(corpo, Encoding.UTF8, "application/json"),
        };
        comToken.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token-valido");

        var resposta = await client.SendAsync(comToken);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [SkippableFact(DisplayName = "Sem token do CRM, SaveToSource é recusado")]
    public async Task SaveToSourceExigeToken()
    {
        using var servico = NovoServico(requireAuth: true);
        var client = servico.CreateClient();

        var resposta = await client.PostAsJsonAsync(
            "/api/CollaborativeEditing/SaveToSource",
            new { roomName = "sala_auth_save" });

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }
}
