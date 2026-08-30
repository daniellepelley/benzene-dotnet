using Benzene.Abstractions.Hosting;

namespace Benzene.SelfHost;

public class CompositeBenzeneWorker : IBenzeneWorker
{
    private readonly IReadOnlyList<IBenzeneWorker> _workers;
    public CompositeBenzeneWorker(IEnumerable<IBenzeneWorker> workers)
    {
        // Materialize once. Callers pass a deferred query (BenzeneWorkerBuilder.Create hands us
        // `_apps.Select(factory => factory(resolver))`, and every factory news up a fresh worker),
        // so re-enumerating in StopAsync would build a SECOND, never-started worker set and stop
        // those instead of the running ones - silently skipping every worker's drain/close/commit.
        _workers = workers.ToList();
    }
    
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Start every worker (in parallel), then await them together. If any one fails, roll back the
        // workers that DID start - otherwise a partial composite start leaks their running consume
        // loops / open connections with nothing tracking them (StopAsync is only called on a clean
        // start). SafeStart captures a *synchronous* throw as a faulted task, so one worker throwing
        // before its first await still lets the others start (and get rolled back) rather than
        // aborting the materialization mid-way.
        //
        // IMPORTANT: Task.WhenAll alone is not enough to detect a fault. It only completes once
        // EVERY constituent task has completed - but some real workers (SqsConsumer-shaped: see
        // src/Benzene.HostedService/BenzeneHostedServiceStartup.cs's own doc comment) run their full
        // lifetime inline on the task returned from StartAsync, which never completes until told to
        // stop. If such a worker is composed alongside one that fails - at startup, or mid-lifetime,
        // any time later - Task.WhenAll would wait forever for the long-running worker, permanently
        // hiding the fault (#291). So we race a first-fault signal, fed by a fault continuation on
        // EVERY worker's task, against Task.WhenAll: whichever settles first decides the outcome.
        // With zero faults this changes nothing - firstFault's task never completes, so
        // Task.WhenAll's own completion (success or, if every task happens to settle including some
        // faulted ones, its aggregate fault) still drives the result, unchanged from before.
        var started = _workers
            .Select(x => (worker: x, task: SafeStart(x, cancellationToken)))
            .ToArray();

        var whenAll = Task.WhenAll(started.Select(x => x.task));

        // A continuation attached here also has the side effect of "observing" each task's
        // exception (accessing a faulted antecedent's Exception is how the runtime decides whether
        // to invoke an OnlyOnFaulted continuation) - the same protection this class relies on
        // elsewhere to avoid an unobserved-task-exception surfacing later via the finalizer.
        var firstFault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var (_, task) in started)
        {
            task.ContinueWith(
                _ => firstFault.TrySetResult(),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        var winner = await Task.WhenAny(whenAll, firstFault.Task).ConfigureAwait(false);

        if (winner != firstFault.Task)
        {
            // No fault raced ahead of full completion: either every worker started cleanly, or
            // every worker's task had already settled (including a fault) by the time we checked -
            // either way this reproduces the original fully-synchronous behavior, rollback included.
            try
            {
                await whenAll.ConfigureAwait(false);
                return;
            }
            catch
            {
                await RollbackAsync(started, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        // firstFault won the race: at least one sibling has faulted while another may still be
        // starting/running indefinitely - the shape Task.WhenAll alone can never surface. Roll back
        // and surface the fault instead of waiting on `whenAll`, which may never complete.
        await RollbackAsync(started, cancellationToken).ConfigureAwait(false);
        await started.First(x => x.task.IsFaulted).task.ConfigureAwait(false);
    }

    private static async Task RollbackAsync(
        (IBenzeneWorker worker, Task task)[] started, CancellationToken cancellationToken)
    {
        foreach (var (worker, task) in started)
        {
            // Stop every worker whose task hasn't ALREADY reached a terminal faulted/cancelled
            // state - that covers a worker still starting/running (the long-running shape above)
            // as well as one that already started successfully. A narrower IsCompletedSuccessfully
            // check would skip stopping a still-running sibling entirely.
            if (!task.IsFaulted && !task.IsCanceled)
            {
                try
                {
                    await worker.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort rollback: don't let a stop fault mask the original start failure.
                }
            }
        }
    }

    private static Task SafeStart(IBenzeneWorker worker, CancellationToken cancellationToken)
    {
        try
        {
            return worker.StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var tasks = _workers
            .Select(x => x.StopAsync(cancellationToken))
            .ToArray();
        await Task.WhenAll(tasks);
    }
}