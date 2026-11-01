using System.Numerics;
using CoopStory.Protocol;
using CoopStory.Sidecar.Session;

namespace CoopStory.SelfTest;

internal static class CorpseControlTests
{
    private static readonly Guid Session = Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00");
    private static readonly NetEntityId Host = NetEntityId.Create(1, 1);
    private static readonly NetEntityId Guest = NetEntityId.Create(1, 2);
    private static readonly NetEntityId Body = NetEntityId.Create(1, 1000);
    private static CorpseCommand Reserve => new(Session, Host, Body, 1, CorpseAction.LootBody, CorpseCommandKind.Reserve, 0);
    private static CorpseActorObservation Actor(NetEntityId actor, long tick = 100) => new(actor, Vector3.Zero, tick, true);
    private static CorpseObservation Target => new(Body, 123, Vector3.One, 100, true, true, false);

    public static Task Codec()
    {
        var encoded = CorpseControlCodec.EncodeCommand(Reserve);
        Check.Equal("01010100010000000100000001000000E8030000010000000000000000000000443322116655887799AABBCCDDEEFF00",
            Convert.ToHexString(encoded));
        Check.Equal(Reserve, CorpseControlCodec.DecodeCommand(encoded));
        foreach (var kind in Enum.GetValues<CorpseCommandKind>())
        foreach (var action in Enum.GetValues<CorpseAction>())
        {
            var command = Reserve with { Kind = kind, Action = action, Token = kind == CorpseCommandKind.Reserve ? 0UL : 9UL };
            Check.Equal(command, CorpseControlCodec.DecodeCommand(CorpseControlCodec.EncodeCommand(command)));
            var reply = new CorpseReply(command, CorpseDecision.Allowed, true, CorpseUsePhase.Reserved, 9);
            Check.Equal(reply, CorpseControlCodec.DecodeReply(CorpseControlCodec.EncodeReply(reply)));
        }
        for (var length = 0; length < encoded.Length; length++)
            Check.Throws<ProtocolException>(() => CorpseControlCodec.DecodeCommand(encoded[..length]));
        Check.Throws<ProtocolException>(() => CorpseControlCodec.DecodeCommand([.. encoded, 0]));
        foreach (var offset in new[] { 0, 1, 2, 3 })
        {
            var malformed = encoded.ToArray(); malformed[offset] = 255;
            Check.Throws<ProtocolException>(() => CorpseControlCodec.DecodeCommand(malformed));
        }
        foreach (var invalid in new[] { Reserve with { Token = 1 }, Reserve with { RequestId = 0 },
            Reserve with { Kind = CorpseCommandKind.Start }, Reserve with { Actor = Body },
            Reserve with { SessionId = Guid.Empty } })
            Check.Throws<ProtocolException>(() => CorpseControlCodec.EncodeCommand(invalid));
        var validReply = new CorpseReply(Reserve, CorpseDecision.Allowed, true, CorpseUsePhase.Reserved, 1);
        var replyBytes = CorpseControlCodec.EncodeReply(validReply);
        foreach (var offset in new[] { 48, 49, 50, 51, 52, 53, 54, 55 })
        {
            var malformed = replyBytes.ToArray(); malformed[offset] = 255;
            Check.Throws<ProtocolException>(() => CorpseControlCodec.DecodeReply(malformed));
        }
        Check.Throws<ProtocolException>(() => CorpseControlCodec.EncodeReply(validReply with { HasState = false }));
        Check.Throws<ProtocolException>(() => CorpseControlCodec.EncodeReply(validReply with { StateToken = 0 }));
        return Task.CompletedTask;
    }

