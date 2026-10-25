using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace SelfAvoidingPaths;

/// <summary>
/// One frontier layer: a hash-sharded map from a fixed-width byte key (the
/// encoded mate-state) to a path count.
///
/// Two profiling-driven choices:
///   1. Keys are probed as a <see cref="ReadOnlySpan{Byte}"/> and copied into a
///      contiguous arena only on a genuine insert — no per-probe key allocation.
///   2. Counts are stored as fixed-width little-endian <c>ulong</c> limbs inline
///      in a second arena and added in place, so a merge allocates nothing and
///      there is no per-value heap object (unlike <see cref="BigInteger"/>,
///      whose magnitude is a separate <c>uint[]</c>). The final count is
///      converted to <see cref="BigInteger"/> exactly once, at the end.
/// Each shard has its own lock, so concurrent inserts contend only within a shard.
/// </summary>
public sealed class StateStore
{
    public const int ShardCount = 256;   // power of two; low 8 hash bits select shard
    private const int ShardMask = ShardCount - 1;

    // Array pooling recycles shard arenas (a released source shard returns its
    // arrays and the growing next layer rents them back), which slashes the
    // per-layer allocation churn and GC time. The catch: pooled memory stays
    // resident, so it partially undoes the early-shard-free peak reduction.
    // Hence it is a TRADE-OFF, off by default:
    //   * off  -> memory-lean: lowest peak working set (best for reaching high n)
    //   * on   -> fast: ~1.4x faster, but ~2x peak working set
    public static bool PoolingEnabled;

    private static readonly ArrayPool<byte> BytePool = ArrayPool<byte>.Shared;
    private static readonly ArrayPool<ulong> UlongPool = ArrayPool<ulong>.Shared;
    private static readonly ArrayPool<int> IntPool = ArrayPool<int>.Shared;

