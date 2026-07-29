using Jurius.CollabEditing.Services;
using Jurius.CollabEditing.Tests.Infra;
using Newtonsoft.Json;
using Syncfusion.EJ2.DocumentEditor;
using Xunit;

namespace Jurius.CollabEditing.Tests;

/// <summary>
/// A FALHA QUE ESTES TESTES TRANCAM.
///
/// `WordDocument.UpdateActions()` não aplica as operações no documento: ela as
/// PENDURA no SFDT, numa chave `iOps`, para o NAVEGADOR aplicar depois do
/// `open()`. As `sections` saem idênticas às da entrada.
///
/// Por isso o serviço passou a gravar mentira: o `Syncfusion.DocIO` ignora `iOps`
/// ao converter para .docx, então o arquivo subia com o texto ANTIGO, o Nextcloud
/// respondia 2xx, a tela dizia "Salvo" e a fila do Redis era apagada.
///
/// Não precisam de Redis: são sobre a conversão do documento, e rodam sempre.
/// </summary>
public class ReaplicacaoTests
{
    private const string TextoInicial = "abcdef";

    private static string Reaplicar(byte[] docx, params ActionInfo[] acoes)
    {
        using var stream = new MemoryStream(docx, writable: false);
        var document = WordDocument.Load(stream, FormatType.Docx);
        try
        {
            return DocumentReplay.ReplayToSfdt(document, acoes.ToList());
        }
        finally
        {
            document.Dispose();
        }
    }

    private static ActionInfo Acao(params DocumentOperation[] operacoes) => new()
    {
        RoomName = "sala",
        ConnectionId = "conexao",
        CurrentUser = "Teste",
        Version = 1,
        Operations = operacoes.ToList(),
    };

    private static Syncfusion.DocIO.DLS.WordDocument Abrir(string sfdt) => WordDocument.Save(sfdt);

    [Fact(DisplayName = "Reaplicar uma inserção MUDA o documento (era isto que UpdateActions não fazia)")]
    public void ReaplicarInsercaoAlteraODocumento()
    {
        var sfdt = Reaplicar(
            DocumentFactory.NewDocx(TextoInicial),
            Acao(new DocumentOperation { Action = "Insert", Offset = 7, Length = 2, Text = "ZZ" }));

        Assert.Contains("abcdefZZ", DocumentFactory.TextOfSfdt(sfdt));
    }

    [Fact(DisplayName = "WordDocument.UpdateActions NÃO aplica nada — a regressão que trouxe este código")]
    public void UpdateActionsApenasPenduraAsOperacoes()
    {
        using var stream = new MemoryStream(DocumentFactory.NewDocx(TextoInicial), writable: false);
        var document = WordDocument.Load(stream, FormatType.Docx);
        document.OptimizeSfdt = false;

        document.UpdateActions(new List<ActionInfo>
        {
            Acao(new DocumentOperation { Action = "Insert", Offset = 7, Length = 2, Text = "ZZ" }),
        });

        var sfdt = JsonConvert.SerializeObject(document);
        document.Dispose();

        // O texto NÃO muda...
        Assert.DoesNotContain("abcdefZZ", DocumentFactory.TextOfSfdt(sfdt));
        // ...a operação fica pendurada esperando o navegador...
        Assert.Contains("\"iOps\":[{", sfdt);
        // ...e é justamente por isso que gravar um SFDT assim é proibido.
        Assert.Throws<UnmaterializedSnapshotException>(() => DocumentReplay.EnsureMaterialized(sfdt));
    }

    [Fact(DisplayName = "SFDT compactado com operações penduradas também é recusado")]
    public void SnapshotCompactadoComOperacoesPenduradasEhRecusado()
    {
        using var stream = new MemoryStream(DocumentFactory.NewDocx(TextoInicial), writable: false);
        var document = WordDocument.Load(stream, FormatType.Docx);
        // Compactado é o padrão do serviço — é a forma que o ImportFile devolve.
        Assert.True(document.OptimizeSfdt);

        document.UpdateActions(new List<ActionInfo>
        {
            Acao(new DocumentOperation { Action = "Insert", Offset = 7, Length = 2, Text = "ZZ" }),
        });

        var sfdt = JsonConvert.SerializeObject(document);
        document.Dispose();

        // A busca por texto puro não alcança o que está dentro do zip.
        Assert.DoesNotContain("\"iOps\":[{", sfdt);
        Assert.Throws<UnmaterializedSnapshotException>(() => DocumentReplay.EnsureMaterialized(sfdt));
    }

    [Fact(DisplayName = "Um documento de verdade passa pela verificação")]
    public void DocumentoMaterializadoPassa()
    {
        DocumentReplay.EnsureMaterialized(DocumentFactory.BrowserSnapshotOf("Ola"));
        DocumentReplay.EnsureMaterialized(DocumentFactory.BrowserSnapshot(DocumentFactory.NewRichDocx("Peticao")));
        DocumentReplay.EnsureMaterialized(
            Reaplicar(
                DocumentFactory.NewDocx(TextoInicial),
                Acao(new DocumentOperation { Action = "Insert", Offset = 7, Length = 1, Text = "Z" })));
    }

