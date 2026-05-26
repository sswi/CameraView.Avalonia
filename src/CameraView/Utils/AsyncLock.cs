namespace CameraView.Utils;

/// <summary>
/// Async mutual exclusion lock for protecting non-thread-safe resources (e.g. AVCaptureSession).
/// Ported from CameraScanner.Maui.
/// </summary>
internal class AsyncLock : IDisposable
{
    private readonly SemaphoreSlim semaphore = new(1, 1);
    private readonly Task<Releaser> releaser;
    private bool disposed;

    internal AsyncLock()
    {
        releaser = Task.FromResult(new Releaser(this));
    }

    internal Task<Releaser> LockAsync()
    {
        var wait = semaphore.WaitAsync();
        return wait.IsCompleted
            ? releaser
            : wait.ContinueWith(
                (_, state) => new Releaser((AsyncLock)state!),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (!disposed)
        {
            semaphore.Dispose();
            disposed = true;
        }
    }

    internal readonly struct Releaser : IDisposable
    {
        private readonly AsyncLock toRelease;

        internal Releaser(AsyncLock toRelease)
        {
            this.toRelease = toRelease;
        }

        public void Dispose()
        {
          toRelease.semaphore.Release();
        }
    }
}
