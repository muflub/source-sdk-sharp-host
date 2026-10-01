namespace SourceSharp.Host.Gateway.Relay;

/// <summary>
/// A session's held datagrams (plan §8.1b): bounded by count and bytes, the oldest evicted first,
/// released in arrival order. Holding copies each datagram (the steady path never holds).
/// </summary>
public sealed class HoldBuffer(int maxPackets, int maxBytes)
{
    readonly Queue<byte[]> _q = new();

    public int Count => _q.Count;
    public int Bytes { get; private set; }
    public long Evicted { get; private set; }

    /// <summary>Holds a copy; returns how many older datagrams were evicted to make room.</summary>
    public int Add(ReadOnlySpan<byte> datagram)
    {
        var evicted = 0;
        while (_q.Count > 0 && (_q.Count >= maxPackets || Bytes + datagram.Length > maxBytes))
        {
            Bytes -= _q.Dequeue().Length;
            evicted++;
        }
        if (datagram.Length > maxBytes) { Evicted += evicted + 1; return evicted + 1; }
        _q.Enqueue(datagram.ToArray());
        Bytes += datagram.Length;
        Evicted += evicted;
        return evicted;
    }

    /// <summary>Everything held, oldest first; the buffer is left empty.</summary>
    public List<byte[]> Drain()
    {
        var all = _q.ToList();
        _q.Clear();
        Bytes = 0;
        return all;
    }
}
