using Newtonsoft.Json;
using Syncfusion.EJ2.DocumentEditor;
using System.IO.Compression;
using System.Text;

namespace Jurius.CollabEditing.Services
{
    /// <summary>
    /// A ÚNICA forma correta de aplicar operações da fila num documento no servidor.
    ///
    /// O ERRO QUE ISTO CORRIGE (e que gravou "Salvo" sem salvar nada):
    ///
    ///   `WordDocument.UpdateActions(actions)` NÃO aplica as operações no
    ///   documento. Ela apenas PENDURA as operações no SFDT, numa chave `iOps`,
    ///   para que o NAVEGADOR as aplique depois do `open()` (é o que o
    ///   `DocumentHelper.onDocumentChanged` faz no ej2-documenteditor). As
    ///   `sections` do documento saem BYTE A BYTE IGUAIS às da entrada.
    ///
    ///   Por isso `ImportFile` está certo em usá-la: quem abre o documento é o
    ///   navegador, e é ele que aplica as `iOps`.
    ///
    ///   E por isso GRAVAR com ela é uma perda de dados silenciosa: o
    ///   `Syncfusion.DocIO` ignora `iOps` ao converter para .docx. O arquivo
    ///   subia com o texto ANTIGO, o Nextcloud respondia 2xx, a tela dizia
    ///   "Salvo" e a fila do Redis era apagada — as edições desapareciam.
    ///
    ///   Quem aplica de verdade é `CollaborativeEditingHandler.UpdateAction`,
    ///   uma operação por chamada. É o que se usa aqui.
    /// </summary>
    public static class DocumentReplay
    {
        /// <summary>
        /// Assinatura em base64 do cabeçalho de ZIP ("PK\x03\x04") no começo de um
        /// SFDT compactado — é a forma que o `OptimizeSfdt` (ligado por padrão)
        /// produz. Serve para reconhecer o formato sem desserializar nada.
        /// </summary>
        private const string PackedSfdtMarker = "\"sfdt\":\"UEsDBB";

        /// <summary>
        /// Marca de uma operação ainda PENDURADA no SFDT, nunca aplicada ao
        /// conteúdo. Procurada como texto puro de propósito: um documento de 90 MB
        /// não pode ser reserializado inteiro só para provar que está em ordem.
        /// </summary>
        private const string PendingOperationsMarker = "\"iOps\":[{";

        /// <summary>
        /// Prova, UMA vez por processo, que a API de reaplicação realmente altera o
        /// documento. Foi exatamente esta garantia que faltou quando
        /// `UpdateAction` foi trocada por `UpdateActions`: o serviço continuou
        /// compilando, respondendo 200 e gravando o texto antigo. Aqui, se a API
        /// voltar a não aplicar nada, a gravação FALHA — e a fila do Redis fica de
        /// pé, em vez de ser apagada por uma gravação de mentira.
        /// </summary>
        private static readonly Lazy<string> SelfCheck = new(RunSelfCheck);

        /// <summary>
        /// Aplica <paramref name="actions"/> em <paramref name="document"/> e
        /// devolve o SFDT do RESULTADO, pronto para virar .docx.
        /// </summary>
        public static string ReplayToSfdt(WordDocument document, List<ActionInfo> actions)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (actions == null) throw new ArgumentNullException(nameof(actions));

            // Não descarte entradas inválidas em silêncio: perder uma operação é
            // pior do que recusar a gravação e manter a fila inteira no Redis.
            if (actions.Any(action => action == null))
            {
                throw new InvalidDataException("A fila da sala contém uma operação inválida.");
            }

            if (actions.Count > 0)
            {
                string broken = SelfCheck.Value;
                if (broken != null)
                {
                    throw new OperationReplayException(
                        "A reaplicação de operações do Syncfusion não está aplicando as edições " +
                        $"neste servidor ({broken}). Nada foi gravado e a fila da sala foi preservada.");
                }
            }

            var handler = new CollaborativeEditingHandler(document);
            for (var index = 0; index < actions.Count; index++)
            {
                try
                {
                    handler.UpdateAction(actions[index]);
                }
                catch (Exception ex)
                {
                    // Sem o conteúdo da operação: ela carrega texto jurídico.
                    throw new OperationReplayException(
                        $"Falha ao reaplicar a operação {index + 1} de {actions.Count} " +
                        $"({ex.GetType().Name}). Nada foi gravado e a fila da sala foi preservada.",
                        ex);
                }
            }

            return JsonConvert.SerializeObject(handler.Document);
        }

