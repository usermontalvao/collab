using Jurius.CollabEditing.Hubs;
using Jurius.CollabEditing.Model;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using StackExchange.Redis;
using Syncfusion.EJ2.DocumentEditor;
using System.Security.Cryptography;

namespace Jurius.CollabEditing.Services
{
    /// <summary>
    /// CAMINHO ÚNICO de gravação do documento de uma sala no Nextcloud.
    ///
    /// Antes existiam dois caminhos parecidos (o corte por excesso de operações e a
    /// saída da última pessoa), cada um carregando a sua própria lista de operações
    /// pela fila. Isso abria duas falhas: a lista podia ser gravada duas vezes
    /// (texto duplicado) e as chaves do Redis eram APAGADAS inteiras no fim — o que
    /// jogava fora tudo que tivesse sido digitado durante a gravação.
    ///
    /// Aqui a gravação é sempre a mesma sequência, com trava por sala:
    ///   1. tira uma foto do que está pendente (versão + as duas filas);
    ///   2. baixa o .docx, aplica exatamente essas operações e envia de volta;
    ///   3. remove das filas EXATAMENTE a quantidade que foi aplicada.
    /// O passo 3 por quantidade (e não apagando a chave) é o que preserva o que
    /// chegou durante o passo 2.
    /// </summary>
    public interface IRoomPersistence
    {
        Task<SaveOutcome> PersistAsync(
            string roomName,
            string sourcePath,
            bool finalize,
            DocumentSaveSnapshot snapshot = null,
            CancellationToken cancellationToken = default);
    }

    public class RoomPersistence : IRoomPersistence
    {
        /// <summary>Uma gravação por sala de cada vez. Duas em paralelo gravariam a mesma operação duas vezes.</summary>
        private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan LockPoll = TimeSpan.FromMilliseconds(120);

        /// <summary>
        /// Prazo dado às operações de uma sala cuja gravação final falhou. Longo de
        /// propósito: é tempo de sobra para alguém reabrir o documento e para o
        /// serviço ser consertado, sem deixar a chave para sempre.
        /// </summary>
        private static readonly TimeSpan UnsavedRoomTtl = TimeSpan.FromDays(30);

        private readonly IConnectionMultiplexer _redis;
        private readonly INextcloudStorage _storage;
        private readonly IActivityTracker _activity;
        private readonly IHubContext<DocumentEditorHub> _hub;
        private readonly ILogger<RoomPersistence> _logger;

        public RoomPersistence(
            IConnectionMultiplexer redis,
            INextcloudStorage storage,
            IActivityTracker activity,
            IHubContext<DocumentEditorHub> hub,
            ILogger<RoomPersistence> logger)
        {
            _redis = redis;
            _storage = storage;
            _activity = activity;
            _hub = hub;
            _logger = logger;
        }

        public async Task<SaveOutcome> PersistAsync(
            string roomName,
            string sourcePath,
            bool finalize,
            DocumentSaveSnapshot documentSnapshot = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(roomName)) throw new ArgumentException("Sala não informada.", nameof(roomName));

            IDatabase database = _redis.GetDatabase();
            string lockKey = roomName + CollaborativeEditingHelper.SaveLockSuffix;
            string lockToken = Guid.NewGuid().ToString("N");

            if (!await AcquireLockAsync(database, lockKey, lockToken, cancellationToken))
            {
                // Só chega aqui se outra gravação passou de LockWait. Recusar é melhor
                // do que gravar em paralelo: o botão Salvar mostra o erro em vez de
                // um "Salvo" que não aconteceu.
                throw new TimeoutException("Outra gravação desta sala ainda está em andamento.");
            }

            try
            {
                return await PersistInternalAsync(
                    database, roomName, sourcePath, finalize, documentSnapshot, cancellationToken);
            }
            finally
            {
                await database.ScriptEvaluateAsync(
                    CollaborativeEditingHelper.ReleaseLock,
                    new RedisKey[] { lockKey },
                    new RedisValue[] { lockToken });
            }
        }

