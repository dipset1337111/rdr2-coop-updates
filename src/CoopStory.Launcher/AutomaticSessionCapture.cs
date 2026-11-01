namespace CoopStory.Launcher;

public sealed record DiagnosticSessionWindow(Guid Id, DateTimeOffset StartedUtc,
    DateTimeOffset ThroughUtc, bool Final);

// Local-only flight recorder. One serialized export at a time; shutdown waits
// for the final atomic archive replacement. No uploads and no game mutations.
internal sealed class AutomaticSessionCapture : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly object _gate = new();
    private DateTimeOffset? _ended;
    private readonly Guid _id = Guid.NewGuid();
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    public AutomaticSessionCapture(Action<DiagnosticSessionWindow> export,
        Action<Exception> onError, TimeSpan? interval = null)
    {
        var period = interval ?? TimeSpan.FromMinutes(1);
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _worker = Task.Run(async () =>
        {
            void Save(bool final)
            {
                DateTimeOffset through;
                lock (_gate) through = _ended ?? DateTimeOffset.UtcNow;
                try { export(new(_id, _started, through, final)); }
                catch (Exception e) { try { onError(e); } catch { /* Reporting must not stop capture. */ } }
            }
            try
            {
                Save(false);
                using var timer = new PeriodicTimer(period);
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false)) Save(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally { Save(true); }
        });
    }
    public void Stop()
    {
        lock (_gate)
        {
            if (_ended.HasValue) return;
            _ended = DateTimeOffset.UtcNow;
            _stop.Cancel();
        }
    }
    public void Dispose()
    {
        Stop();
        _worker.GetAwaiter().GetResult();
        // CTS is intentionally retained for idempotent late process-exit callbacks.
    }
}