        /// <summary>
        /// Recusa um SFDT cujas operações continuam PENDURADAS em `iOps` em vez de
        /// aplicadas ao conteúdo. Converter um desses para .docx grava o texto
        /// antigo — é a assinatura exata da falha que trouxe este código aqui.
        /// </summary>
        public static void EnsureMaterialized(string sfdt)
        {
            if (string.IsNullOrEmpty(sfdt)) return;

            if (sfdt.Contains(PendingOperationsMarker, StringComparison.Ordinal))
            {
                throw new UnmaterializedSnapshotException();
            }

            // Forma compactada: as operações penduradas ficam DENTRO do zip, então
            // a busca por texto acima não as alcança.
            if (!sfdt.Contains(PackedSfdtMarker, StringComparison.Ordinal)) return;

            string unpacked = TryUnpack(sfdt);
            if (unpacked != null && unpacked.Contains(PendingOperationsMarker, StringComparison.Ordinal))
            {
                throw new UnmaterializedSnapshotException();
            }
        }

        private static string TryUnpack(string sfdt)
        {
            try
            {
                using var reader = new JsonTextReader(new StringReader(sfdt));
                string packed = null;
                while (reader.Read())
                {
                    if (reader.TokenType != JsonToken.PropertyName ||
                        !string.Equals((string)reader.Value, "sfdt", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    packed = reader.ReadAsString();
                    break;
                }
                if (string.IsNullOrEmpty(packed)) return null;

                using var zip = new ZipArchive(
                    new MemoryStream(Convert.FromBase64String(packed)), ZipArchiveMode.Read);
                var entry = zip.Entries.FirstOrDefault();
                if (entry == null) return null;

                using var content = new StreamReader(entry.Open(), Encoding.UTF8);
                return content.ReadToEnd();
            }
            catch (Exception)
            {
                // A verificação é uma rede de segurança: não sabendo ler o
                // formato, não há o que afirmar — e a gravação segue pelas outras
                // garantias (versão da sala e releitura do arquivo gravado).
                return null;
            }
        }

        /// <summary>
        /// Insere um texto conhecido num documento mínimo e confere que ele
        /// aparece no SFDT resultante. Devolve `null` quando está tudo certo.
        /// </summary>
        private static string RunSelfCheck()
        {
            const string seed = "ab";
            const string probe = "Zq";

            try
            {
                using var seedStream = new MemoryStream();
                var minimal = new Syncfusion.DocIO.DLS.WordDocument();
                minimal.EnsureMinimal();
                minimal.LastParagraph.AppendText(seed);
                minimal.Save(seedStream, Syncfusion.DocIO.FormatType.Docx);
                minimal.Dispose();
                seedStream.Position = 0;

                WordDocument document = WordDocument.Load(seedStream, FormatType.Docx);
                document.OptimizeSfdt = false;

                var handler = new CollaborativeEditingHandler(document);
                handler.UpdateAction(new ActionInfo
                {
                    RoomName = "autoteste",
                    ConnectionId = "autoteste",
                    CurrentUser = "autoteste",
                    Version = 1,
                    Operations = new List<DocumentOperation>
                    {
                        new DocumentOperation
                        {
                            // Deslocamento do Syncfusion é 1-based: depois de "ab", 3.
                            Action = "Insert",
                            Offset = seed.Length + 1,
                            Length = probe.Length,
                            Text = probe,
                        },
                    },
                });

                string sfdt = JsonConvert.SerializeObject(handler.Document);
                document.Dispose();

                return sfdt.Contains(probe, StringComparison.Ordinal)
                    ? null
                    : "a operação de teste não apareceu no documento";
            }
            catch (Exception ex)
            {
                return ex.GetType().Name;
            }
        }
    }

    /// <summary>
    /// Não foi possível reaplicar a fila de operações no documento. NADA é gravado
    /// e NADA sai do Redis: as operações continuam disponíveis para quem reabrir o
    /// documento e para a próxima tentativa.
    /// </summary>
    public class OperationReplayException : Exception
    {
        public OperationReplayException(string message) : base(message) { }

        public OperationReplayException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// O SFDT recebido tem operações PENDURADAS (`iOps`) em vez de aplicadas ao
    /// conteúdo. Gravá-lo escreveria o texto antigo no Nextcloud.
    /// </summary>
    public class UnmaterializedSnapshotException : Exception
    {
        public UnmaterializedSnapshotException()
            : base("O documento enviado tem edições pendentes de aplicação; gravá-lo " +
                   "escreveria o texto anterior no Nextcloud.")
        {
        }
    }
}
