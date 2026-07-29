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

    /// <summary>Versão corrente da sala, do jeito que o editor a conhece.</summary>
    private static async Task<int> VersaoDaSala(HttpClient client, string sala) =>
        await AbrirDocumento(client, sala);

    /// <summary>
    /// O botão Salvar do editor: manda o DOCUMENTO INTEIRO que está na tela.
    ///
    /// O documento aqui é montado do ZERO com o texto esperado, de propósito: nada
    /// que o serviço produz entra nesta conta. Assim, quando o teste encontra o
    /// texto no arquivo do Nextcloud, é porque o que foi enviado foi de fato
    /// gravado — e não porque as duas pontas repetiram o mesmo engano.
    ///
    /// (Era exatamente esse engano: o snapshot vinha do ImportFile, que devolve as
    /// operações PENDURADAS em `iOps` para o navegador aplicar. O DocIO as ignora,
    /// e o arquivo subia com o texto antigo.)
    /// </summary>
    private static async Task<SaveOutcome> Salvar(HttpClient client, string sala, string textoNaTela)
    {
        var versao = await VersaoDaSala(client, sala);
        var response = await EnviarSnapshot(
            client, sala, DocumentFactory.BrowserSnapshotOf(textoNaTela), versao);
        response.EnsureSuccessStatusCode();
        return JsonConvert.DeserializeObject<SaveOutcome>(await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> EnviarSnapshot(
        HttpClient client, string sala, string sfdt, int versao) =>
        client.PostAsJsonAsync("/api/CollaborativeEditing/SaveToSource", new
        {
            roomName = sala,
            sfdt,
            version = versao,
        });

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

        var resultado = await Salvar(client, sala, "OlaZZ");

        Assert.True(resultado.Uploaded, "O serviço respondeu sem ter enviado nada ao Nextcloud.");
        Assert.True(resultado.Verified, "O serviço confirmou sem ter RELIDO o arquivo do Nextcloud.");
        Assert.Equal(1, resultado.Operations);
        Assert.Equal(0, resultado.StillPending);
        Assert.Equal(1, servico.Storage.UploadCount);

        var texto = DocumentFactory.TextOf(servico.Storage.Read(CaminhoDocumento));
        Assert.Contains("OlaZZ", texto);

        // O caminho gravado tem de ser EXATAMENTE o caminho que será reaberto.
        Assert.Equal(new[] { CaminhoDocumento }, servico.Storage.UploadedPaths);
        Assert.Contains(CaminhoDocumento, servico.Storage.DownloadedPaths);
    }

    [SkippableFact(DisplayName = "Salvar nunca troca o caminho registrado da sala")]
    public async Task SalvarRecusaCaminhoDiferenteDoAberto()
    {
        using var servico = NovoServico();
        var client = servico.CreateClient();
        const string sala = "sala_caminho";

        var versao = await AbrirDocumento(client, sala);
        var operacao = await EnviarOperacao(
            client,
            DocumentFactory.Insert(sala, "conexao-da-ana", versao, "ZZ", offset: 4));

        var response = await client.PostAsJsonAsync(
            "/api/CollaborativeEditing/SaveToSource",
            new
            {
                roomName = sala,
                filePath = "Clientes/Outro/documento.docx",
                sfdt = DocumentFactory.BrowserSnapshotOf("OlaZZ"),
                version = operacao.Version,
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, servico.Storage.UploadCount);

        // A recusa não toca na fila nem no arquivo certo.
        var resultado = await Salvar(client, sala, "OlaZZ");
        Assert.True(resultado.Verified);
        Assert.Contains("OlaZZ", DocumentFactory.TextOf(servico.Storage.Read(CaminhoDocumento)));
    }

    [SkippableFact(DisplayName = "Salvar recusa snapshot defasado e preserva as operações")]
    public async Task SalvarRecusaSnapshotDefasado()
    {
        using var servico = NovoServico();
        var client = servico.CreateClient();
        const string sala = "sala_snapshot_defasado";

        var versao = await AbrirDocumento(client, sala);
        var primeira = await EnviarOperacao(
            client, DocumentFactory.Insert(sala, "conexao-da-ana", versao, "A", offset: 4));

        // A tela deste navegador está em "OlaA" — e é essa a versão que ele conhece.
        var versaoAntiga = primeira.Version;
        var snapshotAntigo = DocumentFactory.BrowserSnapshotOf("OlaA");

        // Enquanto ele preparava o documento, chegou a edição de outra pessoa.
        await EnviarOperacao(
            client, DocumentFactory.Insert(sala, "conexao-do-bruno", primeira.Version, "B", offset: 5));

        var response = await EnviarSnapshot(client, sala, snapshotAntigo, versaoAntiga);

        // Gravar o "OlaA" agora APAGARIA o "B" da outra pessoa.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, servico.Storage.UploadCount);

        // A fila fica intacta: sincronizando e salvando de novo, nada se perde.
        var resultado = await Salvar(client, sala, "OlaAB");
        Assert.True(resultado.Uploaded);
        Assert.True(resultado.Verified);
        Assert.Equal(2, resultado.Operations);
        Assert.Contains("OlaAB", DocumentFactory.TextOf(servico.Storage.Read(CaminhoDocumento)));
    }

    [SkippableFact(DisplayName = "Salvar só confirma depois de reler o mesmo DOCX no Nextcloud")]
    public async Task SalvarDetectaPutQueNaoSubstituiuOArquivo()
    {
        using var servico = NovoServico();
        var client = servico.CreateClient();
        const string sala = "sala_put_falso";

        var versao = await AbrirDocumento(client, sala);
        await EnviarOperacao(
            client, DocumentFactory.Insert(sala, "conexao-da-ana", versao, "ZZ", offset: 4));

        // WebDAV respondendo 2xx sem substituir o arquivo.
        servico.Storage.IgnoreUploads = true;

        var failed = await EnviarSnapshot(
            client, sala, DocumentFactory.BrowserSnapshotOf("OlaZZ"), await VersaoDaSala(client, sala));

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.DoesNotContain("OlaZZ", DocumentFactory.TextOf(servico.Storage.Read(CaminhoDocumento)));

        // A fila só é aparada depois da verificação. Corrigido o armazenamento,
        // a mesma edição continua disponível e pode ser gravada de verdade.
        servico.Storage.IgnoreUploads = false;
        var resultado = await Salvar(client, sala, "OlaZZ");
        Assert.True(resultado.Uploaded);
        Assert.Equal(1, resultado.Operations);
        Assert.Contains("OlaZZ", DocumentFactory.TextOf(servico.Storage.Read(CaminhoDocumento)));
    }

    [SkippableFact(DisplayName = "Salvar avisa a sala inteira — quem não clicou também vê \"Salvo\"")]
    public async Task SalvarAvisaASalaInteira()
    {
        using var servico = NovoServico();
        var client = servico.CreateClient();
        const string sala = "sala_aviso_salvo";

        var versao = await AbrirDocumento(client, sala);

        await using var ana = await servico.ConnectHubAsync();
        await using var bruno = await servico.ConnectHubAsync();

        var avisosParaAna = new List<System.Text.Json.JsonElement>();
        var avisosParaBruno = new List<System.Text.Json.JsonElement>();
        ana.On<string, System.Text.Json.JsonElement>("dataReceived", (tipo, dados) =>
        {
            if (tipo == "saved") lock (avisosParaAna) avisosParaAna.Add(dados);
        });
        bruno.On<string, System.Text.Json.JsonElement>("dataReceived", (tipo, dados) =>
        {
            if (tipo == "saved") lock (avisosParaBruno) avisosParaBruno.Add(dados);
        });

        await ana.InvokeAsync("JoinGroup", new RoomMemberInfo { RoomName = sala, CurrentUser = "Ana" });
        await bruno.InvokeAsync("JoinGroup", new RoomMemberInfo { RoomName = sala, CurrentUser = "Bruno" });

        await EnviarOperacao(client, DocumentFactory.Insert(sala, "conexao-da-ana", versao, "ZZ", offset: 4));

        // Ana clica em Salvar; Bruno não fez nada.
        var resultado = await Salvar(client, sala, "OlaZZ");
        Assert.True(resultado.Uploaded);

        Assert.True(
            await Ate(() => { lock (avisosParaBruno) return avisosParaBruno.Count > 0; }),
            "Bruno não foi avisado de que o documento foi gravado.");
        Assert.True(
            await Ate(() => { lock (avisosParaAna) return avisosParaAna.Count > 0; }),
            "Quem salvou também deve receber o aviso (confirma a rodada completa).");

        System.Text.Json.JsonElement aviso;
        lock (avisosParaBruno) aviso = avisosParaBruno[0];
        Assert.True(aviso.GetProperty("uploaded").GetBoolean());
        Assert.True(
            aviso.GetProperty("verified").GetBoolean(),
            "O aviso só pode liberar “Salvo” depois da releitura do Nextcloud.");
        Assert.Equal(0, aviso.GetProperty("stillPending").GetInt64());
        Assert.Equal(1, aviso.GetProperty("operations").GetInt32());
    }

    [SkippableFact(DisplayName = "Salvar sem nada pendente não inventa gravação")]
    public async Task SalvarSemPendenciaNaoGrava()
    {
        using var servico = NovoServico();
        var client = servico.CreateClient();
        const string sala = "sala_vazia";

        await AbrirDocumento(client, sala);
        var resultado = await Salvar(client, sala, TextoInicial);

        Assert.False(resultado.Uploaded);
        Assert.False(resultado.Verified);
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

        // Os dois navegadores estão na mesma versão e com o mesmo texto na tela.
        var versaoNaTela = await VersaoDaSala(client, sala);
        var naTela = DocumentFactory.BrowserSnapshotOf("OlaZZ");

        async Task<SaveOutcome> SalvarAgora()
        {
            var resposta = await EnviarSnapshot(client, sala, naTela, versaoNaTela);
            resposta.EnsureSuccessStatusCode();
            return JsonConvert.DeserializeObject<SaveOutcome>(await resposta.Content.ReadAsStringAsync());
        }

        var resultados = await Task.WhenAll(SalvarAgora(), SalvarAgora());

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

        // A edição do Bruno entra DENTRO da janela de gravação: depois da foto
        // atômica da fila e antes de o arquivo terminar de subir. É a janela em que
        // apagar a chave do Redis (em vez de aparar por quantidade) perderia o "BB".
        Task<ActionInfo> durante = null;
        servico.Storage.DuringUpload = () =>
        {
            servico.Storage.DuringUpload = null;
            // A requisição chega durante o upload, mas a operação inteira espera a
            // mesma trava da gravação. Isto impede fotografar o RPUSH antes do LSET
            // transformado e impede a migração entre as duas filas no meio do corte.
            durante = EnviarOperacao(
                client, DocumentFactory.Insert(sala, "conexao-do-bruno", primeira.Version, "BB", offset: 6));
            return Task.CompletedTask;
        };

        var salvamento = await Salvar(client, sala, "OlaAA");
        Assert.NotNull(durante);
        var operacaoDurante = await durante;
        Assert.NotNull(operacaoDurante);
        Assert.Equal(1, salvamento.Operations);
        // O "BB" NÃO entrou nesta gravação; foi enfileirado logo depois que a
        // trava saiu e continua pendente — não foi gravado duas vezes nem apagado.
        Assert.Equal(0, salvamento.StillPending);
        Assert.Contains("OlaAA", DocumentFactory.TextOf(servico.Storage.Read(CaminhoDocumento)));

        // Segunda gravação: a conta de deslocamento tem de continuar batendo (era
        // aqui que a fórmula antiga embaralhava o texto).
        var segundoSalvamento = await Salvar(client, sala, "OlaAABB");
        Assert.Equal(1, segundoSalvamento.Operations);
        Assert.Equal(0, segundoSalvamento.StillPending);

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
        await Salvar(client, sala, "OlaZZ");

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

    [SkippableFact(DisplayName = "Documento chegando com as edições PENDURADAS é recusado e a fila é preservada")]
    public async Task SnapshotComOperacoesPenduradasEhRecusado()
    {
        using var servico = NovoServico();
        var client = servico.CreateClient();
        const string sala = "sala_penduradas";

        var versao = await AbrirDocumento(client, sala);
        await EnviarOperacao(client, DocumentFactory.Insert(sala, "conexao-da-ana", versao, "ZZ", offset: 4));

        // O SFDT do ImportFile traz as operações em `iOps`, para o NAVEGADOR
        // aplicar. Convertê-lo para .docx grava o texto ANTIGO — foi assim que
        // "Salvo" passou a mentir. O serviço tem de recusar.
        var doImportFile = await client.PostAsJsonAsync("/api/CollaborativeEditing/ImportFile", new
        {
            roomName = sala,
            filePath = CaminhoDocumento,
            fileName = "peticao.docx",
        });
        doImportFile.EnsureSuccessStatusCode();
        var conteudo = JsonConvert.DeserializeObject<DocumentContent>(
            await doImportFile.Content.ReadAsStringAsync());

        var recusa = await EnviarSnapshot(client, sala, conteudo.sfdt, conteudo.version);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, recusa.StatusCode);
        Assert.Equal(0, servico.Storage.UploadCount);
        Assert.DoesNotContain("OlaZZ", DocumentFactory.TextOf(servico.Storage.Read(CaminhoDocumento)));

        // Nada saiu do Redis: o mesmo texto ainda pode ser gravado de verdade.
        var resultado = await Salvar(client, sala, "OlaZZ");
        Assert.True(resultado.Uploaded);
        Assert.Equal(1, resultado.Operations);
        Assert.Contains("OlaZZ", DocumentFactory.TextOf(servico.Storage.Read(CaminhoDocumento)));
    }

    [SkippableFact(DisplayName = "Front antigo (sem o documento no corpo) continua gravando pela fila")]
    public async Task FrontAntigoSemSnapshotAindaGrava()
    {
        using var servico = NovoServico();
        var client = servico.CreateClient();
        const string sala = "sala_front_antigo";

        var versao = await AbrirDocumento(client, sala);
        await EnviarOperacao(client, DocumentFactory.Insert(sala, "conexao-da-ana", versao, "ZZ", offset: 4));

        // É o corpo que o front anterior manda: só a sala. Durante a implantação as
        // duas versões conversam com o mesmo serviço.
        var response = await client.PostAsJsonAsync(
            "/api/CollaborativeEditing/SaveToSource", new { roomName = sala });
        response.EnsureSuccessStatusCode();
        var resultado = JsonConvert.DeserializeObject<SaveOutcome>(
            await response.Content.ReadAsStringAsync());

        Assert.True(resultado.Uploaded);
        Assert.True(resultado.Verified);
        Assert.Contains("OlaZZ", DocumentFactory.TextOf(servico.Storage.Read(CaminhoDocumento)));
    }

    [SkippableFact(DisplayName = "Documento com tabela, lista, imagem e revisão é gravado inteiro")]
    public async Task DocumentoRicoEhGravadoInteiro()
    {
        using var servico = NovoServico();
        const string marca = "PeticaoInicial";
        servico.Storage.Seed(CaminhoDocumento, DocumentFactory.NewRichDocx(marca));

        var client = servico.CreateClient();
        const string sala = "sala_documento_rico";

        var versao = await AbrirDocumento(client, sala);
        await EnviarOperacao(
            client,
            DocumentFactory.Insert(sala, "conexao-da-ana", versao, "ZZ", offset: marca.Length + 1));

        var naTela = DocumentFactory.BrowserSnapshot(DocumentFactory.NewRichDocx(marca + "ZZ"));
        var response = await EnviarSnapshot(client, sala, naTela, await VersaoDaSala(client, sala));
        response.EnsureSuccessStatusCode();

        var gravado = DocumentFactory.TextOf(servico.Storage.Read(CaminhoDocumento));
        Assert.Contains(marca + "ZZ", gravado);
        Assert.Contains("R$ 1.000,00", gravado);
        Assert.Contains("Primeiro pedido", gravado);
        Assert.Contains("Trecho com revisão", gravado);
    }

    [SkippableFact(DisplayName = "Gravação final que FALHA não apaga as operações da sala")]
    public async Task GravacaoFinalQueFalhaNaoApagaAsOperacoes()
    {
        using var servico = NovoServico();
        var client = servico.CreateClient();
        const string sala = "sala_final_falha";

        var versao = await AbrirDocumento(client, sala);

        var conexao = await servico.ConnectHubAsync();
        await conexao.InvokeAsync("JoinGroup", new RoomMemberInfo { RoomName = sala, CurrentUser = "Ana" });
        await EnviarOperacao(client, DocumentFactory.Insert(sala, "conexao-da-ana", versao, "ZZ", offset: 4));

        // O Nextcloud está fora do ar exatamente quando a última pessoa sai.
        servico.Storage.FailUploads = true;
        await conexao.DisposeAsync();

        Assert.True(
            await Ate(() => servico.Storage.UploadCount > 0),
            "A saída da última pessoa nem tentou gravar.");
        Assert.DoesNotContain("OlaZZ", DocumentFactory.TextOf(servico.Storage.Read(CaminhoDocumento)));

        // A EDIÇÃO NÃO PODE TER SIDO APAGADA junto com a sala: ela é a única cópia
        // do que foi digitado. Com o armazenamento de volta, reabrir e salvar
        // recupera o texto.
        servico.Storage.FailUploads = false;
        Assert.Equal(versao + 1, await VersaoDaSala(client, sala));

        var resultado = await Salvar(client, sala, "OlaZZ");
        Assert.True(resultado.Uploaded);
        Assert.Equal(1, resultado.Operations);
        Assert.Contains("OlaZZ", DocumentFactory.TextOf(servico.Storage.Read(CaminhoDocumento)));
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
