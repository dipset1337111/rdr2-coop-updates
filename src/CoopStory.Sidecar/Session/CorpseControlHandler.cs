using CoopStory.Protocol;

namespace CoopStory.Sidecar.Session;

// Host-side adapter for a future authenticated reliable transport. The caller
// supplies the identity from its connection, never from the payload itself.
// This class is not yet wired into SidecarRuntime or the native bridge.
internal sealed class CorpseControlHandler
{
    private readonly object _gate = new();
    private readonly Guid _session;
    private readonly NetEntityId _host;
    private readonly NetEntityId _guest;
    private readonly AuthoritativeCorpseRegistry _registry;
    private readonly Func<NetEntityId, CorpseActorObservation?> _actor;
    private readonly Func<NetEntityId, CorpseObservation?> _target;

    public CorpseControlHandler(Guid session, NetEntityId host, NetEntityId guest,
        AuthoritativeCorpseRegistry registry,
        Func<NetEntityId, CorpseActorObservation?> actor,
        Func<NetEntityId, CorpseObservation?> target)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(target);
        if (!registry.MatchesAuthority(session, host, guest))
            throw new ArgumentException("Corpse authority does not match the handler session.");
        _session = session; _host = host; _guest = guest;
        _registry = registry; _actor = actor; _target = target;
    }

    public byte[] Handle(NetEntityId authenticatedActor, ReadOnlySpan<byte> payload, long nowMs)
    {
        var command = CorpseControlCodec.DecodeCommand(payload);
        lock (_gate)
        {
            if (command.SessionId != _session || command.Actor != authenticatedActor ||
                (authenticatedActor != _host && authenticatedActor != _guest) ||
                command.Target == _host || command.Target == _guest)
                return Encode(command, new(CorpseDecision.Invalid, null));
            if (command.Kind == CorpseCommandKind.Reconcile)
                return Encode(command, _registry.Reconcile(command.Actor, command.Target,
                    command.RequestId, command.Action, command.Token, nowMs));
            if (command.Kind == CorpseCommandKind.Reserve)
            {
                var actor = _actor(command.Actor);
                var target = _target(command.Target);
                if (!actor.HasValue || !target.HasValue || actor.Value.Actor != command.Actor ||
                    target.Value.Target != command.Target)
                    return Encode(command, new(CorpseDecision.Unavailable, null));
                return Encode(command, _registry.Reserve(actor.Value, target.Value,
                    command.RequestId, command.Action, nowMs));
            }
            var state = Find(command);
            if (state is null) return Encode(command, new(CorpseDecision.WrongOwner, null));
            if (command.Kind == CorpseCommandKind.Start && state.Phase == CorpseUsePhase.Reserved)
            {
                // Reservation and actual start can be seconds apart. Revalidate
                // host observations before the bridge may issue an item.
                var actor = _actor(command.Actor);
                var target = _target(command.Target);
                if (!actor.HasValue || !target.HasValue || actor.Value.Actor != command.Actor ||
                    target.Value.Target != command.Target || target.Value.ModelHash != state.ModelHash ||
                    !actor.Value.Alive || !target.Value.Dead ||
                    (command.Action == CorpseAction.LootBody ? !target.Value.CanLootBody : !target.Value.CanSkinAnimal) ||
                    !Fresh(actor.Value.ObservedAtMs, nowMs) || !Fresh(target.Value.ObservedAtMs, nowMs))
                    return Encode(command, new(CorpseDecision.Unavailable, null));
                var distance = System.Numerics.Vector3.DistanceSquared(actor.Value.Position, target.Value.Position);
                if (!float.IsFinite(distance) || distance > 9f)
                    return Encode(command, new(CorpseDecision.TooFar, null));
            }
            var result = command.Kind switch
            {
                CorpseCommandKind.Start => _registry.Start(command.Actor, command.Target, command.Token, nowMs),
                CorpseCommandKind.Cancel => _registry.Cancel(command.Actor, command.Target, command.Token, nowMs),
                // A peer report is not proof of item delivery. Release an unstarted
                // reservation or quarantine an executing action until native verification.
                CorpseCommandKind.ReportComplete => _registry.Cancel(command.Actor, command.Target, command.Token, nowMs),
                _ => new CorpseResolution(CorpseDecision.Invalid, null)
            };
            return Encode(command, result);
        }
    }

    // Only the trusted engine-receipt verifier may call this, never Handle().
    // Requires exact correlation; changing action/request cannot complete a newer lease.
    public CorpseResolution ConfirmNativeCompletion(CorpseCommand receipt, long nowMs)
    {
        CorpseControlCodec.Validate(receipt);
        lock (_gate)
        {
            if (receipt.SessionId != _session || receipt.Kind != CorpseCommandKind.ReportComplete || Find(receipt) is null)
                return new(CorpseDecision.WrongOwner, null);
            return _registry.Complete(receipt.Actor, receipt.Target, receipt.Token, nowMs);
        }
    }
    internal bool MatchesActor(NetEntityId actor) => actor == _host || actor == _guest;
    internal void DisconnectActor(NetEntityId actor, long nowMs)
    {
        lock (_gate)
            if (!_registry.Disconnect(actor, nowMs))
                throw new InvalidOperationException("Cannot retire corpse authority at a stale time.");
    }
    private CorpseUseState? Find(CorpseCommand command) => _registry.Capture().FirstOrDefault(s =>
        s.Target == command.Target && s.Actor == command.Actor && s.Token == command.Token &&
        s.RequestId == command.RequestId && s.Action == command.Action);
    private static bool Fresh(long observed, long now) => observed >= 0 && observed <= now && now - observed <= 1500;
    private static byte[] Encode(CorpseCommand command, CorpseResolution result)
    {
        var state = result.State;
        var own = state is not null && state.Actor == command.Actor && state.Target == command.Target &&
            state.RequestId == command.RequestId && state.Action == command.Action;
        return CorpseControlCodec.EncodeReply(new(command, result.Decision, own,
            own ? state!.Phase : CorpseUsePhase.Available, own ? state!.Token : 0));
    }
}
