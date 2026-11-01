using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using CoopStory.Protocol;
using CoopStory.Sidecar.Session;

namespace CoopStory.SelfTest;

internal static class CorpseConnectionTests
{
    private static readonly NetEntityId Host = NetEntityId.Create(1, 1);
    private static readonly NetEntityId Guest = NetEntityId.Create(1, 2);
    private static readonly NetEntityId Body = NetEntityId.Create(1, 1000);
    private static readonly Guid Session = Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00");
    private static CorpseCommand Reserve => new(Session, Guest, Body, 1,
        CorpseAction.LootBody, CorpseCommandKind.Reserve, 0);
    private static ProtocolEnvelope Frame(CorpseCommand c) => new(MessageType.CorpseCommand, 1, 100,
        CorpseControlCodec.EncodeCommand(c));
    private static CorpseControlHandler Handler(AuthoritativeCorpseRegistry registry) => new(Session, Host, Guest,
        registry, id => new(id, Vector3.Zero, 100, true),
        id => new(id, 123, Vector3.One, 100, true, true, false));

    public static Task VersionAndRouting()
    {
        var frame = Frame(Reserve);
        var bytes = ProtocolCodec.Encode(frame);
        Check.Equal((ushort)38, ProtocolConstants.Version);
        Check.Equal(MessageType.CorpseCommand, ProtocolCodec.Decode(bytes).Type);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 37);
        Check.Throws<ProtocolException>(() => ProtocolCodec.Decode(bytes));
        SidecarRuntime.ValidateBinaryControlPayload(frame);
        Check.False(SidecarRuntime.IsReplaceableBridgeDeliveryType(MessageType.CorpseCommand));
        Check.False(SidecarRuntime.IsReplaceableBridgeDeliveryType(MessageType.CorpseReply));
        foreach (var role in new[] { SessionRole.Host, SessionRole.Guest })
        foreach (var type in new[] { MessageType.CorpseCommand, MessageType.CorpseReply })
        {
            // Generic forwarding cannot bypass the experimental channel gate.
            Check.False(SidecarRuntime.IsPeerMessageAuthorized(role, type));
            Check.False(SidecarRuntime.IsLocalBridgeMessageAuthorized(role, type));
        }
        Check.Throws<ProtocolException>(() => SidecarRuntime.ValidateBinaryControlPayload(
            frame with { Payload = new byte[48] }));
        return Task.CompletedTask;
    }

    public static Task ReconnectRejectsQueuedFrames()
    {
        var registry = new AuthoritativeCorpseRegistry(Host, Guest, sessionId: Session);
        var connection = new CorpseControlConnection(Handler(registry), Guest);
        var first = connection.Open();
        Check.Throws<InvalidOperationException>(() => connection.Open());
        var reply = connection.Receive(first, Frame(Reserve), 100)!;
        var token = CorpseControlCodec.DecodeReply(reply.Payload.Span).StateToken;
        connection.Close(first, 101);
        var second = connection.Open();
        Check.True(second != first);
        var start = Reserve with { Kind = CorpseCommandKind.Start, Token = token };
        Check.Null(connection.Receive(first, Frame(start), 102));
        Check.Equal(CorpseUsePhase.Available, registry.Capture().Single().Phase);
        connection.Close(first, 102); // A late close cannot retire the new connection.
        var reserve = Reserve with { RequestId = 2 };
        reply = connection.Receive(second, Frame(reserve), 103)!;
        token = CorpseControlCodec.DecodeReply(reply.Payload.Span).StateToken;
        start = reserve with { Kind = CorpseCommandKind.Start, Token = token };
        Check.Equal(CorpseUsePhase.Executing, CorpseControlCodec.DecodeReply(
            connection.Receive(second, Frame(start), 104)!.Payload.Span).Phase);
        connection.Close(second, 105);
        Check.Equal(CorpseUsePhase.Uncertain, registry.Capture().Single().Phase);
        var third = connection.Open();
        Check.Null(connection.Receive(second, Frame(start), 106));
        Check.Throws<ProtocolException>(() => connection.Receive(third, Frame(start) with { Version = 36 }, 106));
        Check.Throws<ProtocolException>(() => connection.Receive(third, Frame(start) with { Type = MessageType.CorpseReply }, 106));
        return Task.CompletedTask;
    }

    private sealed class FailingStore : ICorpseStateStore
    {
        public bool Fail;
        private CorpseRegistrySnapshot? _saved;
        public CorpseRegistrySnapshot? Load() => _saved;
        public void Save(CorpseRegistrySnapshot snapshot)
        {
            if (Fail) throw new IOException("Injected disk failure");
            _saved = snapshot;
        }
    }
    public static Task CloseFailureDisablesChannel()
    {
        var store = new FailingStore();
        var registry = new AuthoritativeCorpseRegistry(Host, Guest, store: store, sessionId: Session);
        var connection = new CorpseControlConnection(Handler(registry), Guest);
        var generation = connection.Open();
        var reply = connection.Receive(generation, Frame(Reserve), 100)!;
        var token = CorpseControlCodec.DecodeReply(reply.Payload.Span).StateToken;
        var start = Reserve with { Kind = CorpseCommandKind.Start, Token = token };
        connection.Receive(generation, Frame(start), 101);
        store.Fail = true;
        Check.Throws<IOException>(() => connection.Close(generation, 102));
        Check.Null(connection.Receive(generation, Frame(start), 103));
        Check.Throws<InvalidOperationException>(() => connection.Open());
        return Task.CompletedTask;
    }

    public static async Task TcpRoundtrip()
    {
        var registry = new AuthoritativeCorpseRegistry(Host, Guest, sessionId: Session);
        var connection = new CorpseControlConnection(Handler(registry), Guest);
        var generation = connection.Open();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
        using var server = await accept;
        var serverTask = Task.Run(async () =>
        {
            var stream = server.GetStream();
            for (var i = 0; i < 4; i++)
            {
                var request = await ProtocolCodec.ReadAsync(stream, timeout.Token)
                    ?? throw new IOException("Unexpected EOF");
                var response = connection.Receive(generation, request, 100 + i)
                    ?? throw new IOException("Channel unexpectedly retired");
                await ProtocolCodec.WriteAsync(stream, response, timeout.Token);
            }
        }, timeout.Token);
        var command = Reserve;
        for (var i = 0; i < 4; i++)
        {
            var bytes = ProtocolCodec.Encode(Frame(command));
            // Deliberately split the header and payload across socket writes.
            await client.GetStream().WriteAsync(bytes.AsMemory(0, 7), timeout.Token);
            await client.GetStream().WriteAsync(bytes.AsMemory(7), timeout.Token);
            var response = await ProtocolCodec.ReadAsync(client.GetStream(), timeout.Token)
                ?? throw new IOException("Unexpected EOF");
            Check.Equal(MessageType.CorpseReply, response.Type);
            var reply = CorpseControlCodec.DecodeReply(response.Payload.Span);
            Check.Equal(command, reply.Command);
            Check.Equal(i == 0 ? CorpseUsePhase.Reserved : i == 1 ? CorpseUsePhase.Executing : CorpseUsePhase.Uncertain,
                reply.Phase);
            command = command with { Kind = i == 0 ? CorpseCommandKind.Start : i == 1 ? CorpseCommandKind.ReportComplete : CorpseCommandKind.Reconcile,
                Token = reply.StateToken };
        }
        await serverTask;
        Check.Equal(CorpseUsePhase.Uncertain, registry.Capture().Single().Phase);
        connection.Close(generation, 104);
    }
}
