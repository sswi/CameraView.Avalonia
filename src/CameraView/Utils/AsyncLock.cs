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
        this.releaser = Task.FromResult(new Releaser(this));
    }

    internal Task<Releaser> LockAsync()
    {
        var wait = this.semaphore.WaitAsync();
        return wait.IsCompleted
            ? this.releaser
            : wait.ContinueWith(
                (_, state) => new Releaser((AsyncLock)state!),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (!this.disposed)
        {
            this.semaphore.Dispose();
            this.disposed = true;
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
            this.toRelease.semaphore.Release();
        }
    }
}
