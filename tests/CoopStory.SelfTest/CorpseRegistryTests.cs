using System.Numerics;
using CoopStory.Protocol;
using CoopStory.Sidecar.Session;

namespace CoopStory.SelfTest;

internal static class CorpseRegistryTests
{
    private static readonly NetEntityId Host = NetEntityId.Create(1, 1);
    private static readonly NetEntityId Guest = NetEntityId.Create(1, 2);
    private static readonly NetEntityId Body = NetEntityId.Create(1, 1000);
    private static CorpseActorObservation Actor(NetEntityId id, long tick = 100) =>
        new(id, Vector3.Zero, tick, true);
    private static CorpseObservation Target(long tick = 100) =>
        new(Body, 123, Vector3.One, tick, true, true, false);

    public static Task Lifecycle()
    {
        var registry = new AuthoritativeCorpseRegistry(Host, Guest);
        var grant = registry.Reserve(Actor(Host), Target(), 1, CorpseAction.LootBody, 100);
        Check.Equal(CorpseDecision.Allowed, grant.Decision);
        var token = grant.State!.Token;
        Check.False(registry.Complete(Host, Body, token, 101).Changed);
        Check.Equal(CorpseDecision.Busy,
            registry.Reserve(Actor(Guest), Target(), 1, CorpseAction.LootBody, 102).Decision);
        Check.Equal(CorpseDecision.WrongOwner, registry.Start(Guest, Body, token, 103).Decision);
        Check.True(registry.Start(Host, Body, token, 104).Changed);
        Check.False(registry.Start(Host, Body, token, 105).Changed);
        Check.True(registry.Complete(Host, Body, token, 106).Changed);
        Check.False(registry.Complete(Host, Body, token, 107).Changed);
        Check.False(registry.Cancel(Host, Body, token, 108).Changed);
        Check.Equal(CorpseUsePhase.Consumed, registry.Capture().Single().Phase);
        Check.Equal(CorpseDecision.Busy,
            registry.Reserve(Actor(Guest), Target(), 2, CorpseAction.LootBody, 109).Decision);
        return Task.CompletedTask;
    }

    public static Task Validation()
    {
        var registry = new AuthoritativeCorpseRegistry(Host, Guest);
        CorpseResolution Reserve(CorpseActorObservation actor, CorpseObservation target,
            CorpseAction action = CorpseAction.LootBody) => registry.Reserve(actor, target, 1, action, 2000);
        Check.Equal(CorpseDecision.Invalid, Reserve(Actor(NetEntityId.Create(9, 9)), Target()).Decision);
        Check.Equal(CorpseDecision.Stale, Reserve(Actor(Host), Target()).Decision);
        var actor = Actor(Host, 2000);
        var target = Target(2000);
        Check.Equal(CorpseDecision.Stale, Reserve(actor with { ObservedAtMs = 2001 }, target).Decision);
        Check.Equal(CorpseDecision.Invalid, Reserve(actor with { Position = new(float.NaN, 0, 0) }, target).Decision);
        Check.Equal(CorpseDecision.TooFar, Reserve(actor with { Position = new(10, 0, 0) }, target).Decision);
        Check.Equal(CorpseDecision.Unavailable, Reserve(actor with { Alive = false }, target).Decision);
        Check.Equal(CorpseDecision.Unavailable, Reserve(actor, target with { Dead = false }).Decision);
        Check.Equal(CorpseDecision.Unavailable, Reserve(actor, target, CorpseAction.SkinAnimal).Decision);
        Check.Equal(CorpseDecision.Invalid, Reserve(actor, target with { Target = Guest }).Decision);
        Check.Equal(0, registry.Capture().Count);
        var animal = target with { CanLootBody = false, CanSkinAnimal = true };
        Check.Equal(CorpseDecision.Unavailable, Reserve(actor, animal).Decision);
        Check.Equal(CorpseDecision.Allowed, Reserve(actor, animal, CorpseAction.SkinAnimal).Decision);
        return Task.CompletedTask;
    }

