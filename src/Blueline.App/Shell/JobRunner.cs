using System.Windows;
using Blueline.Core;

namespace Blueline.App.Shell;

/// <summary>Reports a job's state to the status bar. Safe from any thread.</summary>
public sealed class JobContext(JobRunner runner, CancellationToken cancel)
{
    public CancellationToken Cancel { get; } = cancel;

    public void Status(string text) => runner.Publish(text, null);

    public void Progress(string text, int done, int total) =>
        runner.Publish(text, total > 0 ? (double)done / total : null);
}

/// <summary>
/// One long operation at a time, off the UI thread, with progress and cancel. Failures are logged and shown
/// where the user is looking, so a module's error path is a thrown exception with a clear message.
/// </summary>
public sealed class JobRunner(SessionLog log)
{
    private CancellationTokenSource? _cancel;

    public bool IsBusy => _cancel != null;

    /// <summary>Title or status, fraction (null = indeterminate); null title means idle.</summary>
    public event Action<string?, double?>? Updated;
    public event Action<string, Exception>? Failed;

    /// <summary>The shell's error banner hooks this; a failed job then needs no handling in its module.</summary>
    internal void Publish(string? text, double? fraction)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;
        dispatcher.BeginInvoke(() =>
        {
            if (_cancel != null || text == null)
                Updated?.Invoke(text, fraction);
        });
    }

    public void CancelCurrent() => _cancel?.Cancel();

    /// <summary>Runs <paramref name="work"/> on a worker, then <paramref name="done"/> on the UI thread with the result.</summary>
    public async Task<bool> RunAsync<T>(string title, Func<JobContext, Task<T>> work, Action<T> done)
    {
        if (_cancel != null)
        {
            Failed?.Invoke(title, new InvalidOperationException("Another job is still running. Wait for it, or cancel it."));
            return false;
        }

        _cancel = new CancellationTokenSource();
        log.Info($"Started: {title}");
        Updated?.Invoke(title, null);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        T result;
        try
        {
            var context = new JobContext(this, _cancel.Token);
            result = await Task.Run(() => work(context), _cancel.Token);
            log.Info($"Finished: {title} ({clock.Elapsed.TotalSeconds:F1}s)");
        }
        catch (OperationCanceledException)
        {
            log.Info($"Cancelled: {title}");
            return false;
        }
        catch (Exception e)
        {
            log.Error($"Failed: {title}", e);
            Failed?.Invoke(title, e);
            return false;
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            Updated?.Invoke(null, null);
        }

        // After the job is cleared, so the completion can start another job and its notice is not overwritten.
        try
        {
            done(result);
            return true;
        }
        catch (Exception e)
        {
            log.Error($"Failed: {title}", e);
            Failed?.Invoke(title, e);
            return false;
        }
    }

    public Task<bool> RunAsync<T>(string title, Func<JobContext, T> work, Action<T> done) =>
        RunAsync(title, context => Task.FromResult(work(context)), done);
}
