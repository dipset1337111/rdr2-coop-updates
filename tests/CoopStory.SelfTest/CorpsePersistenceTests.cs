using System.Numerics;
using System.Text;
using CoopStory.Protocol;
using CoopStory.Sidecar.Session;

namespace CoopStory.SelfTest;

internal static class CorpsePersistenceTests
{
    private static readonly NetEntityId Host = NetEntityId.Create(1, 1);
    private static readonly NetEntityId Guest = NetEntityId.Create(1, 2);
    private static readonly NetEntityId Body = NetEntityId.Create(1, 1000);
    private static CorpseActorObservation Actor(NetEntityId actor, long tick) => new(actor, Vector3.Zero, tick, true);
    private static CorpseObservation Target(long tick) => new(Body, 123, Vector3.One, tick, true, true, true);
    private sealed class DirectoryLease : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "CoopStory-Corpse-" + Guid.NewGuid().ToString("N"));
        public string Journal => Path.Combine(DirectoryPath, "corpse.json");
        public DirectoryLease() => Directory.CreateDirectory(DirectoryPath);
        public void Dispose()
        {
            foreach (var file in Directory.GetFiles(DirectoryPath)) File.Delete(file);
            Directory.Delete(DirectoryPath);
        }
    }
    private sealed class FailingStore : ICorpseStateStore
    {
        public CorpseRegistrySnapshot? Value;
        public bool Fail;
        public int Writes;
        public CorpseRegistrySnapshot? Load() => Value;
        public void Save(CorpseRegistrySnapshot snapshot)
        {
            if (Fail) throw new IOException("Injected pre-commit failure");
            Value = snapshot; Writes++;
        }
    }

    public static Task RestartPhases()
    {
        foreach (var action in new[] { CorpseAction.LootBody, CorpseAction.SkinAnimal })
        foreach (var phase in new[] { CorpseUsePhase.Reserved, CorpseUsePhase.Executing, CorpseUsePhase.Consumed })
        {
            using var dir = new DirectoryLease();
            var session = Guid.NewGuid();
            ulong token;
            using (var store = new CorpseFileStore(dir.Journal, createNew: true))
            {
                var registry = new AuthoritativeCorpseRegistry(Host, Guest, store: store, sessionId: session);
                var grant = registry.Reserve(Actor(Host, 10000), Target(10000), 1, action, 10000);
                token = grant.State!.Token;
                if (phase != CorpseUsePhase.Reserved) registry.Start(Host, Body, token, 10001);
                if (phase == CorpseUsePhase.Consumed) registry.Complete(Host, Body, token, 10002);
            }
            using (var store = new CorpseFileStore(dir.Journal))
            {
                var registry = new AuthoritativeCorpseRegistry(Host, Guest, store: store, sessionId: session);
                var expected = phase == CorpseUsePhase.Reserved ? CorpseUsePhase.Available :
                    phase == CorpseUsePhase.Executing ? CorpseUsePhase.Uncertain : CorpseUsePhase.Consumed;
                Check.Equal(expected, registry.Capture().Single().Phase);
                var replay = registry.Reserve(Actor(Host, 1), Target(1), 1, action, 1);
                Check.Equal(CorpseDecision.Replayed, replay.Decision);
                Check.Equal(expected, replay.State!.Phase);
                Check.False(replay.Changed);
                if (expected == CorpseUsePhase.Available)
                {
                    var grant = registry.Reserve(Actor(Guest, 2), Target(2), 1, action, 2);
                    Check.Equal(CorpseDecision.Allowed, grant.Decision);
                    Check.True(grant.State!.Token > token);
                    Check.Equal(CorpseDecision.WrongOwner, registry.Complete(Host, Body, token, 3).Decision);
                }
                else
                {
                    Check.Equal(CorpseDecision.Busy,
                        registry.Reserve(Actor(Guest, 2), Target(2), 1, action, 2).Decision);
                    Check.Equal(expected == CorpseUsePhase.Uncertain,
                        registry.Complete(Host, Body, token, 3).Changed);
                    Check.Equal(CorpseUsePhase.Consumed, registry.Capture().Single().Phase);
                }
            }
            // Recovery itself was persisted; repeated restarts cannot undo it.
            using (var store = new CorpseFileStore(dir.Journal))
            {
                var registry = new AuthoritativeCorpseRegistry(Host, Guest, store: store, sessionId: session);
                Check.Equal(phase == CorpseUsePhase.Reserved ? CorpseUsePhase.Available : CorpseUsePhase.Consumed,
                    registry.Capture().Single().Phase);
            }
        }
        return Task.CompletedTask;
    }

    public static Task WriteFailureRollsBack()
    {
        var store = new FailingStore();
        var registry = new AuthoritativeCorpseRegistry(Host, Guest, store: store, sessionId: Guid.NewGuid());
        store.Fail = true;
        Check.Throws<IOException>(() => registry.Reserve(Actor(Host, 100), Target(100), 1, CorpseAction.LootBody, 100));
        Check.Equal(0, registry.Capture().Count);
        Check.Equal(0, store.Value!.States.Length);
        store.Fail = false;
        var grant = registry.Reserve(Actor(Host, 100), Target(100), 1, CorpseAction.LootBody, 100);
        Check.Equal(1UL, grant.State!.Token);
        var token = grant.State.Token;
        store.Fail = true;
        Check.Throws<IOException>(() => registry.Start(Host, Body, token, 101));
        Check.Equal(CorpseUsePhase.Reserved, registry.Capture().Single().Phase);
        Check.Equal(CorpseUsePhase.Reserved, store.Value!.States.Single().Phase);
        store.Fail = false;
        registry.Start(Host, Body, token, 101);
        store.Fail = true;
        Check.Throws<IOException>(() => registry.Complete(Host, Body, token, 102));
        Check.Equal(CorpseUsePhase.Executing, registry.Capture().Single().Phase);
        Check.Throws<IOException>(() => registry.Disconnect(Host, 103));
        Check.Equal(CorpseUsePhase.Executing, registry.Capture().Single().Phase);
        store.Fail = false;
        registry.Complete(Host, Body, token, 104);
        var writes = store.Writes;
        registry.Complete(Host, Body, token, 105);
        Check.Equal(writes, store.Writes);
        return Task.CompletedTask;
    }

    public static Task CorruptionAndWriterExclusion()
    {
        using var dir = new DirectoryLease();
        var session = Guid.NewGuid();
        Check.Throws<IOException>(() => new CorpseFileStore(dir.Journal));
        using (var store = new CorpseFileStore(dir.Journal, createNew: true))
        {
            _ = new AuthoritativeCorpseRegistry(Host, Guest, store: store, sessionId: session);
            Check.Throws<IOException>(() => new CorpseFileStore(dir.Journal));
        }
        var original = File.ReadAllBytes(dir.Journal);
        Check.Throws<IOException>(() => new CorpseFileStore(dir.Journal, createNew: true));
        using (var store = new CorpseFileStore(dir.Journal))
        {
            Check.Throws<InvalidDataException>(() => new AuthoritativeCorpseRegistry(Host, Guest,
                store: store, sessionId: Guid.NewGuid()));
            Check.Throws<InvalidDataException>(() => new AuthoritativeCorpseRegistry(Guest, Host,
                store: store, sessionId: session));
        }
        Check.True(original.SequenceEqual(File.ReadAllBytes(dir.Journal)));
        // An orphan temporary file never replaces the committed journal.
        File.WriteAllText(dir.Journal + ".orphan.tmp", "partial");
        using (var store = new CorpseFileStore(dir.Journal)) Check.True(store.Load() is not null);
        foreach (var invalid in new[] { "{", "{}", "{\"Version\":1,\"Payload\":\"e30=\",\"Sha256\":\"BAD\"}" })
        {
            File.WriteAllText(dir.Journal, invalid, Encoding.UTF8);
            using var store = new CorpseFileStore(dir.Journal);
            Check.Throws<InvalidDataException>(() => store.Load());
            Check.Equal(invalid, File.ReadAllText(dir.Journal));
        }
        return Task.CompletedTask;
    }

    public static Task SemanticValidation()
    {
        var session = Guid.NewGuid();
        var store = new FailingStore();
        var registry = new AuthoritativeCorpseRegistry(Host, Guest, store: store, sessionId: session);
        registry.Reserve(Actor(Host, 100), Target(100), 1, CorpseAction.LootBody, 100);
        var good = store.Value!;
        foreach (var bad in new[]
        {
            good with { Version = 99 },
            good with { LastToken = 0 },
            good with { States = [good.States[0], good.States[0]] },
            good with { Requests = [] },
            good with { States = [good.States[0] with { Phase = (CorpseUsePhase)255 }] },
            good with { Requests = [good.Requests[0] with { ModelHash = 999 }] }
        })
        {
            store.Value = bad;
            var writes = store.Writes;
            Check.Throws<InvalidDataException>(() => new AuthoritativeCorpseRegistry(Host, Guest,
                store: store, sessionId: session));
            Check.Equal(writes, store.Writes);
        }
        return Task.CompletedTask;
    }
}
