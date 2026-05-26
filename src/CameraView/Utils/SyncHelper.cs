namespace CameraView.Utils;

/// <summary>
/// Ensures an async operation runs only once at a time.
/// If a new invocation arrives while the previous one is still running, the new one is skipped.
/// Ported from CameraScanner.Maui.
/// </summary>
public class SyncHelper
{
    private readonly object syncRoot = new();
    private Task? currentTask;

    public bool IsRunning
    {
        get
        {
            lock (this.syncRoot)
                return this.currentTask != null;
        }
    }

    public async Task RunOnceAsync(Func<Task> task)
    {
        if (!this.TryBeginExecution(out var execution))
            return;

        try
        {
            await task().ConfigureAwait(false);
            execution.SetResult(true);
        }
        catch (Exception ex)
        {
            execution.SetException(ex);
            throw;
        }
        finally
        {
            this.EndExecution(execution.Task);
        }
    }

    private bool TryBeginExecution(out TaskCompletionSource<bool> execution)
    {
        lock (this.syncRoot)
        {
            if (this.currentTask != null)
            {
                execution = null!;
                return false;
            }

            execution = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.currentTask = execution.Task;
            return true;
        }
    }

    private void EndExecution(Task<bool> executionTask)
    {
        lock (this.syncRoot)
        {
            if (ReferenceEquals(this.currentTask, executionTask))
                this.currentTask = null;
        }
    }
}
