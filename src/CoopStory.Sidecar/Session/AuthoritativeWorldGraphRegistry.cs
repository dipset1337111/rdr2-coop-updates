using CoopStory.Protocol;

namespace CoopStory.Sidecar.Session;

// Outcome of applying an authoritative host graph frame to the sidecar cache.
// Counters for these results feed the graph/desync diagnostics.
internal enum WorldGraphApplyDisposition
{
    Applied,
    Duplicate,
    Stale,
    CapacityRejected,
    Ignored
}

internal readonly record struct WorldGraphRegistrySnapshot(
    int Nodes,
    int Edges,
    long Revision,
    long Applied,
    long Duplicate,
    long Stale,
    long CapacityRejected,
    long CascadedDespawns);

/// <summary>
/// Sidecar copy of the host-authoritative desired world.
/// It never stores RDR2 handles: only stable NetEntityId nodes and their parent edges.
/// The bridge remains responsible for local ped handles and rendering.
/// </summary>
internal sealed class AuthoritativeWorldGraphRegistry
{
    private sealed record Node(
        WorldEntityStatePayload State,
        uint Sequence,
        ulong Tick);

    // One lock keeps node topology and per-entity sequence tombstones coherent while network reception and a pipe-reconnect replay happen concurrently.
    private readonly object _sync = new();
    private readonly Dictionary<NetEntityId, Node> _nodes = [];
    private readonly Dictionary<NetEntityId, uint> _sequences = [];
    private readonly int _maximumNodes;
    private uint? _discardedHistoryFloor;
    private long _revision;
    private long _applied;
    private long _duplicate;
    private long _stale;
    private long _capacityRejected;
    private long _cascadedDespawns;

    public AuthoritativeWorldGraphRegistry(int maximumNodes = 48)
    {
        if (maximumNodes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        }

        _maximumNodes = maximumNodes;
    }

    // Spawn/update is an upsert; despawn removes its dependency subtree.
    // The cache stores desired wire state only, not native RDR2 proxy handles.
    public WorldGraphApplyDisposition Apply(ProtocolEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        lock (_sync)
        {
            return envelope.Type switch
            {
                MessageType.EntitySpawn or MessageType.EntityUpdate =>
                    ApplyStateLocked(envelope),
                MessageType.EntityDespawn => ApplyDespawnLocked(envelope),
                _ => WorldGraphApplyDisposition.Ignored
            };
        }
    }

