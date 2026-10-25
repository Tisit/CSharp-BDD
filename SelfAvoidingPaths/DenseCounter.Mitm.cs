using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SelfAvoidingPaths;

// Meet in the middle (--mitm, even n): sweep only the top n/2 rows, then join.
//
// The 180-degree rotation maps the grid onto itself and swaps the two corners,
// so the number of ways to complete a middle-row state from below equals the
// forward count f of its rotation. With rot = reverse the positions and swap
// '(' <-> ')' (the free end stays the free end):
//
//   a(n) = sum over states s, u of f(s) * J(s, rot(u)) * f(u)
//
// where occ(s) == reverse(occ(u)) and J = 1 iff the top arcs of s and the
// bottom arcs of rot(u) join into one path from free end to free end (no loop).
// Within an occupancy class of k crossing edges J depends only on the k-symbol
// paren patterns, and its nonzeros are the open meanders (A005316(k)). So per
// odd k the join is one sparse pattern-pair list applied to every occupancy
// pattern P of k positions: a sparse-times-dense product over a matrix whose
// rows are paren patterns and whose columns are occupancy patterns.
//
// The term for P equals the term for reverse(P) (rotate the whole picture), and
// for even n no P with odd k is its own reverse, so only P < reverse(P) is
// evaluated and the sum doubled.
//
// Per class k, per batch of occupancy columns:
//   G[s][j] = f(s placed on P_j),  H[r][j] = f(pattern r placed on reverse(P_j))
//   T_j = sum_s G[s][j] * sum_{r : J(s, rot r)} H[r][j]
// The inner sum is additions only (16-bit residues widened into 32-bit SIMD
// lanes); one multiply per (s, j). Pattern pairs are kept as per-s partner
// lists while they are small (k <= 21: 294 MB at k = 21) and regenerated on the
// fly per s above that (k = 23 has 678 M pairs but only 12 column patterns at n = 24).

public sealed partial class DenseCounter
{
    /// <summary>Meet in the middle for even n (modular runs only): sweep the top n/2 rows, then join.</summary>
    public bool Mitm { get; set; }

    private bool MitmActive => Mitm && _rows % 2 == 0;

    // Research hook: SAP_MITM_TIMING=1 prints the join's per-class timing to stderr.
    private static readonly bool MitmTiming = Environment.GetEnvironmentVariable("SAP_MITM_TIMING") == "1";
    // Test hook: SAP_MITM_FLY=1 regenerates partner lists for every class (normally only k > MitmListMaxK).
    private static readonly bool MitmForceFly = Environment.GetEnvironmentVariable("SAP_MITM_FLY") == "1";
    private const int MitmListMaxK = 21;
    // Target size of one gathered column batch (H; G is the same size): ~L2/L3 resident.
    private static readonly long MitmBatchBytes =
        (long.TryParse(Environment.GetEnvironmentVariable("SAP_MITM_BATCH_KB"), out long kb) ? kb : 2048) * 1024;

    private sealed partial class Engine<TArith, TStore>
    {
        /// <summary>a(n) mod the run's modulus from the middle-row vector in _value (deferred empty).</summary>
        private ulong MitmJoin()
        {
            int n = _o._n;
            int bits = _value switch { Packed12Store => 12, Native16Store => 16, uint[] => 32, _ => 64 };
            if (!_a.Scalar) throw new NotSupportedException("--mitm needs a modular run");
            bool wrap64 = bits == 64 && _modulus == 0;
            ulong m = wrap64 ? 0 : _modulus != 0 ? _modulus : 1UL << bits;
            bool fast = !wrap64 && m <= 4096 && Avx2.IsSupported
                && (ulong)Catalan(n / 2) * 4095 < 1UL << 32;     // largest class k = n-1 has Catalan(n/2) patterns

            // The deferred array is empty at a row boundary: release it so the
            // join's scratch (partner lists, column batches) fits in its place.
            (_deferred as IDisposable)?.Dispose();

            var sw = Stopwatch.StartNew();
            ulong total = 0;
            for (int k = 1; k < n; k += 2)
            {
                long t0 = sw.ElapsedTicks;
                var cls = new MeanderClass(k);
                int[] pats = HalfPatterns(n, k);
                bool fly = MitmForceFly || k > MitmListMaxK;
                int[][]? lists = fly ? null : cls.AllPartners(_opts);
                long tList = sw.ElapsedTicks;
                (ulong tk, double gatherSec) = fast ? JoinClass<ushort>(cls, pats, lists, m, wrap64)
                                                    : JoinClass<ulong>(cls, pats, lists, m, wrap64);
                total = AddMod(total, tk, m, wrap64);
                if (MitmTiming)
                    Console.Error.WriteLine($"mitm k={k,2}: patterns {cls.Count,7:N0} x {pats.Length,9:N0} cols, " +
                        $"lists {(fly ? "on the fly" : $"{(tList - t0) * 1.0 / Stopwatch.Frequency:F2}s")}, " +
                        $"gather {gatherSec:F2}s, total {(sw.ElapsedTicks - t0) * 1.0 / Stopwatch.Frequency:F2}s");
            }
            if (MitmTiming) Console.Error.WriteLine($"mitm join: {sw.Elapsed.TotalSeconds:F2}s");
            return AddMod(total, total, m, wrap64);
        }