    private static byte[] RentBytes(int n) => PoolingEnabled ? BytePool.Rent(n) : new byte[n];
    private static ulong[] RentUlongs(int n) => PoolingEnabled ? UlongPool.Rent(n) : new ulong[n];
    private static int[] RentInts(int n) => PoolingEnabled ? IntPool.Rent(n) : new int[n];
    private static void ReturnBytes(byte[] a) { if (PoolingEnabled && a.Length > 0) BytePool.Return(a); }
    private static void ReturnUlongs(ulong[] a) { if (PoolingEnabled) UlongPool.Return(a); }
    private static void ReturnInts(int[] a) { if (PoolingEnabled) IntPool.Return(a); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int NextPow2(int x)
    {
        if (x < 2) return 2;
        x--; x |= x >> 1; x |= x >> 2; x |= x >> 4; x |= x >> 8; x |= x >> 16;
        return x + 1;
    }

    public readonly int KeyWidth;
    public readonly int ValWidth;        // number of 64-bit limbs per count
    public readonly Shard[] Shards;

    public StateStore(int keyWidth, int valWidth, int initCapPerShard = 8)
    {
        KeyWidth = keyWidth;
        ValWidth = valWidth;
        Shards = new Shard[ShardCount];
        for (int s = 0; s < ShardCount; s++) Shards[s] = new Shard(keyWidth, valWidth, initCapPerShard);
    }

    public long Count
    {
        get
        {
            long n = 0;
            foreach (var sh in Shards) n += sh?.Count ?? 0;
            return n;
        }
    }

    /// <summary>FNV-1a over the key, then avalanche so the low 8 bits mix well.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Hash(ReadOnlySpan<byte> key)
    {
        ulong h = 1469598103934665603UL;
        for (int i = 0; i < key.Length; i++) { h ^= key[i]; h *= 1099511628211UL; }
        h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33;
        return h;
    }

    /// <summary>Add the multi-limb count <paramref name="cnt"/> to <paramref name="key"/>'s entry.</summary>
    public void Accumulate(ReadOnlySpan<byte> key, ReadOnlySpan<ulong> cnt)
    {
        ulong h = Hash(key);
        Shard sh = Shards[(int)(h & ShardMask)];
        lock (sh.Sync) sh.Add(key, cnt, h);
    }

    /// <summary>Release a shard once fully consumed: return its arrays, then drop it.</summary>
    public void ReleaseShard(int s)
    {
        Shards[s]?.Release();
        Shards[s] = null!;
    }

    /// <summary>Ripple-carry add: dst += src (equal length). Throws on overflow.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddInPlace(Span<ulong> dst, ReadOnlySpan<ulong> src)
    {
        ulong carry = 0;
        for (int i = 0; i < dst.Length; i++)
        {
            UInt128 s = (UInt128)dst[i] + src[i] + carry;
            dst[i] = (ulong)s;
            carry = (ulong)(s >> 64);
        }
        if (carry != 0)
            throw new OverflowException(
                "Count exceeded the limb width; increase FrontierCounter value-width margin.");
    }

    public static BigInteger ToBigInteger(ReadOnlySpan<ulong> limbs)
    {
        BigInteger r = BigInteger.Zero;
        for (int i = limbs.Length - 1; i >= 0; i--) r = (r << 64) | limbs[i];
        return r;
    }

    /// <summary>One shard: open-addressing table over shared key + value arenas.</summary>
    public sealed class Shard
    {
        public readonly object Sync = new();
        private readonly int _keyWidth;
        private readonly int _valWidth;
        public byte[] Keys;        // >= Count * KeyWidth, inline keys (pool-rented)
        public ulong[] Vals;       // >= Count * ValWidth, inline counts (pool-rented)
        public int Count;
        private int _cap;          // capacity (power of two)
        private int[] _slots;      // open addressing: entryIndex+1, 0 = empty (pool-rented)
        private int _slotLen;      // logical slot count (power of two); array may be longer
        private int _slotMask;

        public Shard(int keyWidth, int valWidth, int initCap = 8)
        {
            _keyWidth = keyWidth;
            _valWidth = valWidth;
            _cap = NextPow2(Math.Max(8, initCap));
            Keys = RentBytes(_cap * keyWidth);
            Vals = RentUlongs(_cap * valWidth);
            _slotLen = NextPow2(_cap * 2);
            _slots = RentInts(_slotLen);
            Array.Clear(_slots, 0, _slotLen); // rented arrays may hold stale data
            _slotMask = _slotLen - 1;
            Count = 0;
        }

        /// <summary>Return the arenas to their pools (or drop for GC). Shard unusable after.</summary>
        public void Release()
        {
            if (Keys is not null) ReturnBytes(Keys);
            if (Vals is not null) ReturnUlongs(Vals);
            if (_slots is not null) ReturnInts(_slots);
            Keys = null!; Vals = null!; _slots = null!;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<ulong> ValAt(int idx) => Vals.AsSpan(idx * _valWidth, _valWidth);

        // Caller holds Sync.
        public void Add(ReadOnlySpan<byte> key, ReadOnlySpan<ulong> cnt, ulong hash)
        {
            int slot = (int)(hash >> 8) & _slotMask;
            while (true)
            {
                int e = _slots[slot];
                if (e == 0) // empty slot -> key not present -> insert
                {
                    if (Count == _cap) { Grow(); InsertNew(key, cnt, hash); return; }
                    InsertAt(Count, slot, key, cnt);
                    return;
                }
                int ei = e - 1;
                if (key.SequenceEqual(Keys.AsSpan(ei * _keyWidth, _keyWidth)))
                {
                    AddInPlace(Vals.AsSpan(ei * _valWidth, _valWidth), cnt);
                    return;
                }
                slot = (slot + 1) & _slotMask;
            }
        }

        private void InsertAt(int idx, int slot, ReadOnlySpan<byte> key, ReadOnlySpan<ulong> cnt)
        {
            key.CopyTo(Keys.AsSpan(idx * _keyWidth, _keyWidth));
            cnt.CopyTo(Vals.AsSpan(idx * _valWidth, _valWidth));
            _slots[slot] = idx + 1;
            Count++;
        }

        // Re-probe for a fresh slot after a resize (the key is known absent).
        private void InsertNew(ReadOnlySpan<byte> key, ReadOnlySpan<ulong> cnt, ulong hash)
        {
            int slot = (int)(hash >> 8) & _slotMask;
            while (_slots[slot] != 0) slot = (slot + 1) & _slotMask;
            InsertAt(Count, slot, key, cnt);
        }

        private void Grow()
        {
            int newCap = _cap * 2;

            byte[] newKeys = RentBytes(newCap * _keyWidth);
            Array.Copy(Keys, newKeys, Count * _keyWidth);
            ReturnBytes(Keys);
            Keys = newKeys;

            ulong[] newVals = RentUlongs(newCap * _valWidth);
            Array.Copy(Vals, newVals, Count * _valWidth);
            ReturnUlongs(Vals);
            Vals = newVals;

            _cap = newCap;
            _slotLen = NextPow2(newCap * 2);
            int[] newSlots = RentInts(_slotLen);
            Array.Clear(newSlots, 0, _slotLen);
            ReturnInts(_slots);
            _slots = newSlots;
            _slotMask = _slotLen - 1;

            for (int i = 0; i < Count; i++) // reinsert existing entries into fresh slots
            {
                ulong h = Hash(Keys.AsSpan(i * _keyWidth, _keyWidth));
                int slot = (int)(h >> 8) & _slotMask;
                while (_slots[slot] != 0) slot = (slot + 1) & _slotMask;
                _slots[slot] = i + 1;
            }
        }
    }
}
