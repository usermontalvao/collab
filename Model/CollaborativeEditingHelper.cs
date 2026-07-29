using Syncfusion.EJ2.DocumentEditor;

namespace Jurius.CollabEditing.Model
{
    /// <summary>
    /// Chaves e scripts Lua do cache de operações. Base: o exemplo oficial da
    /// Syncfusion (EJ2-Document-Editor-Collaborative-Editing) — é a parte que
    /// garante a ordem/versão das operações.
    ///
    /// DUAS mudanças nossas em relação ao exemplo:
    ///
    /// 1. <see cref="SourceInfoSuffix"/> guarda o caminho do arquivo no Nextcloud.
    ///
    /// 2. O deslocamento entre "versão da operação" e "índice na lista do Redis"
    ///    passou a ser um CONTADOR DE OPERAÇÕES JÁ GRAVADAS
    ///    (<see cref="OperationOffsetSuffix"/>) em vez de `revisão × limite`.
    ///    O exemplo só corta a lista de <see cref="SaveThreshold"/> em
    ///    <see cref="SaveThreshold"/> operações, então `revisão × limite` bate.
    ///    Nós também gravamos sob demanda (o botão Salvar), e aí o corte tem um
    ///    tamanho qualquer: com a conta antiga, toda operação seguinte iria para o
    ///    índice errado da lista — texto embaralhado. Contando as operações
    ///    removidas, a conta continua exata para qualquer tamanho de corte.
    /// </summary>
    public class CollaborativeEditingHelper
    {
        // Número máximo de operações mantidas no Redis. Ao atingir o limite, as
        // mais antigas são gravadas no documento de origem.
        internal static int SaveThreshold = 100;

        /// <summary>
        /// Quantas operações já saíram da lista (porque foram gravadas no arquivo).
        /// É o deslocamento entre a versão de uma operação e o índice dela na lista.
        /// Substitui o `_revision_info` do exemplo — ver o comentário da classe.
        /// </summary>
        internal static string OperationOffsetSuffix = "_op_offset";

        internal static string VersionInfoSuffix = "_version_info";

        internal static string UserInfoSuffix = "_user_info";

        internal static string ConnectionIdRoomMappingKey = "ej_de_connection_id_room_mapping";

        /// <summary>
        /// Operações já retiradas da lista principal e à espera de serem gravadas
        /// no arquivo. Continuam sendo enviadas a quem abre o documento, senão o
        /// texto delas sumiria da tela até a gravação terminar.
        /// </summary>
        internal static string ActionsToRemoveSuffix = "_actions_to_remove";

        /// <summary>Caminho do arquivo no Nextcloud que originou a sala.</summary>
        internal static string SourceInfoSuffix = "_source_path";

        /// <summary>Trava de gravação da sala (uma gravação por sala de cada vez).</summary>
        internal static string SaveLockSuffix = "_save_lock";

        /// <summary>
        /// Registra o caminho uma vez e recusa reutilizar a mesma sala para outro
        /// arquivo. A comparação e o SET são atômicos e o caminho nunca vai ao log.
        /// </summary>
        internal static string RegisterSource = @"
            local current = redis.call('GET', KEYS[1])
            if current and current ~= ARGV[1] then
                return 0
            end
            redis.call('SET', KEYS[1], ARGV[1])
            return 1";

        internal static string InsertScript = @"
                -- Chaves: versão, lista de operações, deslocamento, fila de gravação
                local versionKey = KEYS[1]
                local listKey = KEYS[2]
                local offsetKey = KEYS[3]
                local updateKey = KEYS[4]

                -- Argumentos: operação, versão do cliente e limite do cache
                local item = ARGV[1]
                local clientVersion = tonumber(ARGV[2])
                local threshold = tonumber(ARGV[3])

                -- Uma versão nova por operação
                local version = redis.call('INCR', versionKey)

                -- Quantas operações já saíram da lista (deslocamento)
                local offset = redis.call('GET', offsetKey)
                if not offset then
                    redis.call('SET', offsetKey, '0')
                    offset = 0
                else
                    offset = tonumber(offset)
                end

                -- Índice do cliente dentro da lista que ainda existe
                clientVersion = clientVersion - offset
                if clientVersion < 0 then
                    clientVersion = 0
                end

                -- Guarda a operação e devolve o que o cliente ainda não viu
                local length = redis.call('RPUSH', listKey, item)
                local previousOps = redis.call('LRANGE', listKey, clientVersion, -1)

                -- Passou do dobro do limite: as mais antigas vão para a fila de gravação
                local cacheLimit = threshold * 2;
                local elementToRemove = nil
                if length % cacheLimit == 0 then
                    elementToRemove = redis.call('LRANGE', listKey, 0, threshold - 1)
                    redis.call('LTRIM', listKey, threshold, -1)
                    redis.call('INCRBY', offsetKey, threshold)
                    for _, v in ipairs(elementToRemove) do
                        redis.call('RPUSH', updateKey, v)
                    end
                end

