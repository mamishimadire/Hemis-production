using System.Threading.Channels;

namespace HemisAudit.Services
{
    public class BackgroundTaskQueue : IBackgroundTaskQueue
    {
        private readonly Channel<Func<IServiceProvider, CancellationToken, Task>> _channel;

        public BackgroundTaskQueue(int capacity = 200)
        {
            _channel = Channel.CreateBounded<Func<IServiceProvider, CancellationToken, Task>>(
                new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait });
        }

        public void QueueBackgroundWorkItem(Func<IServiceProvider, CancellationToken, Task> workItem)
        {
            if (!_channel.Writer.TryWrite(workItem))
                throw new InvalidOperationException("Upload queue is full — try again shortly.");
        }

        public async Task<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken)
        {
            return await _channel.Reader.ReadAsync(cancellationToken);
        }
    }

    // Runs queued work items one at a time in a background loop for the app's lifetime, each
    // in its own DI scope (so a job can resolve scoped services like IEngagementDatasetService
    // without borrowing the original HTTP request's scope, which is disposed as soon as the
    // request that enqueued the job returns).
    public class QueuedHostedService : BackgroundService
    {
        private readonly IBackgroundTaskQueue _queue;
        private readonly IServiceProvider _services;
        private readonly ILogger<QueuedHostedService> _logger;

        public QueuedHostedService(IBackgroundTaskQueue queue, IServiceProvider services, ILogger<QueuedHostedService> logger)
        {
            _queue = queue; _services = services; _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Func<IServiceProvider, CancellationToken, Task> workItem;
                try
                {
                    workItem = await _queue.DequeueAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    await using var scope = _services.CreateAsyncScope();
                    await workItem(scope.ServiceProvider, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background upload job failed unexpectedly.");
                }
            }
        }
    }
}
