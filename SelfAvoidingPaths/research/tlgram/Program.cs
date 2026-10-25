// Gram matrix of one-defect noncrossing link patterns on k points at loop weight 0:
// G[a,b] = 1 if gluing top pattern a to bottom pattern b gives a single path (no loops), else 0.
// This is the straight-cut connection matrix of A007764 within one occupancy class of k edges
// (free end never nested inside an arc, so there are Catalan((k+1)/2) patterns).
// usage: tlgram <p> <E> <kmin> <kmax>   p-adic valuations of the Smith invariant factors (local SNF
//                                       over Z/p^E; p = 2 uses arithmetic mod 2^64, E ignored)
//        tlgram rank <p> <kmin> <kmax>  rank over GF(p) only, p <= 7 (fast: k=19 ~8 s, k=21 ~2-6 min)
// See RESEARCH-BRIEF.md, "Join matrix mod small primes".
using System.Numerics;
using System.Diagnostics;

if (args[0] == "rank")      // tlsnf rank <p> <kmin> <kmax>: rank over GF(p) only (p <= 7), much faster
{
    int rp = int.Parse(args[1]);
    for (int k = int.Parse(args[2]) | 1; k <= int.Parse(args[3]); k += 2)
    {
        var sw0 = Stopwatch.StartNew();
        var pats = Patterns(k);
        long rank = rp == 2 ? Rank2(pats) : RankSmall(pats, (byte)rp);
        Console.WriteLine($"k={k,2} m={pats.Count,7} rank mod {rp} = {rank}   ({sw0.Elapsed.TotalSeconds:F1}s)");
    }
    return;
}

int p = int.Parse(args[0]), E = int.Parse(args[1]), kmin = int.Parse(args[2]), kmax = int.Parse(args[3]);
for (int k = kmin | 1; k <= kmax; k += 2)
{
    var sw = Stopwatch.StartNew();
    var pats = Patterns(k);
    int m = pats.Count;
    var a = new ulong[m][];
    Parallel.For(0, m, i =>
    {
        a[i] = new ulong[m];
        var uf = new int[k];
        for (int j = 0; j < m; j++) a[i][j] = SinglePath(pats[i], pats[j], uf) ? 1UL : 0UL;
    });
    var hist = p == 2 ? Snf2(a) : SnfOdd(a, (ulong)p, E);
    int cap = p == 2 ? 64 : E;
    string h = string.Join(" ", hist.Select((c, v) => (c, v)).Where(t => t.c > 0)
        .Select(t => t.v >= cap ? $"v>={cap}:{t.c}" : $"v{t.v}:{t.c}"));
    Console.WriteLine($"k={k,2} m={m,6} p={p}  {h}   ({sw.Elapsed.TotalSeconds:F1}s)");
}

// One-defect patterns: partner[i] = j for an arc, -1 for the defect (never nested inside an arc).
static List<int[]> Patterns(int k)
{
    var res = new List<int[]>();
    var cur = new int[k];
    var stack = new int[k];
    void Rec(int i, int sp, bool defect)
    {
        if (i == k) { if (sp == 0 && defect) res.Add((int[])cur.Clone()); return; }
        int rem = k - i;
        if (sp + (defect ? 0 : 1) > rem) return;
        stack[sp] = i; Rec(i + 1, sp + 1, defect);                       // open
        if (sp > 0) { int o = stack[sp - 1]; cur[o] = i; cur[i] = o; Rec(i + 1, sp - 1, defect); stack[sp - 1] = o; }  // close
        if (!defect && sp == 0) { cur[i] = -1; Rec(i + 1, 0, true); }    // defect at height 0
    }
    Rec(0, 0, false);
    return res;
}

static bool SinglePath(int[] x, int[] y, int[] uf)
{
    int k = x.Length, comps = k;
    for (int i = 0; i < k; i++) uf[i] = i;
    int Find(int i) { while (uf[i] != i) i = uf[i] = uf[uf[i]]; return i; }
    void Join(int i, int j) { int a = Find(i), b = Find(j); if (a != b) { uf[a] = b; comps--; } }
    for (int i = 0; i < k; i++) { if (x[i] > i) Join(i, x[i]); if (y[i] > i) Join(i, y[i]); }
    return comps == 1;
}

// Local Smith form mod 2^64 (wraparound arithmetic). Returns hist[v] = #invariant factors with 2-valuation v (64 = zero).
static int[] Snf2(ulong[][] a)
{
    int m = a.Length;
    var hist = new int[65];
    for (int i = 0; i < m; i++)
    {
        int br = -1, bc = -1, bv = 64;
        for (int r = i; r < m && bv > 0; r++)
            for (int c = i; c < m; c++)
            {
                ulong x = a[r][c];
                if (x == 0) continue;
                int v = BitOperations.TrailingZeroCount(x);
                if (v < bv) { bv = v; br = r; bc = c; if (v == 0) break; }
            }
        if (br < 0) { hist[64] += m - i; break; }
        (a[i], a[br]) = (a[br], a[i]);
        if (bc != i) for (int r = 0; r < m; r++) (a[r][i], a[r][bc]) = (a[r][bc], a[r][i]);
        hist[bv]++;
        ulong u = a[i][i] >> bv, inv = u;                    // Newton inverse of an odd number mod 2^64
        for (int t = 0; t < 6; t++) inv *= 2 - u * inv;
        ulong[] pr = a[i];
        int ii = i, vv = bv;
        Parallel.For(i + 1, m, r =>
        {
            ulong[] row = a[r];
            ulong x = row[ii];
            if (x == 0) return;
            ulong f = (x >> vv) * inv;
            for (int j = ii + 1; j < row.Length; j++) row[j] -= f * pr[j];
            row[ii] = 0;
        });
    }
    return hist;
}