    // Reconstruct a reliable reconnect snapshot in parent-first order so the guest bridge can create mount/attachment parents before their children.
    public IReadOnlyList<ProtocolEnvelope> CaptureSpawnSnapshot()
    {
        lock (_sync)
        {
            var order = _nodes.Keys
                .OrderBy(DependencyDepthLocked)
                .ThenBy(static id => id.Value)
                .ToArray();
            var result = new List<ProtocolEnvelope>(order.Length);
            foreach (var entityId in order)
            {
                var node = _nodes[entityId];
                result.Add(new ProtocolEnvelope(
                    MessageType.EntitySpawn,
                    node.Sequence,
                    node.Tick,
                    BinaryPayloadCodec.EncodeWorldEntityState(node.State)));
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _nodes.Clear();
            _sequences.Clear();
            _discardedHistoryFloor = null;
            _revision++;
        }
    }

    public WorldGraphRegistrySnapshot ReadSnapshot()
    {
        lock (_sync)
        {
            return new WorldGraphRegistrySnapshot(
                _nodes.Count,
                _nodes.Values.Count(static node =>
                    node.State.ParentEntityId.IsValid),
                _revision,
                _applied,
                _duplicate,
                _stale,
                _capacityRejected,
                _cascadedDespawns);
        }
    }

    private WorldGraphApplyDisposition ApplyStateLocked(
        ProtocolEnvelope envelope)
    {
        var state = BinaryPayloadCodec.DecodeWorldEntityState(
            envelope.Payload.Span);
        var sequenceDisposition = CheckSequenceLocked(
            state.EntityId,
            envelope.Sequence);
        if (sequenceDisposition is not WorldGraphApplyDisposition.Applied)
        {
            return sequenceDisposition;
        }

        // A child's first packet may arrive after its missing parent's removal.
        // Compare against the parent's retained watermark before admitting it.
        var hasParentSequence = _sequences.TryGetValue(state.ParentEntityId, out var parentSequence);
        var parentFloor = hasParentSequence ? (uint?)parentSequence : _discardedHistoryFloor;
        if (state.ParentEntityId.IsValid &&
            !_nodes.ContainsKey(state.ParentEntityId) &&
            parentFloor is uint removedParentFloor &&
            !SequenceNumber.IsNewer(envelope.Sequence, removedParentFloor))
        {
            _stale++;
            return WorldGraphApplyDisposition.Stale;
        }
        // Match the bridge's fixed graph budget.
        // A beyond-budget host entity is intentionally absent rather than causing an unbounded resync replay.
        if (!_nodes.ContainsKey(state.EntityId) &&
            _nodes.Count >= _maximumNodes)
        {
            _capacityRejected++;
            return WorldGraphApplyDisposition.CapacityRejected;
        }

        // Commit ordering only after admission. A rejected frame must remain
        // retryable when capacity becomes available.
        _sequences[state.EntityId] = envelope.Sequence;
        _nodes[state.EntityId] = new Node(
            state,
            envelope.Sequence,
            envelope.Tick);
        _revision++;
        _applied++;
        TrimTombstonesLocked();
        return WorldGraphApplyDisposition.Applied;
    }

    private WorldGraphApplyDisposition ApplyDespawnLocked(
        ProtocolEnvelope envelope)
    {
        var despawn = BinaryPayloadCodec.DecodeEntityDespawn(
            envelope.Payload.Span);
        var sequenceDisposition = CheckSequenceLocked(
            despawn.EntityId,
            envelope.Sequence);
        if (sequenceDisposition is not WorldGraphApplyDisposition.Applied)
        {
            return sequenceDisposition;
        }

        _sequences[despawn.EntityId] = envelope.Sequence;

        // Parent removal must remove every child and tombstone their sequences, preventing a delayed entity update from recreating a floating child.
        var subtree = DescendantsLocked(despawn.EntityId)
            .OrderByDescending(DependencyDepthLocked)
            .ThenBy(static id => id.Value)
            .ToArray();
        foreach (var entityId in subtree)
        {
            if (entityId != despawn.EntityId)
            {
                if (_nodes.TryGetValue(entityId, out var child) &&
                    SequenceNumber.IsNewer(child.Sequence, envelope.Sequence))
                {
                    // Keep the newer desired child for reconnect replay. The
                    // bridge hides it until its missing parent returns.
                    continue;
                }
                // Parent and child frames can arrive out of order across transports.
                // Preserve a newer child watermark, including across uint wrap.
                if (!_sequences.TryGetValue(entityId, out var previous) ||
                    SequenceNumber.IsNewer(envelope.Sequence, previous))
                {
                    _sequences[entityId] = envelope.Sequence;
                }
                _cascadedDespawns++;
            }
            _nodes.Remove(entityId);
        }

        _revision++;
        _applied++;
        TrimTombstonesLocked();
        return WorldGraphApplyDisposition.Applied;
    }

    private WorldGraphApplyDisposition CheckSequenceLocked(
        NetEntityId entityId,
        uint sequence)
    {
        if (!_sequences.TryGetValue(entityId, out var previous))
        {
            // Apply this floor to both states and removals. Otherwise a late
            // removal could recreate a low watermark for an expired identity.
            if (_discardedHistoryFloor is uint floor &&
                !SequenceNumber.IsNewer(sequence, floor))
            {
                _stale++;
                return WorldGraphApplyDisposition.Stale;
            }
            return WorldGraphApplyDisposition.Applied;
        }
        if (sequence == previous)
        {
            _duplicate++;
            return WorldGraphApplyDisposition.Duplicate;
        }
        if (!SequenceNumber.IsNewer(sequence, previous))
        {
            _stale++;
            return WorldGraphApplyDisposition.Stale;
        }

        return WorldGraphApplyDisposition.Applied;
    }

    private IEnumerable<NetEntityId> DescendantsLocked(NetEntityId root)
    {
        // A pending child may reference a parent whose spawn was lost or delayed.
        var result = new HashSet<NetEntityId> { root };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (entityId, node) in _nodes)
            {
                if (!result.Contains(entityId) &&
                    result.Contains(node.State.ParentEntityId))
                {
                    result.Add(entityId);
                    changed = true;
                }
            }
        }

        return result;
    }

    private int DependencyDepthLocked(NetEntityId entityId)
    {
        var depth = 0;
        var visited = new HashSet<NetEntityId>();
        while (_nodes.TryGetValue(entityId, out var node) &&
               node.State.ParentEntityId.IsValid &&
               visited.Add(entityId) &&
               depth <= _nodes.Count)
        {
            depth++;
            entityId = node.State.ParentEntityId;
        }

        return depth;
    }

    private void TrimTombstonesLocked()
    {
        const int maximumTombstones = 128;
        if (_sequences.Count <= maximumTombstones)
        {
            return;
        }

        foreach (var entityId in _sequences.Keys
                     .Where(id => !_nodes.ContainsKey(id))
                     .Take(_sequences.Count - maximumTombstones)
                     .ToArray())
        {
            var discarded = _sequences[entityId];
            if (_discardedHistoryFloor is not uint previousFloor ||
                SequenceNumber.IsNewer(discarded, previousFloor))
            {
                _discardedHistoryFloor = discarded;
            }
            _sequences.Remove(entityId);
        }
    }
}