        private static long Catalan(int j)
        {
            long c = 1;
            for (int i = 0; i < j; i++) c = c * 2 * (2 * i + 1) / (i + 2);
            return c;
        }

        private static ulong AddMod(ulong a, ulong b, ulong m, bool wrap64)
            => wrap64 ? unchecked(a + b) : (ulong)(((UInt128)a + b) % m);

        /// <summary>Occupancy masks over n positions with k bits set and mask &lt; reverse(mask).</summary>
        private static int[] HalfPatterns(int n, int k)
        {
            var list = new List<int>();
            int v = (1 << k) - 1;
            while (v < 1 << n)
            {
                if (v < Reverse(v, n)) list.Add(v);
                int c = v & -v, r = v + c;            // Gosper's hack: next mask with k bits
                v = (((r ^ v) >> 2) / c) | r;
            }
            return [.. list];
        }

        private static int Reverse(int v, int n)
        {
            int r = 0;
            for (int p = 0; p < n; p++) if ((v >> p & 1) != 0) r |= 1 << (n - 1 - p);
            return r;
        }

        /// <summary>2-bit field mask of the mate positions in occupancy mask P.</summary>
        private static ulong Spread(int p)
        {
            ulong m = 0;
            for (int q = 0; q < 32; q++) if ((p >> q & 1) != 0) m |= 3UL << (2 * q);
            return m;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Deposit(ulong mini, ulong mask)
        {
            if (Bmi2.X64.IsSupported) return Bmi2.X64.ParallelBitDeposit(mini, mask);
            ulong r = 0;
            for (ulong bb = 1; mask != 0; bb <<= 1)
            {
                ulong low = mask & (~mask + 1);
                if ((mini & bb) != 0) r |= low;
                mask &= mask - 1;
            }
            return r;
        }

        /// <summary>Column batch width: a multiple of 16 with one gathered batch (H; G is the same size) near targetBytes.</summary>
        private static int BatchWidth(int rows, int cols, int elemBytes, long targetBytes)
        {
            long w = targetBytes / ((long)rows * elemBytes) / 16 * 16;
            long all = (cols + 15) / 16 * 16;
            return (int)Math.Clamp(w, 16, all);
        }

        // Per-worker scratch of one class.
        private sealed class JoinWorker<TE>(MeanderClass cls, long cells, int width) where TE : unmanaged
        {
            public readonly TE[] G = cells > 0 ? new TE[cells] : [];
            public readonly TE[] H = cells > 0 ? new TE[cells] : [];
            public readonly ulong[] Mp = new ulong[width], Mr = new ulong[width];
            public readonly MeanderGen Gen = new(cls);
            public readonly ulong[] Acc = new ulong[16];
            public ulong Sum;
            public long GatherTicks;
        }

        /// <summary>
        /// One class's contribution: sum over P_j of T_j. Classes with few paren
        /// patterns (small k: few rows, many columns) run their column batches in
        /// parallel, each worker gathering its own L2-sized batch; large classes
        /// share one batch at a time and split its rows.
        /// </summary>
        private (ulong sum, double gatherSec) JoinClass<TE>(MeanderClass cls, int[] pats, int[][]? lists, ulong m, bool wrap64)
            where TE : unmanaged
        {
            int rows = cls.Count, threads = _opts.MaxDegreeOfParallelism;
            int esz = Unsafe.SizeOf<TE>();
            const int RowChunk = 64;
            int rowChunks = (rows + RowChunk - 1) / RowChunk;
            bool colPar = rowChunks < 4 * threads;
            long target = colPar ? MitmBatchBytes / 4 : lists is null ? 64L << 20 : MitmBatchBytes;
            int width = BatchWidth(rows, pats.Length, esz, target);
            int batches = (pats.Length + width - 1) / width;
            long cells = (long)rows * width;
            ulong total = 0;
            long gatherTicks = 0;
            object gate = new();

            if (colPar)
            {
                Parallel.For(0, batches, _opts, () => new JoinWorker<TE>(cls, cells, width), (b, _, w) =>
                {
                    long t0 = Stopwatch.GetTimestamp();
                    int valid = SetMasks(pats, b * width, w.Mp, w.Mr);
                    GatherRows(cls, 0, rows, width, valid, w.Mp, w.Mr, w.G, w.H);
                    w.GatherTicks += Stopwatch.GetTimestamp() - t0;
                    w.Sum = AddMod(w.Sum, EvalRows(0, rows, width, w.G, w.H, lists, w, m, wrap64), m, wrap64);
                    return w;
                }, w => { lock (gate) { total = AddMod(total, w.Sum, m, wrap64); gatherTicks += w.GatherTicks; } });
                return (total, gatherTicks * 1.0 / Stopwatch.Frequency / Math.Min(threads, batches));
            }

            var shared = new JoinWorker<TE>(cls, cells, width);
            for (int b = 0; b < batches; b++)
            {
                long t0 = Stopwatch.GetTimestamp();
                int valid = SetMasks(pats, b * width, shared.Mp, shared.Mr);
                Parallel.For(0, rowChunks, _opts, c =>
                    GatherRows(cls, c * RowChunk, Math.Min(rows, (c + 1) * RowChunk), width, valid, shared.Mp, shared.Mr, shared.G, shared.H));
                gatherTicks += Stopwatch.GetTimestamp() - t0;
                Parallel.For(0, rowChunks, _opts, () => new JoinWorker<TE>(cls, 0, 0), (c, _, w) =>
                {
                    ulong v = EvalRows(c * RowChunk, Math.Min(rows, (c + 1) * RowChunk), width, shared.G, shared.H, lists, w, m, wrap64);
                    w.Sum = AddMod(w.Sum, v, m, wrap64);
                    return w;
                }, w => { lock (gate) total = AddMod(total, w.Sum, m, wrap64); });
            }
            return (total, gatherTicks * 1.0 / Stopwatch.Frequency);
        }

        /// <summary>Mate-position masks of the batch's patterns P_j and reverse(P_j); returns the number of real columns.</summary>
        private int SetMasks(int[] pats, int j0, ulong[] mp, ulong[] mr)
        {
            int valid = Math.Min(mp.Length, pats.Length - j0);
            for (int j = 0; j < valid; j++) { mp[j] = Spread(pats[j0 + j]); mr[j] = Spread(Reverse(pats[j0 + j], _o._n)); }
            return valid;
        }

        // Rows s0..s1-1 of G (pattern s on P_j) and H (pattern s on reverse(P_j));
        // row stride = width, zero past the last real column.
        private void GatherRows<TE>(MeanderClass cls, int s0, int s1, int width, int valid, ulong[] mp, ulong[] mr, TE[] g, TE[] h)
            where TE : unmanaged
        {
            var mc = _o._mc;
            for (int s = s0; s < s1; s++)
            {
                ulong mini = cls.Mini[s];
                long row = (long)s * width;
                for (int j = 0; j < valid; j++)
                {
                    g[row + j] = Cell<TE>(_a.Load(_value, mc.Encode(new Mate(Deposit(mini, mp[j])))));
                    h[row + j] = Cell<TE>(_a.Load(_value, mc.Encode(new Mate(Deposit(mini, mr[j])))));
                }
                for (int j = valid; j < width; j++) { g[row + j] = default; h[row + j] = default; }
            }
        }

        // A residue as a gather cell (typeof tests fold at JIT time).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static TE Cell<TE>(ulong v) where TE : unmanaged
            => typeof(TE) == typeof(ushort) ? (TE)(object)(ushort)v : (TE)(object)v;

        /// <summary>Sum over rows s in [s0, s1) and the batch's columns j of G[s][j] * sum_{r partner of s} H[r][j], mod m.</summary>
        private static ulong EvalRows<TE>(int s0, int s1, int width, TE[] g, TE[] h, int[][]? lists, JoinWorker<TE> w, ulong m, bool wrap64)
            where TE : unmanaged
            => typeof(TE) == typeof(ushort)
                ? EvalRowsFast(s0, s1, width, (ushort[])(object)g, (ushort[])(object)h, lists, w.Gen, (uint)m)
                : EvalRowsGeneric(s0, s1, width, (ulong[])(object)g, (ulong[])(object)h, lists, w.Gen, w.Acc, m, wrap64);

        // Residues < m <= 4096 in 16-bit cells; partner sums in 32-bit lanes
        // (< Catalan((k+1)/2) * 4095 < 2^32, checked in MitmJoin), products in 64-bit.
        private static unsafe ulong EvalRowsFast(int s0, int s1, int width, ushort[] g, ushort[] h, int[][]? lists, MeanderGen gen, uint m)
        {
            ulong local = 0;
            fixed (ushort* gp = g, hp = h)
            {
                for (int s = s0; s < s1; s++)
                {
                    ReadOnlySpan<int> part = lists is not null ? lists[s] : gen.Partners(s);
                    if (part.Length == 0) continue;
                    var vsum = Vector256<ulong>.Zero;
                    ushort* grow = gp + (long)s * width;
                    for (int col = 0; col < width; col += 16)
                    {
                        var a0 = Vector256<uint>.Zero;
                        var a1 = Vector256<uint>.Zero;
                        ushort* hc = hp + col;
                        foreach (int r in part)
                        {
                            ushort* q = hc + (long)r * width;
                            a0 += Avx2.ConvertToVector256Int32(q).AsUInt32();
                            a1 += Avx2.ConvertToVector256Int32(q + 8).AsUInt32();
                        }
                        var g0 = Avx2.ConvertToVector256Int32(grow + col).AsUInt32();
                        var g1 = Avx2.ConvertToVector256Int32(grow + col + 8).AsUInt32();
                        vsum += Avx2.Multiply(a0, g0) + Avx2.Multiply(a1, g1)
                              + Avx2.Multiply(Avx2.ShiftRightLogical(a0.AsUInt64(), 32).AsUInt32(), Avx2.ShiftRightLogical(g0.AsUInt64(), 32).AsUInt32())
                              + Avx2.Multiply(Avx2.ShiftRightLogical(a1.AsUInt64(), 32).AsUInt32(), Avx2.ShiftRightLogical(g1.AsUInt64(), 32).AsUInt32());
                    }
                    // each lane < (width/4) * 2^32 * 2^12: no overflow for width < 2^18
                    local = (local + Vector256.Sum(vsum) % m) % m;
                }
            }
            return local;
        }

        // Any modulus (16/32/64-bit runs): 64-bit cells, scalar. Partner sums need no
        // reduction while m <= 2^32 (< 2^32 * Catalan); 64-bit moduli add modularly.
        private static ulong EvalRowsGeneric(int s0, int s1, int width, ulong[] g, ulong[] h, int[][]? lists, MeanderGen gen, ulong[] acc, ulong m, bool wrap64)
        {
            bool small = !wrap64 && m <= 1UL << 32;
            ulong local = 0;
            for (int s = s0; s < s1; s++)
            {
                ReadOnlySpan<int> part = lists is not null ? lists[s] : gen.Partners(s);
                if (part.Length == 0) continue;
                long grow = (long)s * width;
                for (int col = 0; col < width; col += 16)
                {
                    Array.Clear(acc);
                    foreach (int r in part)
                    {
                        long q = (long)r * width + col;
                        for (int t = 0; t < 16; t++)
                            acc[t] = small || wrap64 ? unchecked(acc[t] + h[q + t]) : AddMod(acc[t], h[q + t], m, false);
                    }
                    for (int t = 0; t < 16; t++)
                    {
                        ulong x = g[grow + col + t];
                        if (wrap64) local = unchecked(local + x * acc[t]);
                        else if (small) local = (local + x * (acc[t] % m) % m) % m;
                        else local = AddMod(local, (ulong)((UInt128)x * acc[t] % m), m, false);
                    }
                }
            }
            return local;
        }
    }
}

/// <summary>
/// The paren patterns of one occupancy class (k crossing edges, k odd) at a
/// straight row cut, read left to right: the free end is the ')' where the height
/// (starting at 1) first reaches 0, with balanced arcs on either side, so there are
/// Catalan((k+1)/2) patterns. Index = generation order.
/// </summary>
internal sealed class MeanderClass
{
    public readonly int K;
    public readonly int Count;
    public readonly int[] Bits;      // bit i = 1 iff position i (from the left) is '('
    public readonly ulong[] Mini;    // the pattern as a k-position mate string (position 0 = rightmost)
    public readonly int[] Rank;      // Bits -> index, -1 if not a pattern