    public static Task AuthorityAndCompletion()
    {
        var registry = new AuthoritativeCorpseRegistry(Host, Guest, sessionId: Session);
        var handler = new CorpseControlHandler(Session, Host, Guest, registry, id => Actor(id), id => Target);
        CorpseReply Send(NetEntityId identity, CorpseCommand command, long tick = 100) =>
            CorpseControlCodec.DecodeReply(handler.Handle(identity, CorpseControlCodec.EncodeCommand(command), tick));
        Check.Equal(CorpseDecision.Invalid, Send(Guest, Reserve).Decision);
        Check.Equal(CorpseDecision.Invalid, Send(Host, Reserve with { SessionId = Guid.NewGuid() }).Decision);
        Check.Equal(0, registry.Capture().Count);
        var grant = Send(Host, Reserve);
        Check.Equal(CorpseDecision.Allowed, grant.Decision);
        Check.Equal(grant.StateToken, Send(Host, Reserve).StateToken);
        var competitor = Send(Guest, Reserve with { Actor = Guest });
        Check.Equal(CorpseDecision.Busy, competitor.Decision);
        Check.False(competitor.HasState); Check.Equal(0UL, competitor.StateToken);
        var start = Reserve with { Kind = CorpseCommandKind.Start, Token = grant.StateToken };
        Check.Equal(CorpseDecision.WrongOwner, Send(Host, start with { RequestId = 2 }).Decision);
        Check.Equal(CorpseDecision.WrongOwner, Send(Host, start with { Action = CorpseAction.SkinAnimal }).Decision);
        Check.Equal(CorpseDecision.Allowed, Send(Host, start).Decision);
        Check.Equal(CorpseDecision.Replayed, Send(Host, start).Decision);
        var report = start with { Kind = CorpseCommandKind.ReportComplete };
        Check.Equal(CorpseUsePhase.Uncertain, Send(Host, report).Phase);
        Check.Equal(CorpseUsePhase.Uncertain, Send(Host, report).Phase);
        Check.Equal(CorpseDecision.Busy, Send(Guest, Reserve with { Actor = Guest }).Decision);
        Check.False(handler.ConfirmNativeCompletion(report with { RequestId = 2 }, 100).Changed);
        Check.True(handler.ConfirmNativeCompletion(report, 100).Changed);
        Check.False(handler.ConfirmNativeCompletion(report, 100).Changed);
        Check.Equal(CorpseUsePhase.Consumed, Send(Host, report).Phase);
        Check.Equal(CorpseUsePhase.Consumed, registry.Capture().Single().Phase);
        return Task.CompletedTask;
    }

    public static Task ReconciliationUsesJournal()
    {
        var registry = new AuthoritativeCorpseRegistry(Host, Guest, sessionId: Session);
        var online = true;
        var handler = new CorpseControlHandler(Session, Host, Guest, registry,
            id => online ? Actor(id) : null, id => online ? Target : null);
        CorpseReply Send(NetEntityId actor, CorpseCommand command, long tick = 100) =>
            CorpseControlCodec.DecodeReply(handler.Handle(actor, CorpseControlCodec.EncodeCommand(command), tick));
        var query = Reserve with { Kind = CorpseCommandKind.Reconcile };
        Check.False(Send(Host, query).HasState); // Unknown is never reported available.
        var first = Send(Host, Reserve);
        online = false; // Lost reserve reply and body unloaded: token is unknown locally.
        var recovered = Send(Host, query);
        Check.Equal(CorpseUsePhase.Available, recovered.Phase);
        Check.Equal(first.StateToken, recovered.StateToken);
        Check.Equal(CorpseDecision.Replayed, Send(Host, query).Decision);
        var start = Reserve with { Kind = CorpseCommandKind.Start, Token = first.StateToken };
        Check.Equal(CorpseDecision.Unavailable, Send(Host, start).Decision);
        online = true;
        var secondRequest = Reserve with { RequestId = 2 };
        var second = Send(Host, secondRequest);
        Check.False(Send(Host, query).HasState); // Old request cannot cancel the new reservation.
        Check.False(Send(Host, secondRequest with { Kind = CorpseCommandKind.Reconcile, Token = first.StateToken }).HasState);
        Check.False(Send(Guest, secondRequest with { Kind = CorpseCommandKind.Reconcile, Actor = Guest }).HasState);
        start = secondRequest with { Kind = CorpseCommandKind.Start, Token = second.StateToken };
        Check.Equal(CorpseUsePhase.Executing, Send(Host, start).Phase);
        online = false;
        query = start with { Kind = CorpseCommandKind.Reconcile };
        Check.Equal(CorpseUsePhase.Uncertain, Send(Host, query).Phase);
        Check.Equal(CorpseUsePhase.Uncertain, Send(Host, query).Phase);
        Check.True(handler.ConfirmNativeCompletion(start with { Kind = CorpseCommandKind.ReportComplete }, 100).Changed);
        Check.Equal(CorpseUsePhase.Consumed, Send(Host, query).Phase);
        Check.Equal(CorpseUsePhase.Consumed, registry.Capture().Single().Phase);
        return Task.CompletedTask;
    }

