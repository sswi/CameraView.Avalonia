namespace CameraView.Utils;

/// <summary>
/// Ensures an async operation runs only once at a time.
/// If a new invocation arrives while the previous one is still running, the new one is skipped.
/// Ported from CameraScanner.Maui.
/// </summary>
public class SyncHelper
{
    private readonly Lock syncRoot = new();
    private Task? currentTask;

    public bool IsRunning
    {
        get
        {
            lock (syncRoot)
                return currentTask != null;
        }
    }

    public async Task RunOnceAsync(Func<Task> task)
    {
        if (!TryBeginExecution(out var execution))
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
            EndExecution(execution.Task);
        }
    }

    private bool TryBeginExecution(out TaskCompletionSource<bool> execution)
    {
        lock (syncRoot)
        {
            if (currentTask != null)
            {
                execution = null!;
                return false;
            }

            execution = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            currentTask = execution.Task;
            return true;
        }
    }

    private void EndExecution(Task<bool> executionTask)
    {
        lock (syncRoot)
        {
            if (ReferenceEquals(currentTask, executionTask))
                currentTask = null;
        }
    }
}
