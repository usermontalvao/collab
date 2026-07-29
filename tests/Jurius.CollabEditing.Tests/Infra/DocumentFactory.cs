using Newtonsoft.Json;
using Syncfusion.EJ2.DocumentEditor;

namespace Jurius.CollabEditing.Tests.Infra;

/// <summary>Gera e lê .docx de verdade — os testes conferem o TEXTO, não o JSON.</summary>
public static class DocumentFactory
{
    public static byte[] NewDocx(string text = "")
    {
        using var stream = new MemoryStream();
        var document = new Syncfusion.DocIO.DLS.WordDocument();
        document.EnsureMinimal();
        if (!string.IsNullOrEmpty(text)) document.LastParagraph.AppendText(text);
        document.Save(stream, Syncfusion.DocIO.FormatType.Docx);
        document.Dispose();
        return stream.ToArray();
    }

    /// <summary>
    /// .docx com tudo o que costuma quebrar reconstrução de documento: tabela,
    /// lista numerada, imagem e uma alteração controlada (revisão).
    /// </summary>
    public static byte[] NewRichDocx(string marker)
    {
        using var stream = new MemoryStream();
        var document = new Syncfusion.DocIO.DLS.WordDocument();
        document.EnsureMinimal();
        document.LastParagraph.AppendText(marker);

        var section = document.LastSection;

        var table = section.AddTable();
        table.ResetCells(2, 2);
        table[0, 0].AddParagraph().AppendText("Pedido");
        table[0, 1].AddParagraph().AppendText("Valor");
        table[1, 0].AddParagraph().AppendText("Custas");
        table[1, 1].AddParagraph().AppendText("R$ 1.000,00");

        foreach (var item in new[] { "Primeiro pedido", "Segundo pedido" })
        {
            var bullet = section.AddParagraph();
            bullet.AppendText(item);
            bullet.ListFormat.ApplyDefNumberedStyle();
        }

        var imageParagraph = section.AddParagraph();
        imageParagraph.AppendPicture(OnePixelPng());

        document.TrackChanges = true;
        section.AddParagraph().AppendText("Trecho com revisão");
        document.TrackChanges = false;

        document.Save(stream, Syncfusion.DocIO.FormatType.Docx);
        document.Dispose();
        return stream.ToArray();
    }

    private static byte[] OnePixelPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8AAAwAB/wD/AAEA" +
        "AQAAAAAAAA==");

    /// <summary>
    /// O SFDT que o NAVEGADOR posta ao salvar: o documento inteiro já montado, sem
    /// nenhuma operação pendente. Construído a partir de um .docx feito do zero, de
    /// propósito — nada que o serviço produz entra nesta conta, então quando o teste
    /// encontra o texto no arquivo é porque o que subiu foi o que foi enviado.
    /// </summary>
    public static string BrowserSnapshot(byte[] docx)
    {
        using var stream = new MemoryStream(docx, writable: false);
        var document = WordDocument.Load(stream, FormatType.Docx);
        // O navegador serializa sem compactar; manter igual facilita inspecionar.
        document.OptimizeSfdt = false;
        var sfdt = JsonConvert.SerializeObject(document);
        document.Dispose();
        return sfdt;
    }

    /// <summary>Atalho: o navegador está exibindo este texto.</summary>
    public static string BrowserSnapshotOf(string text) => BrowserSnapshot(NewDocx(text));

    /// <summary>Texto de um SFDT, do mesmo jeito que o serviço o converteria.</summary>
    public static string TextOfSfdt(string sfdt)
    {
        var document = WordDocument.Save(sfdt);
        var text = document.GetText() ?? string.Empty;
        document.Dispose();
        return text;
    }

    public static string TextOf(byte[] docx)
    {
        using var stream = new MemoryStream(docx, writable: false);
        var document = new Syncfusion.DocIO.DLS.WordDocument(stream, Syncfusion.DocIO.FormatType.Docx);
        var text = document.GetText() ?? string.Empty;
        document.Dispose();
        return text;
    }

    /// <summary>
    /// Uma inserção de texto no formato que o Document Editor manda: uma operação
    /// por caractere, com deslocamento crescente. O deslocamento do Syncfusion
    /// começa em 1 (0 é antes do início do documento e estoura).
    /// </summary>
    public static ActionInfo Insert(string roomName, string connectionId, int version, string text, int offset = 1)
    {
        var operations = new List<DocumentOperation>();
        for (var i = 0; i < text.Length; i++)
        {
            operations.Add(new DocumentOperation
            {
                Action = "Insert",
                Offset = offset + i,
                Length = 1,
                Text = text[i].ToString(),
            });
        }

        return new ActionInfo
        {
            RoomName = roomName,
            ConnectionId = connectionId,
            CurrentUser = "Teste",
            Version = version,
            Operations = operations,
        };
    }
}
