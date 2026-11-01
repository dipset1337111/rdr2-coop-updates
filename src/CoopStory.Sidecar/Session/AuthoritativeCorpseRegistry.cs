using System.Numerics;
using CoopStory.Protocol;

namespace CoopStory.Sidecar.Session;

internal readonly record struct CorpseObservation(
    NetEntityId Target, uint ModelHash, Vector3 Position, long ObservedAtMs,
    bool Dead, bool CanLootBody, bool CanSkinAnimal);
internal readonly record struct CorpseActorObservation(
    NetEntityId Actor, Vector3 Position, long ObservedAtMs, bool Alive);
internal sealed record CorpseUseState(
    NetEntityId Target, uint ModelHash, NetEntityId Actor, uint RequestId,
    CorpseAction Action, ulong Token, CorpseUsePhase Phase, long ExpiresAtMs);
internal readonly record struct CorpseResolution(
    CorpseDecision Decision, CorpseUseState? State, bool Changed = false);

/// <summary>
/// Session-scoped host arbitration, not yet connected to network/native loot.
/// Never issues inventory rewards. A grant only permits an engine operation;
/// completion must come from a trusted, verified engine receipt.
/// Observations and actor identity must be resolved by the host, not supplied
/// directly by an untrusted peer. Consumed/uncertain rows are never evicted.
/// </summary>
internal sealed class AuthoritativeCorpseRegistry
{
    private const long ObservationMaxAgeMs = 1500;
    private const long ReservationMs = 3000;
    private readonly object _sync = new();
    private readonly NetEntityId _host;
    private readonly NetEntityId _guest;
    private readonly int _capacity;
    private readonly Dictionary<NetEntityId, CorpseUseState> _states = [];
    private readonly Dictionary<NetEntityId, CorpseUseState> _requests = [];
    private ulong _token;
    private long _clock = -1;
    private readonly Guid _sessionId;
    private readonly ICorpseStateStore? _store;
    internal bool MatchesAuthority(Guid sessionId, NetEntityId host, NetEntityId guest)
        => sessionId != Guid.Empty && _sessionId == sessionId && _host == host && _guest == guest;


    public AuthoritativeCorpseRegistry(NetEntityId host, NetEntityId guest, int capacity = 2048,
        ICorpseStateStore? store = null, Guid sessionId = default)
    {
        if (!host.IsValid || !guest.IsValid || host == guest || capacity < 1)
            throw new ArgumentException("Distinct authenticated players and positive capacity required.");
        if (store is not null && sessionId == Guid.Empty)
            throw new ArgumentException("A persistent corpse registry requires its session identity.");
        _host = host; _guest = guest; _capacity = capacity;
        _store = store; _sessionId = sessionId;
        if (_store is not null)
        {
            var saved = _store.Load();
            if (saved is not null) Restore(saved);
            // Persist recovery before any grant can be returned.
            _store.Save(Snapshot());
        }
    }

    public CorpseResolution Reserve(CorpseActorObservation actor, CorpseObservation target,
        uint requestId, CorpseAction action, long nowMs)
        => Persist(() => ReserveCore(actor, target, requestId, action, nowMs));

    private CorpseResolution ReserveCore(CorpseActorObservation actor, CorpseObservation target,
        uint requestId, CorpseAction action, long nowMs)
    {
        lock (_sync)
        {
            if (!Known(actor.Actor) || requestId == 0 || !target.Target.IsValid ||
                target.Target == _host || target.Target == _guest || target.ModelHash == 0 ||
                action is not (CorpseAction.LootBody or CorpseAction.SkinAnimal))
                return new(CorpseDecision.Invalid, null);
            if (!Advance(nowMs)) return new(CorpseDecision.Stale, null);
            if (_requests.TryGetValue(actor.Actor, out var request))
            {
                if (request.RequestId == requestId)
                {
                    if (request.Target != target.Target || request.Action != action ||
                        request.ModelHash != target.ModelHash)
                        return new(CorpseDecision.Invalid, null);
                    var replay = _states[request.Target];
                    return replay.Token == request.Token
                        ? new(CorpseDecision.Replayed, replay)
                        : new(CorpseDecision.Stale, null);
                }
                if (!SequenceNumber.IsNewer(requestId, request.RequestId))
                    return new(CorpseDecision.Stale, null);
            }
            if (!Fresh(actor.ObservedAtMs, nowMs) || !Fresh(target.ObservedAtMs, nowMs))
                return new(CorpseDecision.Stale, null);
            if (!Finite(actor.Position) || !Finite(target.Position))
                return new(CorpseDecision.Invalid, null);
            if (!actor.Alive || !target.Dead ||
                (action == CorpseAction.LootBody ? !target.CanLootBody : !target.CanSkinAnimal))
                return new(CorpseDecision.Unavailable, null);
            if (Vector3.DistanceSquared(actor.Position, target.Position) > 9f)
                return new(CorpseDecision.TooFar, null);
            if (_states.TryGetValue(target.Target, out var current))
            {
                if (current.ModelHash != target.ModelHash)
                    return new(CorpseDecision.Invalid, current);
                if (current.Phase != CorpseUsePhase.Available)
                    return new(CorpseDecision.Busy, current);
            }
            if (_states.Values.Any(s => s.Actor == actor.Actor &&
                s.Phase is CorpseUsePhase.Reserved or CorpseUsePhase.Executing or CorpseUsePhase.Uncertain))
                return new(CorpseDecision.Busy, null);
            if (!_states.ContainsKey(target.Target) && _states.Count >= _capacity)
                return new(CorpseDecision.Capacity, null);
            if (_token == ulong.MaxValue || nowMs > long.MaxValue - ReservationMs)
                return new(CorpseDecision.Capacity, null);
            var state = new CorpseUseState(target.Target, target.ModelHash, actor.Actor,
                requestId, action, ++_token, CorpseUsePhase.Reserved, nowMs + ReservationMs);
            _states[target.Target] = state;
            _requests[actor.Actor] = state;
            return new(CorpseDecision.Allowed, state, true);
        }
    }