// Local Smith form mod p^E, p odd, p^E < 2^32.
static int[] SnfOdd(ulong[][] a, ulong p, int E)
{
    ulong P = 1; for (int t = 0; t < E; t++) P *= p;
    if (P >= 1UL << 32) throw new ArgumentException("p^E must be < 2^32");
    int m = a.Length;
    foreach (var row in a) for (int j = 0; j < m; j++) row[j] %= P;
    int Val(ulong x) { int v = 0; while (x % p == 0) { x /= p; v++; } return v; }
    var hist = new int[E + 1];
    for (int i = 0; i < m; i++)
    {
        int br = -1, bc = -1, bv = E;
        for (int r = i; r < m && bv > 0; r++)
            for (int c = i; c < m; c++)
            {
                ulong x = a[r][c];
                if (x == 0) continue;
                int v = x % p != 0 ? 0 : Val(x);
                if (v < bv) { bv = v; br = r; bc = c; if (v == 0) break; }
            }
        if (br < 0) { hist[E] += m - i; break; }
        (a[i], a[br]) = (a[br], a[i]);
        if (bc != i) for (int r = 0; r < m; r++) (a[r][i], a[r][bc]) = (a[r][bc], a[r][i]);
        hist[bv]++;
        ulong pv = 1; for (int t = 0; t < bv; t++) pv *= p;
        ulong inv = (ulong)ModInv((long)(a[i][i] / pv % P), (long)P);
        ulong[] pr = a[i];
        int ii = i;
        Parallel.For(i + 1, m, r =>
        {
            ulong[] row = a[r];
            ulong x = row[ii];
            if (x == 0) return;
            ulong f = x / pv % P * inv % P;
            for (int j = ii + 1; j < row.Length; j++)
            {
                ulong y = row[j] + P - f * pr[j] % P;
                row[j] = y >= P ? y - P : y;
            }
            row[ii] = 0;
        });
    }
    return hist;
}

// Rank over GF(2) with bit-packed rows.
static long Rank2(List<int[]> pats)
{
    int m = pats.Count, k = pats[0].Length, w = (m + 63) / 64;
    var a = new ulong[m][];
    Parallel.For(0, m, i =>
    {
        var row = a[i] = new ulong[w];
        var uf = new int[k];
        for (int j = 0; j < m; j++) if (SinglePath(pats[i], pats[j], uf)) row[j >> 6] |= 1UL << (j & 63);
    });
    long rank = 0;
    for (int c = 0; c < m && rank < m; c++)
    {
        int wi = c >> 6; ulong bit = 1UL << (c & 63);
        int piv = -1;
        for (int r = (int)rank; r < m; r++) if ((a[r][wi] & bit) != 0) { piv = r; break; }
        if (piv < 0) continue;
        (a[rank], a[piv]) = (a[piv], a[rank]);
        ulong[] pr = a[rank];
        Parallel.For((int)rank + 1, m, r =>
        {
            ulong[] row = a[r];
            if ((row[wi] & bit) == 0) return;
            for (int t = wi; t < row.Length; t++) row[t] ^= pr[t];
        });
        rank++;
    }
    return rank;
}

// Rank over GF(p), p in 3..7, byte rows with SIMD updates (reduce by min(y, y-p)).
static long RankSmall(List<int[]> pats, byte p)
{
    int m = pats.Count, k = pats[0].Length, vw = Vector<byte>.Count, len = (m + vw - 1) / vw * vw;
    var a = new byte[m][];
    Parallel.For(0, m, i =>
    {
        var row = a[i] = new byte[len];
        var uf = new int[k];
        for (int j = 0; j < m; j++) if (SinglePath(pats[i], pats[j], uf)) row[j] = 1;
    });
    var inv = new byte[p];
    for (int x = 1; x < p; x++) for (int y = 1; y < p; y++) if (x * y % p == 1) inv[x] = (byte)y;
    var vp = new Vector<byte>(p);
    long rank = 0;
    for (int c = 0; c < m && rank < m; c++)
    {
        int piv = -1;
        for (int r = (int)rank; r < m; r++) if (a[r][c] != 0) { piv = r; break; }
        if (piv < 0) continue;
        (a[rank], a[piv]) = (a[piv], a[rank]);
        byte[] pr = a[rank];
        byte ip = inv[pr[c]];
        int start = c / vw * vw;
        Parallel.For((int)rank + 1, m, r =>
        {
            byte[] row = a[r];
            int x = row[c];
            if (x == 0) return;
            var f = new Vector<byte>((byte)((p - x * ip % p) % p));     // row += f * pr kills column c
            for (int t = start; t < len; t += vw)
            {
                var y = new Vector<byte>(row, t) + f * new Vector<byte>(pr, t);   // < p + (p-1)^2 < 256
                for (int s = 1; s < p; s++) y = Vector.Min(y, y - vp);
                y.CopyTo(row, t);
            }
        });
        rank++;
    }
    return rank;
}

static long ModInv(long a, long m)
{
    long g = m, x = 0, x1 = 1, b = a % m;
    while (b != 0) { long q = g / b; (g, b) = (b, g - q * b); (x, x1) = (x1, x - q * x1); }
    if (g != 1) throw new InvalidOperationException("not a unit");
    return (x % m + m) % m;
}
