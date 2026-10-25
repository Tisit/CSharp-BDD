using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SelfAvoidingPaths;

/// <summary>Block-wise raw I/O for large primitive arrays (past the 2 GB span limit).</summary>
internal static class ArrayIO
{
    private const int Block = 1 << 24;   // elements per Write/Read call

    public static void Write<T>(T[] a, Stream s) where T : unmanaged
    {
        for (int off = 0; off < a.Length; off += Block)
        {
            int cnt = Math.Min(Block, a.Length - off);
            s.Write(MemoryMarshal.AsBytes(a.AsSpan(off, cnt)));
        }
    }

    public static void Read<T>(T[] a, Stream s) where T : unmanaged
    {
        for (int off = 0; off < a.Length; off += Block)
        {
            int cnt = Math.Min(Block, a.Length - off);
            Span<byte> bytes = MemoryMarshal.AsBytes(a.AsSpan(off, cnt));
            int got = 0;
            while (got < bytes.Length)
            {
                int k = s.Read(bytes[got..]);
                if (k <= 0) throw new EndOfStreamException("truncated checkpoint");
                got += k;
            }
        }
    }
}

// Dense (GGCount-style) engine — a C# port of the method that holds the
// A007764 record (n = 26, 27):
//
//   H. Iwashita, Y. Nakazawa, J. Kawahara, T. Uno, S. Minato,
//   "Efficient Computation of the Number of Paths in a Grid Graph with
//   Minimal Perfect Hash Functions", TCS-TR-A-13-64, Hokkaido Univ., 2013.
//   Reference implementation: https://github.com/kunisura/GGCount
//
// Key ideas (vs. the hash-table frontier engine in FrontierCounter):
//  * A mate state is a noncrossing parenthesis string ('.', '(', ')') over
//    the frontier. All *valid* strings are ranked into consecutive integers
//    0..codeSize-1 by precomputed Motzkin-number code tables — a minimal
//    perfect hash: encode(mate) = codeLR[leftHalf] + codeR[rightHalf].
//  * Counts live in one dense array indexed by rank: no keys, no hashing,
//    no dedup (duplicate children are literally the same array slot).
//  * The array is updated IN PLACE, one grid vertex at a time, with a small
//    width-(w-1) "deferred" side array — peak memory is one layer, not two.
//  * Lock-free parallelism: codes are grouped by the '.'-vs-paren pattern of
//    the left-part positions; one vertex update never crosses groups
//    (transitions flip '(' <-> ')' but never paren <-> '.', except at the
//    two "ungrouped" positions around the current vertex).
//  * Counts are pluggable: fixed-width limbs (exact) or a single ulong
//    modulo m (8 bytes/state; m = 2^64 is free wraparound) — combine runs
//    over coprime moduli with the Chinese Remainder Theorem for the exact
//    value at a fraction of the RAM.

internal enum MV : byte { N = 0, R = 1, L = 2, X = 3 }   // '.', ')', '(', unused

/// <summary>Mate string, 2 bits per frontier position, position 0 = rightmost.</summary>
internal struct Mate(ulong id)
{
    public ulong Id = id;

    public static Mate At(int k, MV u) => new((ulong)u << (2 * k));

    public readonly MV Get(int k) => (MV)((Id >> (2 * k)) & 3);
    public void Set(int k, MV v) => Id = (Id & ~(3UL << (2 * k))) | ((ulong)v << (2 * k));

    /// <summary>Pair (position k, position k-1) as a 4-bit value; high 2 bits = k.</summary>
    public readonly int GetPair(int k) => (int)((Id >> (2 * (k - 1))) & 0xF);
    public void SetPair(int k, int w) => Id = (Id & ~(0xFUL << (2 * (k - 1)))) | ((ulong)w << (2 * (k - 1)));

    /// <summary>Remove position k (higher positions shift down one slot).</summary>
    public readonly Mate Shrink(int k)
    {
        ulong m = (1UL << (2 * k)) - 1;
        return new(((Id & ~m) >> 2) | (Id & m));
    }

    public readonly Mate GetRight(int k) => new(Id & ((1UL << (2 * k)) - 1));
    public readonly Mate ShiftLeft(int k) => new(Id << (2 * k));
    public readonly Mate ShiftRight(int k) => new(Id >> (2 * k));

    public static Mate operator |(Mate a, Mate b) => new(a.Id | b.Id);
}

// Pair constants (high 2 bits = position p, low 2 = position p-1).
internal static class MP
{
    public const int NN = 0x0, NR = 0x1, NL = 0x2;
    public const int RN = 0x4, RR = 0x5, RL = 0x6;
    public const int LN = 0x8, LR = 0x9, LL = 0xA;
}

/// <summary>
/// Minimal perfect hash for mate strings of a fixed width: ranks every valid
/// noncrossing string (left boundary height hL, right boundary height hR)
/// into consecutive integers. Split into left/right halves so the raw-ID
/// lookup tables stay small: encode(m) = codeLR[leftId] + codeR[rightId].
/// </summary>
internal sealed class MateCodec
{
    internal struct CodeTable
    {
        public long Base;      // first code of this left-mate's block
        public long Size;      // number of right mates in the block
        public Mate MateL;     // left half, pre-shifted into full-width position
        public Mate[] MateR;   // the right-mate list shared by all lefts landing at this height
        public int Height;     // split height h: MateR == RightBlock(h)
    }

    private readonly int _wL, _wR, _hL, _hR, _hC;
    private long _codeSize, _codeSizeL;
    private readonly long[] _codeLR;    // left raw id -> block base code
    private readonly long[] _codeL;     // left raw id -> left index (into _codeTable)
    private readonly long[] _codeR;     // right raw id -> offset within its height block
    private readonly Mate[][] _rightBlock;   // per split-height: all right mates
    private readonly long[] _rightFill;      // fill cursor per height block
    private readonly CodeTable[] _codeTable;

    public int LeftWidth => _wL;
    public int RightWidth => _wR;
    public int LeftHeight => _hL;
    public int CenterHeight => _hC;
    public long CodeSize => _codeSize;
    public long CodeSizeL => _codeSizeL;

    public MateCodec(int width, int rightWidth, int leftHeight, int rightHeight)
    {
        if (width < 1) throw new ArgumentOutOfRangeException(nameof(width));
        _wR = (rightWidth < 1) ? 1 : Math.Min(width, rightWidth);
        _wL = width - _wR;
        _hL = leftHeight;
        _hR = rightHeight;
        _hC = Math.Min(_hL + _wL, _hR + _wR);

        long mateSizeL = (_wL >= 1) ? 3L << (2 * (_wL - 1)) : 1;
        long mateSizeR = 3L << (2 * (_wR - 1));
        _codeLR = new long[mateSizeL];
        _codeL = new long[mateSizeL];
        _codeR = new long[mateSizeR];

        _rightBlock = new Mate[_hC + 1][];
        _rightFill = new long[_hC + 1];
        for (int h = 0; h <= _hC; h++)
        {
            _rightBlock[h] = new Mate[Motzkin(_wR, h, _hR)];
            FillCodeR(h, _wR, h, _hR, default);
        }

        long nLeft = 0;
        for (int h = 0; h <= _hC; h++) nLeft += Motzkin(_wL, _hL, h);
        _codeTable = new CodeTable[nLeft];
        FillCodeL(_wL, _hL, default);
    }

    public ref readonly CodeTable Table(long i) => ref _codeTable[i];
    /// <summary>Highest split height (right-mate lists exist for 0..MaxSplitHeight).</summary>
    public int MaxSplitHeight => _hC;
    public Mate[] RightBlock(int h) => _rightBlock[h];

    public long Encode(in Mate mate)
    {
        ulong mL = mate.ShiftRight(_wR).Id;
        ulong mR = mate.GetRight(_wR).Id;
        return _codeLR[(long)mL] + _codeR[(long)mR];
    }

    public long EncodeL(in Mate mateL) => _codeL[(long)mateL.Id];

    // Raw halves of Encode, for callers that can hoist the block-invariant part
    // out of an inner loop: Encode(m) == CodeLRAt(leftRaw) + CodeRAt(rightRaw)
    // where leftRaw = m.Id >> 2*RightWidth and rightRaw = m.Id & rightMask.
    public long CodeLRAt(ulong leftRaw) => _codeLR[(long)leftRaw];
    public long CodeRAt(ulong rightRaw) => _codeR[(long)rightRaw];

    /// <summary>Motzkin-style path count: width w, height h1 -> h2, staying >= 0.</summary>
    private static readonly Dictionary<(int, int, int), long> MotzkinCache = new();

    private static long Motzkin(int w, int h1, int h2)
    {
        if (w == 0 && h1 == h2) return 1;
        if (w <= 0 || h1 < 0 || h2 < 0) return 0;
        if (w < Math.Abs(h1 - h2)) return 0;
        if (MotzkinCache.TryGetValue((w, h1, h2), out long m)) return m;
        m = Motzkin(w - 1, h1 + 1, h2) + Motzkin(w - 1, h1, h2) + Motzkin(w - 1, h1 - 1, h2);
        MotzkinCache[(w, h1, h2)] = m;
        return m;
    }

    // Enumerate all right-half strings landing block h; record each mate's
    // offset within its block (the low part of the perfect hash).
    private void FillCodeR(int h, int w, int h1, int h2, Mate mR)
    {
        if (w == 0 && h1 == h2)
        {
            _codeR[(long)mR.Id] = _rightFill[h];
            _rightBlock[h][_rightFill[h]++] = mR;
            return;
        }
        if (w <= 0 || h1 < 0 || h2 < 0) return;
        if (w < Math.Abs(h1 - h2)) return;

        w--;
        FillCodeR(h, w, h1, h2, mR);
        mR.Set(w, MV.R);
        FillCodeR(h, w, h1 - 1, h2, mR);
        mR.Set(w, MV.L);
        FillCodeR(h, w, h1 + 1, h2, mR);
    }

    // Enumerate all left-half strings; assign each a contiguous code block
    // sized by the right-mate list at its landing height. Block bases are
    // padded to EVEN counter indices so that two counters from different
    // blocks never share a byte under 12-bit packed storage (adjacent 12-bit
    // counters share a middle byte; within a block all writes come from one
    // worker, so only cross-block adjacency could race).
    private void FillCodeL(int w, int h, Mate mL)
    {
        if (w == 0 && 0 <= h && h <= _hC)
        {
            if ((_codeSize & 1) != 0) _codeSize++;   // even-align the block base
            Mate[] block = _rightBlock[h];
            _codeTable[_codeSizeL] = new CodeTable
            {
                Base = _codeSize,
                Size = block.Length,
                MateL = mL.ShiftLeft(_wR),
                MateR = block,
                Height = h,
            };
            _codeLR[(long)mL.Id] = _codeSize;
            _codeSize += block.Length;
            _codeL[(long)mL.Id] = _codeSizeL++;
            return;
        }
        if (w <= 0 || h < 0 || w < h - _hC) return;

        w--;
        FillCodeL(w, h, mL);
        mL.Set(w, MV.R);
        FillCodeL(w, h - 1, mL);
        mL.Set(w, MV.L);
        FillCodeL(w, h + 1, mL);
    }
}

/// <summary>
/// Pluggable count arithmetic over an opaque counter store (flat array,
/// chunked array, or bit-packed chunks). Counter index i is a logical index;
/// the store's layout is the arith's own business. Scratch transfers use a
/// small ulong[TmpLen] buffer.
/// </summary>
internal interface IArith<TStore> where TStore : class
{
    /// <summary>ulongs of scratch needed to hold one counter</summary>
    int TmpLen { get; }
    TStore Allocate(long counters);
    /// <summary>dst[di] += src[si]</summary>
    void Add(TStore dst, long di, TStore src, long si);
    bool IsZero(TStore a, long i);
    /// <summary>tmp = a[i]</summary>
    void Save(TStore a, long i, ulong[] tmp);
    /// <summary>a[i] = tmp</summary>
    void Restore(ulong[] tmp, TStore a, long i);
    void SetZero(TStore a, long i);
    /// <summary>set counter i to the integer 1</summary>
    void SetOne(TStore a, long i);

    // Scalar fast path (counter fits one ulong): load/store a counter as a
    // plain value so a multi-step update decodes and encodes each counter once.
    // Only valid when Scalar is true (a JIT-time constant per arith struct).
    bool Scalar { get; }
    ulong Load(TStore a, long i);
    void Store(TStore a, long i, ulong v);
    /// <summary>(x + y) mod m for reduced x, y</summary>
    ulong Sum(ulong x, ulong y);
    /// <summary>Dump the raw store to a stream (for checkpointing).</summary>
    void Serialize(TStore a, Stream s);
    /// <summary>Reload a store previously written by <see cref="Serialize"/>.</summary>
    void Deserialize(TStore a, Stream s);
}

/// <summary>Single-ulong counts mod m; m == 0 means mod 2^64 (free wraparound).</summary>
internal readonly struct Mod64Arith(ulong modulus) : IArith<ulong[]>
{
    private readonly ulong _m = modulus;
    public int TmpLen => 1;
    public ulong[] Allocate(long counters) => new ulong[counters];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(ulong[] dst, long di, ulong[] src, long si)
    {
        ulong b = src[si];
        ulong s = unchecked(dst[di] + b);
        if (_m == 0) { dst[di] = s; return; }   // 2^64: free wraparound (uniform, predictable)
        // Branch-free reduction (see Mod32Arith.Add): subtract m iff s >= m or the add wrapped.
        ulong wrap = (ulong)-(long)(s < b ? 1UL : 0UL) | (ulong)-(long)(s >= _m ? 1UL : 0UL);
        dst[di] = s - (_m & wrap);
    }

    public bool IsZero(ulong[] a, long i) => a[i] == 0;
    public void Save(ulong[] a, long i, ulong[] tmp) => tmp[0] = a[i];
    public void Restore(ulong[] tmp, ulong[] a, long i) => a[i] = tmp[0];
    public void SetZero(ulong[] a, long i) => a[i] = 0;
    public void SetOne(ulong[] a, long i) => a[i] = 1;
    public bool Scalar => true;
    public ulong Load(ulong[] a, long i) => a[i];
    public void Store(ulong[] a, long i, ulong v) => a[i] = v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Sum(ulong x, ulong y)
    {
        ulong s = unchecked(x + y);
        if (_m == 0) return s;
        ulong wrap = (ulong)-(long)(s < y ? 1UL : 0UL) | (ulong)-(long)(s >= _m ? 1UL : 0UL);
        return s - (_m & wrap);
    }
    public void Serialize(ulong[] a, Stream s) => ArrayIO.Write(a, s);
    public void Deserialize(ulong[] a, Stream s) => ArrayIO.Read(a, s);
}

/// <summary>Single-uint counts mod m; m == 0 means mod 2^32 (free wraparound). Half the RAM of Mod64.</summary>
internal readonly struct Mod32Arith(uint modulus) : IArith<uint[]>
{
    // Modulus widened to 64 bits (0 -> 2^32) so the reduction is one uniform
    // branch-free form: with residues uniform in [0, m) the "sum >= m" test is
    // a coin flip, and as a branch it mispredicted ~half the time (a prime
    // modulus swept 1.7x slower than 2^32 before this).
    private readonly ulong _m = modulus == 0 ? 1UL << 32 : modulus;
    public int TmpLen => 1;
    public uint[] Allocate(long counters) => new uint[counters];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(uint[] dst, long di, uint[] src, long si)
    {
        ulong t = (ulong)dst[di] + src[si] - _m;              // in [-m, m): wraps below zero
        dst[di] = (uint)(t + (_m & (ulong)((long)t >> 63)));  // add m back iff negative
    }

    public bool IsZero(uint[] a, long i) => a[i] == 0;
    public void Save(uint[] a, long i, ulong[] tmp) => tmp[0] = a[i];
    public void Restore(ulong[] tmp, uint[] a, long i) => a[i] = (uint)tmp[0];
    public void SetZero(uint[] a, long i) => a[i] = 0;
    public void SetOne(uint[] a, long i) => a[i] = 1;
    public bool Scalar => true;
    public ulong Load(uint[] a, long i) => a[i];
    public void Store(uint[] a, long i, ulong v) => a[i] = (uint)v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Sum(ulong x, ulong y)
    {
        ulong t = x + y - _m;
        return t + (_m & (ulong)((long)t >> 63));
    }
    public void Serialize(uint[] a, Stream s) => ArrayIO.Write(a, s);
    public void Deserialize(uint[] a, Stream s) => ArrayIO.Read(a, s);
}

/// <summary>
/// One flat native allocation of packed 12-bit counters (counter i in the 16
/// bits at byte (i*3)/2, shifted up 4 when i is odd). Native because a single
/// .NET array is capped near 2 GB, and n=24 needs ~11 GB; flat (rather than
/// chunked) so every access is plain address arithmetic with no per-access
/// chunk lookup. Zero-initialized. Freed deterministically by Dispose (the
/// engine disposes its stores at the end of a sweep), with a finalizer backstop.
/// </summary>
internal sealed unsafe class Packed12Store : IDisposable
{
    public readonly long Counters;
    public readonly long Bytes;
    private byte* _p;

    public Packed12Store(long counters)
    {
        Counters = counters;
        Bytes = (counters * 3 + 1) / 2 + 1;    // +1 so the u16 access of the last counter stays in bounds
        // +32 slack (not part of Bytes/serialization): Packed12Simd.Unpack reads a
        // few bytes past a 16-counter chunk.
        _p = (byte*)NativeMemory.AllocZeroed((nuint)(Bytes + 32));
        GC.AddMemoryPressure(Bytes);
    }

    public byte* Ptr => _p;

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    ~Packed12Store() => Free();

    private void Free()
    {
        if (_p == null) return;
        NativeMemory.Free(_p);
        _p = null;
        GC.RemoveMemoryPressure(Bytes);
    }
}