    // Call before the engine can issue rewards, not after observing an animation.
    public CorpseResolution Start(NetEntityId actor, NetEntityId target, ulong token, long nowMs)
        => Persist(() => Transition(actor, target, token, nowMs, CorpseUsePhase.Executing));

    // Only a trusted receipt may close an executing/uncertain action. Repeated
    // receipts do not produce Changed=true and must never trigger another award.
    public CorpseResolution Complete(NetEntityId actor, NetEntityId target, ulong token, long nowMs)
        => Persist(() => Transition(actor, target, token, nowMs, CorpseUsePhase.Consumed));

    public CorpseResolution Cancel(NetEntityId actor, NetEntityId target, ulong token, long nowMs)
        => Persist(() => Transition(actor, target, token, nowMs, CorpseUsePhase.Available));

    private CorpseResolution Transition(NetEntityId actor, NetEntityId target,
        ulong token, long nowMs, CorpseUsePhase desired)
    {
        lock (_sync)
        {
            if (!Known(actor) || token == 0) return new(CorpseDecision.Invalid, null);
            if (!Advance(nowMs)) return new(CorpseDecision.Stale, null);
            if (!_states.TryGetValue(target, out var state) ||
                state.Actor != actor || state.Token != token)
                return new(CorpseDecision.WrongOwner, null);
            if (state.Phase == desired) return new(CorpseDecision.Replayed, state);
            if (state.Phase == CorpseUsePhase.Consumed || state.Phase == CorpseUsePhase.Available)
                return new(CorpseDecision.Unavailable, state);
            if (desired == CorpseUsePhase.Executing && state.Phase != CorpseUsePhase.Reserved)
                return new(CorpseDecision.Unavailable, state);
            if (desired == CorpseUsePhase.Consumed && state.Phase == CorpseUsePhase.Reserved)
                return new(CorpseDecision.Unavailable, state);
            // Once reward delivery may have started, cancellation cannot prove
            // that no reward was granted. Keep the resource locked for review.
            if (desired == CorpseUsePhase.Available && state.Phase != CorpseUsePhase.Reserved)
                desired = CorpseUsePhase.Uncertain;
            if (desired == state.Phase) return new(CorpseDecision.Replayed, state);
            state = state with { Phase = desired, ExpiresAtMs = 0 };
            _states[target] = state;
            return new(CorpseDecision.Allowed, state, true);
        }
    }

    // Reconcile means abandon any unstarted action, not re-authorize it. The
    // journal, not a streamed-in world proxy, is the source of the answer.
    public CorpseResolution Reconcile(NetEntityId actor, NetEntityId target, uint requestId,
        CorpseAction action, ulong token, long nowMs) => Persist(() =>
    {
        if (!Known(actor) || requestId == 0 || !target.IsValid ||
            action is not (CorpseAction.LootBody or CorpseAction.SkinAnimal))
            return new CorpseResolution(CorpseDecision.Invalid, null);
        if (!Advance(nowMs)) return new CorpseResolution(CorpseDecision.Stale, null);
        if (!_states.TryGetValue(target, out var state) || state.Actor != actor ||
            state.RequestId != requestId || state.Action != action ||
            (token != 0 && state.Token != token))
            return new CorpseResolution(CorpseDecision.WrongOwner, null);
        if (state.Phase is CorpseUsePhase.Available or CorpseUsePhase.Consumed or CorpseUsePhase.Uncertain)
            return new CorpseResolution(CorpseDecision.Replayed, state);
        return Transition(actor, target, state.Token, nowMs, CorpseUsePhase.Available);
    });

    public bool Disconnect(NetEntityId actor, long nowMs)
        => Persist(() => DisconnectCore(actor, nowMs));

    private bool DisconnectCore(NetEntityId actor, long nowMs)
    {
        lock (_sync)
        {
            if (!Known(actor) || !Advance(nowMs)) return false;
            foreach (var state in _states.Values.Where(s => s.Actor == actor).ToArray())
            {
                if (state.Phase == CorpseUsePhase.Reserved)
                    _states[state.Target] = state with { Phase = CorpseUsePhase.Available, ExpiresAtMs = 0 };
                else if (state.Phase == CorpseUsePhase.Executing)
                    _states[state.Target] = state with { Phase = CorpseUsePhase.Uncertain, ExpiresAtMs = 0 };
            }
            return true;
        }
    }

