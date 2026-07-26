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
