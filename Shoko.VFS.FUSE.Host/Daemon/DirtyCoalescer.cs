namespace Shoko.VFS.FUSE.Host.Daemon;

/// <summary>
/// Debounces rapid async triggers into a single action run. A burst of
/// <c>Trigger()</c> calls within one <c>window</c> coalesces into one
/// invocation of <c>action</c>. ActionResult is run on a background task.
/// </summary>
public sealed class DirtyCoalescer : IDisposable
{
    private readonly Func<Task> _action;
    private readonly TimeSpan _window;
    private readonly object _gate = new();
    private int _pending;
    private int _running;
    private int _disposed;

    /// <param name="action">The coalesced work (e.g. invalidate, full reconcile).</param>
    /// <param name="window">Debounce window: rapid triggers within this span share one run.</param>
    public DirtyCoalescer(Func<Task> action, TimeSpan window)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
        _window = window;
    }

    /// <summary>Number of triggers not yet consumed by a run.</summary>
    public int Pending { get { lock (_gate) return _pending; } }

    /// <summary>Queues a dirty signal. Safe to call concurrently from any thread.</summary>
    public void Trigger()
    {
        lock (_gate)
        {
            if (_disposed != 0)
                return;
            _pending++;
            if (_running == 0)
            {
                _running = 1;
                _ = Task.Run(PumpAsync);
            }
        }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            // Debounce: wait out the window so rapid triggers coalesce, then
            // consume everything that arrived.
            await Task.Delay(_window);

            bool proceed;
            lock (_gate)
            {
                if (_pending == 0 || _disposed != 0)
                {
                    _running = 0;
                    return;
                }
                _pending = 0;
                proceed = true;
            }

            if (!proceed)
                return;
            try
            {
                await _action().ConfigureAwait(false);
            }
            catch
            {
                // Errors are surfaced by the caller's logging (the action is
                // responsible for its own robust error handling).
            }

            lock (_gate)
            {
                if (_pending == 0 || _disposed != 0)
                {
                    _running = 0;
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = 1;
    }
}