                local values = {version, previousOps, elementToRemove}
                return values";

        internal static string UpdateRecord = @"
            local listKey = KEYS[1]
            local offsetKey = KEYS[2]

            local item = ARGV[1]
            local clientVersion = tonumber(ARGV[2])

            local offset = redis.call('GET', offsetKey)
            if not offset then
                offset = 0
            else
                offset = tonumber(offset)
            end

            local index = clientVersion - offset

            -- Índice negativo = a operação JÁ foi gravada no arquivo e saiu da
            -- lista (uma gravação entrou entre o RPUSH e este LSET). Gravar aqui
            -- escreveria por cima de outra operação — o LSET com índice negativo
            -- conta a partir do FIM da lista. Melhor não fazer nada.
            if index < 0 then
                return 0
            end

            if index >= redis.call('LLEN', listKey) then
                return 0
            end

            redis.call('LSET', listKey, index, item)
            return 1";

        internal static string EffectivePendingOperations = @"
             local listKey = KEYS[1]
             local offsetKey = KEYS[2]

             local clientVersion = tonumber(ARGV[1])

             local offset = redis.call('GET', offsetKey)
             if not offset then
                offset = 0
             else
                offset = tonumber(offset)
             end

             clientVersion = clientVersion - offset

             if clientVersion >= 0 then
                return redis.call('LRANGE', listKey, clientVersion, -1)
            else
                return {}
            end";

        /// <summary>
        /// ACRÉSCIMO NOSSO. Lê, na MESMA chamada, a versão corrente da sala e todas
        /// as operações pendentes. Ler as duas coisas em comandos separados abre uma
        /// janela em que uma operação entra no meio: o cliente receberia o documento
        /// com N operações aplicadas mas uma versão de N-1 (ou vice-versa) e passaria
        /// a aplicar operação repetida. É exatamente o que corrompe o texto.
        /// Retorna { version, processingValues, listValues }.
        /// </summary>
        internal static string ImportSnapshot = @"
            local versionKey = KEYS[1]
            local listKey = KEYS[2]
            local processingKey = KEYS[3]

            local version = redis.call('GET', versionKey)
            if not version then
                version = 0
            else
                version = tonumber(version)
            end

            local processingValues = redis.call('LRANGE', processingKey, 0, -1)
            local listValues = redis.call('LRANGE', listKey, 0, -1)

            return {version, processingValues, listValues}";

        /// <summary>
        /// ACRÉSCIMO NOSSO. Remove da frente das duas filas EXATAMENTE a quantidade
        /// de operações que acabou de ser gravada no arquivo, e soma essa quantidade
        /// ao deslocamento.
        ///
        /// Por que por quantidade e não apagando a chave: enquanto o documento é
        /// baixado, montado e enviado ao Nextcloud (segundos), quem está digitando
        /// continua enfileirando operações. Apagar a chave jogaria fora justamente
        /// essas — o texto digitado durante o "Salvando…" sumiria.
        /// KEYS adicionais: usuários da sala, versão e caminho de origem.
        /// ARGV[3]: 1 para a gravação final, 0 para uma gravação intermediária.
        /// ARGV[4]: prazo, em segundos, dos metadados de uma sala vazia.
        ///
        /// Na gravação final, as chaves de metadados só são removidas se, NESTA
        /// MESMA execução atômica, não houver operação restante nem usuário na
        /// sala. Mesmo nesse caso, versão/caminho/deslocamento ficam por prazo
        /// limitado: uma operação HTTP que já estava em voo quando o último
        /// WebSocket fechou ainda consegue entrar sem virar uma fila órfã.
        /// Retorna { total ainda pendente nas duas filas, deslocamento novo,
        /// sala efetivamente encerrada }.
        /// </summary>
        internal static string TrimPersistedOperations = @"
            local listKey = KEYS[1]
            local processingKey = KEYS[2]
            local offsetKey = KEYS[3]
            local usersKey = KEYS[4]
            local versionKey = KEYS[5]
            local sourceKey = KEYS[6]

            local processingCount = tonumber(ARGV[1])
            local listCount = tonumber(ARGV[2])
            local finalize = tonumber(ARGV[3])
            local roomTtl = tonumber(ARGV[4])

            if processingCount > 0 then
                redis.call('LTRIM', processingKey, processingCount, -1)
            end

            if listCount > 0 then
                redis.call('LTRIM', listKey, listCount, -1)
                redis.call('INCRBY', offsetKey, listCount)
            end

            local offset = redis.call('GET', offsetKey)
            if not offset then
                offset = 0
            else
                offset = tonumber(offset)
            end

            local remaining =
                redis.call('LLEN', processingKey) + redis.call('LLEN', listKey)
            local cleared = 0

            if finalize == 1 and remaining == 0 and redis.call('HLEN', usersKey) == 0 then
                redis.call('EXPIRE', offsetKey, roomTtl)
                redis.call('EXPIRE', versionKey, roomTtl)
                redis.call('EXPIRE', sourceKey, roomTtl)
                cleared = 1
            end