        private async Task<SaveOutcome> PersistInternalAsync(
            IDatabase database,
            string roomName,
            string sourcePath,
            bool finalize,
            DocumentSaveSnapshot documentSnapshot,
            CancellationToken cancellationToken)
        {
            string registeredSource =
                await database.StringGetAsync(roomName + CollaborativeEditingHelper.SourceInfoSuffix);
            if (!string.IsNullOrWhiteSpace(registeredSource))
            {
                if (!string.IsNullOrWhiteSpace(sourcePath) &&
                    !string.Equals(sourcePath, registeredSource, StringComparison.Ordinal))
                {
                    throw new RoomSourceConflictException();
                }
                sourcePath = registeredSource;
            }

            // 1) Foto do pendente. Mesma leitura atômica do ImportFile.
            var snapshot = (RedisResult[])await database.ScriptEvaluateAsync(
                CollaborativeEditingHelper.ImportSnapshot,
                new RedisKey[]
                {
                    roomName + CollaborativeEditingHelper.VersionInfoSuffix,
                    roomName,
                    roomName + CollaborativeEditingHelper.ActionsToRemoveSuffix,
                },
                Array.Empty<RedisValue>());

            int version = int.Parse(snapshot[0].ToString());
            var processing = ((RedisResult[])snapshot[1])
                .Select(value => JsonConvert.DeserializeObject<ActionInfo>(value.ToString()))
                .ToList();
            var pending = ((RedisResult[])snapshot[2])
                .Select(value => JsonConvert.DeserializeObject<ActionInfo>(value.ToString()))
                .ToList();

            var actions = new List<ActionInfo>(processing.Count + pending.Count);
            actions.AddRange(processing);
            actions.AddRange(pending);

            if (documentSnapshot != null && documentSnapshot.Version != version)
            {
                throw new RoomVersionConflictException(documentSnapshot.Version, version);
            }

            var outcome = new SaveOutcome
            {
                Operations = actions.Count,
                Version = version,
                SavedAt = DateTime.UtcNow.ToString("o"),
            };

            if (actions.Count == 0)
            {
                _logger.LogInformation(
                    "Sala {Room}: nada pendente para gravar (versão {Version}, final {Final}).",
                    roomName, version, finalize);

                outcome.StillPending = await TrimPersistedOperationsAsync(
                    database, roomName, processing.Count, pending.Count, finalize);
                await NotifyRoomSavedAsync(roomName, outcome, finalize, cancellationToken);
                return outcome;
            }

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                _logger.LogError("Sala {Room} sem caminho de origem no Nextcloud; nada foi gravado.", roomName);
                throw new InvalidOperationException("A sala não tem arquivo de origem registrado.");
            }

            // 2) O botão Salvar manda o documento COMPLETO já montado no navegador.
            // É a única cópia que existe com todas as operações da sala aplicadas e
            // resolvidas (o navegador é o dono da resolução de conflitos).
            //
            // Gravações de background não têm navegador: elas reaplicam a fila no
            // arquivo, e agora pela API que realmente altera o documento — ver
            // DocumentReplay.
            try
            {
                if (documentSnapshot != null)
                {
                    await UploadSnapshotAsync(
                        roomName, sourcePath, documentSnapshot.Sfdt, cancellationToken);
                }
                else
                {
                    await ApplyAndUploadAsync(roomName, sourcePath, actions, cancellationToken);
                }
            }
            catch (Exception)
            {
                // A gravação final é a última chance de escrever o arquivo. Falhando
                // aqui, as operações NÃO podem ser apagadas junto com a sala: elas
                // ficam no Redis (com prazo, para a memória não crescer sem fim) e
                // quem reabrir o documento recebe o texto por elas.
                if (finalize) await PreserveRoomAsync(database, roomName);
                throw;
            }

            // Chegar aqui significa que o arquivo foi enviado E RELIDO do Nextcloud
            // com o mesmo conteúdo — ver UploadAndVerifyAsync. É o único lugar em
            // que a tela ganha o direito de dizer "Salvo".
            outcome.Uploaded = true;
            outcome.Verified = true;