    public static Task RetryAndTimeout()
    {
        var registry = new AuthoritativeCorpseRegistry(Host, Guest);
        var first = registry.Reserve(Actor(Host), Target(), uint.MaxValue, CorpseAction.LootBody, 100);
        var replay = registry.Reserve(Actor(Host), Target(), uint.MaxValue, CorpseAction.LootBody, 101);
        Check.Equal(CorpseDecision.Replayed, replay.Decision);
        Check.Equal(first.State!.Token, replay.State!.Token);
        Check.Equal(first.State.ExpiresAtMs, replay.State.ExpiresAtMs);
        Check.Equal(CorpseDecision.Invalid, registry.Reserve(Actor(Host),
            Target() with { Target = NetEntityId.Create(1, 1001) }, uint.MaxValue, CorpseAction.LootBody, 102).Decision);
        Check.Equal(CorpseDecision.Stale, registry.Start(Host, Body, first.State.Token, 99).Decision);
        Check.Equal(CorpseDecision.Unavailable, registry.Start(Host, Body, first.State.Token, 3100).Decision);
        Check.Equal(CorpseUsePhase.Available, registry.Capture().Single().Phase);
        var expiredReplay = registry.Reserve(Actor(Host, 3100), Target(3100), uint.MaxValue, CorpseAction.LootBody, 3100);
        Check.Equal(CorpseUsePhase.Available, expiredReplay.State!.Phase);
        Check.False(expiredReplay.Changed);
        var next = registry.Reserve(Actor(Host, 3100), Target(3100), 1, CorpseAction.LootBody, 3100);
        Check.Equal(CorpseDecision.Allowed, next.Decision);
        Check.True(next.State!.Token != first.State.Token);
        Check.Equal(CorpseDecision.WrongOwner, registry.Complete(Host, Body, first.State.Token, 3101).Decision);
        return Task.CompletedTask;
    }

    public static Task DisconnectAndCapacity()
    {
        var registry = new AuthoritativeCorpseRegistry(Host, Guest, 1);
        var grant = registry.Reserve(Actor(Guest), Target(), 1, CorpseAction.LootBody, 100);
        Check.True(registry.Disconnect(Guest, 101));
        Check.Equal(CorpseUsePhase.Available, registry.Capture().Single().Phase);
        grant = registry.Reserve(Actor(Host), Target(), 1, CorpseAction.LootBody, 102);
        var token = grant.State!.Token;
        registry.Start(Host, Body, token, 103);
        Check.True(registry.Disconnect(Host, 104));
        Check.Equal(CorpseUsePhase.Uncertain, registry.Capture().Single().Phase);
        Check.Equal(CorpseDecision.Busy, registry.Reserve(Actor(Guest, 10000), Target(10000),
            2, CorpseAction.LootBody, 10000).Decision);
        Check.False(registry.Cancel(Host, Body, token, 10001).Changed);
        Check.Equal(CorpseUsePhase.Uncertain, registry.Capture().Single().Phase);
        Check.True(registry.Complete(Host, Body, token, 10002).Changed);
        var other = Target(10003) with { Target = NetEntityId.Create(1, 1001) };
        Check.Equal(CorpseDecision.Capacity, registry.Reserve(Actor(Guest, 10003), other,
            2, CorpseAction.LootBody, 10003).Decision);
        // Retaining a tombstone is preferable to allowing its consumed body to be looted twice.
        Check.Equal(Body, registry.Capture().Single().Target);
        Check.Equal(CorpseUsePhase.Consumed, registry.Capture().Single().Phase);
        Check.Equal(CorpseDecision.Invalid, registry.Reserve(Actor(Guest, 10003),
            Target(10003) with { ModelHash = 999 }, 2, CorpseAction.LootBody, 10003).Decision);
        return Task.CompletedTask;
    }

    public static async Task ConcurrentClaims()
    {
        for (var i = 0; i < 20; i++)
        {
            var registry = new AuthoritativeCorpseRegistry(Host, Guest);
            using var gate = new Barrier(2);
            var attempts = new[] { Host, Guest }.Select(actor => Task.Run(() =>
            {
                gate.SignalAndWait();
                return registry.Reserve(Actor(actor), Target(), 1, CorpseAction.LootBody, 100);
            })).ToArray();
            var results = await Task.WhenAll(attempts);
            Check.Equal(1, results.Count(r => r.Decision == CorpseDecision.Allowed));
            Check.Equal(1, results.Count(r => r.Decision == CorpseDecision.Busy));
            var owner = results.Single(r => r.Decision == CorpseDecision.Allowed).State!.Actor;
            Check.Equal(CorpseDecision.Busy, registry.Reserve(Actor(owner),
                Target() with { Target = NetEntityId.Create(1, 1001) }, 2, CorpseAction.LootBody, 101).Decision);
        }
    }
}
