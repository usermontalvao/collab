using System.Collections.Concurrent;

namespace Jurius.CollabEditing.Services
{
    /// <summary>
    /// Registro do que está acontecendo no serviço, para a página inicial mostrar
    /// atividade de verdade (e não só "no ar").
    ///
    /// A página é pública, então NADA aqui pode identificar cliente ou processo: o
    /// nome da sala já chega como hash do caminho e o nome de quem edita é
    /// reduzido a iniciais antes de sair. Contadores e horários não identificam
    /// ninguém.
    ///
    /// É memória do processo (últimos 60 eventos). Reiniciou o container, zera —
    /// é painel de acompanhamento, não auditoria.
    /// </summary>
    public interface IActivityTracker
    {
        void Record(string type, string roomName, string userName = null, string detail = null);
        void CountOperation();
        void CountSave(int operations);
        void CountImport();
        ActivitySnapshot Snapshot();
    }

    public class ActivityEvent
    {
        public string At { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Room { get; set; } = string.Empty;
        public string Who { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
    }

    public class ActivitySnapshot
    {
        public long Operations { get; set; }
        public long Saves { get; set; }
        public long SavedOperations { get; set; }
        public long Imports { get; set; }
        public string LastOperationAt { get; set; }
        public string LastSaveAt { get; set; }
        public List<ActivityEvent> Events { get; set; } = new();
    }

    public class ActivityTracker : IActivityTracker
    {
        private const int MaxEvents = 60;

        private readonly ConcurrentQueue<ActivityEvent> _events = new();
        private long _operations;
        private long _saves;
        private long _savedOperations;
        private long _imports;
        private string _lastOperationAt;
        private string _lastSaveAt;

        /// <summary>Nome completo vira iniciais: "Lisliandra Montalvão" -> "L.M.".</summary>
        public static string MaskUser(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return "—";
            var parts = userName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var initials = parts
                .Where(part => char.IsLetter(part[0]))
                .Take(2)
                .Select(part => char.ToUpperInvariant(part[0]) + ".");
            var masked = string.Concat(initials);
            return string.IsNullOrEmpty(masked) ? "—" : masked;
        }

        /// <summary>A sala já é um hash; ainda assim só os primeiros caracteres saem.</summary>
        public static string MaskRoom(string roomName)
        {
            if (string.IsNullOrWhiteSpace(roomName)) return "—";
            return roomName.Length <= 12 ? roomName : $"{roomName[..12]}…";
        }

        public void Record(string type, string roomName, string userName = null, string detail = null)
        {
            _events.Enqueue(new ActivityEvent
            {
                At = DateTime.UtcNow.ToString("o"),
                Type = type,
                Room = MaskRoom(roomName),
                Who = MaskUser(userName),
                Detail = detail ?? string.Empty,
            });

            while (_events.Count > MaxEvents && _events.TryDequeue(out _))
            {
                // mantém só a janela recente
            }
        }

        public void CountOperation()
        {
            Interlocked.Increment(ref _operations);
            _lastOperationAt = DateTime.UtcNow.ToString("o");
        }

        public void CountSave(int operations)
        {
            Interlocked.Increment(ref _saves);
            Interlocked.Add(ref _savedOperations, operations);
            _lastSaveAt = DateTime.UtcNow.ToString("o");
        }

        public void CountImport() => Interlocked.Increment(ref _imports);

        public ActivitySnapshot Snapshot()
        {
            return new ActivitySnapshot
            {
                Operations = Interlocked.Read(ref _operations),
                Saves = Interlocked.Read(ref _saves),
                SavedOperations = Interlocked.Read(ref _savedOperations),
                Imports = Interlocked.Read(ref _imports),
                LastOperationAt = _lastOperationAt,
                LastSaveAt = _lastSaveAt,
                Events = _events.Reverse().ToList(),
            };
        }
    }
}
