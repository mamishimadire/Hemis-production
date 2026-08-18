namespace HemisAudit.Services
{
    // A minimal in-process work queue for jobs that must outlive the HTTP request that
    // started them (e.g. importing a multi-million-row dataset upload). Not durable across
    // an app restart by design — an interrupted job's uploaded file stays on disk and the
    // analyst can retry from the "Modify columns" step.
    public interface IBackgroundTaskQueue
    {
        void QueueBackgroundWorkItem(Func<IServiceProvider, CancellationToken, Task> workItem);
        Task<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken);
    }
}
