namespace ERCTelemetry.Core.Session;

/// <summary>Append-only, capped list of race-control events. Owned by the session store
/// (single writer); snapshots expose the newest entries. Every appended entry gets a
/// monotonically increasing sequence number that survives session resets, giving
/// consumers a reliable "have I seen this?" identity.</summary>
public sealed class EventFeed
{
    private const int Capacity = 200;

    private readonly Queue<RaceEventEntry> _entries = new();
    private long _sequence;

    /// <summary>Stamps the entry with the next sequence number, appends it and returns
    /// the sequenced entry.</summary>
    public RaceEventEntry Add(RaceEventEntry entry)
    {
        var sequenced = entry with { Sequence = ++_sequence };
        _entries.Enqueue(sequenced);
        while (_entries.Count > Capacity)
        {
            _entries.Dequeue();
        }

        return sequenced;
    }

    public IReadOnlyList<RaceEventEntry> Latest(int count)
    {
        if (_entries.Count == 0)
        {
            return Array.Empty<RaceEventEntry>();
        }

        var take = Math.Min(count, _entries.Count);
        return _entries.Skip(_entries.Count - take).ToArray();
    }

    /// <summary>Clears stored entries (session change). Sequence numbers keep increasing
    /// so consumers never see an old number repeat.</summary>
    public void Clear() => _entries.Clear();
}