    private sealed class ReconcileStore : ICorpseStateStore
    {
        public bool Fail;
        public CorpseRegistrySnapshot? Snapshot;
        public CorpseRegistrySnapshot? Load() => Snapshot;
        public void Save(CorpseRegistrySnapshot snapshot)
        {
            if (Fail) throw new IOException("Injected reconciliation journal failure");
            Snapshot = snapshot;
        }
    }
    public static Task ReconciliationCommitsBeforeReply()
    {
        var store = new ReconcileStore();
        var registry = new AuthoritativeCorpseRegistry(Host, Guest, store: store, sessionId: Session);
        var handler = new CorpseControlHandler(Session, Host, Guest, registry, id => Actor(id), id => Target);
        byte[] Send(CorpseCommand command) => handler.Handle(Host, CorpseControlCodec.EncodeCommand(command), 100);
        var granted = CorpseControlCodec.DecodeReply(Send(Reserve));
        var query = Reserve with { Kind = CorpseCommandKind.Reconcile };
        store.Fail = true;
        Check.Throws<IOException>(() => Send(query));
        Check.Equal(CorpseUsePhase.Reserved, registry.Capture().Single().Phase);
        store.Fail = false;
        Check.Equal(CorpseUsePhase.Available, CorpseControlCodec.DecodeReply(Send(query)).Phase);
        Check.Equal(CorpseUsePhase.Available, store.Snapshot!.States.Single().Phase);
        var restored = new AuthoritativeCorpseRegistry(Host, Guest, store: store, sessionId: Session);
        var result = restored.Reconcile(Host, Body, 1, CorpseAction.LootBody, 0, 100);
        Check.Equal(CorpseUsePhase.Available, result.State!.Phase);
        Check.Equal(granted.StateToken, result.State.Token);
        return Task.CompletedTask;
    }

    public static Task StartRevalidatesWorld()
    {
        var actor = Actor(Guest);
        var target = Target;
        var registry = new AuthoritativeCorpseRegistry(Host, Guest, sessionId: Session);
        var handler = new CorpseControlHandler(Session, Host, Guest, registry, _ => actor, _ => target);
        var reserve = Reserve with { Actor = Guest };
        CorpseReply Send(CorpseCommand command, long tick = 100) => CorpseControlCodec.DecodeReply(
            handler.Handle(Guest, CorpseControlCodec.EncodeCommand(command), tick));
        var grant = Send(reserve);
        var start = reserve with { Kind = CorpseCommandKind.Start, Token = grant.StateToken };
        actor = actor with { Position = new Vector3(10, 0, 0) };
        Check.Equal(CorpseDecision.TooFar, Send(start).Decision);
        actor = Actor(Guest);
        target = target with { ModelHash = 999 };
        Check.Equal(CorpseDecision.Unavailable, Send(start).Decision);
        target = Target;
        Check.Equal(CorpseDecision.Unavailable, Send(start, 1700).Decision);
        Check.Equal(CorpseUsePhase.Reserved, registry.Capture().Single().Phase);
        actor = Actor(Guest, 1700); target = Target with { ObservedAtMs = 1700 };
        Check.Equal(CorpseDecision.Allowed, Send(start, 1700).Decision);
        // A new handler may only bind to the same registry/session identities.
        Check.Throws<ArgumentException>(() => new CorpseControlHandler(Guid.NewGuid(), Host, Guest,
            registry, _ => actor, _ => target));
        return Task.CompletedTask;
    }
}