            // 3) Remove das filas exatamente o que foi aplicado. Inclusive na
            // gravação final: uma operação ou reabertura que tenha ocorrido durante
            // o upload impede atomicamente que a sala seja apagada.
            outcome.StillPending = await TrimPersistedOperationsAsync(
                database, roomName, processing.Count, pending.Count, finalize);

            _activity.CountSave(actions.Count);
            _activity.Record("gravou no Nextcloud", roomName, null,
                $"{actions.Count} operações · {(finalize ? "final" : "sob demanda")}");
            _logger.LogInformation(
                "Sala {Room}: {Count} operações gravadas (versão {Version}, final {Final}, restaram {Pending}).",
                roomName, actions.Count, version, finalize, outcome.StillPending);

            await NotifyRoomSavedAsync(roomName, outcome, finalize, cancellationToken);
            return outcome;
        }

        /// <summary>
        /// Avisa a sala INTEIRA que o documento acabou de ser gravado no Nextcloud.
        /// É o que faz o "Alterações pendentes" do outro navegador virar "Salvo" na
        /// hora em que UMA pessoa salva: sem este aviso, quem não clicou continuava
        /// vendo pendência de um conteúdo que já estava gravado. Na gravação final
        /// (última pessoa saindo) a sala está vazia e não há quem avisar.
        /// </summary>
        private async Task NotifyRoomSavedAsync(
            string roomName,
            SaveOutcome outcome,
            bool finalize,
            CancellationToken cancellationToken)
        {
            if (finalize) return;

            try
            {
                await _hub.Clients.Group(roomName).SendAsync("dataReceived", "saved", new
                {
                    version = outcome.Version,
                    operations = outcome.Operations,
                    stillPending = outcome.StillPending,
                    uploaded = outcome.Uploaded,
                    verified = outcome.Verified,
                    savedAt = outcome.SavedAt,
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                // O documento JÁ está gravado; falhar o aviso não pode falhar a
                // gravação. O outro navegador atualiza no próximo salvamento.
                _logger.LogWarning(ex, "Sala {Room}: não foi possível avisar a sala da gravação.", roomName);
            }
        }

        private async Task ApplyAndUploadAsync(
            string roomName,
            string sourcePath,
            List<ActionInfo> actions,
            CancellationToken cancellationToken)
        {
            using Stream source = await _storage.DownloadAsync(sourcePath, cancellationToken);
            WordDocument document = WordDocument.Load(source, FormatType.Docx);

            string sfdt;
            try
            {
                // NÃO use WordDocument.UpdateActions aqui: ela só PENDURA as
                // operações no SFDT para o navegador aplicar, e o DocIO as ignora
                // ao gravar o .docx — o arquivo subia com o texto antigo. Ver
                // DocumentReplay, que aplica pela API que altera o documento.
                sfdt = DocumentReplay.ReplayToSfdt(document, actions);
            }
            finally
            {
                document.Dispose();
            }

            await UploadSfdtAsync(sourcePath, sfdt, cancellationToken);
            _logger.LogInformation(
                "Sala {Room}: {Count} operações reaplicadas e documento enviado ao Nextcloud.",
                roomName, actions.Count);
        }

        private async Task UploadSnapshotAsync(
            string roomName,
            string sourcePath,
            string sfdt,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sfdt))
            {
                throw new InvalidDataException("O snapshot do editor está vazio.");
            }

            await UploadSfdtAsync(sourcePath, sfdt, cancellationToken);
            _logger.LogInformation(
                "Sala {Room}: documento do editor enviado e relido do Nextcloud.",
                roomName);
        }

        /// <summary>
        /// Converte o SFDT em .docx e grava, mas só depois de conferir que ele NÃO
        /// tem operações penduradas — um SFDT nessas condições vira um .docx com o
        /// texto anterior, e foi assim que "Salvo" passou a mentir.
        /// </summary>
        private async Task UploadSfdtAsync(
            string sourcePath,
            string sfdt,
            CancellationToken cancellationToken)
        {
            DocumentReplay.EnsureMaterialized(sfdt);

            using var stream = new MemoryStream();
            Syncfusion.DocIO.DLS.WordDocument docx = WordDocument.Save(sfdt);
            try
            {
                docx.Save(stream, Syncfusion.DocIO.FormatType.Docx);
            }
            finally
            {
                docx.Dispose();
            }

            await UploadAndVerifyAsync(sourcePath, stream, cancellationToken);
        }

        /// <summary>
        /// Um PUT 2xx só confirma que o WebDAV aceitou a requisição. Para a tela
        /// poder dizer "Salvo", relê o mesmo caminho e compara os bytes do DOCX.
        /// </summary>
        private async Task UploadAndVerifyAsync(
            string sourcePath,
            MemoryStream content,
            CancellationToken cancellationToken)
        {
            byte[] expected = content.ToArray();
            await _storage.UploadAsync(
                sourcePath, new MemoryStream(expected, writable: false), cancellationToken);

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                using Stream downloaded = await _storage.DownloadAsync(sourcePath, cancellationToken);
                using var buffer = new MemoryStream();
                await downloaded.CopyToAsync(buffer, cancellationToken);

                if (CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(expected), SHA256.HashData(buffer.ToArray())))
                {
                    return;
                }

                if (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken);
                }
            }

            throw new IOException(
                "O Nextcloud aceitou a gravação, mas a releitura não corresponde ao DOCX enviado.");
        }

        /// <summary>
        /// A gravação final falhou e a sala já está vazia: as operações continuam
        /// sendo a ÚNICA cópia do que foi digitado. Elas ficam onde estão, só com
        /// prazo de validade — apagar agora perderia o trabalho, e deixar para
        /// sempre encheria o Redis de salas mortas. Dentro do prazo, reabrir o
        /// documento reconstrói o texto pela fila (ImportFile).
        /// </summary>
        private async Task PreserveRoomAsync(IDatabase database, string roomName)
        {
            try
            {
                foreach (var suffix in new[]
                {
                    string.Empty,
                    CollaborativeEditingHelper.ActionsToRemoveSuffix,
                    CollaborativeEditingHelper.OperationOffsetSuffix,
                    CollaborativeEditingHelper.VersionInfoSuffix,
                    CollaborativeEditingHelper.SourceInfoSuffix,
                })
                {
                    await database.KeyExpireAsync(roomName + suffix, UnsavedRoomTtl);
                }

                _logger.LogWarning(
                    "Sala {Room}: gravação final falhou; as operações foram mantidas por {Days} dias.",
                    roomName, UnsavedRoomTtl.TotalDays);
            }
            catch (Exception ex)
            {
                // Não conseguir marcar o prazo é melhor do que apagar: as chaves
                // continuam lá, com as edições intactas.
                _logger.LogError(ex, "Sala {Room}: não foi possível marcar o prazo das operações.", roomName);
            }
        }

        private static async Task<long> TrimPersistedOperationsAsync(
            IDatabase database,
            string roomName,
            int processingCount,
            int pendingCount,
            bool finalize)
        {
            var trimmed = (RedisResult[])await database.ScriptEvaluateAsync(
                CollaborativeEditingHelper.TrimPersistedOperations,
                new RedisKey[]
                {
                    roomName,
                    roomName + CollaborativeEditingHelper.ActionsToRemoveSuffix,
                    roomName + CollaborativeEditingHelper.OperationOffsetSuffix,
                    roomName + CollaborativeEditingHelper.UserInfoSuffix,
                    roomName + CollaborativeEditingHelper.VersionInfoSuffix,
                    roomName + CollaborativeEditingHelper.SourceInfoSuffix,
                },
                new RedisValue[]
                {
                    processingCount.ToString(),
                    pendingCount.ToString(),
                    finalize ? "1" : "0",
                    ((long)UnsavedRoomTtl.TotalSeconds).ToString(),
                });

            return long.Parse(trimmed[0].ToString());
        }

        private static async Task<bool> AcquireLockAsync(
            IDatabase database,
            string lockKey,
            string lockToken,
            CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + LockWait;
            while (true)
            {
                if (await database.StringSetAsync(lockKey, lockToken, LockTtl, When.NotExists)) return true;
                if (DateTime.UtcNow >= deadline) return false;
                await Task.Delay(LockPoll, cancellationToken);
            }
        }
    }
}