    [Fact(DisplayName = "Enter, Backspace, apagar faixa e formatação são reaplicados de verdade")]
    public void OperacoesDeTecladoEFormatacaoSaoReaplicadas()
    {
        var docx = DocumentFactory.NewDocx(TextoInicial);

        // Enter no meio: "abc" | "def".
        var enter = DocumentFactory.TextOfSfdt(Reaplicar(
            docx, Acao(new DocumentOperation { Action = "Insert", Offset = 4, Length = 1, Text = "\n" })));
        Assert.Contains("abc", enter);
        Assert.Contains("def", enter);
        Assert.DoesNotContain("abcdef", enter);

        // Backspace no fim.
        Assert.Contains("abcde", DocumentFactory.TextOfSfdt(Reaplicar(
            docx, Acao(new DocumentOperation { Action = "Delete", Offset = 6, Length = 1, Text = "f" }))));

        // Apagar uma faixa selecionada.
        Assert.Contains("abf", DocumentFactory.TextOfSfdt(Reaplicar(
            docx, Acao(new DocumentOperation { Action = "Delete", Offset = 3, Length = 3, Text = "cde" }))));

        // Negrito nas três primeiras letras.
        var negrito = Abrir(Reaplicar(docx, Acao(new DocumentOperation
        {
            Action = "Format",
            Offset = 1,
            Length = 3,
            Type = "CharacterFormat",
            Format = "{\"bold\":true}",
        })));
        var primeiraLinha = (Syncfusion.DocIO.DLS.WParagraph)negrito.LastSection.Body.ChildEntities[0];
        Assert.True(((Syncfusion.DocIO.DLS.WTextRange)primeiraLinha.ChildEntities[0]).CharacterFormat.Bold);
        negrito.Dispose();

        // Centralizar o parágrafo.
        var centralizado = Abrir(Reaplicar(docx, Acao(new DocumentOperation
        {
            Action = "Format",
            Offset = 1,
            Length = 3,
            Type = "ParagraphFormat",
            Format = "{\"textAlignment\":\"Center\"}",
        })));
        var paragrafo = (Syncfusion.DocIO.DLS.WParagraph)centralizado.LastSection.Body.ChildEntities[0];
        Assert.Equal(
            Syncfusion.DocIO.DLS.HorizontalAlignment.Center,
            paragrafo.ParagraphFormat.HorizontalAlignment);
        centralizado.Dispose();
    }

    [Fact(DisplayName = "Alteração de seção é reaplicada de verdade")]
    public void AlteracaoDeSecaoEhReaplicada()
    {
        var secao = Abrir(Reaplicar(
            DocumentFactory.NewDocx(TextoInicial),
            Acao(new DocumentOperation
            {
                Action = "Format",
                Offset = 1,
                Length = 1,
                Type = "SectionFormat",
                Format = "{\"pageWidth\":600.0,\"pageHeight\":800.0}",
            })));

        Assert.Equal(600f, secao.LastSection.PageSetup.PageSize.Width, 1f);
        Assert.Equal(800f, secao.LastSection.PageSetup.PageSize.Height, 1f);
        secao.Dispose();
    }

    [Fact(DisplayName = "Tabela, lista, imagem e revisão sobrevivem à reaplicação")]
    public void DocumentoRicoSobreviveAReaplicacao()
    {
        const string marca = "PeticaoInicial";
        var sfdt = Reaplicar(
            DocumentFactory.NewRichDocx(marca),
            Acao(new DocumentOperation
            {
                Action = "Insert",
                Offset = marca.Length + 1,
                Length = 2,
                Text = "ZZ",
            }));

        var documento = Abrir(sfdt);
        var texto = documento.GetText() ?? string.Empty;

        Assert.Contains(marca + "ZZ", texto);
        Assert.Contains("R$ 1.000,00", texto);
        Assert.Contains("Primeiro pedido", texto);
        Assert.Contains("Trecho com revisão", texto);
        Assert.Contains(
            documento.LastSection.Body.ChildEntities.OfType<Syncfusion.DocIO.DLS.WTable>(),
            _ => true);
        Assert.True(
            documento.LastSection.Body.ChildEntities
                .OfType<Syncfusion.DocIO.DLS.WParagraph>()
                .SelectMany(p => p.ChildEntities.OfType<Syncfusion.DocIO.DLS.WPicture>())
                .Any(),
            "A imagem não sobreviveu à reaplicação.");
        documento.Dispose();
    }

    [Fact(DisplayName = "Uma operação inválida na fila FALHA a gravação em vez de ser descartada")]
    public void OperacaoInvalidaFalhaAGravacao()
    {
        using var stream = new MemoryStream(DocumentFactory.NewDocx(TextoInicial), writable: false);
        var document = WordDocument.Load(stream, FormatType.Docx);

        Assert.Throws<InvalidDataException>(
            () => DocumentReplay.ReplayToSfdt(document, new List<ActionInfo> { null }));

        document.Dispose();
    }
}
