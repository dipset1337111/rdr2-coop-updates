using CoopStory.Protocol;

namespace CoopStory.Sidecar.Session;

// Experimental reliable channel, not installed in the live peer router yet.
// The transport supplies an authenticated actor and captures the generation
// before reading each frame. Reconnect cannot revive an old read callback.
internal sealed class CorpseControlConnection
{
    private readonly object _gate = new();
    private readonly CorpseControlHandler _handler;
    private readonly NetEntityId _actor;
    private ulong _generation;
    private bool _open;
    private bool _faulted;
    private uint _replySequence;

    public CorpseControlConnection(CorpseControlHandler handler, NetEntityId authenticatedActor)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!handler.MatchesActor(authenticatedActor)) throw new ArgumentException("Unknown corpse connection actor.");
        _handler = handler; _actor = authenticatedActor;
    }
    public ulong Open()
    {
        lock (_gate)
        {
            if (_open || _faulted || _generation == ulong.MaxValue)
                throw new InvalidOperationException("Corpse channel is open, faulted or exhausted.");
            _open = true;
            return ++_generation;
        }
    }
    public ProtocolEnvelope? Receive(ulong capturedGeneration, ProtocolEnvelope envelope, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        lock (_gate)
        {
            if (!_open || _faulted || capturedGeneration != _generation) return null;
            if (envelope.Version != ProtocolConstants.Version || envelope.Type != MessageType.CorpseCommand)
                throw new ProtocolException("Corpse channel requires a current-version command.");
            var reply = _handler.Handle(_actor, envelope.Payload.Span, nowMs);
            // Handler/journal commit finishes before any reply is available to send.
            return new ProtocolEnvelope(MessageType.CorpseReply, ++_replySequence,
                checked((ulong)nowMs), reply);
        }
    }
    public void Close(ulong capturedGeneration, long nowMs)
    {
        lock (_gate)
        {
            if (!_open || capturedGeneration != _generation) return;
            _open = false; // Invalidate queued callbacks even if persistence fails.
            try { _handler.DisconnectActor(_actor, nowMs); }
            catch { _faulted = true; throw; }
        }
    }
}
