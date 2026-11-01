using System.Buffers.Binary;

namespace CoopStory.Protocol;

public enum CorpseAction : byte { LootBody = 1, SkinAnimal = 2 }
public enum CorpseUsePhase : byte { Available, Reserved, Executing, Uncertain, Consumed }
public enum CorpseDecision : byte
{
    Allowed, Replayed, Invalid, Stale, Unavailable, Busy, TooFar, Capacity, WrongOwner
}
public enum CorpseCommandKind : byte { Reserve = 1, Start = 2, Cancel = 3, ReportComplete = 4, Reconcile = 5 }
public readonly record struct CorpseCommand(Guid SessionId, NetEntityId Actor,
    NetEntityId Target, uint RequestId, CorpseAction Action, CorpseCommandKind Kind, ulong Token);
public readonly record struct CorpseReply(CorpseCommand Command, CorpseDecision Decision,
    bool HasState, CorpseUsePhase Phase, ulong StateToken);

// Staged payload codec. Registered in protocol 38; the native
// bridge and capability negotiation must be implemented before live routing.
// All integer fields are little endian. GUID uses .NET TryWriteBytes layout.
public static class CorpseControlCodec
{
    public const int CommandSize = 48;
    public const int ReplySize = 64;
    public static byte[] EncodeCommand(CorpseCommand command)
    {
        Validate(command);
        var bytes = new byte[CommandSize];
        bytes[0] = 1;
        bytes[1] = (byte)command.Kind;
        bytes[2] = (byte)command.Action;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), command.RequestId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), command.Actor.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), command.Target.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), command.Token);
        command.SessionId.TryWriteBytes(bytes.AsSpan(32, 16));
        return bytes;
    }
    public static CorpseCommand DecodeCommand(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != CommandSize || bytes[0] != 1 || bytes[3] != 0)
            throw new ProtocolException("Invalid corpse command layout.");
        var command = new CorpseCommand(new Guid(bytes[32..48]),
            new NetEntityId(BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..])),
            new NetEntityId(BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..])),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
            (CorpseAction)bytes[2], (CorpseCommandKind)bytes[1],
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]));
        Validate(command);
        return command;
    }
    public static byte[] EncodeReply(CorpseReply reply)
    {
        ValidateReply(reply);
        var bytes = new byte[ReplySize];
        EncodeCommand(reply.Command).CopyTo(bytes, 0);
        bytes[48] = (byte)reply.Decision;
        bytes[49] = reply.HasState ? (byte)1 : (byte)0;
        bytes[50] = (byte)reply.Phase;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(56), reply.StateToken);
        return bytes;
    }
    public static CorpseReply DecodeReply(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ReplySize || bytes[49] > 1 ||
            bytes[51] != 0 || bytes[52] != 0 || bytes[53] != 0 || bytes[54] != 0 || bytes[55] != 0)
            throw new ProtocolException("Invalid corpse reply layout.");
        var reply = new CorpseReply(DecodeCommand(bytes[..CommandSize]),
            (CorpseDecision)bytes[48], bytes[49] != 0, (CorpseUsePhase)bytes[50],
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[56..]));
        ValidateReply(reply);
        return reply;
    }
    public static void Validate(CorpseCommand command)
    {
        if (command.SessionId == Guid.Empty || !command.Actor.IsValid || !command.Target.IsValid ||
            command.Actor == command.Target || command.RequestId == 0 ||
            !Enum.IsDefined(command.Action) || !Enum.IsDefined(command.Kind) ||
            (command.Kind == CorpseCommandKind.Reserve ? command.Token != 0 :
             command.Kind != CorpseCommandKind.Reconcile && command.Token == 0))
            throw new ProtocolException("Invalid corpse command fields.");
    }
    private static void ValidateReply(CorpseReply reply)
    {
        Validate(reply.Command);
        if (!Enum.IsDefined(reply.Decision) || !Enum.IsDefined(reply.Phase) ||
            (reply.HasState ? reply.StateToken == 0 : reply.StateToken != 0 || reply.Phase != CorpseUsePhase.Available) ||
            (reply.Decision is CorpseDecision.Allowed or CorpseDecision.Replayed && !reply.HasState) ||
            (reply.HasState && reply.Command.Kind != CorpseCommandKind.Reserve &&
             !(reply.Command.Kind == CorpseCommandKind.Reconcile && reply.Command.Token == 0) &&
             reply.StateToken != reply.Command.Token))
            throw new ProtocolException("Invalid corpse reply fields.");
    }
}