    public MeanderClass(int k)
    {
        if (k < 1 || k % 2 == 0 || k > 25) throw new ArgumentOutOfRangeException(nameof(k));
        K = k;
        var bits = new List<int>();
        void Gen(int i, int h, int b)
        {
            if (i == k) { if (h == 0) bits.Add(b); return; }
            if (h > k - i) return;
            Gen(i + 1, h + 1, b | 1 << i);                // '('
            if (h > 0) Gen(i + 1, h - 1, b);             // ')': the drop to 0 is the free end
        }
        Gen(0, 1, 0);
        Bits = [.. bits];
        Count = Bits.Length;
        Mini = new ulong[Count];
        Rank = new int[1 << k];
        Array.Fill(Rank, -1);
        for (int s = 0; s < Count; s++)
        {
            Rank[Bits[s]] = s;
            ulong mm = 0;
            for (int i = 0; i < k; i++)
                mm |= (ulong)((Bits[s] >> i & 1) != 0 ? MV.L : MV.R) << (2 * (k - 1 - i));
            Mini[s] = mm;
        }
    }

    /// <summary>Partner lists of every pattern, in parallel.</summary>
    public int[][] AllPartners(ParallelOptions opts)
    {
        var lists = new int[Count][];
        Parallel.For(0, (Count + 63) / 64, opts, () => new MeanderGen(this), (c, _, gen) =>
        {
            int end = Math.Min(Count, (c + 1) * 64);
            for (int s = c * 64; s < end; s++) lists[s] = gen.Partners(s).ToArray();
            return gen;
        }, _ => { });
        return lists;
    }
}

/// <summary>
/// Enumerates, for a top pattern s, every pattern r with J(s, rot(r)) = 1: a DFS
/// over the bottom pattern t = rot(r) from left to right (open an arc, close the
/// innermost open arc, or place the free end where no arc is open), pruning any
/// closing arc that would close a loop. Components are paths, tracked by their
/// endpoints (end[x] = other end of x's path), so a union and its undo are O(1);
/// k-1 edges without a loop on k points is one path. Not thread-safe (one per worker).
/// </summary>
internal sealed class MeanderGen
{
    private readonly MeanderClass _c;
    private readonly int _k;
    private readonly int[] _end, _stack;
    private int[] _out = new int[64];
    private int _n, _sp, _u;
    private bool _free;