            return {remaining, offset, cleared}";

        /// <summary>Solta a trava só se ela ainda for NOSSA (comparação com o token).</summary>
        internal static string ReleaseLock = @"
            if redis.call('GET', KEYS[1]) == ARGV[1] then
                return redis.call('DEL', KEYS[1])
            end
            return 0";
    }

    /// <summary>Pedido de gravação do documento de uma sala no Nextcloud.</summary>
    public class SaveInfo
    {
        public string RoomName { get; set; }

        /// <summary>Caminho do arquivo no Nextcloud (acréscimo nosso).</summary>
        public string SourcePath { get; set; }

        /// <summary>
        /// `false` = gravação intermediária (a sala continua viva).
        /// `true` = a última pessoa saiu: grava e limpa as chaves da sala.
        /// </summary>
        public bool Finalize { get; set; }
    }

    /// <summary>Resultado de uma gravação — o que o botão Salvar espera para dizer "Salvo".</summary>
    public class SaveOutcome
    {
        /// <summary>Quantas operações foram aplicadas ao .docx nesta gravação.</summary>
        public int Operations { get; set; }

        /// <summary>Versão da sala no momento em que a gravação foi tirada.</summary>
        public int Version { get; set; }

        /// <summary>`true` quando o arquivo foi realmente enviado ao Nextcloud.</summary>
        public bool Uploaded { get; set; }

        /// <summary>
        /// `true` quando o arquivo foi RELIDO do Nextcloud depois da gravação e os
        /// bytes conferiram. Um PUT 2xx não prova nada — é este campo, e só ele,
        /// que autoriza a tela a dizer "Salvo".
        /// </summary>
        public bool Verified { get; set; }

        /// <summary>Quantas operações continuaram pendentes (chegaram durante a gravação).</summary>
        public long StillPending { get; set; }

        public string SavedAt { get; set; }
    }

    /// <summary>Corpo do POST de ImportFile vindo do front.</summary>
    public class ImportFileInfo
    {
        public string fileName { get; set; }
        public string roomName { get; set; }
        /// <summary>Caminho do arquivo no Nextcloud, relativo à raiz do usuário.</summary>
        public string filePath { get; set; }
    }

    /// <summary>Corpo do POST de SaveToSource (o botão Salvar do editor).</summary>
    public class SaveToSourceInfo
    {
        public string roomName { get; set; }
        /// <summary>
        /// Opcional: o serviço já guarda o caminho da sala no ImportFile. Só é usado
        /// quando a chave da sala expirou.
        /// </summary>
        public string filePath { get; set; }

        /// <summary>
        /// Snapshot SFDT completo que está visível no editor no instante do
        /// salvamento. O botão Salvar usa este conteúdo em vez de tentar reconstruir
        /// o documento repetindo operações no motor server-side da Syncfusion.
        /// </summary>
        public string sfdt { get; set; }

        /// <summary>
        /// Versão da sala já incorporada em <see cref="sfdt"/>. O servidor só grava
        /// o snapshot quando ela coincide com a versão atômica do Redis.
        /// </summary>
        public int? version { get; set; }
    }

    public class DocumentSaveSnapshot
    {
        public string Sfdt { get; set; }
        public int Version { get; set; }
    }

    public class RoomVersionConflictException : Exception
    {
        public RoomVersionConflictException(int requestedVersion, int currentVersion)
            : base($"O editor está na versão {requestedVersion}, mas a sala já está na versão {currentVersion}.")
        {
            RequestedVersion = requestedVersion;
            CurrentVersion = currentVersion;
        }

        public int RequestedVersion { get; }
        public int CurrentVersion { get; }
    }

    public class RoomSourceConflictException : Exception
    {
        public RoomSourceConflictException()
            : base("A sala já está vinculada a outro arquivo.")
        {
        }
    }

    /// <summary>
    /// Quem entra na sala. ACRÉSCIMO NOSSO: o `ActionInfo` da Syncfusion só tem o
    /// nome; aqui vai também o id do usuário no CRM. Quem chama `JoinGroup` é o
    /// nosso próprio código, não o Document Editor — por isso dá para estender.
    ///
    /// NÃO existe campo de foto aqui, de propósito. A foto de perfil do CRM pode
    /// ser um `data:` embutido de megabytes; mandá-la na entrada da sala estoura o
    /// limite de mensagem do SignalR e o servidor DERRUBA a conexão — a co-edição
    /// morre antes de começar. Cada cliente resolve a foto pelo `UserId`,
    /// direto do Supabase (ver `userAvatars.ts` no CRM).
    /// </summary>
    public class RoomMemberInfo
    {
        public string RoomName { get; set; }
        public string ConnectionId { get; set; }
        public string CurrentUser { get; set; }
        public string UserId { get; set; }
        /// <summary>Está digitando neste instante (não é persistido).</summary>
        public bool Typing { get; set; }
    }

    public class DocumentContent
    {
        public int version { get; set; }

        public string sfdt { get; set; }
    }
}
