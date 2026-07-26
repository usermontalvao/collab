using System.Threading.Channels;
using Jurius.CollabEditing.Model;

namespace Jurius.CollabEditing.Services
{
    /// <summary>Fila de gravações no documento de origem. Igual ao exemplo oficial.</summary>
    public interface IBackgroundTaskQueue
    {
        ValueTask QueueBackgroundWorkItemAsync(SaveInfo workItem);

        ValueTask<SaveInfo> DequeueAsync(CancellationToken cancellationToken);
    }

    public class BackgroundTaskQueue : IBackgroundTaskQueue
    {
        private readonly Channel<SaveInfo> _queue;

        public BackgroundTaskQueue(int capacity)
        {
            var options = new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait
            };
            _queue = Channel.CreateBounded<SaveInfo>(options);
        }

        public async ValueTask QueueBackgroundWorkItemAsync(SaveInfo workItem)
        {
            if (workItem == null)
            {
                throw new ArgumentNullException(nameof(workItem));
            }

            await _queue.Writer.WriteAsync(workItem);
        }

        public async ValueTask<SaveInfo> DequeueAsync(CancellationToken cancellationToken)
        {
            return await _queue.Reader.ReadAsync(cancellationToken);
        }
    }
}