    public MeanderGen(MeanderClass c)
    {
        _c = c;
        _k = c.K;
        _end = new int[_k];
        _stack = new int[_k];
    }

    public ReadOnlySpan<int> Partners(int s)
    {
        int b = _c.Bits[s];
        int sp = 0;
        for (int i = 0; i < _k; i++)
        {
            _end[i] = i;
            if ((b >> i & 1) != 0) _stack[sp++] = i;
            else if (sp > 0) { int j = _stack[--sp]; _end[i] = j; _end[j] = i; }
        }
        _n = 0; _sp = 0; _u = 0; _free = false;
        Dfs(0);
        return _out.AsSpan(0, _n);
    }

    private void Dfs(int i)
    {
        if (i == _k)
        {
            if (_free && _sp == 0)
            {
                if (_n == _out.Length) Array.Resize(ref _out, _n * 2);
                _out[_n++] = _c.Rank[_u];
            }
            return;
        }
        int after = _k - 1 - i;                          // positions after i
        int bit = 1 << (_k - 1 - i);                     // rot: t position i -> r position k-1-i, parens swapped
        if (_sp > 0)
        {
            int j = _stack[_sp - 1];
            if (_end[i] != j)                            // else this arc closes a loop
            {
                int a = _end[i], bb = _end[j];
                _end[a] = bb; _end[bb] = a;
                _sp--; _u |= bit;
                Dfs(i + 1);
                _u &= ~bit; _stack[_sp++] = j;            // deeper pushes overwrote the slot
                _end[a] = i; _end[bb] = j;
            }
        }
        if (_sp + 1 + (_free ? 0 : 1) <= after)
        {
            _stack[_sp++] = i;
            Dfs(i + 1);
            _sp--;
        }
        if (!_free && _sp == 0)
        {
            _free = true;
            Dfs(i + 1);
            _free = false;
        }
    }
}
