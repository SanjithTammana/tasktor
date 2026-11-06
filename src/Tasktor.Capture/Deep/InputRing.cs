using System.Runtime.InteropServices;

namespace Tasktor.Capture.Deep;

/// <summary>
/// C §4 <c>InputRing</c>: the only thing the low-level hook callbacks touch. A fixed, pinned, allocation-free
/// single-producer / single-consumer ring of 65,536 <see cref="RawInput"/>s. The writer (hook thread) never blocks and
/// never waits for the reader: when the ring is full it overwrites the oldest entries, and the reader counts what it
/// lost in <see cref="Dropped"/> (reported on the card). Each slot carries a sequence stamp, so the reader detects an
/// entry overwritten while it was being copied and never returns a torn one.
/// </summary>
public sealed class InputRing
{
    public const int DefaultCapacity = 65_536;

    private readonly Slot[] _slots;
    private readonly long _mask;
    private long _head; // next sequence to write; only the writer stores it
    private long _read; // next sequence to read; only the reader touches it

    public InputRing(int capacity = DefaultCapacity)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be a power of two ≥ 2");
        }

        _slots = GC.AllocateArray<Slot>(capacity, pinned: true); // the hook writes into memory the GC never moves
        _mask = capacity - 1;
        for (var i = 0; i < capacity; i++)
        {
            _slots[i].Stamp = -1;
        }
    }

    public int Capacity => _slots.Length;

    /// <summary>Entries lost to overwrite before the reader got to them (reader-side count).</summary>
    public long Dropped { get; private set; }

    /// <summary>Hook thread only. O(1), no allocation, no lock, no wait.</summary>
    public void Write(in RawInput input)
    {
        var seq = _head;
        ref var slot = ref _slots[seq & _mask];
        Volatile.Write(ref slot.Stamp, -1); // "being written": a reader copying this slot now will retry or drop it
        Interlocked.MemoryBarrier(); // the value store must not become visible before the stamp (weak memory, ARM64)
        slot.Value = input;
        Volatile.Write(ref slot.Stamp, seq);
        Volatile.Write(ref _head, seq + 1);
    }

    /// <summary>Logic thread only. Copies up to <c>into.Length</c> entries in order; returns how many.</summary>
    public int Drain(Span<RawInput> into)
    {
        var head = Volatile.Read(ref _head);
        if (head - _read > _slots.Length)
        {
            Dropped += head - _slots.Length - _read; // lapped: the oldest are gone
            _read = head - _slots.Length;
        }

        var n = 0;
        while (n < into.Length && _read < head)
        {
            ref var slot = ref _slots[_read & _mask];
            var before = Volatile.Read(ref slot.Stamp);
            var value = slot.Value;
            Interlocked.MemoryBarrier();
            var after = Volatile.Read(ref slot.Stamp);
            if (before != _read || after != _read)
            {
                // The writer lapped this slot while we read: everything up to the writer's current position minus one
                // ring is lost. Resynchronise there and keep going.
                var now = Volatile.Read(ref _head);
                var resume = Math.Max(_read + 1, now - _slots.Length + 1);
                Dropped += resume - _read;
                _read = resume;
                head = now;
                continue;
            }

            into[n++] = value;
            _read++;
        }

        return n;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Slot
    {
        public long Stamp;
        public RawInput Value;
    }
}
