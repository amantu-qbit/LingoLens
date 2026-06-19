namespace LingoLens.Pipeline;

/// <summary>
/// Marshals overlay-render calls onto the UI/compositor thread. The pipeline never blocks the render
/// lane on inference, but <see cref="LingoLens.Core.Overlay.IOverlayRenderer"/> implementations
/// (DirectComposition/Direct2D) are generally affinitized to the thread that created them, so the
/// final <c>Present</c> must hop onto that thread.
/// </summary>
/// <remarks>
/// The host (WPF App) can register a concrete dispatcher that wraps its <c>Dispatcher</c>. When none
/// is registered the pipeline falls back to <see cref="SynchronizationContextUiDispatcher"/> capturing
/// whichever <see cref="SynchronizationContext"/> is current when capture starts.
/// </remarks>
public interface IUiDispatcher
{
    /// <summary>True if the calling thread is the UI thread (Post can be elided).</summary>
    bool IsOnUiThread { get; }

    /// <summary>Queue <paramref name="action"/> to run on the UI thread; returns immediately.</summary>
    void Post(Action action);
}

/// <summary>
/// Default <see cref="IUiDispatcher"/> built on a captured <see cref="SynchronizationContext"/>.
/// If no context is available (pure console/test host) it runs actions inline, which is acceptable
/// because such hosts have no affinitized renderer.
/// </summary>
public sealed class SynchronizationContextUiDispatcher : IUiDispatcher
{
    private readonly SynchronizationContext? _context;
    private readonly int _uiThreadId;

    /// <summary>Capture the supplied context (or the current one) as the UI thread.</summary>
    public SynchronizationContextUiDispatcher(SynchronizationContext? context = null)
    {
        _context = context ?? SynchronizationContext.Current;
        _uiThreadId = Environment.CurrentManagedThreadId;
    }

    public bool IsOnUiThread =>
        _context is null || Environment.CurrentManagedThreadId == _uiThreadId;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_context is null)
        {
            action();
            return;
        }

        _context.Post(static state => ((Action)state!).Invoke(), action);
    }
}