/// <summary>
/// 12-bit packed counts mod m (m == 0 means mod 4096), 1.5 bytes per counter —
/// the narrowest storage with enough pairwise-coprime moduli (odd values near
/// 4096) to cover 100+ digit answers. Stored in one flat native block (see
/// <see cref="Packed12Store"/>), so the counter count may exceed .NET's
/// single-array limit (required for n >= 24). Thread safety relies on the
/// codecs' even-aligned block bases: counters that share a byte are always in
/// the same block, and one block is only ever touched by one worker in a
/// vertex update.
/// </summary>
internal readonly unsafe struct Mod12Arith(uint modulus) : IArith<Packed12Store>
{
    private readonly uint _m = modulus == 0 ? 4096u : modulus;
    public uint M => _m;
    public int TmpLen => 1;

    public Packed12Store Allocate(long counters) => new(counters);

    // Each access is ONE unaligned little-endian u16 load/store (x64) at the
    // counter's byte offset; the two bytes touched are exactly the two the
    // counter spans, so the even-aligned-block race argument is unchanged.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte* Slot(Packed12Store a, long i, out int sh)
    {
        if (BoundsChecks && (ulong)i >= (ulong)a.Counters) ThrowIndex();
        sh = ((int)i & 1) << 2;
        return a.Ptr + ((i * 3) >> 1);
    }

    private static void ThrowIndex() => throw new IndexOutOfRangeException("12-bit counter index out of range");

    // Off by default: the checks cost instructions on every packed access, and
    // the sweep is instruction-bound. Indices come from the codecs' perfect
    // hash, which the selftests cover; SAP_BOUNDS=1 turns the checks back on
    // (a static readonly bool is a JIT-time constant, so off means no code).
    private static readonly bool BoundsChecks = Environment.GetEnvironmentVariable("SAP_BOUNDS") == "1";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Get(Packed12Store a, long i)
    {
        byte* r = Slot(a, i, out int sh);
        return ((uint)Unsafe.ReadUnaligned<ushort>(r) >> sh) & 0xFFF;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Set(Packed12Store a, long i, uint v)
    {
        byte* r = Slot(a, i, out int sh);
        uint cur = Unsafe.ReadUnaligned<ushort>(r);
        Unsafe.WriteUnaligned(r, (ushort)((cur & ~(0xFFFu << sh)) | (v << sh)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(Packed12Store dst, long di, Packed12Store src, long si)
    {
        uint b = Get(src, si);
        byte* r = Slot(dst, di, out int sh);            // destination addressed once for read + write
        uint cur = Unsafe.ReadUnaligned<ushort>(r);
        // both < m <= 4096; branch-free reduction (see Mod32Arith.Add)
        int t = (int)(((cur >> sh) & 0xFFF) + b) - (int)_m;
        uint sum = (uint)(t + ((int)_m & (t >> 31)));
        Unsafe.WriteUnaligned(r, (ushort)((cur & ~(0xFFFu << sh)) | (sum << sh)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsZero(Packed12Store a, long i) => Get(a, i) == 0;
    public void Save(Packed12Store a, long i, ulong[] tmp) => tmp[0] = Get(a, i);
    public void Restore(ulong[] tmp, Packed12Store a, long i) => Set(a, i, (uint)tmp[0]);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetZero(Packed12Store a, long i) => Set(a, i, 0);
    public void SetOne(Packed12Store a, long i) => Set(a, i, 1);
    public bool Scalar => true;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Load(Packed12Store a, long i) => Get(a, i);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(Packed12Store a, long i, ulong v) => Set(a, i, (uint)v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Sum(ulong x, ulong y)
    {
        int t = (int)x + (int)y - (int)_m;
        return (uint)(t + ((int)_m & (t >> 31)));
    }

    private const int IoBlock = 1 << 26;

    public void Serialize(Packed12Store a, Stream s)
    {
        for (long k = 0; k < a.Bytes; k += IoBlock)
            s.Write(new ReadOnlySpan<byte>(a.Ptr + k, (int)Math.Min(IoBlock, a.Bytes - k)));
    }

    public void Deserialize(Packed12Store a, Stream s)
    {
        for (long k = 0; k < a.Bytes; k += IoBlock)
            s.ReadExactly(new Span<byte>(a.Ptr + k, (int)Math.Min(IoBlock, a.Bytes - k)));
    }
}

/// <summary>
/// AVX2 bulk conversion between packed 12-bit counters and plain 16-bit
/// values, for contiguous counter ranges that start at an EVEN counter index
/// (all block bases are even), so 16-counter chunks are 24-byte aligned runs.
/// Unpack reads up to 4 bytes past a chunk (the store keeps 32 bytes of
/// slack); Pack writes exactly the range's bytes (8+4-byte stores, scalar
/// tail), never a neighbour's.
/// </summary>
internal static unsafe class Packed12Simd
{
    public static readonly bool Supported = Avx2.IsSupported;

    private static readonly Vector256<byte> UnpackShuffle =
        Vector256.Create((byte)0, 1, 1, 2, 3, 4, 4, 5, 6, 7, 7, 8, 9, 10, 10, 11,
                                                   0, 1, 1, 2, 3, 4, 4, 5, 6, 7, 7, 8, 9, 10, 10, 11);
    private static readonly Vector256<byte> PackShuffle =
        Vector256.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80,
                                                   0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Get(byte* b, long i) => ((uint)Unsafe.ReadUnaligned<ushort>(b + ((i * 3) >> 1)) >> (((int)i & 1) << 2)) & 0xFFF;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Set(byte* b, long i, uint v)
    {
        byte* r = b + ((i * 3) >> 1);
        int sh = ((int)i & 1) << 2;
        uint cur = Unsafe.ReadUnaligned<ushort>(r);
        Unsafe.WriteUnaligned(r, (ushort)((cur & ~(0xFFFu << sh)) | (v << sh)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ushort> Load16(byte* p)
    {
        var v = Vector256.Create(
            Vector128.Load(p), Vector128.Load(p + 12));
        var x = Avx2.Shuffle(v, UnpackShuffle).AsUInt16();
        return Avx2.Blend(x & Vector256.Create((ushort)0x0FFF),
            Avx2.ShiftRightLogical(x, 4), 0b10101010);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store16(byte* p, Vector256<ushort> v)
    {
        var x = v.AsUInt32();
        var t = (x & Vector256.Create(0xFFFFu))
              | (Avx2.ShiftRightLogical(x, 4) & Vector256.Create(0xFFF000u));
        var b = Avx2.Shuffle(t.AsByte(), PackShuffle);
        var lo = b.GetLower().AsUInt64();
        var hi = b.GetUpper().AsUInt64();
        Unsafe.WriteUnaligned(p, lo.ToScalar());
        Unsafe.WriteUnaligned(p + 8, lo.AsUInt32().GetElement(2));
        Unsafe.WriteUnaligned(p + 12, hi.ToScalar());
        Unsafe.WriteUnaligned(p + 20, hi.AsUInt32().GetElement(2));
    }

    /// <summary>dst[k] = counter (start + k), k in [0, count).</summary>
    public static void Unpack(byte* b, long start, int count, ushort* dst)
    {
        byte* p = b + ((start * 3) >> 1);
        int k = 0;
        for (; k + 16 <= count; k += 16, p += 24) Load16(p).Store(dst + k);
        for (; k < count; k++) dst[k] = (ushort)Get(b, start + k);
    }

    /// <summary>counter (start + k) = src[k], k in [0, count).</summary>
    public static void Pack(ushort* src, byte* b, long start, int count)
    {
        byte* p = b + ((start * 3) >> 1);
        int k = 0;
        for (; k + 16 <= count; k += 16, p += 24)
            Store16(p, Vector256.Load(src + k));
        for (; k < count; k++) Set(b, start + k, src[k]);
    }

    /// <summary>
    /// Like <see cref="Pack"/>, but first reduces each value mod m. Values may be
    /// any lazily accumulated sum below 16m (m &lt;= 4096, so 16m &lt;= 65536): a
    /// conditional-subtract ladder 8m, 4m, 2m, m, each step min(x, x - km), where
    /// x - km wraps above x exactly when x &lt; km (km &lt;= 32768).
    /// </summary>
    public static void PackReduce(ushort* src, byte* b, long start, int count, uint m)
    {
        byte* p = b + ((start * 3) >> 1);
        var m1 = Vector256.Create((ushort)m);
        var m2 = Vector256.Create((ushort)(2 * m));
        var m4 = Vector256.Create((ushort)(4 * m));
        var m8 = Vector256.Create((ushort)(8 * m));
        int k = 0;
        for (; k + 16 <= count; k += 16, p += 24)
        {
            var x = Vector256.Load(src + k);
            x = Vector256.Min(x, x - m8);
            x = Vector256.Min(x, x - m4);
            x = Vector256.Min(x, x - m2);
            x = Vector256.Min(x, x - m1);
            Store16(p, x);
        }
        for (; k < count; k++) Set(b, start + k, src[k] % m);
    }

    /// <summary>counter (dst + k) += counter (src + k) mod m, k in [0, count); ranges disjoint.</summary>
    public static void AddRange(byte* b, long dst, long src, int count, uint m)
    {
        byte* pd = b + ((dst * 3) >> 1), ps = b + ((src * 3) >> 1);
        var vm = Vector256.Create((ushort)m);
        int k = 0;
        for (; k + 16 <= count; k += 16, pd += 24, ps += 24)
        {
            var s = Load16(pd) + Load16(ps);                                      // < 2m <= 8192: no overflow
            Store16(pd, Vector256.Min(s, s - vm));     // s - m wraps high when s < m
        }
        for (; k < count; k++)
        {
            uint s = Get(b, dst + k) + Get(b, src + k);
            Set(b, dst + k, s >= m ? s - m : s);
        }
    }
}

/// <summary>One flat native allocation of 16-bit counters (native: n=24 exceeds .NET's single-array limit). Zero-initialized.</summary>
internal sealed unsafe class Native16Store : IDisposable
{
    public readonly long Counters;
    public readonly long Bytes;
    private ushort* _p;

    public Native16Store(long counters)
    {
        Counters = counters;
        Bytes = counters * 2;
        _p = (ushort*)NativeMemory.AllocZeroed((nuint)Bytes);
        GC.AddMemoryPressure(Bytes);
    }

    public ushort* Ptr => _p;

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    ~Native16Store() => Free();

    private void Free()
    {
        if (_p == null) return;
        NativeMemory.Free(_p);
        _p = null;
        GC.RemoveMemoryPressure(Bytes);
    }
}

/// <summary>
/// 16-bit counts mod m (m == 0 means mod 65536), 2 bytes per counter. Every
/// counter owns whole bytes, so (unlike 12-bit) no block-alignment argument is
/// needed for lock-freedom.
/// </summary>
internal readonly unsafe struct Mod16Arith(uint modulus) : IArith<Native16Store>
{
    private readonly int _m = modulus == 0 ? 65536 : (int)modulus;
    public int TmpLen => 1;

    public Native16Store Allocate(long counters) => new(counters);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort* Slot(Native16Store a, long i)
    {
        if ((ulong)i >= (ulong)a.Counters) ThrowIndex();
        return a.Ptr + i;
    }

    private static void ThrowIndex() => throw new IndexOutOfRangeException("16-bit counter index out of range");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(Native16Store dst, long di, Native16Store src, long si)
    {
        int b = *Slot(src, si);
        ushort* r = Slot(dst, di);
        // both < m <= 65536; branch-free reduction (see Mod32Arith.Add)
        int t = *r + b - _m;
        *r = (ushort)(t + (_m & (t >> 31)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsZero(Native16Store a, long i) => *Slot(a, i) == 0;
    public void Save(Native16Store a, long i, ulong[] tmp) => tmp[0] = *Slot(a, i);
    public void Restore(ulong[] tmp, Native16Store a, long i) => *Slot(a, i) = (ushort)tmp[0];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetZero(Native16Store a, long i) => *Slot(a, i) = 0;
    public void SetOne(Native16Store a, long i) => *Slot(a, i) = 1;
    public bool Scalar => true;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Load(Native16Store a, long i) => *Slot(a, i);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(Native16Store a, long i, ulong v) => *Slot(a, i) = (ushort)v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Sum(ulong x, ulong y)
    {
        int t = (int)x + (int)y - _m;
        return (uint)(t + (_m & (t >> 31)));
    }

    private const int IoBlock = 1 << 26;

    public void Serialize(Native16Store a, Stream s)
    {
        byte* p = (byte*)a.Ptr;
        for (long k = 0; k < a.Bytes; k += IoBlock)
            s.Write(new ReadOnlySpan<byte>(p + k, (int)Math.Min(IoBlock, a.Bytes - k)));
    }

    public void Deserialize(Native16Store a, Stream s)
    {
        byte* p = (byte*)a.Ptr;
        for (long k = 0; k < a.Bytes; k += IoBlock)
            s.ReadExactly(new Span<byte>(p + k, (int)Math.Min(IoBlock, a.Bytes - k)));
    }
}

/// <summary>Fixed-width little-endian limb counts (exact); throws on overflow.</summary>
internal readonly struct LimbArith(int width) : IArith<ulong[]>
{
    private readonly int _w = width;
    public int TmpLen => _w;
    public ulong[] Allocate(long counters) => new ulong[checked(counters * _w)];

    public void Add(ulong[] dst, long di, ulong[] src, long si)
    {
        long d = di * _w, s = si * _w;
        ulong carry = 0;
        for (int k = 0; k < _w; k++)
        {
            UInt128 x = (UInt128)dst[d + k] + src[s + k] + carry;
            dst[d + k] = (ulong)x;
            carry = (ulong)(x >> 64);
        }
        if (carry != 0)
            throw new OverflowException("Count exceeded the limb width; increase the value-width margin.");
    }

    public bool IsZero(ulong[] a, long i)
    {
        long b = i * _w;
        for (int k = 0; k < _w; k++) if (a[b + k] != 0) return false;
        return true;
    }

    public void Save(ulong[] a, long i, ulong[] tmp) => Array.Copy(a, i * _w, tmp, 0, _w);
    public void Restore(ulong[] tmp, ulong[] a, long i) => Array.Copy(tmp, 0, a, i * _w, _w);

    public void SetZero(ulong[] a, long i)
    {
        long b = i * _w;
        for (int k = 0; k < _w; k++) a[b + k] = 0;  // no Array.Clear: index may exceed int
    }

    public void SetOne(ulong[] a, long i)
    {
        long b = i * _w;
        a[b] = 1;
        for (int k = 1; k < _w; k++) a[b + k] = 0;
    }

    public bool Scalar => false;
    public ulong Load(ulong[] a, long i) => throw new NotSupportedException();
    public void Store(ulong[] a, long i, ulong v) => throw new NotSupportedException();
    public ulong Sum(ulong x, ulong y) => throw new NotSupportedException();

    public void Serialize(ulong[] a, Stream s) => ArrayIO.Write(a, s);
    public void Deserialize(ulong[] a, Stream s) => ArrayIO.Read(a, s);
}

/// <summary>
/// Counts corner-to-corner self-avoiding paths on the n x n vertex grid
/// (OEIS A007764) with the dense in-place engine. Exact limb mode, single
/// modular mode, or CRT orchestration over coprime moduli.
/// </summary>
public sealed partial class DenseCounter
{
    private readonly int _n;          // vertices per side (OEIS index)
    private readonly int _cols;       // frontier width == _n
    private readonly int _rows;
    private readonly MateCodec _mc;   // full-width codec
    private readonly MateCodec _wc;   // width-1 codec for the deferred array
    private ulong[][][]? _rt;         // right-half transition tables [p][h][i], built on first run
    private RtKinds[][]? _rk;         // per-kind index lists over _rt [p][h]
    private RtEntry[][][]? _re;       // _rt decoded in _rk order (NN, swap, emit) [p][h][k]
    private int[][][]? _rr;           // vectorizable runs of _re's NN and swap entries [p][h] (see BuildRightRuns)
    private int[][]? _sr;             // vectorizable runs of the straddle '.'-left passes [h] (see BuildStraddleRuns)

    // One right-update state in pass order: its index in the block and its two
    // decoded offsets (value target A, deferred D), so the 12-bit loops make one
    // sequential load per state instead of idx[k] then a scattered t[i] plus field
    // extraction. Escapes (after EmitEnd) still use _rt/_rk.
    internal struct RtEntry
    {
        public int I, A, D;
    }

    // See Engine.BuildRightKinds.
    private sealed class RtKinds
    {
        public required int[] Idx;          // entry indices, grouped by kind, ascending within a kind
        public int NnEnd, SwapEnd, EmitEnd; // Idx[0..NnEnd) NN, ..SwapEnd swap, ..EmitEnd emit, ..end escape
    }
    private uint[][]? _lt;            // left-update deferred offsets [h][i]
    private uint[][][]? _llt;         // same after an LL match flip [s][h][i]
    private uint[][]? _lta;           // _lt as absolute offsets in the canonical 3-block layout [h][i]
    private bool[]? _ltaId;           // per h: _lta[h] is the identity (see Engine.IsIdentityLayout)
    private uint[][][]? _llta;        // _llt likewise (layout height h-2) [s][h][i]
    private StraddleTable[]? _st;     // p == RightWidth update tables [h]

    // See Engine.BuildStraddleTables.
    private sealed class StraddleTable
    {
        public required int[] Idx;        // right-mate indices grouped by top value: '.', ')', '('
        public int NEnd, REnd;            // Idx[0..NEnd) top '.', ..REnd top ')', ..end top '('
        public required uint[] A;         // mc right offset after the transition's top change
        public required uint[] DFlip;     // top '(' only: wc right offset (below top) with its matching ')' flipped to '('
    }

    public long StateCount => _mc.CodeSize;
    public long DeferredCount => _wc.CodeSize;
    /// <summary>Bytes of the precomputed update tables (all built from the right-mate lists).</summary>
    public long TransitionTableBytes
    {
        get
        {
            long mates = 0;
            for (int h = 0; h <= _mc.MaxSplitHeight; h++) mates += _mc.RightBlock(h).Length;
            // right transitions (p < wR) + their per-kind index lists (upper bound)
            // + left table + LL table (s in 1..wR)
            return (_mc.RightWidth - 1) * mates * (sizeof(ulong) + sizeof(int))
                + (1 + _mc.RightWidth) * mates * sizeof(uint);
        }
    }

    public DenseCounter(int n)
    {
        if (n < 2) throw new ArgumentOutOfRangeException(nameof(n), "use n >= 2 (n = 1 is trivially 1)");
        if (n > 32) throw new ArgumentOutOfRangeException(nameof(n), "mate strings are limited to 32 positions (one ulong)");
        _n = n;
        _cols = n;
        _rows = n;
        _mc = new MateCodec(_cols, (_cols + 1) / 2, 1, 0);
        // The deferred codec's right width is one LESS than the main codec's:
        // every group-fixed left position q >= mc.wR maps (after Shrink) to
        // q-1 >= wc.wR, i.e. into the deferred codec's LEFT half. States from
        // different parallel groups therefore always hit different deferred
        // BLOCKS, which (with even-aligned block bases) means different bytes —
        // required for lock-free group parallelism under 12-bit packed storage.
        _wc = new MateCodec(_cols - 1, (_cols + 1) / 2 - 1, 1, 0);
    }

    /// <summary>Exact count with fixed-width limb arithmetic.</summary>
    public BigInteger CountExact(int? maxDegreeOfParallelism = null, Action<int, int>? progress = null)
    {
        int valWidth = Math.Max(2, (int)Math.Ceiling(0.25 * _n * _n * 3.3219 / 64.0));
        ulong[] answer = Run<LimbArith, ulong[]>(new LimbArith(valWidth), maxDegreeOfParallelism, progress);
        return StateStore.ToBigInteger(answer);
    }

    /// <summary>Count modulo <paramref name="modulus"/> in 64-bit storage (0 = modulo 2^64).</summary>
    public ulong CountMod(ulong modulus, int? maxDegreeOfParallelism = null, Action<int, int>? progress = null, string? checkpointPath = null, LadderRun? ladder = null)
        => Run<Mod64Arith, ulong[]>(new Mod64Arith(modulus), maxDegreeOfParallelism, progress, checkpointPath, modulus, ladder)[0];

    /// <summary>Count modulo <paramref name="modulus"/> in 32-bit storage (0 = modulo 2^32); half the RAM.</summary>
    public ulong CountMod32(uint modulus, int? maxDegreeOfParallelism = null, Action<int, int>? progress = null, string? checkpointPath = null, LadderRun? ladder = null)
        => Run<Mod32Arith, uint[]>(new Mod32Arith(modulus), maxDegreeOfParallelism, progress, checkpointPath, modulus, ladder)[0];

    /// <summary>Count modulo <paramref name="modulus"/> in 12-bit packed storage (0 = modulo 4096); 1.5 bytes/state, chunked.</summary>
    public ulong CountMod12(uint modulus, int? maxDegreeOfParallelism = null, Action<int, int>? progress = null, string? checkpointPath = null, LadderRun? ladder = null)
        => Run<Mod12Arith, Packed12Store>(new Mod12Arith(modulus), maxDegreeOfParallelism, progress, checkpointPath, modulus, ladder)[0];

    /// <summary>Count modulo <paramref name="modulus"/> in 16-bit storage (0 = modulo 65536); 2 bytes/state.</summary>
    public ulong CountMod16(uint modulus, int? maxDegreeOfParallelism = null, Action<int, int>? progress = null, string? checkpointPath = null, LadderRun? ladder = null)
        => Run<Mod16Arith, Native16Store>(new Mod16Arith(modulus), maxDegreeOfParallelism, progress, checkpointPath, modulus, ladder)[0];

    /// <summary>
    /// Exact count via independent modular runs over pairwise-coprime moduli
    /// (2^storeBits plus smaller coprime values), combined with the CRT. RAM per
    /// run is storeBits/8 bytes/state — the lowest-memory exact mode (storeBits
    /// 12 = 1.5 bytes/state, chunked; the only width that fits n=24 in 16 GB).
    ///
    /// Robustness: each completed run's (modulus, residue) is appended to
    /// <paramref name="stateFile"/> (if given), and an existing matching file is
    /// resumed — a crash never loses completed runs. Correctness: the target bit
    /// count is only a starting estimate; after combining, EXTRA verification
    /// runs must reproduce the reconstructed value mod fresh moduli (at least
    /// ~24 bits' worth). A failed check folds that run into the CRT set and
    /// verification restarts — so an undershot estimate self-heals instead of
    /// silently returning a wrong value.
    /// </summary>
    /// <para><paramref name="known"/> (--crt-known, for re-deriving a published term):
    /// every residue is checked against the known value as soon as it is computed
    /// (a mismatch is re-run once: a non-reproducible one was a transient glitch, a
    /// reproducible one is an engine error and throws), the campaign stops as soon
    /// as the moduli's product exceeds the known value, and no held-out verification
    /// runs are needed. Not an independent computation of a(n).</para>
    public static BigInteger CountWithCrt(int n, int? maxDegreeOfParallelism = null,
        Action<string>? log = null, Action<int, int>? progress = null, int storeBits = 64,
        string? stateFile = null, BigInteger? known = null, LadderOptions? ladder = null,
        DenseCounter? counter = null, bool mitm = false)
    {
        if (n == 1) return BigInteger.One;
        if (storeBits is not (12 or 16 or 32 or 64)) throw new ArgumentOutOfRangeException(nameof(storeBits));
        if (counter is not null && counter._n != n) throw new ArgumentException("counter is for a different n", nameof(counter));
        // Reuse the caller's counter: its codecs (~0.5 GiB committed at n=24) would
        // otherwise stay alive next to a second copy for the whole campaign.
        var dc = counter ?? new DenseCounter(n);
        dc.Mitm |= mitm;
        if (dc.MitmActive && ladder is not null) throw new ArgumentException("--mitm cannot be combined with --ladder yet");
        BigInteger baseMod = BigInteger.One << storeBits;   // the free-wraparound modulus

        // Starting estimate of the answer size. 0.223 digits/vertex covers every
        // known term (empirical slope ~0.217 at n=23) with a slim margin; the
        // verification loop below makes an undershoot harmless, so this can be
        // much tighter than the limb engine's 0.25 bound (fewer runs).
        double estBits = 0.223 * n * n * 3.3219;
        BigInteger need = BigInteger.One << (int)Math.Ceiling(estBits);

        var moduli = new List<ulong>();     // 0 stands for 2^storeBits
        var residues = new List<ulong>();
        BigInteger product = BigInteger.One;
        ulong maxOdd = storeBits == 12 ? 4095UL : storeBits == 16 ? 65535UL : storeBits == 32 ? uint.MaxValue : ulong.MaxValue;
        ulong candidate = maxOdd | 1UL;     // descend over odd values below 2^storeBits

        string header = $"sapcrt v1 n={n} bits={storeBits}";
        void Record(ulong m, ulong r)
        {
            moduli.Add(m);
            residues.Add(r);
            product *= m == 0 ? baseMod : m;
            if (stateFile is not null)
            {
                if (moduli.Count == 1 && !File.Exists(stateFile)) File.WriteAllText(stateFile, header + "\n");
                File.AppendAllText(stateFile, $"{m} {r}\n");
            }
        }

        // Resume any completed runs from a previous interrupted campaign.
        if (stateFile is not null && File.Exists(stateFile))
        {
            string[] lines = File.ReadAllLines(stateFile);
            if (lines.Length > 0 && lines[0] == header)
            {
                foreach (string line in lines.Skip(1))
                {
                    string[] parts = line.Split(' ');
                    if (parts.Length != 2 || !ulong.TryParse(parts[0], out ulong m) || !ulong.TryParse(parts[1], out ulong r)) continue;
                    moduli.Add(m);
                    residues.Add(r);
                    product *= m == 0 ? baseMod : m;
                    if (m != 0 && m <= candidate) candidate = m - 2;
                }
                if (moduli.Count > 0) log?.Invoke($"resumed {moduli.Count} completed run(s) from {Path.GetFileName(stateFile)}");
            }
            else
            {
                File.Delete(stateFile);     // stale file for a different (n, bits)
            }
        }

        // Per-sweep checkpoint path (only when the user opted into persistence via
        // stateFile). Each modulus gets its own file so an interrupted sweep resumes.
        string ckptDir = stateFile is null ? "." : (Path.GetDirectoryName(Path.GetFullPath(stateFile)) ?? ".");
        string? Ckpt(ulong m) => stateFile is null ? null
            : Path.Combine(ckptDir, $"sapckpt_{n}_b{storeBits}_m{m}.bin");

        // Test hook: SAP_CRT_CORRUPT_CALL flips one bit of the N-th sweep's residue,
        // simulating a single transient bit-flip, to exercise the fault-isolation
        // repair path deterministically. Fires once (a later repair re-run has a
        // higher call index, so it returns the correct value). No effect when unset.
        int corruptCall = int.TryParse(Environment.GetEnvironmentVariable("SAP_CRT_CORRUPT_CALL"), out int cc) ? cc : -1;
        int runCall = 0;

        // Residue ladder (--ladder): runs after the first few start mid-grid from an
        // exact row vector rebuilt from earlier runs' dumps. Held-out verification,
        // isolation and repair runs always sweep from scratch (scratch: true), so the
        // final check stays independent of the ladder files.
        ResidueLadder? lad = null;
        if (ladder is not null)
        {
            int planned;
            if (known is BigInteger kp0) planned = CrtKnownRuns(storeBits, kp0);
            else
            {
                var all = CrtModuli(storeBits, (int)Math.Ceiling(estBits) / (storeBits - 1) + 4);
                BigInteger p0 = BigInteger.One;
                planned = 0;
                while (p0 < need && planned < all.Count) { ulong am = all[planned++]; p0 *= am == 0 ? baseMod : am; }
            }
            lad = new ResidueLadder(n, storeBits, dc.StateCount, ladder, planned, log);
        }

        // Run one modulus in the chosen storage width; m == 0 -> free wraparound.
        ulong RunMod(ulong m, bool scratch = false)
        {
            LadderRun? lr = scratch ? null : lad?.Prepare(m);
            if (lr is { StartRow: > 0 }) log?.Invoke($"  (ladder: starting at row {lr.StartRow})");
            ulong r;
            bool ok = false;
            try
            {
                r = storeBits switch
                {
                    12 => dc.CountMod12((uint)m, maxDegreeOfParallelism, progress, Ckpt(m), lr),
                    16 => dc.CountMod16((uint)m, maxDegreeOfParallelism, progress, Ckpt(m), lr),
                    32 => dc.CountMod32((uint)m, maxDegreeOfParallelism, progress, Ckpt(m), lr),
                    _ => dc.CountMod(m, maxDegreeOfParallelism, progress, Ckpt(m), lr),
                };
                ok = true;
            }
            finally { lad?.Finish(lr, ok); }
            if (++runCall == corruptCall) { r ^= 1; log?.Invoke($"[test] corrupted residue of sweep {runCall}"); }
            return r;
        }

        // Next unused modulus: 2^storeBits first, then odd values coprime to all kept.
        ulong NextModulus()
        {
            if (moduli.Count == 0) return 0;
            while (true)
            {
                bool ok = candidate >= 3;
                if (!ok) throw new InvalidOperationException("ran out of coprime moduli for this storage width");
                foreach (ulong km in moduli)
                    if (km != 0 && BigInteger.GreatestCommonDivisor(candidate, km) != 1) { ok = false; break; }
                if (ok) { ulong m = candidate; candidate -= 2; return m; }
                candidate -= 2;
            }
        }

        if (known is BigInteger kv)
        {
            ulong Expected(ulong m) => (ulong)(kv % (m == 0 ? baseMod : m));
            // Residues resumed from a state file are checked too.
            for (int i = 0; i < moduli.Count; i++)
                if (residues[i] != Expected(moduli[i]))
                {
                    log?.Invoke($"resumed residue for modulus {moduli[i]} disagrees with the known value -> re-running it");
                    residues[i] = CheckedRun(moduli[i]);
                    RewriteState();
                }
            while (product <= kv)
            {
                ulong m = NextModulus();
                log?.Invoke($"run {moduli.Count + 1}: counting mod {(m == 0 ? $"2^{storeBits}" : m.ToString())} (checked against the known value)");
                Record(m, CheckedRun(m));
            }
            BigInteger kres = Combine();
            if (kres != kv) throw new InvalidOperationException("CRT combination differs from the known value despite per-residue checks");
            log?.Invoke($"combined {moduli.Count} runs with CRT (product ~2^{(int)BigInteger.Log2(product)}), every residue checked against the known value");
            if (stateFile is not null && File.Exists(stateFile)) File.Delete(stateFile);
            lad?.Cleanup();
            return kres;

            ulong CheckedRun(ulong m)
            {
                ulong r = RunMod(m), want = Expected(m);
                if (r == want) return r;
                log?.Invoke($"residue mod {m} = {r}, known value gives {want}; re-running to confirm");
                ulong r2 = RunMod(m, scratch: true);
                if (r2 == want) { log?.Invoke(lad is null ? "first run was a transient glitch; the re-run matches" : "the from-scratch re-run matches (transient glitch or a bad ladder rung)"); return r2; }
                throw new InvalidOperationException(
                    $"residue mod {(m == 0 ? $"2^{storeBits}" : m.ToString())} reproducibly differs from the known value " +
                    $"({r}, {r2} vs {want}): engine error");
            }
        }

        while (product < need)
        {
            ulong m = NextModulus();
            log?.Invoke($"run {moduli.Count + 1}: counting mod {(m == 0 ? $"2^{storeBits}" : m.ToString())}");
            Record(m, RunMod(m));
        }

        // CRT over the recorded residues, optionally leaving one out (skip < 0 = none).
        BigInteger Combine(int skip = -1) => CrtCombine(moduli, residues, baseMod, skip);

        // Rewrite the whole state file (used after a repair replaces a residue).
        void RewriteState()
        {
            if (stateFile is null) return;
            var sb = new System.Text.StringBuilder(header).Append('\n');
            for (int i = 0; i < moduli.Count; i++) sb.Append(moduli[i]).Append(' ').Append(residues[i]).Append('\n');
            File.WriteAllText(stateFile, sb.ToString());
        }

        // Verification: fresh moduli must reproduce the combined value. One check
        // gives a ~2^-storeBits false-pass chance, so require >= ~24 bits of checks.
        // A failed check within the size-estimate uncertainty just means the answer
        // is bigger than estimated (fold the run in and continue). A failed check
        // *past* that bound means a recorded residue is corrupt (a bit-flip over a
        // multi-day run on non-ECC RAM is a real risk) — isolate and repair the one
        // bad residue rather than throwing away the whole campaign.
        int checksNeeded = Math.Max(1, (24 + storeBits - 1) / storeBits);
        BigInteger healLimit = BigInteger.One << ((int)Math.Ceiling(0.25 * n * n * 3.3219) + 2 * storeBits);
        BigInteger result = Combine();
        var checks = new List<(ulong g, ulong s)>();   // passed samples for the current residue set
        int checksPassed = 0, repairs = 0;
        while (checksPassed < checksNeeded)
        {
            ulong m = NextModulus();
            log?.Invoke($"verify: counting mod {m}");
            ulong r = RunMod(m, scratch: true);
            if (result % m == r)
            {
                checks.Add((m, r));
                checksPassed++;
                continue;
            }

            // Estimate was short: fold this run in and restart verification.
            if (product <= healLimit)
            {
                log?.Invoke("verification mismatch -> answer larger than estimated; adding more runs");
                Record(m, r);
                result = Combine();
                checks.Clear();
                checksPassed = 0;
                continue;
            }

            // Past the heal bound. Rule out a transient bad *check* sweep first by
            // re-running the modulus; a non-reproducible result means this sweep
            // (not a stored residue) glitched — discard and retry.
            log?.Invoke($"verification mismatch past heal bound at mod {m}; re-running to confirm");
            ulong r2 = RunMod(m, scratch: true);
            if (r2 != r)
            {
                log?.Invoke("check sweep was non-reproducible (transient) -> retrying");
                if (result % m == r2) { checks.Add((m, r2)); checksPassed++; }
                continue;
            }

            // Reproducible mismatch -> a stored residue is corrupt. Gather enough
            // independent A-measurements (prior passed checks are valid too, since
            // a passed check equals A mod g) to isolate a single fault.
            var samples = new List<(ulong g, ulong s)>(checks) { (m, r) };
            while (samples.Count < checksNeeded)
            {
                ulong g = NextModulus();
                log?.Invoke($"isolation: sampling mod {g}");
                samples.Add((g, RunMod(g, scratch: true)));
            }
            int bad = -1, candidates = 0;
            for (int k = 0; k < moduli.Count; k++)
            {
                BigInteger xk = Combine(skip: k);
                if (samples.All(t => xk % t.g == t.s)) { bad = k; candidates++; }
            }
            if (candidates != 1 || ++repairs > moduli.Count)
                throw new InvalidOperationException(
                    $"CRT residues inconsistent and not repairable by a single-residue fix ({candidates} candidates). " +
                    (stateFile is null ? "Rerun the computation." : $"Inspect or delete \"{stateFile}\" and rerun."));

            ulong badMod = moduli[bad];
            log?.Invoke($"isolated corrupt residue for modulus {(badMod == 0 ? $"2^{storeBits}" : badMod.ToString())} -> re-running it");
            residues[bad] = RunMod(badMod, scratch: true);
            RewriteState();
            result = Combine();
            checks.Clear();
            checksPassed = 0;
        }

        if (n >= 2 && !result.IsEven)
            throw new InvalidOperationException("sanity check failed: A007764(n) must be even for n >= 2");

        log?.Invoke($"combined {moduli.Count} runs with CRT (product ~2^{(int)BigInteger.Log2(moduli.Aggregate(BigInteger.One, (a, m) => a * (m == 0 ? baseMod : m)))}), verified with {checksNeeded} extra run(s)");
        if (stateFile is not null && File.Exists(stateFile)) File.Delete(stateFile);
        lad?.Cleanup();
        // Sweep up any per-sweep checkpoints (incl. orphans from moduli dropped by
        // self-heal or repair) now that the campaign has completed.
        if (stateFile is not null)
            foreach (string f in Directory.EnumerateFiles(ckptDir, $"sapckpt_{n}_b{storeBits}_m*.bin"))
                try { File.Delete(f); } catch (IOException) { }
        return result;
    }

    // ------------------------------------------------------------ split CRT campaigns
    //
    // A CRT campaign is a list of independent sweeps, so it can be spread over
    // several machines: every machine derives the SAME modulus list (below),
    // runs every N-th entry (--crt-part K/N) into its own state file, and one
    // machine merges the files (--crt-merge). Unlike CountWithCrt, a split
    // campaign cannot add runs after the fact, so its list is sized up front:
    // enough moduli for the answer-size estimate plus a margin, then a few
    // held-out moduli that the merge uses purely for verification.

    private const int SplitMarginBits = 16;

    /// <summary>Deterministic modulus list shared by all parts: 2^storeBits (as 0), then odd values descending, each coprime to all earlier ones.</summary>
    public static List<ulong> CrtModuli(int storeBits, int count)
    {
        var list = new List<ulong>(count);
        if (count <= 0) return list;
        list.Add(0);
        ulong candidate = storeBits == 12 ? 4095UL : storeBits == 16 ? 65535UL : storeBits == 32 ? uint.MaxValue : ulong.MaxValue;
        while (list.Count < count)
        {
            if (candidate < 3) throw new InvalidOperationException("ran out of coprime moduli for this storage width");
            bool ok = true;
            foreach (ulong km in list)
                if (km != 0 && BigInteger.GreatestCommonDivisor(candidate, km) != 1) { ok = false; break; }
            if (ok) list.Add(candidate);
            candidate -= 2;
        }
        return list;
    }

    /// <summary>Verification moduli held out of the combination (>= ~24 bits of checks).</summary>
    public static int CrtCheckCount(int storeBits) => Math.Max(1, (24 + storeBits - 1) / storeBits);

    /// <summary>Default total list length for a split campaign at n (combine set + held-out checks).</summary>
    /// <summary>Runs for a known-value campaign (--crt-known): the shortest prefix of
    /// the deterministic modulus list whose product exceeds the known value.</summary>
    public static int CrtKnownRuns(int storeBits, BigInteger known)
    {
        BigInteger product = BigInteger.One, baseMod = BigInteger.One << storeBits;
        int bits = (int)BigInteger.Log2(known) + 1;
        List<ulong> all = CrtModuli(storeBits, bits / (storeBits - 1) + 4);
        int k = 0;
        while (product <= known) { ulong m = all[k++]; product *= m == 0 ? baseMod : m; }
        return k;
    }

    public static int CrtSplitRuns(int n, int storeBits)
    {
        int bits = (int)Math.Ceiling(0.223 * n * n * 3.3219) + SplitMarginBits;   // same estimate as CountWithCrt, plus margin
        BigInteger need = BigInteger.One << bits, product = BigInteger.One, baseMod = BigInteger.One << storeBits;
        // every modulus has >= storeBits-1 bits, so this many always suffice
        List<ulong> all = CrtModuli(storeBits, bits / (storeBits - 1) + 4);
        int k = 0;
        while (product < need) { ulong m = all[k++]; product *= m == 0 ? baseMod : m; }
        return k + CrtCheckCount(storeBits);
    }

    // The header pins only (n, bits): the list is deterministic and a part's
    // share (i % parts) does not depend on its length, so a campaign can be
    // extended with a larger --crt-runs and existing files stay valid.
    private static string PartHeader(int n, int storeBits) => $"sapcrt-part v2 n={n} bits={storeBits}";

    /// <summary>
    /// Run this machine's share (list indices i with i % parts == part-1) of a
    /// split campaign, appending "modulus residue" lines to <paramref name="stateFile"/>.
    /// Resumable: moduli already in the file are skipped, and each sweep uses
    /// the usual per-sweep checkpoint next to the state file.
    /// </summary>
    public static void RunCrtPart(int n, int storeBits, int part, int parts, int runs, string stateFile,
        int? maxDegreeOfParallelism = null, Action<string>? log = null, Action<int, int>? progress = null,
        BigInteger? known = null)
    {
        if (parts < 1 || part < 1 || part > parts) throw new ArgumentOutOfRangeException(nameof(part));
        if (storeBits is not (12 or 16 or 32 or 64)) throw new ArgumentOutOfRangeException(nameof(storeBits));
        List<ulong> moduli = CrtModuli(storeBits, runs);
        var mine = Enumerable.Range(0, runs).Where(i => i % parts == part - 1).Select(i => moduli[i]).ToList();
        string header = PartHeader(n, storeBits);

        var done = new HashSet<ulong>();
        if (File.Exists(stateFile))
        {
            string[] lines = File.ReadAllLines(stateFile);
            if (lines.Length == 0 || lines[0] != header)
                throw new InvalidOperationException($"\"{stateFile}\" belongs to a different split campaign ({(lines.Length > 0 ? lines[0] : "empty")}); move it away first.");
            foreach (string line in lines.Skip(1))
            {
                string[] p = line.Split(' ');
                if (p.Length == 2 && ulong.TryParse(p[0], out ulong m) && ulong.TryParse(p[1], out _)) done.Add(m);
            }
            if (done.Count > 0) log?.Invoke($"resumed: {done.Count} of this part's {mine.Count} run(s) already done");
        }
        else File.WriteAllText(stateFile, header + "\n");

        var dc = new DenseCounter(n);
        string dir = Path.GetDirectoryName(Path.GetFullPath(stateFile)) ?? ".";
        int k = 0;
        foreach (ulong m in mine)
        {
            k++;
            if (done.Contains(m)) continue;
            string label = m == 0 ? $"2^{storeBits}" : m.ToString();
            log?.Invoke($"part {part}/{parts}: run {k}/{mine.Count}: counting mod {label}");
            string ckpt = Path.Combine(dir, $"sapckpt_{n}_b{storeBits}_m{m}.bin");
            ulong Sweep() => storeBits switch
            {
                12 => dc.CountMod12((uint)m, maxDegreeOfParallelism, progress, ckpt),
                16 => dc.CountMod16((uint)m, maxDegreeOfParallelism, progress, ckpt),
                32 => dc.CountMod32((uint)m, maxDegreeOfParallelism, progress, ckpt),
                _ => dc.CountMod(m, maxDegreeOfParallelism, progress, ckpt),
            };
            ulong r = Sweep();
            if (known is BigInteger kv)
            {
                // --crt-known: check at once (a re-run separates a transient glitch from an engine error)
                ulong want = (ulong)(kv % (m == 0 ? BigInteger.One << storeBits : m));
                if (r != want)
                {
                    log?.Invoke($"residue mod {label} = {r}, known value gives {want}; re-running to confirm");
                    ulong r2 = Sweep();
                    if (r2 != want)
                        throw new InvalidOperationException($"residue mod {label} reproducibly differs from the known value ({r}, {r2} vs {want}): engine error");
                    r = r2;
                }
            }
            File.AppendAllText(stateFile, $"{m} {r}\n");
        }
        log?.Invoke($"part {part}/{parts} complete: {mine.Count} run(s) in {Path.GetFileName(stateFile)}");
    }

    /// <summary>
    /// Merge the state files of a split campaign: checks they belong together and
    /// cover at least the default-length modulus list (the longest covered prefix
    /// is used, so an extended campaign counts), combines all but the held-out
    /// moduli with the CRT, and verifies the result against the held-out residues.
    /// Throws with a description of what is missing or inconsistent.
    /// </summary>
    /// <para><paramref name="knownOf"/> (--crt-known): the known value of a(n), if any.
    /// Then the shortest modulus prefix whose product exceeds it suffices, every
    /// residue is checked against it, and no held-out moduli are needed.</para>
    public static (BigInteger value, int n) MergeCrtParts(IEnumerable<string> stateFiles, Action<string>? log = null,
        Func<int, BigInteger?>? knownOf = null)
    {
        int? n = null, bits = null;
        var res = new Dictionary<ulong, ulong>();
        foreach (string f in stateFiles)
        {
            string[] lines = File.ReadAllLines(f);
            var h = lines.Length > 0 ? System.Text.RegularExpressions.Regex.Match(lines[0],
                @"^sapcrt-part v2 n=(\d+) bits=(\d+)$") : null;
            if (h is null || !h.Success) throw new InvalidOperationException($"\"{f}\" is not a split-campaign state file");
            int fn = int.Parse(h.Groups[1].Value), fb = int.Parse(h.Groups[2].Value);
            if ((n ?? fn) != fn || (bits ?? fb) != fb)
                throw new InvalidOperationException($"\"{f}\" is from a different campaign ({lines[0]})");
            (n, bits) = (fn, fb);
            foreach (string line in lines.Skip(1))
            {
                string[] p = line.Split(' ');
                if (p.Length != 2 || !ulong.TryParse(p[0], out ulong m) || !ulong.TryParse(p[1], out ulong r)) continue;
                if (res.TryGetValue(m, out ulong prev) && prev != r)
                    throw new InvalidOperationException($"conflicting residues for modulus {m}: {prev} vs {r} — rerun that modulus");
                res[m] = r;
            }
        }
        if (n is null) throw new InvalidOperationException("no state files given");

        BigInteger? known = knownOf?.Invoke(n.Value);
        if (known is BigInteger kv)
        {
            int kRuns = CrtKnownRuns(bits!.Value, kv);
            BigInteger kBase = BigInteger.One << bits.Value;
            List<ulong> km = CrtModuli(bits.Value, kRuns);
            var kMissing = km.Where(m => !res.ContainsKey(m)).ToList();
            if (kMissing.Count > 0)
                throw new InvalidOperationException($"{kMissing.Count} of {kRuns} run(s) missing, e.g. modulus " +
                    string.Join(", ", kMissing.Take(5).Select(m => m == 0 ? $"2^{bits}" : m.ToString())) +
                    " — finish the remaining parts first");
            foreach (ulong m in km)
                if (res[m] != (ulong)(kv % (m == 0 ? kBase : m)))
                    throw new InvalidOperationException($"residue for modulus {(m == 0 ? $"2^{bits}" : m.ToString())} differs from the known value — rerun that modulus");
            BigInteger kx = CrtCombine(km, km.Select(m => res[m]).ToList(), kBase);
            if (kx != kv) throw new InvalidOperationException("CRT combination differs from the known value despite per-residue checks");
            log?.Invoke($"merged {kRuns} runs, every residue checked against the known value");
            return (kx, n.Value);
        }

        int defaultRuns = CrtSplitRuns(n.Value, bits!.Value);
        List<ulong> moduli = CrtModuli(bits.Value, defaultRuns + res.Count);
        var missing = moduli.Take(defaultRuns).Where(m => !res.ContainsKey(m)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"{missing.Count} of {defaultRuns} run(s) missing, e.g. modulus " +
                string.Join(", ", missing.Take(5).Select(m => m == 0 ? $"2^{bits}" : m.ToString())) +
                " — finish the remaining parts first");

        int runs = defaultRuns;
        while (runs < moduli.Count && res.ContainsKey(moduli[runs])) runs++;   // longest covered prefix
        int checks = CrtCheckCount(bits.Value);
        int combineCount = runs - checks;
        BigInteger baseMod = BigInteger.One << bits.Value;
        var cm = moduli.Take(combineCount).ToList();
        BigInteger x = CrtCombine(cm, cm.Select(m => res[m]).ToList(), baseMod);
        foreach (ulong m in moduli.Take(runs).Skip(combineCount))
            if (x % m != res[m])
                throw new InvalidOperationException(
                    $"verification failed at held-out modulus {m}: the answer is larger than the {combineCount}-run " +
                    "estimate or a residue is corrupt. Rerun the parts with a larger --crt-runs (existing results are reused).");
        if (n >= 2 && !x.IsEven)
            throw new InvalidOperationException("sanity check failed: A007764(n) must be even for n >= 2");
        log?.Invoke($"merged {runs} runs: combined {combineCount} (product ~2^{(int)BigInteger.Log2(cm.Aggregate(BigInteger.One, (a, m) => a * (m == 0 ? baseMod : m)))}), verified with {checks} held out");
        return (x, n.Value);
    }

    /// <summary>CRT over (modulus, residue) pairs; modulus 0 stands for <paramref name="baseMod"/>. Optionally leaves index <paramref name="skip"/> out.</summary>
    private static BigInteger CrtCombine(IList<ulong> moduli, IList<ulong> residues, BigInteger baseMod, int skip = -1)
    {
        BigInteger x = BigInteger.Zero, acc = BigInteger.One;
        bool first = true;
        for (int i = 0; i < moduli.Count; i++)
        {
            if (i == skip) continue;
            BigInteger mi = moduli[i] == 0 ? baseMod : moduli[i];
            if (first) { x = residues[i]; acc = mi; first = false; continue; }
            BigInteger t = ((residues[i] - x) % mi + mi) % mi;
            t = t * ModInverse(acc % mi, mi) % mi;
            x += acc * t;
            acc *= mi;
        }
        return x;
    }

    private static BigInteger ModInverse(BigInteger a, BigInteger m)
    {
        (BigInteger old_r, BigInteger r) = (a % m, m);
        (BigInteger old_s, BigInteger s) = (BigInteger.One, BigInteger.Zero);
        while (r != 0)
        {
            BigInteger q = old_r / r;
            (old_r, r) = (r, old_r - q * r);
            (old_s, s) = (s, old_s - q * s);
        }
        if (old_r != 1) throw new InvalidOperationException("moduli not coprime");
        return ((old_s % m) + m) % m;
    }

    // ------------------------------------------------------------ the sweep

    /// <summary>Run the full sweep; returns the answer counter (TmpLen ulongs).</summary>
    private ulong[] Run<TArith, TStore>(TArith arith, int? maxDop, Action<int, int>? progress,
        string? checkpointPath = null, ulong modulus = 0, LadderRun? ladder = null)
        where TArith : struct, IArith<TStore> where TStore : class
    {
        var engine = new Engine<TArith, TStore>(this, arith, maxDop, checkpointPath, modulus, ladder);
        try { return engine.Sweep(progress); }
        finally { engine.ReleaseStores(); }
    }

    private sealed partial class Engine<TArith, TStore>
        where TArith : struct, IArith<TStore> where TStore : class
    {
        // "SAC2": v2 = flat 12-bit layout; older (chunked) checkpoints are rejected and the sweep restarts.
        private const int CkptMagic = 0x53_41_43_32;
        private readonly DenseCounter _o;
        private readonly TArith _a;
        private readonly ParallelOptions _opts;
        private readonly TStore _value;
        private readonly TStore _deferred;
        private readonly int[]? _groups;     // '.'-vs-paren patterns of grouped left positions
        private readonly string? _ckpt;      // per-sweep checkpoint path (null = disabled)
        private readonly ulong _modulus;     // stamped into the checkpoint header
        private readonly LadderRun? _ladder; // residue ladder: start row / rung dumps (null = plain sweep)
        private readonly ulong[][][] _rt;    // see BuildRightTransitions
        private readonly RtKinds[][] _rk;    // see RtKinds
        private readonly RtEntry[][][] _re;  // see RtEntry
        private readonly int[][][] _rr;      // see BuildRightRuns
        private readonly int[][] _sr;        // see BuildStraddleRuns
        private readonly uint[][] _lt;       // see BuildLeftTable
        private readonly uint[][][] _llt;    // see BuildLeftLLTable
        private readonly uint[][] _lta;      // see BuildAbsTables
        private readonly bool[] _ltaId;      // _lta[h][i] == i for all i (see IsIdentityLayout)
        private readonly uint[][][] _llta;   // see BuildAbsTables
        private readonly StraddleTable[] _st; // see BuildStraddleTables

        // Checkpoints are written at row boundaries but rate-limited: a full
        // dump is ~10 GB at n >= 23, so writing every row would add ~15-20%
        // sweep time (SATA SSD) and terabytes of wear per CRT campaign. A
        // crash costs at most this interval in recompute. SAP_CKPT_EVERY_SEC
        // overrides; the STOP_AFTER_ROW test hook forces every-row writes so
        // crash/resume tests stay deterministic.
        private readonly Stopwatch _ckptTimer = Stopwatch.StartNew();
        private readonly double _ckptMinSec =
            double.TryParse(Environment.GetEnvironmentVariable("SAP_CKPT_EVERY_SEC"), out double s) ? s
            : Environment.GetEnvironmentVariable("SAP_CKPT_STOP_AFTER_ROW") is not null ? 0
            : 600;

        public Engine(DenseCounter o, TArith a, int? maxDop, string? checkpointPath, ulong modulus, LadderRun? ladder)
        {
            _o = o;
            _a = a;
            _ladder = ladder;
            _opts = new ParallelOptions { MaxDegreeOfParallelism = maxDop ?? Environment.ProcessorCount };
            _ckpt = checkpointPath;
            _modulus = modulus;

            _rt = o._rt ??= BuildRightTransitions(o._mc, o._wc);
            _rk = o._rk ??= BuildRightKinds(_rt);
            _re = o._re ??= BuildRightEntries(_rt, _rk);
            _lt = o._lt ??= BuildLeftTable(o._mc, o._wc);
            _llt = o._llt ??= BuildLeftLLTable(o._mc, o._wc);
            _lta = o._lta ??= BuildAbsTable(o._wc, _lt, 0);
            _llta = o._llta ??= BuildAbsLLTables(o._wc, _llt);
            _ltaId = o._ltaId ??= IsIdentityLayout(o._wc, _lta);
            _st = o._st ??= BuildStraddleTables(o._mc, o._wc);
            _rr = o._rr ??= BuildRightRuns(_re, _rk);
            _sr = o._sr ??= BuildStraddleRuns(_st, _lt);
            _value = a.Allocate(o._mc.CodeSize);
            _deferred = a.Allocate(o._wc.CodeSize);

            if (o._mc.LeftWidth >= 3)
            {
                int groupWidth = o._mc.LeftWidth - 2;
                _groups = new int[1 << groupWidth];
                for (int i = 0; i < _groups.Length; i++) _groups[i] = i;
                // Densest groups first: better load balance under work stealing.
                Array.Sort(_groups, (x, y) => int.PopCount(y) - int.PopCount(x));
            }
        }

        // Free natively allocated counter stores now rather than at finalization
        // (a CRT campaign allocates ~11 GB per run at n=24).
        public void ReleaseStores()
        {
            (_value as IDisposable)?.Dispose();
            (_deferred as IDisposable)?.Dispose();
        }

        public ulong[] Sweep(Action<int, int>? progress)
        {
            var mc = _o._mc;
            int cols = _o._cols, rows = _o._rows;

            // Resume from a mid-sweep checkpoint if one matches; else start fresh.
            // At a row boundary the deferred array is fully flushed (the p==1 pass
            // re-absorbs it into _value), so the row index + _value + _deferred is
            // the complete state.
            // The first and last rows are closed-form (E3, research/e3_live_states.py):
            // after row 0 exactly the 2^(n-1) EdgeRowStates are reachable, each
            // with count 1, and before the last row exactly those same states are
            // completable, each in exactly one way. So the sweep starts at row 1
            // from that set and stops before the last row with the sum over it,
            // skipping 2 of n rows. SAP_FULL_SWEEP=1 sweeps all rows (A/B, tests).
            bool full = FullSweep || rows < 2;
            // --mitm: stop at the middle row boundary and join (DenseCounter.Mitm.cs).
            bool mitm = _o.MitmActive;
            int lastRow = mitm ? rows / 2 : full ? rows : rows - 1;
            ulong[] edgeStates = full ? [] : EdgeRowStates(cols);

            int startRow = TryLoadCheckpoint(lastRow);
            if (startRow == 0 && _ladder is { StartRow: > 0 } lr && !full)
            {
                // Residue ladder: the exact row-boundary vector is already known from
                // earlier runs; load it reduced mod this run's modulus (deferred is empty).
                long t0 = Stopwatch.GetTimestamp();
                LoadRung(lr);
                startRow = lr.StartRow;
                if (LadderTiming) Console.Error.WriteLine($"ladder load row {startRow}: {Stopwatch.GetElapsedTime(t0).TotalSeconds:F2}s");
            }
            else if (startRow == 0)
            {
                if (full) _a.SetOne(_value, mc.Encode(Mate.At(cols - 1, MV.R)));   // root: one open end at the left
                else
                {
                    foreach (ulong id in edgeStates) _a.SetOne(_value, mc.Encode(new Mate(id)));
                    startRow = 1;
                }
            }

            // Test hook: SAP_CKPT_STOP_AFTER_ROW makes a *fresh* sweep abort right
            // after checkpointing the given row, so crash/resume can be tested
            // deterministically. Fires only on a fresh sweep (not a resumed one),
            // so a rerun completes instead of looping. No effect when unset.
            bool fresh = startRow == (full ? 0 : 1) && !_resumed;
            int stopAfter = int.TryParse(Environment.GetEnvironmentVariable("SAP_CKPT_STOP_AFTER_ROW"), out int sr) ? sr : -1;

            // Research hook (E2): SAP_DEFERRED_CENSUS=<csv path> counts live
            // (nonzero) deferred slots after every update. Slow; off when unset.
            string? censusPath = Environment.GetEnvironmentVariable("SAP_DEFERRED_CENSUS");
            var census = censusPath is null ? null : new DeferredCensus(censusPath);

            // Research hook: SAP_PTIME=1 prints wall time per update position p.
            long[]? pTime = Environment.GetEnvironmentVariable("SAP_PTIME") == "1" ? new long[cols] : null;

            int first = full ? 0 : 1;
            int total = (lastRow - first) * (cols - 1), done = (startRow - first) * (cols - 1);
            for (int i = startRow; i < lastRow; i++)
            {
                long rowStart = Stopwatch.GetTimestamp();
                for (int j = 0; j < cols - 1; j++)
                {
                    long t0 = pTime is null ? 0 : Stopwatch.GetTimestamp();
                    Update(cols - 1 - j);
                    if (pTime is not null) pTime[cols - 1 - j] += Stopwatch.GetTimestamp() - t0;
                    census?.Record(this, i, cols - 1 - j);
                    progress?.Invoke(++done, total);
                }
                if (RowBits) ReportRowBits(i + 1);
                // The ladder plans from the first measured row time (and may add dump rows here).
                if (_ladder is not null && !full) _ladder.OnRow?.Invoke(i + 1, Stopwatch.GetElapsedTime(rowStart).TotalSeconds);
                if (_ladder is not null && !full && _ladder.DumpRows.Contains(i + 1))
                {
                    long t0 = Stopwatch.GetTimestamp();
                    DumpRung(_ladder, i + 1);
                    if (LadderTiming) Console.Error.WriteLine($"ladder dump row {i + 1}: {Stopwatch.GetElapsedTime(t0).TotalSeconds:F2}s");
                }
                SaveCheckpoint(i + 1, lastRow);
                if (fresh && stopAfter == i + 1)
                    throw new OperationCanceledException($"test stop after row {i + 1}");
            }

            census?.Finish(_o._wc.CodeSize);
            if (pTime is not null)
                for (int q = cols - 1; q >= 1; q--)
                    Console.Error.WriteLine($"ptime p={q} {(q < mc.RightWidth ? "R" : q > mc.RightWidth ? "L" : "G")} {pTime[q] * 1.0 / Stopwatch.Frequency:F3}s");

            var answer = new ulong[_a.TmpLen];
            if (mitm) answer[0] = MitmJoin();
            else if (full) _a.Save(_value, mc.Encode(Mate.At(0, MV.R)), answer);
            else
            {
                TStore acc = _a.Allocate(1);
                foreach (ulong id in edgeStates) _a.Add(acc, 0, _value, mc.Encode(new Mate(id)));
                _a.Save(acc, 0, answer);
                (acc as IDisposable)?.Dispose();
            }
            if (_ckpt is not null && File.Exists(_ckpt)) File.Delete(_ckpt);
            return answer;
        }

        // Residue ladder I/O, in even-aligned chunks (12-bit counters share bytes
        // only within an even/odd pair, so chunks never race).
        // Research hook: SAP_LADDER_TIMING=1 prints the time of each rung load and dump.
        private static readonly bool LadderTiming = Environment.GetEnvironmentVariable("SAP_LADDER_TIMING") == "1";

        private void LoadRung(LadderRun lr)
        {
            long size = _o._mc.CodeSize;
            long chunks = (size + LadderRun.Chunk - 1) / LadderRun.Chunk;
            Parallel.For(0L, chunks, _opts, () => new ulong[LadderRun.Chunk], (c, _, buf) =>
            {
                long start = c * LadderRun.Chunk;
                int len = (int)Math.Min(LadderRun.Chunk, size - start);
                lr.Fill(start, buf.AsSpan(0, len));
                for (int k = 0; k < len; k++) _a.Store(_value, start + k, buf[k]);
                return buf;
            }, _ => { });
        }

        private void DumpRung(LadderRun lr, int boundary)
        {
            long size = _o._mc.CodeSize;
            long chunks = (size + LadderRun.Chunk - 1) / LadderRun.Chunk;
            ulong m = _modulus;
            Parallel.For(0L, chunks, _opts, () => new ulong[LadderRun.Chunk], (c, _, buf) =>
            {
                long start = c * LadderRun.Chunk;
                int len = (int)Math.Min(LadderRun.Chunk, size - start);
                for (int k = 0; k < len; k++)
                {
                    ulong v = _a.Load(_value, start + k);
                    buf[k] = m == 0 ? v : v % m;
                }
                lr.Dump(boundary, start, buf.AsSpan(0, len));
                return buf;
            }, _ => { });
            lr.Dumped.Add(boundary);
        }

        // Research hook: SAP_ROWBITS=1 prints, at every row boundary, the bit length
        // of the largest counter and the nonzero fraction (meaningful in exact mode;
        // sizes the residue-ladder rungs). Slow; off when unset.
        private static readonly bool RowBits = Environment.GetEnvironmentVariable("SAP_ROWBITS") == "1";

        private void ReportRowBits(int boundary)
        {
            long size = _o._mc.CodeSize;
            int maxBits = 0;
            long nonzero = 0;
            object gate = new();
            Parallel.For(0L, size, _opts, () => (0, 0L, new ulong[_a.TmpLen]), (k, _, acc) =>
            {
                _a.Save(_value, k, acc.Item3);
                int bits = 0;
                for (int w = acc.Item3.Length - 1; w >= 0; w--)
                    if (acc.Item3[w] != 0) { bits = 64 * w + 64 - BitOperations.LeadingZeroCount(acc.Item3[w]); break; }
                return (Math.Max(acc.Item1, bits), acc.Item2 + (bits > 0 ? 1 : 0), acc.Item3);
            }, acc => { lock (gate) { maxBits = Math.Max(maxBits, acc.Item1); nonzero += acc.Item2; } });
            Console.Error.WriteLine($"rowbits boundary={boundary} maxbits={maxBits} nonzero={nonzero * 1.0 / size:F4}");
        }

        // E2 research census: per update, live deferred slots, deferred blocks
        // (left-half code) holding any live slot, and those blocks' total size.
        private sealed class DeferredCensus(string path)
        {
            private readonly StreamWriter _w = new(path) { AutoFlush = false };
            private long _peakLive, _peakBlockSlots;
            private bool _header;

            public void Record(Engine<TArith, TStore> e, int row, int p)
            {
                var wc = e._o._wc;
                long live = 0, liveBlocks = 0, blockSlots = 0;
                object gate = new();
                Parallel.For(0L, wc.CodeSizeL, () => (0L, 0L, 0L), (b, _, acc) =>
                {
                    ref readonly var t = ref wc.Table(b);
                    long nz = 0;
                    for (long k = 0; k < t.Size; k++)
                        if (!e._a.IsZero(e._deferred, t.Base + k)) nz++;
                    return nz == 0 ? acc : (acc.Item1 + nz, acc.Item2 + 1, acc.Item3 + t.Size);
                }, acc => { lock (gate) { live += acc.Item1; liveBlocks += acc.Item2; blockSlots += acc.Item3; } });
                if (!_header) { _w.WriteLine("row,p,live,liveBlocks,liveBlockSlots,deferredSize,blocks"); _header = true; }
                _w.WriteLine($"{row},{p},{live},{liveBlocks},{blockSlots},{wc.CodeSize},{wc.CodeSizeL}");
                _peakLive = Math.Max(_peakLive, live);
                _peakBlockSlots = Math.Max(_peakBlockSlots, blockSlots);
            }

            public void Finish(long size)
            {
                _w.Dispose();
                Console.Error.WriteLine($"deferred census: peak live {_peakLive:N0} / {size:N0} = {100.0 * _peakLive / size:F2}%;"
                    + $" peak live-block slots {_peakBlockSlots:N0} = {100.0 * _peakBlockSlots / size:F2}%");
            }
        }

        // Atomically write value+deferred + the next row to resume at. Written to a
        // temp file then moved into place, so a crash mid-write cannot corrupt a
        // good prior checkpoint.
        private static readonly bool FullSweep = Environment.GetEnvironmentVariable("SAP_FULL_SWEEP") == "1";

        /// <summary>
        /// The 2^(cols-1) row-boundary states reachable after row 0 (each exactly
        /// once) and completable before the last row (each exactly once): reading
        /// from the left, any '.'s, the free end ')', then any sequence of '.' and
        /// '(' '.'* ')' (non-nested arcs with only '.' inside).
        /// </summary>
        internal static ulong[] EdgeRowStates(int cols)
        {
            var list = new List<ulong>(1 << Math.Min(cols - 1, 30));
            void After(int k, ulong id)          // position k is next (descending); free end already placed
            {
                if (k < 0) { list.Add(id); return; }
                After(k - 1, id);                                        // '.'
                for (int close = k - 1; close >= 0; close--)             // '(' at k, ')' at close, '.' between
                    After(close - 1, id | ((ulong)MV.L << (2 * k)) | ((ulong)MV.R << (2 * close)));
            }
            for (int f = cols - 1; f >= 0; f--) After(f - 1, (ulong)MV.R << (2 * f));
            if (list.Count != 1L << (cols - 1)) throw new InvalidOperationException("edge-row state count mismatch");
            return [.. list];
        }

        private void SaveCheckpoint(int nextRow, int lastRow)
        {
            if (_ckpt is null) return;
            if (nextRow >= lastRow) return;    // sweep is done; a final checkpoint would only be deleted
            if (_ckptTimer.Elapsed.TotalSeconds < _ckptMinSec) return;
            string tmp = _ckpt + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(CkptMagic);
                bw.Write(_o._n);
                bw.Write(_modulus);
                bw.Write(_o._mc.CodeSize);
                bw.Write(_o._wc.CodeSize);
                bw.Write(nextRow);
                bw.Flush();
                _a.Serialize(_value, fs);
                _a.Serialize(_deferred, fs);
            }
            File.Move(tmp, _ckpt, overwrite: true);
            _ckptTimer.Restart();
        }

        // Load a checkpoint whose header matches this sweep exactly; returns the
        // row to resume at (0 = none/mismatch, i.e. start fresh).
        private bool _resumed;

        private int TryLoadCheckpoint(int lastRow)
        {
            if (_ckpt is null || !File.Exists(_ckpt)) return 0;
            try
            {
                using var fs = new FileStream(_ckpt, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 20);
                using var br = new BinaryReader(fs);
                if (br.ReadInt32() != CkptMagic) return 0;
                if (br.ReadInt32() != _o._n) return 0;
                if (br.ReadUInt64() != _modulus) return 0;
                if (br.ReadInt64() != _o._mc.CodeSize) return 0;
                if (br.ReadInt64() != _o._wc.CodeSize) return 0;
                int nextRow = br.ReadInt32();
                if (nextRow <= 0 || nextRow > lastRow) return 0;
                _a.Deserialize(_value, fs);
                _a.Deserialize(_deferred, fs);
                _resumed = true;
                return nextRow;
            }
            catch (IOException) { return 0; }   // covers EndOfStreamException (truncated file)
        }

        // One grid-vertex update at pair position (p, p-1), in place.
        private void Update(int p)
        {
            var mc = _o._mc;
            if (_groups is not null)
            {
                int ungroupPos = Math.Max(p - mc.RightWidth, 1);
                // Shared-counter work queue, one group per grab, heaviest first.
                // Parallel.For's default range partitioner hands out growing
                // chunks of consecutive indices, which (with the groups sorted
                // densest-first) lumps several of the heaviest groups onto one
                // worker while the rest idle at the per-update barrier —
                // measured 1.68x slower at n=19 on 6 cores.
                int next = -1;
                Parallel.For(0, _opts.MaxDegreeOfParallelism, _opts, _ =>
                {
                    var tmp = new ulong[_a.TmpLen];
                    int i;
                    while ((i = Interlocked.Increment(ref next)) < _groups.Length)
                        UpdateGroup(p, ungroupPos, _groups[i], default, mc.LeftWidth, mc.LeftHeight, tmp);
                });
            }
            else
            {
                var tmp = new ulong[_a.TmpLen];
                for (long i = 0; i < mc.CodeSizeL; i++)
                    UpdateBlock(p, in mc.Table(i), tmp);
            }
        }

        // Enumerate the left mates belonging to one group. Positions
        // ungroupPos/ungroupPos-1 are free (all of '.', ')', '('); every other
        // left position is fixed by the group bit: 0 -> '.', 1 -> paren.
        private void UpdateGroup(int p, int ungroupPos, int group, Mate mL, int w, int h, ulong[] tmp)
        {
            var mc = _o._mc;
            if (w == 0 && 0 <= h && h <= mc.CenterHeight)
            {
                UpdateBlock(p, in mc.Table(mc.EncodeL(mL)), tmp);
                return;
            }
            if (w <= 0 || h < 0 || w < h - mc.CenterHeight) return;

            w--;
            if (w == ungroupPos || w == ungroupPos - 1)
            {
                mL.Set(w, MV.N); UpdateGroup(p, ungroupPos, group, mL, w, h, tmp);
                mL.Set(w, MV.R); UpdateGroup(p, ungroupPos, group, mL, w, h - 1, tmp);
                mL.Set(w, MV.L); UpdateGroup(p, ungroupPos, group, mL, w, h + 1, tmp);
            }
            else
            {
                int k = (w > ungroupPos) ? w - 2 : w;
                if (((group >> k) & 1) != 0)
                {
                    mL.Set(w, MV.R); UpdateGroup(p, ungroupPos, group, mL, w, h - 1, tmp);
                    mL.Set(w, MV.L); UpdateGroup(p, ungroupPos, group, mL, w, h + 1, tmp);
                }
                else
                {
                    mL.Set(w, MV.N); UpdateGroup(p, ungroupPos, group, mL, w, h, tmp);
                }
            }
        }

        // ---- raw mate-string bit helpers (mirror Mate's ops on bare ulongs) ----
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int RawGetPair(ulong id, int p) => (int)((id >> (2 * (p - 1))) & 0xF);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong RawSetPair(ulong id, int p, int w)
            => (id & ~(0xFUL << (2 * (p - 1)))) | ((ulong)w << (2 * (p - 1)));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong RawSetPos(ulong id, int k, ulong v)
            => (id & ~(3UL << (2 * k))) | (v << (2 * k));
        /// <summary>Remove position k (must hold '.'); identical to Mate.Shrink.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong RawShrink(ulong id, int k)
        {
            ulong m = (1UL << (2 * k)) - 1;
            return ((id & ~m) >> 2) | (id & m);
        }

        // One code block of one grid-cell update. Dispatches to a specialized
        // form depending on where the update pair (p, p-1) falls relative to
        // the codec's left/right split: both specializations hoist the
        // block-invariant half of every Encode out of the per-state loop
        // (the left mate is fixed within a block, and codeR[MateR[i]] == i by
        // construction), turning 2-lookup encodes into <= 1 lookup. The
        // straddling position p == RightWidth keeps the generic form.
        private void UpdateBlock(int p, in MateCodec.CodeTable block, ulong[] tmp)
        {
            int wR = _o._mc.RightWidth;
            if (p < wR) UpdateBlockRight(p, in block, tmp);
            else if (p > wR) UpdateBlockLeft(p, in block, tmp);
            else if (p > 1 && Simd12 && !GenericStraddle) UpdateBlockStraddle12(in block);
            else if (p > 1 && _a.Scalar && !GenericStraddle) UpdateBlockStraddle(in block);
            else UpdateBlockGeneric(p, in block, tmp);
        }

        // The frontier transition table for one grid cell, applied in place to
        // every state of one code block. Faithful port of GGCount updateBlock.
        private void UpdateBlockGeneric(int p, in MateCodec.CodeTable block, ulong[] tmp)
        {
            var mc = _o._mc;
            var wc = _o._wc;

            for (long i = 0; i < block.Size; i++)
            {
                Mate mate = block.MateL | block.MateR[i];
                long c = block.Base + i;

                switch (mate.GetPair(p))
                {
                    case MP.NN: // [..] -> may open a new arc [()]
                    {
                        long d = wc.Encode(mate.Shrink(p));
                        if (!_a.IsZero(_value, c))
                        {
                            Mate m2 = mate;
                            m2.SetPair(p, MP.LR);
                            _a.Add(_value, mc.Encode(m2), _value, c);
                        }
                        _a.Add(_value, c, _deferred, d);
                        _a.SetZero(_deferred, d);
                        break;
                    }
                    case MP.NL: // [.(] <-> [(.]
                    case MP.NR: // [.)] <-> [).]
                    {
                        long d = wc.Encode(mate.Shrink(p));
                        Mate m2 = mate;
                        m2.SetPair(p, mate.GetPair(p) == MP.NL ? MP.LN : MP.RN);
                        long cc = mc.Encode(m2);

                        if (p == 1)
                        {
                            _a.Add(_deferred, d, _value, cc);   // d += cc
                            _a.Add(_value, cc, _value, c);      // cc += c
                            _a.Add(_value, c, _deferred, d);    // c += d
                            _a.SetZero(_deferred, d);
                        }
                        else
                        {
                            _a.Save(_value, c, tmp);            // tmp = c
                            _a.Add(_value, c, _value, cc);      // c += cc
                            _a.Add(_value, c, _deferred, d);    // c += d
                            _a.Restore(tmp, _deferred, d);      // d = tmp
                        }
                        break;
                    }
                    case MP.LL: // [((---)] -> [..---(]
                    {
                        Mate m2 = mate;
                        m2.SetPair(p, MP.NN);
                        int q = p - 1, s = 1;
                        while (s > 0)
                        {
                            q--;
                            MV v = m2.Get(q);
                            if (v == MV.L) s++;
                            else if (v == MV.R) s--;
                        }
                        m2.Set(q, MV.L);
                        Emit(p, in m2, c);
                        break;
                    }
                    case MP.RR: // [(---))] -> [)---..]
                    {
                        Mate m2 = mate;
                        m2.SetPair(p, MP.NN);
                        int q = p, s = 1;
                        while (s > 0)
                        {
                            q++;
                            MV v = m2.Get(q);
                            if (v == MV.L) s--;
                            else if (v == MV.R) s++;
                        }
                        m2.Set(q, MV.R);
                        Emit(p, in m2, c);
                        break;
                    }
                    case MP.RL: // [)(] -> [..]  (join two fragments)
                    {
                        Mate m2 = mate;
                        m2.SetPair(p, MP.NN);
                        Emit(p, in m2, c);
                        break;
                    }
                }
            }
        }

        // ---- table-driven form of the p < RightWidth update ----
        //
        // For p < RightWidth every transition depends only on the right mate
        // (and p): the left half is block-fixed, so each target code is a
        // block-invariant base (block.Base for the value array, dBase for the
        // deferred array) plus an offset that is a pure function of
        // (p, split height h, right-mate index i). Those offsets are tabulated
        // once per counter: one ulong per (p, h, i), laid out in MateR order so
        // the update streams it exactly like it used to stream MateR, instead of
        // doing the pair decode, the paren-matching scans (the dominant branch
        // mispredicts in the VTune profile) and the random _codeR gathers per state.
        //   bits 0..2   kind (RtNone/RtNN/RtSwap/RtEmit/RtEscape)
        //   bits 3..7   escape only: unmatched ')' count carried into the left half
        //   bits 8..35  A: value-array offset (NN/swap: cc; emit/escape at p == 1: target)
        //   bits 36..63 D: deferred offset (NN/swap: d; emit/escape at p > 1: target)
        // Offsets are within one right-half block (< 3^16 even at width 32), so 28 bits suffice.
        private const int RtNone = 0, RtNN = 1, RtSwap = 2, RtEmit = 3, RtEscape = 4;
        private const ulong RtMask28 = (1UL << 28) - 1;

        private static ulong RtPack(int kind, long a, long d, int esc = 0)
        {
            if ((ulong)a > RtMask28 || (ulong)d > RtMask28 || (uint)esc > 31)
                throw new InvalidOperationException("transition-table field overflow");
            return (ulong)kind | ((ulong)esc << 3) | ((ulong)a << 8) | ((ulong)d << 36);
        }

        private static ulong[][][] BuildRightTransitions(MateCodec mc, MateCodec wc)
        {
            int wR = mc.RightWidth;
            var rt = new ulong[wR][][];            // index p in 1..wR-1; [0] unused
            for (int p = 1; p < wR; p++)
            {
                rt[p] = new ulong[mc.MaxSplitHeight + 1][];
                for (int h = 0; h <= mc.MaxSplitHeight; h++)
                {
                    Mate[] block = mc.RightBlock(h);
                    var t = rt[p][h] = new ulong[block.Length];
                    for (int i = 0; i < block.Length; i++)
                        t[i] = RightTransition(mc, wc, block[i].Id, p);
                }
            }
            return rt;
        }

        // One table entry: the GGCount transition cases for a right-half pair.
        private static ulong RightTransition(MateCodec mc, MateCodec wc, ulong rightId, int p)
        {
            int wR = mc.RightWidth;
            int pair = RawGetPair(rightId, p);
            switch (pair)
            {
                case MP.NN:
                    return RtPack(RtNN, mc.CodeRAt(RawSetPair(rightId, p, MP.LR)), wc.CodeRAt(RawShrink(rightId, p)));
                case MP.NL:
                case MP.NR:
                    return RtPack(RtSwap, mc.CodeRAt(RawSetPair(rightId, p, pair == MP.NL ? MP.LN : MP.RN)),
                        wc.CodeRAt(RawShrink(rightId, p)));
                case MP.LL:
                {
                    if (p == 1) throw new InvalidOperationException("unexpected '((' at pair (1,0)");
                    ulong r2 = RawSetPair(rightId, p, MP.NN);
                    int q = p - 1, s = 1;
                    while (s > 0)
                    {
                        q--;
                        ulong v = (r2 >> (2 * q)) & 3;
                        if (v == (ulong)MV.L) s++;
                        else if (v == (ulong)MV.R) s--;
                    }
                    r2 = RawSetPos(r2, q, (ulong)MV.L);
                    return RtPack(RtEmit, 0, wc.CodeRAt(RawShrink(r2, p - 1)));
                }
                case MP.RR:
                {
                    ulong r2 = RawSetPair(rightId, p, MP.NN);
                    int q = p, s = 1;
                    while (s > 0)
                    {
                        q++;
                        if (q >= wR)
                        {
                            // Matching '(' is in the left half; s ')'s remain
                            // unmatched. The right half of the result is r2 as is.
                            return p == 1 ? RtPack(RtEscape, mc.CodeRAt(r2), 0, s)
                                          : RtPack(RtEscape, 0, wc.CodeRAt(RawShrink(r2, p - 1)), s);
                        }
                        ulong v = (r2 >> (2 * q)) & 3;
                        if (v == (ulong)MV.L) s--;
                        else if (v == (ulong)MV.R) s++;
                    }
                    r2 = RawSetPos(r2, q, (ulong)MV.R);
                    return p == 1 ? RtPack(RtEmit, mc.CodeRAt(r2), 0)
                                  : RtPack(RtEmit, 0, wc.CodeRAt(RawShrink(r2, p - 1)));
                }
                case MP.RL:
                {
                    ulong r2 = RawSetPair(rightId, p, MP.NN);
                    return p == 1 ? RtPack(RtEmit, mc.CodeRAt(r2), 0)
                                  : RtPack(RtEmit, 0, wc.CodeRAt(RawShrink(r2, p - 1)));
                }
                default:
                    return RtPack(RtNone, 0, 0);
            }
        }

        // Per-(p, h) index lists of the table entries grouped by kind, so the
        // update runs one tight loop per kind instead of a per-state switch
        // (whose indirect jump was the dominant mispredict) and never visits
        // no-op states. Kinds run NN, Swap, Emit, Escape: every emission
        // (Emit/Escape) lands in the deferred slot (or, at p == 1, the value)
        // of an NN state, which must already have consumed that slot this
        // update — so all NN/Swap entries of a block must run before any of
        // its emitters. (The original single pass got this implicitly from the
        // '.', ')', '(' enumeration order, which puts each NN target ahead of
        // its emitter; escaped emissions go to a sibling block of the same
        // group that UpdateGroup visits first.) Within a kind, order is free.
        // (RtKinds itself lives on DenseCounter so every arith shares it.)
        private static RtEntry[][][] BuildRightEntries(ulong[][][] rt, RtKinds[][] rk)
        {
            var re = new RtEntry[rt.Length][][];
            for (int p = 1; p < rt.Length; p++)
            {
                re[p] = new RtEntry[rt[p].Length][];
                for (int h = 0; h < rt[p].Length; h++)
                {
                    RtKinds kinds = rk[p][h];
                    var list = re[p][h] = new RtEntry[kinds.EmitEnd];
                    for (int k = 0; k < kinds.EmitEnd; k++)
                    {
                        int i = kinds.Idx[k];
                        ulong e = rt[p][h][i];
                        list[k] = new RtEntry { I = i, A = (int)((e >> 8) & RtMask28), D = (int)(e >> 36) };
                    }
                }
            }
            return re;
        }

        // Run lists for the 12-bit vector passes. In the recursive ranking the
        // positions below the updated pair are the innermost recursion levels, so
        // a transition maps whole sub-blocks onto sub-blocks in order: consecutive
        // entries whose I, A and D all step by one (runs grow ~2.8x per p, ~350 at
        // p = 9 for n = 20). A run is kept only if it is long enough to vectorize
        // and its A range misses its I range, so doing its entries all at once
        // (reads before writes) matches the sequential loop. Flat pairs (first
        // entry k, length), ascending: [0] = number of NN pairs, then the NN
        // pairs, then the swap pairs (none at p == 1, the row edge's other form).
        private const int MinRun = 16;

        private static int[][][] BuildRightRuns(RtEntry[][][] re, RtKinds[][] rk)
        {
            var rr = new int[re.Length][][];
            for (int p = 1; p < re.Length; p++)
            {
                rr[p] = new int[re[p].Length][];
                for (int h = 0; h < re[p].Length; h++)
                {
                    RtEntry[] l = re[p][h];
                    var runs = new List<int> { 0 };
                    void Scan(int from, int to)
                    {
                        for (int k = from; k < to;)
                        {
                            int j = k + 1;
                            while (j < to && l[j].I == l[j - 1].I + 1 && l[j].A == l[j - 1].A + 1 && l[j].D == l[j - 1].D + 1) j++;
                            int len = j - k;
                            if (len >= MinRun && Math.Abs((long)l[k].A - l[k].I) >= len) { runs.Add(k); runs.Add(len); }
                            k = j;
                        }
                    }
                    Scan(0, rk[p][h].NnEnd);
                    runs[0] = (runs.Count - 1) / 2;
                    if (p > 1) Scan(rk[p][h].NnEnd, rk[p][h].SwapEnd);
                    rr[p][h] = runs.ToArray();
                }
            }
            return rr;
        }

        // Straddle ('.'-left) passes: entries k whose state i = Idx[k], partner
        // offset A[i] and deferred offset lt[i] >> 2 all step by one, within one
        // top-value group (runs ~1000 at n = 20). The passes read and write
        // different buffers for v, the partner block and d, so any run is safe.
        // Flat pairs (first entry k, length), ascending.
        private static int[][] BuildStraddleRuns(StraddleTable[] st, uint[][] lt)
        {
            var sr = new int[st.Length][];
            for (int h = 0; h < st.Length; h++)
            {
                var runs = new List<int>();
                StraddleTable t = st[h];
                if (t is not null)
                {
                    int[] idx = t.Idx;
                    int[] cuts = [0, t.NEnd, t.REnd, idx.Length];
                    for (int g = 0; g < 3; g++)
                        for (int k = cuts[g]; k < cuts[g + 1];)
                        {
                            int j = k + 1;
                            while (j < cuts[g + 1] && idx[j] == idx[j - 1] + 1 && t.A[idx[j]] == t.A[idx[j - 1]] + 1
                                   && (lt[h][idx[j]] >> 2) == (lt[h][idx[j - 1]] >> 2) + 1) j++;
                            if (j - k >= MinRun) { runs.Add(k); runs.Add(j - k); }
                            k = j;
                        }
                }
                sr[h] = runs.ToArray();
            }
            return sr;
        }

        private static RtKinds[][] BuildRightKinds(ulong[][][] rt)
        {
            var rk = new RtKinds[rt.Length][];
            for (int p = 1; p < rt.Length; p++)
            {
                rk[p] = new RtKinds[rt[p].Length];
                for (int h = 0; h < rt[p].Length; h++)
                {
                    ulong[] t = rt[p][h];
                    var lists = new List<int>[5];
                    for (int k = 0; k < 5; k++) lists[k] = new List<int>();
                    for (int i = 0; i < t.Length; i++) lists[(int)(t[i] & 7)].Add(i);
                    var idx = new List<int>();
                    idx.AddRange(lists[RtNN]);
                    int nnEnd = idx.Count;
                    idx.AddRange(lists[RtSwap]);
                    int swapEnd = idx.Count;
                    idx.AddRange(lists[RtEmit]);
                    int emitEnd = idx.Count;
                    idx.AddRange(lists[RtEscape]);
                    rk[p][h] = new RtKinds { Idx = idx.ToArray(), NnEnd = nnEnd, SwapEnd = swapEnd, EmitEnd = emitEnd };
                }
            }
            return rk;
        }

        // Specialized update for p < RightWidth, driven by the precomputed
        // transition table (see BuildRightTransitions) in per-kind passes (see
        // RtKinds for why NN/Swap must precede Emit/Escape). Also gates on zero
        // counts: adding zero is a no-op, so skipping it saves the
        // random-access read-modify-write entirely.
        private void UpdateBlockRight(int p, in MateCodec.CodeTable block, ulong[] tmp)
        {
            var a = _a;                          // locals: lets the JIT keep them in registers
            TStore value = _value, deferred = _deferred;
            int wR = _o._mc.RightWidth;
            long cBase = block.Base;
            long dBase = _o._wc.CodeLRAt(block.MateL.Id >> (2 * wR));
            ulong[] t = _rt[p][block.Height];
            RtKinds rk = _rk[p][block.Height];
            int[] idx = rk.Idx;
            bool edge = p == 1;
            int k = 0;

            if (a.Scalar)
            {
                if (Simd12) UpdateBlockRight12(block, t, rk, idx, _re[p][block.Height], _rr[p][block.Height], cBase, dBase, edge);
                else UpdateBlockRightScalar(block, t, rk, idx, cBase, dBase, edge);
                return;
            }

            for (; k < rk.NnEnd; k++)
            {
                int i = idx[k];
                ulong e = t[i];
                long c = cBase + i;
                long d = dBase + (long)(e >> 36);
                if (!a.IsZero(value, c))
                    a.Add(value, cBase + (long)((e >> 8) & RtMask28), value, c);
                if (!a.IsZero(deferred, d))
                {
                    a.Add(value, c, deferred, d);
                    a.SetZero(deferred, d);
                }
            }

            if (edge)
            {
                for (; k < rk.SwapEnd; k++)
                {
                    int i = idx[k];
                    ulong e = t[i];
                    long c = cBase + i;
                    long d = dBase + (long)(e >> 36);
                    long cc = cBase + (long)((e >> 8) & RtMask28);
                    a.Add(deferred, d, value, cc);
                    a.Add(value, cc, value, c);
                    a.Add(value, c, deferred, d);
                    a.SetZero(deferred, d);
                }
                for (; k < rk.EmitEnd; k++)
                {
                    int i = idx[k];
                    long c = cBase + i;
                    if (a.IsZero(value, c)) continue;
                    a.Add(value, cBase + (long)((t[i] >> 8) & RtMask28), value, c);
                }
            }
            else
            {
                for (; k < rk.SwapEnd; k++)
                {
                    int i = idx[k];
                    ulong e = t[i];
                    long c = cBase + i;
                    long d = dBase + (long)(e >> 36);
                    long cc = cBase + (long)((e >> 8) & RtMask28);
                    if (a.IsZero(value, c) && a.IsZero(value, cc) && a.IsZero(deferred, d)) continue;
                    a.Save(value, c, tmp);
                    a.Add(value, c, value, cc);
                    a.Add(value, c, deferred, d);
                    a.Restore(tmp, deferred, d);
                }
                for (; k < rk.EmitEnd; k++)
                {
                    int i = idx[k];
                    long c = cBase + i;
                    if (a.IsZero(value, c)) continue;
                    a.Add(deferred, dBase + (long)(t[i] >> 36), value, c);
                }
            }

            if (k < idx.Length) EscapeAll(in block, t, idx, k, edge);
        }

        // UpdateBlockRight for single-word counters: each counter is loaded
        // once into a register, combined there, and stored once, instead of
        // re-decoding it per IArith call (the dominant cost of packed 12-bit).
        // Same passes, same order, same arithmetic as the generic form above.
        private void UpdateBlockRightScalar(in MateCodec.CodeTable block, ulong[] t, RtKinds rk, int[] idx,
            long cBase, long dBase, bool edge)
        {
            var a = _a;
            TStore value = _value, deferred = _deferred;
            int k = 0;

            for (; k < rk.NnEnd; k++)
            {
                int i = idx[k];
                ulong e = t[i];
                long c = cBase + i;
                long d = dBase + (long)(e >> 36);
                ulong vc = a.Load(value, c);
                if (vc != 0)
                {
                    long cc = cBase + (long)((e >> 8) & RtMask28);   // cc != c (NN opens an arc)
                    a.Store(value, cc, a.Sum(a.Load(value, cc), vc));
                }
                ulong vd = a.Load(deferred, d);
                if (vd != 0)
                {
                    a.Store(value, c, a.Sum(vc, vd));
                    a.Store(deferred, d, 0);
                }
            }

            if (edge)
            {
                for (; k < rk.SwapEnd; k++)
                {
                    int i = idx[k];
                    ulong e = t[i];
                    long c = cBase + i;
                    long d = dBase + (long)(e >> 36);
                    long cc = cBase + (long)((e >> 8) & RtMask28);
                    // d += cc; cc += c; c += d; d = 0
                    ulong vc = a.Load(value, c), vcc = a.Load(value, cc);
                    ulong vd = a.Sum(a.Load(deferred, d), vcc);
                    a.Store(value, cc, a.Sum(vcc, vc));
                    a.Store(value, c, a.Sum(vc, vd));
                    a.Store(deferred, d, 0);
                }
                for (; k < rk.EmitEnd; k++)
                {
                    int i = idx[k];
                    long c = cBase + i;
                    ulong vc = a.Load(value, c);
                    if (vc == 0) continue;
                    long tg = cBase + (long)((t[i] >> 8) & RtMask28);
                    a.Store(value, tg, a.Sum(a.Load(value, tg), vc));
                }
            }
            else
            {
                for (; k < rk.SwapEnd; k++)
                {
                    int i = idx[k];
                    ulong e = t[i];
                    long c = cBase + i;
                    long d = dBase + (long)(e >> 36);
                    long cc = cBase + (long)((e >> 8) & RtMask28);
                    // tmp = c; c += cc; c += d; d = tmp
                    ulong vc = a.Load(value, c), vcc = a.Load(value, cc), vd = a.Load(deferred, d);
                    if ((vc | vcc | vd) == 0) continue;
                    a.Store(value, c, a.Sum(a.Sum(vc, vcc), vd));
                    a.Store(deferred, d, vc);
                }
                for (; k < rk.EmitEnd; k++)
                {
                    int i = idx[k];
                    long c = cBase + i;
                    ulong vc = a.Load(value, c);
                    if (vc == 0) continue;
                    long tg = dBase + (long)(t[i] >> 36);
                    a.Store(deferred, tg, a.Sum(a.Load(deferred, tg), vc));
                }
            }

            if (k < idx.Length) EscapeAll(in block, t, idx, k, edge);
        }

        // The escaped-RR entries idx[k..] of one block (see EscapeBase).
        private void EscapeAll(in MateCodec.CodeTable block, ulong[] t, int[] idx, int k, bool edge)
        {
            var a = _a;
            TStore value = _value, deferred = _deferred;
            long cBase = block.Base;
            ulong leftRaw = block.MateL.Id >> (2 * _o._mc.RightWidth);
            // Left-half base code per carried ')' count, block-invariant,
            // filled on first use (-1 = not yet).
            Span<long> escBase = stackalloc long[32];
            escBase.Fill(-1);
            for (; k < idx.Length; k++)
            {
                int i = idx[k];
                long c = cBase + i;
                if (a.IsZero(value, c)) continue;
                ulong e = t[i];
                int esc = (int)(e >> 3) & 31;
                long b = escBase[esc];
                if (b < 0) b = escBase[esc] = EscapeBase(leftRaw, esc, edge);
                if (edge) a.Add(value, b + (long)((e >> 8) & RtMask28), value, c);
                else a.Add(deferred, b + (long)(e >> 36), value, c);
            }
        }

        // Left-half part of an escaped RR target: the carried ')' count s is
        // matched by walking up the left half; that '(' becomes ')'. The result
        // is the left code base of the value codec (row edge, p == 1) or of the
        // deferred codec (the p-1 shrink stays in the right half).
        private long EscapeBase(ulong leftRaw, int s, bool edge)
        {
            int k = -1;
            while (s > 0)
            {
                k++;
                ulong v = (leftRaw >> (2 * k)) & 3;
                if (v == (ulong)MV.L) s--;
                else if (v == (ulong)MV.R) s++;
            }
            ulong l = RawSetPos(leftRaw, k, (ulong)MV.R);
            return edge ? _o._mc.CodeLRAt(l) : _o._wc.CodeLRAt(l);
        }

        // ---- 12-bit left-half loops on unpacked value blocks (AVX2) ----
        //
        // In a left update the value counters of a block (c = cBase + i) and of
        // its partner block (cc = partner + i) are contiguous ranges; only the
        // deferred slots are scattered. So the block's values are unpacked into a
        // plain ushort scratch buffer (AVX2, 16 counters per step), the loop runs
        // on that buffer with scalar packed access for the deferred slots only,
        // and the buffer is packed back if it changed. The NN target block write
        // (V[encBase + i] += V[cBase + i]) is a pure contiguous mod-add. Same
        // arithmetic, same targets; within one block the order of states does not
        // matter (distinct d's, see UpdateBlockStraddle). SAP_NO_SIMD=1 disables.
        private static readonly bool Simd12 = typeof(TStore) == typeof(Packed12Store) && Packed12Simd.Supported
            && Environment.GetEnvironmentVariable("SAP_NO_SIMD") != "1";

        [ThreadStatic] private static ushort[]? t_c, t_cc;

        private static ushort[] Scratch(ref ushort[]? buf, int n)
        {
            if (buf is null || buf.Length < n) buf = new ushort[Math.Max(n, 1024)];
            return buf;
        }

        [ThreadStatic] private static ushort[]? t_d;

        // Lazy modular reduction: inside an unpacked block values are summed with
        // plain adds and reduced once, in PackReduce. Every buffer value enters
        // below m, and no counter gains more than a few terms per block-update
        // (right path: NN target < 2m, swap < 3m, deferred emit target < 4m,
        // row-edge value target < 5m; left and straddle paths < 3m, see there),
        // far below PackReduce's 16m limit.
        // UpdateBlockRight on unpacked buffers: for p < RightWidth every value
        // access of a block (c, cc, edge-emit targets) lies in the block itself and
        // every deferred access (except escapes) in the block's own deferred block
        // D(B). So both are unpacked, the passes run on plain ushorts, both are
        // packed back, and only then do the escapes run (they read the block's
        // values and write other blocks, through the packed store as before).
        private unsafe void UpdateBlockRight12(in MateCodec.CodeTable block, ulong[] t, RtKinds rk, int[] idx, RtEntry[] re,
            int[] runs, long cBase, long dBase, bool edge)
        {
            byte* vb = ((Packed12Store)(object)_value).Ptr;
            byte* db = ((Packed12Store)(object)_deferred).Ptr;
            uint m = ((Mod12Arith)(object)_a).M;
            int n = (int)block.Size;
            BlockOf(_o._wc, block.MateL.Id >> (2 * _o._mc.RightWidth), out _, out int dn);   // see the comment above
            ushort[] cbuf = Scratch(ref t_c, n), dbuf = Scratch(ref t_d, dn);
            int nnEnd = rk.NnEnd, swapEnd = rk.SwapEnd, emitEnd = rk.EmitEnd;
            fixed (ushort* v = cbuf, d = dbuf)
            fixed (RtEntry* ep = re)
            {
                Packed12Simd.Unpack(vb, cBase, n, v);
                Packed12Simd.Unpack(db, dBase, dn, d);
                RtEntry* e = ep, nnStop = ep + nnEnd, swapStop = ep + swapEnd, emitStop = ep + emitEnd;
                int r = 1, nnRunEnd = 1 + 2 * runs[0];
                for (; r < nnRunEnd; r += 2)                        // NN runs, 16 at a time (see BuildRightRuns)
                {
                    RtEntry* rs = ep + runs[r];
                    for (; e < rs; e++)
                    {
                        uint vc = v[e->I];
                        v[e->A] += (ushort)vc;
                        ushort* dd = d + e->D;
                        v[e->I] = (ushort)(vc + *dd);
                        *dd = 0;
                    }
                    ushort* ci = v + rs->I, ca = v + rs->A, cd = d + rs->D;
                    int len = runs[r + 1] & ~15;
                    for (int j = 0; j < len; j += 16)
                    {
                        var vc = Vector256.Load(ci + j);
                        (Vector256.Load(ca + j) + vc).Store(ca + j);
                        (vc + Vector256.Load(cd + j)).Store(ci + j);
                        Vector256<ushort>.Zero.Store(cd + j);
                    }
                    e = rs + len;                                   // the run's tail goes through the scalar loop
                }
                for (; e < nnStop; e++)
                {
                    uint vc = v[e->I];
                    v[e->A] += (ushort)vc;                          // cc += c (old c; an add of 0 is harmless)
                    ushort* dd = d + e->D;
                    v[e->I] = (ushort)(vc + *dd);                   // c += d; d = 0
                    *dd = 0;
                }
                if (edge)
                {
                    for (; e < swapStop; e++)
                    {
                        ushort* cc = v + e->A;
                        ushort* dd = d + e->D;
                        uint vc = v[e->I], vcc = *cc;               // d += cc; cc += c; c += d; d = 0
                        uint vd = *dd + vcc;
                        *cc = (ushort)(vcc + vc);
                        v[e->I] = (ushort)(vc + vd);
                        *dd = 0;
                    }
                    for (; e < emitStop; e++) v[e->A] += v[e->I];
                }
                else
                {
                    for (; r < runs.Length; r += 2)                 // swap runs, 16 at a time
                    {
                        RtEntry* rs = ep + runs[r];
                        for (; e < rs; e++)
                        {
                            ushort* dd = d + e->D;
                            uint vc = v[e->I];
                            v[e->I] = (ushort)(vc + v[e->A] + *dd);
                            *dd = (ushort)vc;
                        }
                        ushort* ci = v + rs->I, ca = v + rs->A, cd = d + rs->D;
                        int len = runs[r + 1] & ~15;
                        for (int j = 0; j < len; j += 16)
                        {
                            var vc = Vector256.Load(ci + j);
                            (vc + Vector256.Load(ca + j) + Vector256.Load(cd + j)).Store(ci + j);
                            vc.Store(cd + j);
                        }
                        e = rs + len;
                    }
                    for (; e < swapStop; e++)
                    {
                        ushort* dd = d + e->D;
                        uint vc = v[e->I];                          // tmp = c; c += cc; c += d; d = tmp
                        v[e->I] = (ushort)(vc + v[e->A] + *dd);
                        *dd = (ushort)vc;
                    }
                    for (; e < emitStop; e++) d[e->D] += v[e->I];
                }
                Packed12Simd.PackReduce(v, vb, cBase, n, m);
                Packed12Simd.PackReduce(d, db, dBase, dn, m);
            }
            if (emitEnd < idx.Length) EscapeAll(in block, t, idx, emitEnd, edge);
        }

        // ---- the canonical 3-block deferred layout of a left/straddle update ----
        //
        // A left (or straddle) update of a block touches up to three deferred
        // blocks, one per top right value v (wc left raw = dTop[4] | v, base
        // dTop[v]). Their split heights are H + delta(v) with delta('.') = 0,
        // delta(')') = -1, delta('(') = +1, where H depends only on the block's
        // height h and the case: H = h for NN, swap, straddle, LL closing in the
        // left half, RR and RL (the shrink removes a '.', or the case removes or
        // flips a balanced pair), H = h - 2 for LL continuing into the right half.
        // So the three blocks' sizes, and hence their offsets in one scratch
        // buffer ('.', ')', '(' in that order), are fixed per (h, case), and the
        // per-state tables can hold final buffer offsets (_lta, _llta). Slot
        // `total` is a dummy that absorbs LtNone entries (branch-free emits).
        private static int WcSize(MateCodec wc, int h) => h >= 0 && h <= wc.MaxSplitHeight ? wc.RightBlock(h).Length : 0;

        private static int LayoutStart(MateCodec wc, int H, int v) => v switch
        {
            (int)MV.N => 0,
            (int)MV.R => WcSize(wc, H),
            _ => WcSize(wc, H) + WcSize(wc, H - 1),
        };

        private static int LayoutTotal(MateCodec wc, int H) => WcSize(wc, H) + WcSize(wc, H - 1) + WcSize(wc, H + 1);

        private static int LayoutSize(MateCodec wc, int H, int v) =>
            WcSize(wc, H + (v == (int)MV.R ? -1 : v == (int)MV.L ? 1 : 0));

        private static uint[][] BuildAbsTable(MateCodec wc, uint[][] lt, int dh)
        {
            var abs = new uint[lt.Length][];
            for (int h = 0; h < lt.Length; h++)
            {
                int H = h + dh;
                uint dummy = (uint)LayoutTotal(wc, H);
                var t = abs[h] = new uint[lt[h].Length];
                for (int i = 0; i < t.Length; i++)
                {
                    uint e = lt[h][i];
                    t[i] = e == LtNone ? dummy : (uint)LayoutStart(wc, H, (int)(e & 3)) + (e >> 2);
                }
            }
            return abs;
        }

        // FillCodeR enumerates a right block by its top symbol first, in the order
        // '.', ')', '(' (= the canonical layout's segment order), and below the top
        // in the same recursive order as the wc right blocks of width wR-1. So for
        // an unchanged right half the canonical offset of state i is i itself, and
        // the 12-bit left loops can run as straight vector passes (LeftNN12 etc.).
        // Checked per h rather than assumed; a false entry keeps the table path.
        private static bool[] IsIdentityLayout(MateCodec wc, uint[][] lta)
        {
            var id = new bool[lta.Length];
            for (int h = 0; h < lta.Length; h++)
            {
                uint[] t = lta[h];
                bool ok = t.Length == LayoutTotal(wc, h);
                for (int i = 0; ok && i < t.Length; i++) ok = t[i] == (uint)i;
                id[h] = ok;
            }
            return id;
        }

        private static uint[][][] BuildAbsLLTables(MateCodec wc, uint[][][] llt)
        {
            var abs = new uint[llt.Length][][];
            for (int s = 1; s < llt.Length; s++) abs[s] = BuildAbsTable(wc, llt[s], -2);
            return abs;
        }

        // Unpack the existing blocks of the canonical layout for height H into buf
        // (seg[v] = canonical start, len[v] = size, 0 when the block does not exist).
        // A size that differs from the layout's would mean the height derivation
        // above is wrong for this case: fail loudly rather than corrupt counts.
        private unsafe void UnpackTops(Span<long> dTop, int H, ushort* buf, int* seg, int* len)
        {
            var wc = _o._wc;
            byte* db = ((Packed12Store)(object)_deferred).Ptr;
            for (int v = 0; v <= 2; v++)
            {
                seg[v] = LayoutStart(wc, H, v);
                len[v] = 0;
                if (!BlockOf(wc, (ulong)dTop[4] | (ulong)v, out long bas, out int size) || bas != dTop[v])
                {
                    new Span<ushort>(buf + seg[v], LayoutSize(wc, H, v)).Clear();   // missing block: zeros, never packed back
                    continue;
                }
                if (size != LayoutSize(wc, H, v))
                    throw new InvalidOperationException($"deferred layout mismatch (H={H}, v={v}: {size} vs {LayoutSize(wc, H, v)})");
                len[v] = size;
                Packed12Simd.Unpack(db, bas, size, buf + seg[v]);
            }
            buf[LayoutTotal(wc, H)] = 0;                    // dummy slot
        }

        private unsafe void PackTops(Span<long> dTop, ushort* buf, int* seg, int* len, uint m)
        {
            byte* db = ((Packed12Store)(object)_deferred).Ptr;
            for (int v = 0; v <= 2; v++)
                if (len[v] > 0) Packed12Simd.PackReduce(buf + seg[v], db, dTop[v], len[v], m);
        }

        private int TopsSize(int H) => LayoutTotal(_o._wc, H) + 1;  // + dummy slot

        // A codec block by raw left id, if that left exists.
        private static bool BlockOf(MateCodec codec, ulong raw, out long bas, out int size)
        {
            ref readonly var tb = ref codec.Table(codec.EncodeL(new Mate(raw)));
            bool ok = (tb.MateL.Id >> (2 * codec.RightWidth)) == raw;
            bas = ok ? tb.Base : 0;
            size = ok ? (int)tb.Size : 0;
            return ok;
        }

        [ThreadStatic] private static ushort[]? t_l, t_r;

        // UpdateBlockStraddle on unpacked buffers (same passes, targets and order).
        // vL == '.': the block, its bottom-'(' and bottom-')' partner blocks (NN
        // target / swap partners, at split height h+1 / h-1, addressed by A[i]) and
        // the three deferred blocks of the shrunk left (by top value, as dTop).
        // vL == '(' / ')': the block (read only) and the one or two emit blocks.
        private unsafe void UpdateBlockStraddle12(in MateCodec.CodeTable block)
        {
            var mc = _o._mc;
            var wc = _o._wc;
            var a = _a;
            byte* vb = ((Packed12Store)(object)_value).Ptr;
            byte* db = ((Packed12Store)(object)_deferred).Ptr;
            int wR = mc.RightWidth;
            ulong leftRaw = block.MateL.Id >> (2 * wR);
            long cBase = block.Base;
            int n = (int)block.Size;
            uint[] lt = _lt[block.Height];
            StraddleTable st = _st[block.Height];
            int[] idx = st.Idx;
            uint[] at = st.A;
            ulong leftN = leftRaw & ~3UL;
            ushort[] cbuf = Scratch(ref t_c, n);

            switch ((MV)(leftRaw & 3))
            {
                case MV.N:
                {
                    BlockOf(mc, leftRaw | (ulong)MV.L, out long baseL, out int nL);
                    BlockOf(mc, leftRaw | (ulong)MV.R, out long baseR, out int nR);
                    Span<long> dTop = stackalloc long[5];
                    for (int x = 0; x <= 2; x++) dTop[x] = wc.CodeLRAt(leftN | (ulong)x);
                    dTop[4] = (long)leftN;
                    ushort[] lbuf = Scratch(ref t_l, nL), rbuf = Scratch(ref t_r, nR), dbuf = Scratch(ref t_d, TopsSize(block.Height));
                    int* seg = stackalloc int[3], len = stackalloc int[3];
                    fixed (ushort* v = cbuf, lv = lbuf, rv = rbuf, dp = dbuf)
                    {
                        Packed12Simd.Unpack(vb, cBase, n, v);
                        if (nL > 0) Packed12Simd.Unpack(vb, baseL, nL, lv);
                        if (nR > 0) Packed12Simd.Unpack(vb, baseR, nR, rv);
                        UnpackTops(dTop, block.Height, dp, seg, len);
                        ushort* d0 = dp + seg[(int)MV.N], dR = dp + seg[(int)MV.R], dL = dp + seg[(int)MV.L];
                        // Scalar entries up to each run, then the run 16 at a time
                        // (see BuildStraddleRuns); its tail rejoins the scalar loop.
                        int[] sruns = _sr[block.Height];
                        int k = 0, sr = 0;
                        int NextRun(int end) => sr < sruns.Length && sruns[sr] < end ? sruns[sr] : end;
                        for (int stop = NextRun(st.NEnd); ; stop = NextRun(st.NEnd))     // [..]: may open [()]
                        {
                            for (; k < stop; k++)
                            {
                                int i = idx[k];
                                uint vc = v[i];
                                if (vc != 0 && nL > 0)                      // a missing target left drops the state
                                    lv[at[i]] += (ushort)vc;                // lazy: one term per target (< 2m)
                                ushort* d = d0 + (lt[i] >> 2);
                                v[i] = (ushort)(vc + *d);
                                *d = 0;
                            }
                            if (k >= st.NEnd) break;
                            int i0 = idx[k], rl = sruns[sr + 1] & ~15;
                            ushort* ci = v + i0, cl = lv + at[i0], cd = d0 + (lt[i0] >> 2);
                            for (int j = 0; j < rl; j += 16)
                            {
                                var vc = Vector256.Load(ci + j);
                                if (nL > 0) (Vector256.Load(cl + j) + vc).Store(cl + j);
                                (vc + Vector256.Load(cd + j)).Store(ci + j);
                                Vector256<ushort>.Zero.Store(cd + j);
                            }
                            k += rl;
                            sr += 2;
                        }
                        for (int g = 1; g <= 2; g++)                        // [.)] <-> [).], then [.(] <-> [(.]
                        {
                            int end = g == 1 ? st.REnd : idx.Length;
                            ushort* pv = g == 1 ? rv : lv, dg = g == 1 ? dR : dL;
                            bool has = (g == 1 ? nR : nL) > 0;
                            for (int stop = NextRun(end); ; stop = NextRun(end))
                            {
                                for (; k < stop; k++)
                                {
                                    int i = idx[k];
                                    ushort* d = dg + (lt[i] >> 2);
                                    uint vc = v[i];
                                    v[i] = (ushort)(vc + (has ? pv[at[i]] : 0u) + *d);   // partners are never NN targets: < 3m
                                    *d = (ushort)vc;
                                }
                                if (k >= end) break;
                                int i0 = idx[k], rl = sruns[sr + 1] & ~15;
                                ushort* ci = v + i0, cp = pv + at[i0], cd = dg + (lt[i0] >> 2);
                                for (int j = 0; j < rl; j += 16)
                                {
                                    var vc = Vector256.Load(ci + j);
                                    var vp = has ? Vector256.Load(cp + j) : Vector256<ushort>.Zero;
                                    (vc + vp + Vector256.Load(cd + j)).Store(ci + j);
                                    vc.Store(cd + j);
                                }
                                k += rl;
                                sr += 2;
                            }
                        }
                        uint m = ((Mod12Arith)(object)a).M;
                        Packed12Simd.PackReduce(v, vb, cBase, n, m);
                        if (nL > 0) Packed12Simd.PackReduce(lv, vb, baseL, nL, m);
                        PackTops(dTop, dp, seg, len, m);
                    }
                    break;
                }
                case MV.L:
                {
                    // [((---)] -> [..---(]
                    if (!BlockOf(wc, leftN, out long dB, out int nd)) break;
                    uint[] df = st.DFlip;
                    ushort[] dbuf = Scratch(ref t_d, nd);
                    fixed (ushort* v = cbuf, d = dbuf)
                    {
                        Packed12Simd.Unpack(vb, cBase, n, v);
                        Packed12Simd.Unpack(db, dB, nd, d);
                        for (int k = st.REnd; k < idx.Length; k++)
                        {
                            int i = idx[k];
                            uint vc = v[i];
                            if (vc == 0) continue;
                            if (df[i] == LtNone) throw new InvalidOperationException("straddle LL: no matching ')' in the right half");
                            d[df[i]] += (ushort)vc;                         // LL is injective: < 2m
                        }
                        Packed12Simd.PackReduce(d, db, dB, nd, ((Mod12Arith)(object)a).M);
                    }
                    break;
                }
                case MV.R:
                {
                    // RR (vR ')'): the '(' matching the bottom-left ')' becomes ')'. RL (vR '('): join.
                    ulong rrRaw = ulong.MaxValue;
                    for (int q = 1, sc = 1; q < mc.LeftWidth; q++)
                    {
                        ulong x = (leftN >> (2 * q)) & 3;
                        if (x == (ulong)MV.R) sc++;
                        else if (x == (ulong)MV.L && --sc == 0) { rrRaw = RawSetPos(leftN, q, (ulong)MV.R); break; }
                    }
                    long dRR = 0;
                    int nRR = 0;
                    bool hasRR = rrRaw != ulong.MaxValue && BlockOf(wc, rrRaw, out dRR, out nRR);
                    bool hasRL = BlockOf(wc, leftN, out long dRL, out int nRL);
                    ushort[] dbuf = Scratch(ref t_d, nRR + nRL);
                    fixed (ushort* v = cbuf, d = dbuf)
                    {
                        Packed12Simd.Unpack(vb, cBase, n, v);
                        ushort* drr = d, drl = d + nRR;
                        if (hasRR) Packed12Simd.Unpack(db, dRR, nRR, drr);
                        if (hasRL) Packed12Simd.Unpack(db, dRL, nRL, drl);
                        for (int k = st.NEnd; k < st.REnd; k++)
                        {
                            int i = idx[k];
                            uint vc = v[i];
                            if (vc == 0) continue;
                            if (!hasRR) throw new InvalidOperationException("straddle RR: no matching '(' in the left half");
                            drr[lt[i] >> 2] += (ushort)vc;                  // injective: < 2m
                        }
                        for (int k = st.REnd; k < idx.Length; k++)
                        {
                            int i = idx[k];
                            uint vc = v[i];
                            if (vc == 0) continue;
                            if (!hasRL) throw new InvalidOperationException("straddle RL: missing deferred block");
                            drl[lt[i] >> 2] += (ushort)vc;                  // injective: < 2m
                        }
                        uint m = ((Mod12Arith)(object)a).M;
                        if (hasRR) Packed12Simd.PackReduce(drr, db, dRR, nRR, m);
                        if (hasRL) Packed12Simd.PackReduce(drl, db, dRL, nRL, m);
                    }
                    break;
                }
            }
        }

        // Identity-layout forms of the left loops (state i <-> canonical slot i):
        // the same lazy sums as the table loops, 16 states per AVX2 step.
        private static unsafe void IdNN(ushort* c, ushort* d, int n)        // c += d; d = 0
        {
            int i = 0;
            for (; i + 16 <= n; i += 16)
            {
                (Vector256.Load(c + i) + Vector256.Load(d + i)).Store(c + i);
                Vector256<ushort>.Zero.Store(d + i);
            }
            for (; i < n; i++) { c[i] += d[i]; d[i] = 0; }
        }

        private static unsafe void IdSwap(ushort* c, ushort* cc, ushort* d, int n)   // tmp = c; c += cc + d; d = tmp
        {
            int i = 0;
            for (; i + 16 <= n; i += 16)
            {
                var vc = Vector256.Load(c + i);
                (vc + Vector256.Load(cc + i) + Vector256.Load(d + i)).Store(c + i);
                vc.Store(d + i);
            }
            for (; i < n; i++) { ushort vc = c[i]; c[i] = (ushort)(vc + cc[i] + d[i]); d[i] = vc; }
        }

        private static unsafe void IdEmit(ushort* c, ushort* d, int n)      // d += c
        {
            int i = 0;
            for (; i + 16 <= n; i += 16)
                (Vector256.Load(d + i) + Vector256.Load(c + i)).Store(d + i);
            for (; i < n; i++) d[i] += c[i];
        }

        // encValid: the NN target left exists (else its states are dropped, as in
        // the scalar loop, which only wrote there for nonzero counts that cannot occur).
        // at: absolute deferred offsets in the canonical layout for height H.
        private unsafe void LeftNN12(uint[] at, Span<long> dTop, int H, long cBase, long encBase, bool encValid)
        {
            byte* b = ((Packed12Store)(object)_value).Ptr;
            uint m = ((Mod12Arith)(object)_a).M;
            int n = at.Length;
            // cc += c (old c) first, exactly as the per-state loop did.
            if (encValid) Packed12Simd.AddRange(b, encBase, cBase, n, m);
            ushort[] cbuf = Scratch(ref t_c, n), dbuf = Scratch(ref t_d, TopsSize(H));
            int* seg = stackalloc int[3], len = stackalloc int[3];
            fixed (ushort* cp = cbuf, dp = dbuf)
            fixed (uint* tp = at)
            {
                Packed12Simd.Unpack(b, cBase, n, cp);
                UnpackTops(dTop, H, dp, seg, len);
                if (_ltaId[H] && ReferenceEquals(at, _lta[H])) IdNN(cp, dp, n);
                else for (int i = 0; i < n; i++)             // c += d; d = 0 (lazy: < 2m)
                {
                    ushort* d = dp + tp[i];
                    cp[i] += *d;
                    *d = 0;
                }
                Packed12Simd.PackReduce(cp, b, cBase, n, m);
                PackTops(dTop, dp, seg, len, m);
            }
        }

        private unsafe void LeftSwap12(uint[] at, Span<long> dTop, int H, long cBase, long ccBase, bool ccValid)
        {
            byte* b = ((Packed12Store)(object)_value).Ptr;
            uint m = ((Mod12Arith)(object)_a).M;
            int n = at.Length;
            ushort[] cbuf = Scratch(ref t_c, n), ccbuf = Scratch(ref t_cc, n), dbuf = Scratch(ref t_d, TopsSize(H));
            int* seg = stackalloc int[3], len = stackalloc int[3];
            fixed (ushort* cp = cbuf, ccp = ccbuf, dp = dbuf)
            fixed (uint* tp = at)
            {
                Packed12Simd.Unpack(b, cBase, n, cp);
                if (ccValid) Packed12Simd.Unpack(b, ccBase, n, ccp);
                else new Span<ushort>(ccp, n).Clear();    // missing partner: its counts are zero
                UnpackTops(dTop, H, dp, seg, len);
                if (_ltaId[H] && ReferenceEquals(at, _lta[H])) IdSwap(cp, ccp, dp, n);
                else for (int i = 0; i < n; i++)             // tmp = c; c += cc; c += d; d = tmp (lazy: < 3m)
                {
                    ushort* d = dp + tp[i];
                    uint vc = cp[i];
                    cp[i] = (ushort)(vc + ccp[i] + *d);
                    *d = (ushort)vc;
                }
                Packed12Simd.PackReduce(cp, b, cBase, n, m);
                PackTops(dTop, dp, seg, len, m);
            }
        }

        // Branch-free: zero counts add nothing, and LtNone entries point at the
        // dummy slot, which therefore must still be zero afterwards.
        private unsafe void LeftEmit12(uint[] at, Span<long> dTop, int H, long cBase)
        {
            byte* b = ((Packed12Store)(object)_value).Ptr;
            int n = at.Length;
            ushort[] cbuf = Scratch(ref t_c, n), dbuf = Scratch(ref t_d, TopsSize(H));
            int* seg = stackalloc int[3], len = stackalloc int[3];
            fixed (ushort* cp = cbuf, dp = dbuf)
            fixed (uint* tp = at)
            {
                Packed12Simd.Unpack(b, cBase, n, cp);
                UnpackTops(dTop, H, dp, seg, len);
                if (_ltaId[H] && ReferenceEquals(at, _lta[H])) IdEmit(cp, dp, n);
                else for (int i = 0; i < n; i++)             // lazy: one case per block, each target gains one term (< 2m)
                    dp[tp[i]] += cp[i];
                if (dp[LayoutTotal(_o._wc, H)] != 0)
                    throw new InvalidOperationException("left update: no matching ')' in the right half");
                PackTops(dTop, dp, seg, len, ((Mod12Arith)(object)_a).M);
            }
        }

        // ---- tables for the p > RightWidth update ----
        //
        // There the right half passes through unchanged (up to one ')'->'('
        // flip in the LL-continue case), and a state's deferred code is
        //   dTop[top right position] + wc.codeR[right mate minus its top position]
        // where only dTop depends on the block. The per-state half depends only
        // on (split height h, right-mate index i), so it is tabulated once:
        //   bits 0..1  top right position (indexes dTop)
        //   bits 2..31 wc right offset
        // _lt[h][i] covers the unchanged right mate; _llt[s][h][i] the LL case
        // whose matching ')' is the s-th unmatched one inside the right half
        // (uint.MaxValue where the right mate has fewer than s).
        private const uint LtNone = uint.MaxValue;

        private static uint LtPack(MateCodec wc, ulong rightId, int wR)
        {
            ulong off = (ulong)wc.CodeRAt(rightId & ((1UL << (2 * (wR - 1))) - 1));
            if (off >= 1UL << 30) throw new InvalidOperationException("left-update table offset exceeds 30 bits");
            return (uint)(off << 2) | (uint)(rightId >> (2 * (wR - 1)));
        }

        private static uint[][] BuildLeftTable(MateCodec mc, MateCodec wc)
        {
            var lt = new uint[mc.MaxSplitHeight + 1][];
            for (int h = 0; h <= mc.MaxSplitHeight; h++)
            {
                Mate[] block = mc.RightBlock(h);
                lt[h] = new uint[block.Length];
                for (int i = 0; i < block.Length; i++) lt[h][i] = LtPack(wc, block[i].Id, mc.RightWidth);
            }
            return lt;
        }

        private static uint[][][] BuildLeftLLTable(MateCodec mc, MateCodec wc)
        {
            int wR = mc.RightWidth;
            var llt = new uint[wR + 1][][];         // s in 1..wR; [0] unused
            for (int s = 1; s <= wR; s++)
            {
                llt[s] = new uint[mc.MaxSplitHeight + 1][];
                for (int h = 0; h <= mc.MaxSplitHeight; h++)
                {
                    Mate[] block = mc.RightBlock(h);
                    var t = llt[s][h] = new uint[block.Length];
                    for (int i = 0; i < block.Length; i++)
                    {
                        ulong r2 = block[i].Id;
                        int q = wR, s2 = s;
                        while (s2 > 0 && q > 0)
                        {
                            q--;
                            ulong v = (r2 >> (2 * q)) & 3;
                            if (v == (ulong)MV.L) s2++;
                            else if (v == (ulong)MV.R) s2--;
                        }
                        t[i] = s2 == 0 ? LtPack(wc, RawSetPos(r2, q, (ulong)MV.L), wR) : LtNone;
                    }
                }
            }
            return llt;
        }

        // Specialized update for p > RightWidth: the pair (p, p-1) lies entirely
        // in the (block-fixed) left half, so the CASE ITSELF is block-invariant —
        // the whole block runs one tight single-case loop, and blocks whose left
        // pair is inert ('(.'/').'/'()' pointing the wrong way) skip outright.
        // The right half of every produced state is unchanged, so the mc encode
        // is a precomputed base plus the loop index (codeR[MateR[i]] == i); the
        // wc (deferred) encode is a 3-entry base table (dTop, indexed by the top
        // right position, which the shrink pulls into the wc codec's left half)
        // plus a per-state offset streamed from _lt / _llt.
        private void UpdateBlockLeft(int p, in MateCodec.CodeTable block, ulong[] tmp)
        {
            var mc = _o._mc;
            var wc = _o._wc;
            var a = _a;                          // locals: lets the JIT keep them in registers
            TStore value = _value, deferred = _deferred;
            int wR = mc.RightWidth;
            int pl = p - wR;                     // pair position within the left half
            ulong leftRaw = block.MateL.Id >> (2 * wR);
            int pairL = RawGetPair(leftRaw, pl);
            long cBase = block.Base;
            uint[] lt = _lt[block.Height];
            Span<long> dTop = stackalloc long[5];   // [4] = raw wc-left base (for the 12-bit unpacked path)

            // wc-left base table for a shrink at left position k of left id lid:
            // the wc left half is (lid minus k) extended below by the top right
            // position, which varies over only three values.
            void FillDTop(Span<long> t, ulong lid, int k)
            {
                ulong shr = RawShrink(lid, k);
                for (int v = 0; v <= 2; v++) t[v] = wc.CodeLRAt((shr << 2) | (ulong)v);
                t[3] = 0;   // MV.X never occurs in valid strings
                t[4] = (long)(shr << 2);
            }

            switch (pairL)
            {
                case MP.NN:
                {
                    long encBase = mc.CodeLRAt(RawSetPair(leftRaw, pl, MP.LR));
                    FillDTop(dTop, leftRaw, pl);
                    if (Simd12)
                    {
                        LeftNN12(_lta[block.Height], dTop, block.Height, cBase, encBase,
                            BlockOf(mc, RawSetPair(leftRaw, pl, MP.LR), out _, out int encN) && encN == lt.Length);
                        break;
                    }
                    if (a.Scalar)
                    {
                        for (int i = 0; i < lt.Length; i++)
                        {
                            uint e = lt[i];
                            long c = cBase + i;
                            long d = dTop[(int)(e & 3)] + (e >> 2);
                            ulong vc = a.Load(value, c);
                            if (vc != 0)
                                a.Store(value, encBase + i, a.Sum(a.Load(value, encBase + i), vc));
                            ulong vd = a.Load(deferred, d);
                            if (vd != 0)
                            {
                                a.Store(value, c, a.Sum(vc, vd));
                                a.Store(deferred, d, 0);
                            }
                        }
                        break;
                    }
                    for (int i = 0; i < lt.Length; i++)
                    {
                        uint e = lt[i];
                        long c = cBase + i;
                        long d = dTop[(int)(e & 3)] + (e >> 2);
                        if (!a.IsZero(value, c))
                            a.Add(value, encBase + i, value, c);
                        if (!a.IsZero(deferred, d))
                        {
                            a.Add(value, c, deferred, d);
                            a.SetZero(deferred, d);
                        }
                    }
                    break;
                }
                case MP.NL:
                case MP.NR:
                {
                    long ccBase = mc.CodeLRAt(RawSetPair(leftRaw, pl, pairL == MP.NL ? MP.LN : MP.RN));
                    FillDTop(dTop, leftRaw, pl);
                    if (Simd12)
                    {
                        LeftSwap12(_lta[block.Height], dTop, block.Height, cBase, ccBase,
                            BlockOf(mc, RawSetPair(leftRaw, pl, pairL == MP.NL ? MP.LN : MP.RN), out _, out int ccN) && ccN == lt.Length);
                        break;
                    }
                    if (a.Scalar)
                    {
                        for (int i = 0; i < lt.Length; i++)  // tmp = c; c += cc; c += d; d = tmp
                        {
                            uint e = lt[i];
                            long c = cBase + i;
                            long d = dTop[(int)(e & 3)] + (e >> 2);
                            ulong vc = a.Load(value, c), vcc = a.Load(value, ccBase + i), vd = a.Load(deferred, d);
                            if ((vc | vcc | vd) == 0) continue;
                            a.Store(value, c, a.Sum(a.Sum(vc, vcc), vd));
                            a.Store(deferred, d, vc);
                        }
                        break;
                    }
                    for (int i = 0; i < lt.Length; i++)      // p > wR >= 1, so never the p == 1 form
                    {
                        uint e = lt[i];
                        long c = cBase + i;
                        long d = dTop[(int)(e & 3)] + (e >> 2);
                        long cc = ccBase + i;
                        if (a.IsZero(value, c) && a.IsZero(value, cc) && a.IsZero(deferred, d))
                            continue;
                        a.Save(value, c, tmp);
                        a.Add(value, c, value, cc);
                        a.Add(value, c, deferred, d);
                        a.Restore(tmp, deferred, d);
                    }
                    break;
                }
                case MP.LL:
                {
                    // Scan for the matching ')' below p-1: first through the left
                    // bits (block-invariant), possibly continuing into the right.
                    ulong leftNN = RawSetPair(leftRaw, pl, MP.NN);
                    int ql = pl - 1, s = 1;
                    while (s > 0 && ql > 0)
                    {
                        ql--;
                        ulong v = (leftNN >> (2 * ql)) & 3;
                        if (v == (ulong)MV.L) s++;
                        else if (v == (ulong)MV.R) s--;
                    }
                    uint[] t, ta;
                    int H;
                    if (s == 0)
                    {
                        // Closes inside the left half: fully block-invariant.
                        FillDTop(dTop, RawSetPos(leftNN, ql, (ulong)MV.L), pl - 1);
                        (t, ta, H) = (lt, _lta[block.Height], block.Height);
                    }
                    else
                    {
                        // Continues into the right half: the s-th unmatched ')'
                        // there becomes '(' (tabulated per s).
                        FillDTop(dTop, leftNN, pl - 1);
                        (t, ta, H) = (_llt[s][block.Height], _llta[s][block.Height], block.Height - 2);
                    }
                    EmitAllToDeferred(t, ta, H, dTop, cBase);
                    break;
                }
                case MP.RR:
                {
                    // Matching '(' is above p, always within the left half (an
                    // unmatched ')' at p would need ')' at p-1 to dip below 0).
                    ulong leftNN = RawSetPair(leftRaw, pl, MP.NN);
                    int qh = pl, s = 1;
                    while (s > 0)
                    {
                        qh++;
                        ulong v = (leftNN >> (2 * qh)) & 3;
                        if (v == (ulong)MV.L) s--;
                        else if (v == (ulong)MV.R) s++;
                    }
                    FillDTop(dTop, RawSetPos(leftNN, qh, (ulong)MV.R), pl - 1);
                    EmitAllToDeferred(lt, _lta[block.Height], block.Height, dTop, cBase);
                    break;
                }
                case MP.RL:
                {
                    FillDTop(dTop, RawSetPair(leftRaw, pl, MP.NN), pl - 1);
                    EmitAllToDeferred(lt, _lta[block.Height], block.Height, dTop, cBase);
                    break;
                }
                // '(.', ').', '()' as (p,p-1) have no transition: whole block is a no-op.
            }
        }

        // ---- the straddling update p == RightWidth (p > 1) ----
        //
        // The pair is (bottom left position, top right position). The left
        // value vL is block-invariant, so every block runs at most three
        // single-case loops chosen by vL, each over the right mates with one
        // top value vR (per-top index lists: no data-dependent case branch).
        // Every target is a block-invariant base (one left-code lookup per
        // block) plus a tabulated right offset:
        //   value:    mc.CodeLRAt(left') + A[i]           (NN, swaps)
        //   deferred: wc.CodeLRAt(..)    + (lt[i] >> 2)   (right mate below its top)
        //             wc.CodeLRAt(..)    + DFlip[i]       (LL: matching ')' flipped)
        // The shrink at p (NN/swap: drops the '.' at the left bottom) makes the
        // right top the wc left half's bottom; the shrink at p-1 (emits: drops
        // the now-'.' right top) leaves the left with a '.' bottom.
        // Transitions and targets are exactly UpdateBlockGeneric's; iteration
        // order within a block does not matter (all consumed/written d's of one
        // update are distinct, and a block with vL == '.' never emits while the
        // others never consume), and blocks keep their order, so invariant 1 holds.
        // SAP_GENERIC_STRADDLE=1 forces the generic path (A/B testing).
        private static readonly bool GenericStraddle = Environment.GetEnvironmentVariable("SAP_GENERIC_STRADDLE") == "1";

        private void UpdateBlockStraddle(in MateCodec.CodeTable block)
        {
            var mc = _o._mc;
            var wc = _o._wc;
            var a = _a;
            TStore value = _value, deferred = _deferred;
            int wR = mc.RightWidth;
            ulong leftRaw = block.MateL.Id >> (2 * wR);
            long cBase = block.Base;
            uint[] lt = _lt[block.Height];
            StraddleTable st = _st[block.Height];
            int[] idx = st.Idx;
            uint[] at = st.A;
            ulong leftN = leftRaw & ~3UL;          // left with a '.' bottom == (leftRaw >> 2) << 2

            switch ((MV)(leftRaw & 3))
            {
                case MV.N:
                {
                    long baseL = mc.CodeLRAt(leftRaw | (ulong)MV.L);   // bottom '(' (NN target, [.(] partner)
                    long baseR = mc.CodeLRAt(leftRaw | (ulong)MV.R);   // bottom ')' ([.)] partner)
                    long d0 = wc.CodeLRAt(leftN | (ulong)MV.N);
                    long dR = wc.CodeLRAt(leftN | (ulong)MV.R);
                    long dL = wc.CodeLRAt(leftN | (ulong)MV.L);
                    int k = 0;
                    for (; k < st.NEnd; k++)                           // [..]: may open [()]
                    {
                        int i = idx[k];
                        long c = cBase + i;
                        long d = d0 + (lt[i] >> 2);
                        ulong vc = a.Load(value, c);
                        if (vc != 0)
                        {
                            long cc = baseL + at[i];
                            a.Store(value, cc, a.Sum(a.Load(value, cc), vc));
                        }
                        ulong vd = a.Load(deferred, d);
                        if (vd != 0)
                        {
                            a.Store(value, c, a.Sum(vc, vd));
                            a.Store(deferred, d, 0);
                        }
                    }
                    for (; k < st.REnd; k++)                           // [.)] <-> [).]
                    {
                        int i = idx[k];
                        StraddleSwap(value, deferred, cBase + i, baseR + at[i], dR + (lt[i] >> 2));
                    }
                    for (; k < idx.Length; k++)                        // [.(] <-> [(.]
                    {
                        int i = idx[k];
                        StraddleSwap(value, deferred, cBase + i, baseL + at[i], dL + (lt[i] >> 2));
                    }
                    break;
                }
                case MV.L:
                {
                    // [((---)] -> [..---(]
                    long dB = wc.CodeLRAt(leftN);
                    uint[] df = st.DFlip;
                    for (int k = st.REnd; k < idx.Length; k++)
                    {
                        int i = idx[k];
                        ulong vc = a.Load(value, cBase + i);
                        if (vc == 0) continue;
                        if (df[i] == LtNone) throw new InvalidOperationException("straddle LL: no matching ')' in the right half");
                        long tg = dB + df[i];
                        a.Store(deferred, tg, a.Sum(a.Load(deferred, tg), vc));
                    }
                    break;
                }
                case MV.R:
                {
                    // [(---))] -> [)---..]: the '(' matching the bottom-left ')'
                    // lies above it in the left half (block-invariant).
                    long dRR = -1;
                    for (int q = 1, sc = 1; q < mc.LeftWidth; q++)
                    {
                        ulong v = (leftN >> (2 * q)) & 3;
                        if (v == (ulong)MV.R) sc++;
                        else if (v == (ulong)MV.L && --sc == 0) { dRR = wc.CodeLRAt(RawSetPos(leftN, q, (ulong)MV.R)); break; }
                    }
                    for (int k = st.NEnd; k < st.REnd; k++)
                    {
                        int i = idx[k];
                        ulong vc = a.Load(value, cBase + i);
                        if (vc == 0) continue;
                        if (dRR < 0) throw new InvalidOperationException("straddle RR: no matching '(' in the left half");
                        long tg = dRR + (lt[i] >> 2);
                        a.Store(deferred, tg, a.Sum(a.Load(deferred, tg), vc));
                    }
                    // [)(] -> [..]
                    long dRL = wc.CodeLRAt(leftN);
                    for (int k = st.REnd; k < idx.Length; k++)
                    {
                        int i = idx[k];
                        ulong vc = a.Load(value, cBase + i);
                        if (vc == 0) continue;
                        long tg = dRL + (lt[i] >> 2);
                        a.Store(deferred, tg, a.Sum(a.Load(deferred, tg), vc));
                    }
                    break;
                }
            }
        }

        // tmp = c; c += cc; c += d; d = tmp (the p > 1 swap), each counter touched once.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void StraddleSwap(TStore value, TStore deferred, long c, long cc, long d)
        {
            var a = _a;
            ulong vc = a.Load(value, c), vcc = a.Load(value, cc), vd = a.Load(deferred, d);
            if ((vc | vcc | vd) == 0) return;
            a.Store(value, c, a.Sum(a.Sum(vc, vcc), vd));
            a.Store(deferred, d, vc);
        }

        // Per split height h: right mates grouped by top value; the mc right
        // offset after the straddle transition changes the top ('.' -> ')' for
        // NN, '(' / ')' -> '.' for the swaps); and for top '(' the wc offset of
        // the mate below the top with the top's matching ')' flipped to '('.
        private static StraddleTable[] BuildStraddleTables(MateCodec mc, MateCodec wc)
        {
            int wR = mc.RightWidth;
            int topShift = 2 * (wR - 1);
            ulong lowMask = (1UL << topShift) - 1;
            var st = new StraddleTable[mc.MaxSplitHeight + 1];
            for (int h = 0; h <= mc.MaxSplitHeight; h++)
            {
                Mate[] block = mc.RightBlock(h);
                var at = new uint[block.Length];
                var df = new uint[block.Length];
                var byTop = new List<int>[] { new(), new(), new() };   // indexed by MV: '.', ')', '('
                for (int i = 0; i < block.Length; i++)
                {
                    ulong r = block[i].Id, low = r & lowMask;
                    int top = (int)(r >> topShift);
                    byTop[top].Add(i);
                    at[i] = (uint)mc.CodeRAt(top == (int)MV.N ? low | ((ulong)MV.R << topShift) : low);
                    df[i] = LtNone;
                    if (top != (int)MV.L) continue;
                    for (int q = wR - 2, sc = 1; q >= 0; q--)
                    {
                        ulong v = (low >> (2 * q)) & 3;
                        if (v == (ulong)MV.L) sc++;
                        else if (v == (ulong)MV.R && --sc == 0)
                        {
                            df[i] = (uint)wc.CodeRAt(RawSetPos(low, q, (ulong)MV.L));
                            break;
                        }
                    }
                }
                st[h] = new StraddleTable
                {
                    Idx = [.. byTop[0], .. byTop[1], .. byTop[2]],
                    NEnd = byTop[0].Count,
                    REnd = byTop[0].Count + byTop[1].Count,
                    A = at,
                    DFlip = df,
                };
            }
            return st;
        }

        // deferred[dTop[top] + off] += value[cBase + i] for every nonzero state
        // of the block (the LL/RR/RL cases of UpdateBlockLeft).
        private void EmitAllToDeferred(uint[] t, uint[] ta, int H, Span<long> dTop, long cBase)
        {
            var a = _a;
            TStore value = _value, deferred = _deferred;
            if (Simd12)
            {
                LeftEmit12(ta, dTop, H, cBase);
                return;
            }
            for (int i = 0; i < t.Length; i++)
            {
                long c = cBase + i;
                if (a.Scalar)
                {
                    ulong vc = a.Load(value, c);
                    if (vc == 0) continue;
                    uint es = t[i];
                    if (es == LtNone) throw new InvalidOperationException("left update: no matching ')' in the right half");
                    long tg = dTop[(int)(es & 3)] + (es >> 2);
                    a.Store(deferred, tg, a.Sum(a.Load(deferred, tg), vc));
                    continue;
                }
                if (a.IsZero(value, c)) continue;
                uint e = t[i];
                if (e == LtNone) throw new InvalidOperationException("left update: no matching ')' in the right half");
                a.Add(deferred, dTop[(int)(e & 3)] + (e >> 2), value, c);
            }
        }

        // Route a transformed state: at the row edge (p == 1) it re-enters the
        // full-width space directly; elsewhere it waits in the deferred array.
        private void Emit(int p, in Mate m2, long c)
        {
            if (p == 1) _a.Add(_value, _o._mc.Encode(m2), _value, c);
            else _a.Add(_deferred, _o._wc.Encode(m2.Shrink(p - 1)), _value, c);
        }
    }
}