    // Includes consumed/uncertain rows even when the world proxy is absent.
    public IReadOnlyList<CorpseUseState> Capture()
    {
        lock (_sync) return _states.Values.OrderBy(s => s.Target.Value).ToArray();
    }

    private CorpseRegistrySnapshot Snapshot() => new(1, _sessionId, _host, _guest, _token,
        _states.Values.OrderBy(s => s.Target.Value).ToArray(),
        _requests.Values.OrderBy(s => s.Actor.Value).ToArray());

    private T Persist<T>(Func<T> operation)
    {
        lock (_sync)
        {
            if (_store is null) return operation();
            var before = Snapshot();
            var clock = _clock;
            try
            {
                var result = operation();
                var after = Snapshot();
                if (after.LastToken != before.LastToken ||
                    !after.States.SequenceEqual(before.States) ||
                    !after.Requests.SequenceEqual(before.Requests))
                    _store.Save(after); // No permission/ack leaves before this succeeds.
                return result;
            }
            catch
            {
                _states.Clear(); _requests.Clear();
                foreach (var state in before.States) _states.Add(state.Target, state);
                foreach (var request in before.Requests) _requests.Add(request.Actor, request);
                _token = before.LastToken; _clock = clock;
                throw;
            }
        }
    }

    private void Restore(CorpseRegistrySnapshot saved)
    {
        if (saved.Version != 1 || saved.SessionId != _sessionId ||
            saved.Host != _host || saved.Guest != _guest ||
            saved.States is null || saved.Requests is null ||
            saved.States.Length > _capacity || saved.Requests.Length > 2)
            throw new InvalidDataException("Corpse journal identity, version or capacity mismatch.");
        var targets = new HashSet<NetEntityId>();
        var tokens = new HashSet<ulong>();
        var owners = new HashSet<NetEntityId>();
        var cursors = new HashSet<NetEntityId>();
        bool Valid(CorpseUseState? s) => s is not null && s.Target.IsValid &&
            s.Target != _host && s.Target != _guest && Known(s.Actor) && s.ModelHash != 0 &&
            s.RequestId != 0 && s.Token != 0 && s.Token <= saved.LastToken &&
            s.Action is CorpseAction.LootBody or CorpseAction.SkinAnimal &&
            Enum.IsDefined(s.Phase) &&
            (s.Phase == CorpseUsePhase.Reserved ? s.ExpiresAtMs > 0 : s.ExpiresAtMs == 0);
        foreach (var state in saved.States)
        {
            if (!Valid(state) || !targets.Add(state.Target) || !tokens.Add(state.Token) ||
                (state.Phase is CorpseUsePhase.Reserved or CorpseUsePhase.Executing or CorpseUsePhase.Uncertain &&
                 !owners.Add(state.Actor)))
                throw new InvalidDataException("Invalid corpse journal state.");
        }
        foreach (var request in saved.Requests)
        {
            if (!Valid(request) || !cursors.Add(request.Actor) || !targets.Contains(request.Target))
                throw new InvalidDataException("Invalid corpse request cursor.");
            var current = saved.States.Single(s => s.Target == request.Target);
            if (current.Token < request.Token || current.ModelHash != request.ModelHash ||
                (current.Token == request.Token && (current.Actor != request.Actor ||
                    current.RequestId != request.RequestId || current.Action != request.Action)))
                throw new InvalidDataException("Inconsistent corpse request cursor.");
        }
        foreach (var state in saved.States)
        {
            if (!saved.Requests.Any(r => r.Actor == state.Actor && r.Token >= state.Token))
                throw new InvalidDataException("Missing corpse request cursor.");
            // Monotonic deadlines belong to the old process. Unstarted reservations
            // are safe to release; an interrupted reward operation needs reconciliation.
            var recovered = state with { ExpiresAtMs = 0, Phase = state.Phase switch
            {
                CorpseUsePhase.Reserved => CorpseUsePhase.Available,
                CorpseUsePhase.Executing => CorpseUsePhase.Uncertain,
                _ => state.Phase
            }};
            _states.Add(recovered.Target, recovered);
        }
        foreach (var request in saved.Requests) _requests.Add(request.Actor, request);
        _token = saved.LastToken;
        _clock = -1;
    }

    private bool Advance(long nowMs)
    {
        if (nowMs < 0 || nowMs < _clock) return false;
        _clock = nowMs;
        foreach (var state in _states.Values.Where(s =>
            s.Phase == CorpseUsePhase.Reserved && nowMs >= s.ExpiresAtMs).ToArray())
            _states[state.Target] = state with { Phase = CorpseUsePhase.Available, ExpiresAtMs = 0 };
        return true;
    }
    private bool Known(NetEntityId actor) => actor == _host || actor == _guest;
    private static bool Fresh(long observed, long now) => observed >= 0 && observed <= now &&
        now - observed <= ObservationMaxAgeMs;
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
