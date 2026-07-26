using Syncfusion.EJ2.DocumentEditor;

namespace Jurius.CollabEditing.Model
{
    /// <summary>
    /// Chaves e scripts Lua do cache de operações. Copiado do exemplo oficial da
    /// Syncfusion (EJ2-Document-Editor-Collaborative-Editing) — é a parte que
    /// garante a ordem/versão das operações; não alterar sem necessidade.
    /// Único acréscimo nosso: <see cref="SourceInfoSuffix"/>, que guarda o caminho
    /// do arquivo no Nextcloud para o salvamento em background.
    /// </summary>
    public class CollaborativeEditingHelper
    {
        // Número máximo de operações mantidas no Redis. Ao atingir o limite, as
        // mais antigas são gravadas no documento de origem.
        internal static int SaveThreshold = 100;

        internal static string RevisionInfoSuffix = "_revision_info";

        internal static string VersionInfoSuffix = "_version_info";

        internal static string UserInfoSuffix = "_user_info";

        internal static string ConnectionIdRoomMappingKey = "ej_de_connection_id_room_mapping";

        internal static string ActionsToRemoveSuffix = "_actions_to_remove";

        /// <summary>Caminho do arquivo no Nextcloud que originou a sala.</summary>
        internal static string SourceInfoSuffix = "_source_path";

        internal static string InsertScript = @"
                -- Define keys for version, list, and revision
                local versionKey = KEYS[1]
                local listKey = KEYS[2]
                local revisionKey = KEYS[3]
                local updateKey = KEYS[4]

                -- Define arguments: item to insert, client's version, and threshold for cache
                local item = ARGV[1]
                local clientVersion = tonumber(ARGV[2])
                local threshold = tonumber(ARGV[3])

                -- Increment the version for each operation
                local version = redis.call('INCR', versionKey)

                -- Retrieve the current revision, or initialize it if it doesn't exist
                local revision = redis.call('GET', revisionKey)
                if not revision then
                    redis.call('SET', revisionKey, '0')
                    revision = 0
                else
                    revision = tonumber(revision)
                end

                -- Calculate the effective version by multiplying revision by threshold
                local effectiveVersion = revision * threshold
                -- Adjust clientVersion based on effectiveVersion
                clientVersion = clientVersion - effectiveVersion

                -- Add the new item to the list and get the new length
                local length = redis.call('RPUSH', listKey, item)
                -- Retrieve operations since the client's version
                local previousOps = redis.call('LRANGE', listKey, clientVersion, -1)

                -- Define a limit for cache based on threshold
                local cacheLimit = threshold * 2;
                local elementToRemove = nil
                -- If the length of the list reaches the cache limit, trim the list
                if length % cacheLimit == 0 then
                    elementToRemove = redis.call('LRANGE', listKey, 0, threshold - 1)
                    redis.call('LTRIM', listKey, threshold, -1)
                    -- Increment the revision after trimming
                    redis.call('INCR', revisionKey)
                    -- Add elements to remove to updateKey
                    for _, v in ipairs(elementToRemove) do
                        redis.call('RPUSH', updateKey, v)
                    end
                end

                -- Return the current version, operations since client's version, and elements removed
                local values = {version, previousOps, elementToRemove}
                return values";

        internal static string UpdateRecord = @"
             -- Define keys for list and revision
            local listKey = KEYS[1]
            local revisionKey = KEYS[2]

            -- Define arguments: item to insert, client's version, and threshold for cache
            local item = ARGV[1]
            local clientVersion = ARGV[2]
            local threshold = tonumber(ARGV[3])

            -- Retrieve the current revision from Redis, or initialize it if it doesn't exist
            local revision = redis.call('GET', revisionKey)
            if not revision then
                revision = 0
            else
                revision = tonumber(revision)
            end

            -- Calculate the effective version by multiplying revision by threshold
            local effectiveVersion = revision * threshold

            -- Adjust clientVersion based on effectiveVersion
            clientVersion = tonumber(clientVersion) - effectiveVersion

            -- Update the list at the position calculated by the adjusted clientVersion
            -- This effectively 'inserts' the item into the list at the position reflecting the client's view of the list
            redis.call('LSET', listKey, clientVersion, item)";

        internal static string EffectivePendingOperations = @"
             -- Define the keys for accessing the list and revision in Redis
             local listKey = KEYS[1]
             local revisionKey = KEYS[2]

             -- Convert the first argument to a number to represent the client's version
             local clientVersion =  tonumber(ARGV[1])

             -- Convert the second argument to a number for the threshold value
             local threshold = tonumber(ARGV[2])

             -- Retrieve the current revision number from Redis
             local revision = redis.call('GET', revisionKey)
             if not revision then
                revision = 0
             else
                revision = tonumber(revision)
             end
             -- Calculate the effective version by multiplying the revision number by the threshold
             local effectiveVersion = revision * threshold

             -- Adjust the client's version by subtracting the effective version
             clientVersion = clientVersion - effectiveVersion

             -- Return a range of list elements starting from the adjusted client version to the end of the list
             if clientVersion >= 0 then
                return redis.call('LRANGE', listKey, clientVersion, -1)
            else
                return {}
            end";

        internal static string PendingOperations = @"
            local listKey = KEYS[1]
            local processingKey = KEYS[2]
            local startIndex = tonumber(ARGV[1])
            local endIndex = tonumber(ARGV[2])

            -- Fetch the list of operations from the listKey
            local listValues = redis.call('LRANGE', listKey, startIndex, endIndex)

            -- Fetch the list of operations from the processingKey
            local processingValues = redis.call('LRANGE', processingKey, startIndex, endIndex)

            -- Return both lists as a combined result
            return {processingValues, listValues}";

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
    }

    public class SaveInfo
    {
        public string RoomName { get; set; }
        public List<ActionInfo> Action;
        public string UserId;
        public int Version;
        public bool PartialSave;
        /// <summary>Caminho do arquivo no Nextcloud (acréscimo nosso).</summary>
        public string SourcePath { get; set; }
    }

    /// <summary>Corpo do POST de ImportFile vindo do front.</summary>
    public class ImportFileInfo
    {
        public string fileName { get; set; }
        public string roomName { get; set; }
        /// <summary>Caminho do arquivo no Nextcloud, relativo à raiz do usuário.</summary>
        public string filePath { get; set; }
    }

    public class DocumentContent
    {
        public int version { get; set; }

        public string sfdt { get; set; }
    }
}
