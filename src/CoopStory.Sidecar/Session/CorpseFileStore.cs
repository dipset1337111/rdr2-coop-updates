using System.Security.Cryptography;
using System.Text.Json;
using CoopStory.Protocol;

namespace CoopStory.Sidecar.Session;

internal sealed record CorpseRegistrySnapshot(int Version, Guid SessionId,
    NetEntityId Host, NetEntityId Guest, ulong LastToken,
    CorpseUseState[] States, CorpseUseState[] Requests);

internal interface ICorpseStateStore
{
    CorpseRegistrySnapshot? Load();
    void Save(CorpseRegistrySnapshot snapshot);
}

// Local journal only. It does not identify RDR2 save-game bodies across a new
// world/session. Callers must retain the same session and stable entity IDs.
internal sealed class CorpseFileStore : ICorpseStateStore, IDisposable
{
    private sealed record Envelope(int Version, string Payload, string Sha256);
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private readonly string _path;
    private readonly FileStream _lease;
    private bool _allowMissing;

    public CorpseFileStore(string path, bool createNew = false)
    {
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (createNew && File.Exists(_path))
                throw new IOException("A corpse journal already exists; refusing to reset it.");
            if (!createNew && !File.Exists(_path))
                throw new IOException("The corpse journal is missing; explicit creation is required.");
            _allowMissing = createNew;
        }
        catch { _lease.Dispose(); throw; }
    }

    public CorpseRegistrySnapshot? Load()
    {
        ObjectDisposedException.ThrowIf(!_lease.CanRead, this);
        if (_allowMissing && !File.Exists(_path)) return null;
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length == 0 || stream.Length > MaximumFileBytes)
            throw new InvalidDataException("Invalid corpse journal length.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes)
                ?? throw new InvalidDataException("Empty corpse journal.");
            if (envelope.Version != 1) throw new InvalidDataException("Unsupported corpse journal version.");
            var payload = Convert.FromBase64String(envelope.Payload);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(payload)), envelope.Sha256,
                StringComparison.Ordinal)) throw new InvalidDataException("Corpse journal checksum mismatch.");
            return JsonSerializer.Deserialize<CorpseRegistrySnapshot>(payload)
                ?? throw new InvalidDataException("Empty corpse snapshot.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentNullException)
        { throw new InvalidDataException("Malformed corpse journal.", ex); }
    }

    public void Save(CorpseRegistrySnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(!_lease.CanRead, this);
        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1,
            Convert.ToBase64String(payload), Convert.ToHexString(SHA256.HashData(payload))));
        if (bytes.Length > MaximumFileBytes) throw new IOException("Corpse journal is too large.");
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Same-directory replacement: readers see the previous or complete new journal.
            File.Move(temporary, _path, overwrite: true);
            _allowMissing = false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose() => _lease.Dispose();
}
