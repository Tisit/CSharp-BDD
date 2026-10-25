using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Microsoft.Win32.SafeHandles;

namespace SelfAvoidingPaths;

/// <summary>Options for the residue ladder (<c>--ladder</c>). Disk speeds in GB/s; null = measure.</summary>
public sealed record LadderOptions(string Dir, double? BudgetGb = null, int[]? Rungs = null,
    double? DiskReadGBps = null, double? DiskWriteGBps = null);

/// <summary>
/// One sweep's view of the residue ladder: where to start (a row boundary whose
/// exact vector is already known) and at which boundaries to dump this run's
/// residues. Fill and Dump are thread-safe (positional file I/O).
/// </summary>
public sealed class LadderRun
{
    /// <summary>States per I/O chunk (even, so 12-bit chunks are byte-aligned).</summary>
    public const int Chunk = 1 << 16;
    public int StartRow;                                   // 0 = normal start
    public Action<long, Span<ulong>> Fill = (_, _) => { };  // values at StartRow for [start, start+len)
    public HashSet<int> DumpRows = [];
    public Action<int, long, ReadOnlySpan<ulong>> Dump = (_, _, _) => { };
    /// <summary>Called by the engine after each row with (boundary reached, row seconds), before
    /// that boundary's dump check; the ladder plans from it and may add dump rows.</summary>
    public Action<int, double>? OnRow;
    public readonly HashSet<int> Dumped = [];              // filled in by the engine
}

public sealed partial class DenseCounter
{
    // ------------------------------------------------------------ residue ladder
    //
    // The counts at row boundary k are far smaller than a(n): the largest one
    // has about 0.77*n*(k-1) bits (SAP_ROWBITS, n = 12..18), ~47% of a(n)'s bits
    // at mid-grid. So once the first req(k) runs of a CRT campaign have dumped
    // their residues at boundary k, the exact vector there is determined, and
    // every later run can start at row k from that vector reduced mod its own
    // modulus instead of recomputing rows 1..k-1. One extra residue (the
    // "check") is compared state by state when the rung is built, so an
    // undersized estimate or a corrupt dump is caught before the rung is used.
    // Rung files hold the Garner mixed-radix digits (same width as residues).
    //
    // The ladder is disk-bound on slow drives (n=22 on a SATA SSD: no gain), so
    // the rungs are planned with a time model fed by the first measured row time
    // and the measured disk speeds, and a background thread prepares the next
    // run's start vector (one file of residues mod its modulus) during the
    // current sweep, so a run reads one file instead of all digit files.

    /// <summary>Bit-length estimate (upper side) of the largest count at row boundary k.</summary>
    internal static int RungBits(int n, int k) => (int)Math.Ceiling(0.78 * n * (k - 1)) + 2 - ShortRungBits;

    // Test hook: SAP_LADDER_SHORT=<bits> undersizes every rung by that many bits, so
    // the per-state check must reject it (the campaign then continues without it).
    private static readonly int ShortRungBits =
        int.TryParse(Environment.GetEnvironmentVariable("SAP_LADDER_SHORT"), out int sb) ? sb : 0;

    internal sealed class ResidueLadder
    {
        private const uint Magic = 0x4C_50_41_53;   // "SAPL"
        private const int HeaderBytes = 64;
        // Cost model in units of one average sweep row, fitted to per-run timings of the
        // n=22 and n=23 test-box campaigns (N100, 12-bit; reproduces both totals within 1%).
        // CPU terms are relative to the row time, so they carry across machines; I/O terms
        // use the measured disk speeds, and each cost is the larger of the two.
        private const double RowTimeFactor = 1.05;        // average row / 2nd row (the early rows are sparser)
        private const double LoadRowsPerDigit = 0.15;     // direct load: whole-array Horner on all cores, per digit
        private const double PrefetchLoadRows = 0.28;     // loading a prepared start vector (read + store)
        private const double PrefetchStealRowsPerDigit = 0.06;  // sweep slowdown while the prefetch thread runs
        private const double DumpRows = 0.18;             // one dump (load + pack + write)
        private const double BuildRowsPerDigit2 = 0.015, BuildRowsPerDigit = 0.22;   // Garner build: a j^2 + b j
        private readonly int _n, _bits, _lastBoundary, _runs;
        private readonly long _count;
        private readonly string _dir;
        private readonly double _budget;
        private readonly LadderOptions _opt;
        private readonly Action<string>? _log;
        private readonly List<ulong> _order;          // the deterministic modulus list (0 = 2^bits)
        private int[]? _plan;                          // null = not planned yet (waits for a row time)
        private readonly Dictionary<int, int> _req = [];   // rung -> residues needed (digits + 1 check)
        private readonly HashSet<int> _ready = [], _failed = [];
        private readonly Dictionary<int, (string tmp, SafeFileHandle h)> _open = [];
        private readonly List<IDisposable> _fillRes = [];   // the current run's readers / handles
        private readonly List<string> _usedPre = [];        // prefetched files consumed by the current run
        private double _tRow, _rd = 1, _wr = 1;             // seconds per row; disk GB/s
        private Prefetch? _pre;

        private sealed class Prefetch
        {
            public int Rung, Idx;
            public required Task Task;
            public required CancellationTokenSource Cts;
        }

        public ResidueLadder(int n, int bits, long count, LadderOptions opt, int plannedRuns, Action<string>? log)
        {
            _n = n; _bits = bits; _count = count; _log = log; _opt = opt; _runs = plannedRuns;
            _dir = Path.GetFullPath(opt.Dir);
            Directory.CreateDirectory(_dir);
            _lastBoundary = n - 1;
            _order = CrtModuli(bits, plannedRuns + 8);
            _budget = opt.BudgetGb ?? new DriveInfo(_dir).AvailableFreeSpace * 0.9 / 1e9;
            foreach (string f in Directory.EnumerateFiles(_dir, $"sapladder_{n}_b{bits}_*.tmp")) File.Delete(f);
            foreach (string f in Directory.EnumerateFiles(_dir, $"sapladder_{n}_b{bits}_*.pre")) File.Delete(f);

            if (File.Exists(PlanPath))
            {
                // Resume: the files on disk belong to this plan, so keep it.
                var kv = File.ReadAllLines(PlanPath).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
                SetPlan(kv["rungs"].Length == 0 ? [] : kv["rungs"].Split(',').Select(int.Parse).ToArray());
                _tRow = double.Parse(kv["trow"], CultureInfo.InvariantCulture);
                _rd = double.Parse(kv["read"], CultureInfo.InvariantCulture);
                _wr = double.Parse(kv["write"], CultureInfo.InvariantCulture);
                _log?.Invoke($"ladder: resumed plan, rungs at row {string.Join(", ", _plan!)}");
            }
            else if (opt.Rungs is { } r)
            {
                SetPlan(r.Where(k => k >= 2 && k < _lastBoundary && Required(k) < _runs).Distinct().Order().ToArray());
                SavePlan();
                _log?.Invoke($"ladder: rungs at row {string.Join(", ", _plan!)} (given; residues needed {string.Join(", ", _plan!.Select(k => _req[k]))})");
            }
            else _log?.Invoke("ladder: plan follows the first measured row time");

            foreach (int k in _plan ?? [])
            {
                if (File.Exists(Marker(k, "converting"))) { DeleteRung(k); continue; }
                if (File.Exists(Marker(k, "ready"))) _ready.Add(k);
            }
        }

        private long FileBytes => HeaderBytes + _count * _bits / 8;
        private double FileGb => FileBytes / 1e9;
        private BigInteger ModVal(ulong m) => m == 0 ? BigInteger.One << _bits : m;
        private int Required(int k)
        {
            BigInteger need = BigInteger.One << RungBits(_n, k), p = BigInteger.One;
            int j = 0;
            while (p < need)
            {
                if (j == _order.Count) return int.MaxValue;   // needs more residues than the campaign has
                p *= ModVal(_order[j++]);
            }
            return j + 1;
        }
        private string ResPath(int k, int idx) => Path.Combine(_dir, $"sapladder_{_n}_b{_bits}_r{k}_i{idx}.res");
        private string PrePath(int k, int idx) => Path.Combine(_dir, $"sapladder_{_n}_b{_bits}_r{k}_p{idx}.pre");
        private string Marker(int k, string what) => Path.Combine(_dir, $"sapladder_{_n}_b{_bits}_r{k}.{what}");
        private string PlanPath => Path.Combine(_dir, $"sapladder_{_n}_b{_bits}.plan");

        private void SetPlan(int[] rungs)
        {
            _plan = rungs;
            foreach (int k in rungs) _req[k] = Required(k);
        }

        private void SavePlan() => File.WriteAllText(PlanPath, string.Create(CultureInfo.InvariantCulture,
            $"rungs={string.Join(",", _plan!)}\ntrow={_tRow}\nread={_rd}\nwrite={_wr}\n"));

        // ---- planning: a time model fed by the measured row time and disk speeds

        /// <summary>Plan at the first measured row (boundary <paramref name="boundary"/>) of run <paramref name="idx"/>.</summary>
        private void PlanNow(LadderRun lr, int boundary, double rowSeconds, int idx, ulong m)
        {
            _tRow = rowSeconds * RowTimeFactor;
            (_rd, _wr) = (_opt.DiskReadGBps, _opt.DiskWriteGBps) is (double r0, double w0) ? (r0, w0) : MeasureDisk();
            // A rung needs a dump from every one of its first req(k) runs: earlier runs
            // must already have dumped it (they have not, before the plan), and this run
            // can still dump only at boundaries it has not passed.
            var cands = Enumerable.Range(Math.Max(2, boundary), Math.Max(0, _lastBoundary - Math.Max(2, boundary)))
                .Where(k => Required(k) < _runs && Enumerable.Range(0, Math.Min(idx, Required(k))).All(i => File.Exists(ResPath(k, i))))
                .ToArray();
            double plain = _runs * (_lastBoundary - 1) * _tRow;
            int[] best = [];
            double bestT = plain, bestPeak = 0;
            void Try(int[] r)
            {
                var (t, peak) = Simulate(r);
                if (peak <= _budget && t < bestT * 0.99) { bestT = t; bestPeak = peak; best = r; }
            }
            for (int a = 0; a < cands.Length; a++)
            {
                Try([cands[a]]);
                for (int b = a + 1; b < cands.Length; b++)
                {
                    Try([cands[a], cands[b]]);
                    for (int c = b + 1; c < cands.Length; c++) Try([cands[a], cands[b], cands[c]]);
                }
            }
            SetPlan(best);
            SavePlan();
            string head = $"ladder: avg row ~{_tRow:F1}s, disk read {_rd:F2} / write {_wr:F2} GB/s, budget {_budget:F0} GB";
            _log?.Invoke(best.Length == 0 ? $"{head}: no rung pays off -> plain sweeps"
                : $"{head}: rungs at row {string.Join(", ", best)} (residues needed {string.Join(", ", best.Select(k => _req[k]))}); " +
                  $"predicted {bestT / plain:P0} of plain ({bestT / 3600:F2} h vs {plain / 3600:F2} h), peak disk ~{bestPeak:F1} GB");
            AddDumps(lr, idx, m, boundary);
        }

        /// <summary>(campaign seconds, peak disk GB) for a fresh campaign with these rungs.</summary>
        private (double seconds, double peakGb) Simulate(int[] rungs)
        {
            double S = FileGb, t = 0, peak = 0, prevSweep = 0;
            int cores = Environment.ProcessorCount, ready = 1;
            bool prefetched = false;
            var req = rungs.ToDictionary(k => k, Required);
            for (int i = 1; i <= _runs; i++)
            {
                if (ready > 1)
                {
                    int j = req[ready] - 1;
                    if (prefetched)
                    {
                        // prepared on one thread during the previous sweep, which it slows down;
                        // the run stalls if preparing took longer than that sweep
                        double steal = PrefetchStealRowsPerDigit * j * _tRow;
                        double prep = Math.Max(j * S / _rd + S / _wr, steal * cores);
                        t += Math.Max(S / _rd, PrefetchLoadRows * _tRow) + steal + Math.Max(0, prep - prevSweep);
                    }
                    else t += Math.Max(j * S / _rd, LoadRowsPerDigit * j * _tRow);
                    prefetched = true;
                }
                double sweep = (_lastBoundary - ready) * _tRow;
                t += sweep;
                prevSweep = sweep;
                int files = 0;
                foreach (int k in rungs)
                    if (k > ready)
                    {
                        if (i <= req[k]) t += Math.Max(S / _wr, DumpRows * _tRow);
                        files += Math.Min(i, req[k]);
                    }
                peak = Math.Max(peak, (files + (ready > 1 ? req[ready] - 1 + 2 : 0)) * S);
                foreach (int k in rungs)
                    if (req[k] == i && k > ready)
                    {
                        int j = req[k] - 1;
                        t += Math.Max((BuildRowsPerDigit2 * j * j + BuildRowsPerDigit * j) * _tRow, (j + 1) * S / _rd + (j - 1) * S / _wr);
                        ready = k;
                        prefetched = false;
                    }
            }
            return (t, peak);
        }

        /// <summary>Sequential unbuffered write and read of 512 MB in the ladder directory (GB/s).</summary>
        private unsafe (double read, double write) MeasureDisk()
        {
            const int Block = 8 << 20, Blocks = 64;
            string path = Path.Combine(_dir, $"sapladder_{_n}_b{_bits}_disktest.tmp");
            // FILE_FLAG_NO_BUFFERING (Windows): bypass the cache so the device is measured
            var opts = OperatingSystem.IsWindows() ? FileOptions.WriteThrough | (FileOptions)0x20000000 : FileOptions.WriteThrough;
            byte* buf = (byte*)NativeMemory.AlignedAlloc(Block, 4096);
            try
            {
                var span = new Span<byte>(buf, Block);
                new Random(1).NextBytes(span);
                var sw = Stopwatch.StartNew();
                using (var h = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, opts, (long)Block * Blocks))
                    for (int b = 0; b < Blocks; b++) RandomAccess.Write(h, new ReadOnlySpan<byte>(buf, Block), (long)b * Block);
                double w = (double)Block * Blocks / 1e9 / sw.Elapsed.TotalSeconds;
                sw.Restart();
                using (var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.None, opts))
                    for (int b = 0; b < Blocks; b++) RandomAccess.Read(h, new Span<byte>(buf, Block), (long)b * Block);
                double r = (double)Block * Blocks / 1e9 / sw.Elapsed.TotalSeconds;
                return (r, w);
            }
            finally
            {
                NativeMemory.AlignedFree(buf);
                File.Delete(path);
            }
        }

        // ---- per run

        /// <summary>Plan one run of modulus <paramref name="m"/>; null = plain sweep.</summary>
        public LadderRun? Prepare(ulong m)
        {
            int idx = _order.IndexOf(m);
            int start = _ready.Count == 0 ? 0 : _ready.Max();
            var lr = new LadderRun { StartRow = start };
            lr.Dump = (k, s, v) => WriteChunk(_open[k].h, s, v);
            if (start > 0) lr.Fill = TakePrefetch(start, idx) ?? MakeFill(start, m);
            else StopPrefetch();
            if (_plan is null)
            {
                // Plan from the second row: the first one includes JIT and cache warm-up.
                int seen = 0;
                lr.OnRow = (b, secs) => { if (_plan is null && ++seen == 2) PlanNow(lr, b, secs, idx, m); };
            }
            else AddDumps(lr, idx, m, start + 1);
            // the next run's start vector is prepared while this run sweeps
            if (start > 0 && idx >= 0 && idx + 1 < _runs) StartPrefetch(start, idx + 1);
            if (start == 0 && lr.OnRow is null && lr.DumpRows.Count == 0) return null;
            return lr;
        }

        private void AddDumps(LadderRun lr, int idx, ulong m, int minRow)
        {
            if (idx < 0) return;
            foreach (int k in _plan!)
            {
                if (k < minRow || _ready.Contains(k) || _failed.Contains(k) || idx >= _req[k] || File.Exists(ResPath(k, idx))) continue;
                string tmp = ResPath(k, idx) + ".tmp";
                var h = File.OpenHandle(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.None, FileBytes);
                WriteHeader(h, k, 0, m);       // kind 0 = residue, 1 = mixed-radix digit, 2 = prepared start vector
                _open[k] = (tmp, h);
                lr.DumpRows.Add(k);
            }
        }

        /// <summary>Close a run's dump files; keep only complete dumps of a successful run, then build any rung that is now complete.</summary>
        public void Finish(LadderRun? lr, bool ok)
        {
            if (lr is null) return;
            foreach (var d in _fillRes) d.Dispose();
            _fillRes.Clear();
            foreach (string f in _usedPre) File.Delete(f);
            _usedPre.Clear();
            foreach (var (k, (tmp, h)) in _open)
            {
                h.Dispose();
                if (ok && lr.Dumped.Contains(k)) File.Move(tmp, tmp[..^4], overwrite: true);
                else File.Delete(tmp);
            }
            _open.Clear();
            if (!ok || _plan is null) return;
            var complete = _plan.Where(k => !_ready.Contains(k) && !_failed.Contains(k)
                                            && Enumerable.Range(0, _req[k]).All(i => File.Exists(ResPath(k, i)))).ToArray();
            if (complete.Length == 0) return;
            StopPrefetch();   // it prepares from the rung about to be superseded, and would compete for the disk
            foreach (int k in complete) Build(k);
        }

        /// <summary>Delete every ladder file of this campaign.</summary>
        public void Cleanup()
        {
            StopPrefetch();
            foreach (var d in _fillRes) d.Dispose();
            _fillRes.Clear();
            foreach (string f in Directory.EnumerateFiles(_dir, $"sapladder_{_n}_b{_bits}_*")) File.Delete(f);
            foreach (string f in Directory.EnumerateFiles(_dir, $"sapladder_{_n}_b{_bits}.*")) File.Delete(f);
        }

        private void DeleteRung(int k)
        {
            foreach (string f in Directory.EnumerateFiles(_dir, $"sapladder_{_n}_b{_bits}_r{k}_*")) File.Delete(f);
            foreach (string f in Directory.EnumerateFiles(_dir, $"sapladder_{_n}_b{_bits}_r{k}.*")) File.Delete(f);
            _ready.Remove(k);
        }

        // ---- background prefetch of the next run's start vector

        private void StartPrefetch(int k, int idx)
        {
            StopPrefetch();
            ulong m = _order[idx];
            var cts = new CancellationTokenSource();
            var task = Task.Factory.StartNew(() => PrefetchBody(k, idx, m, cts.Token), cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _pre = new Prefetch { Rung = k, Idx = idx, Task = task, Cts = cts };
        }

        private void PrefetchBody(int k, int idx, ulong m, CancellationToken ct)
        {
            string path = PrePath(k, idx), tmp = path + ".tmp";
            bool done = false;
            try
            {
                using var reader = new RungReader(this, k, m);
                using var h = File.OpenHandle(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.None, FileBytes);
                WriteHeader(h, k, 2, m);
                var buf = new ulong[LadderRun.Chunk];
                for (long start = 0; start < _count && !ct.IsCancellationRequested; start += LadderRun.Chunk)
                {
                    int len = (int)Math.Min(LadderRun.Chunk, _count - start);
                    reader.Eval(start, buf.AsSpan(0, len));
                    WriteChunk(h, start, buf.AsSpan(0, len));
                }
                done = !ct.IsCancellationRequested;
            }
            finally
            {
                if (done) File.Move(tmp, path, overwrite: true);
                else File.Delete(tmp);
            }
        }

        private void StopPrefetch()
        {
            if (_pre is not { } p) return;
            p.Cts.Cancel();
            try { p.Task.Wait(); } catch (AggregateException) { }
            p.Cts.Dispose();
            File.Delete(PrePath(p.Rung, p.Idx));
            _pre = null;
        }

        /// <summary>The prepared start vector for (rung k, run idx), waiting for it if needed; null if there is none.</summary>
        private Action<long, Span<ulong>>? TakePrefetch(int k, int idx)
        {
            if (_pre is not { } p || p.Rung != k || p.Idx != idx) { StopPrefetch(); return null; }
            var sw = Stopwatch.StartNew();
            try { p.Task.Wait(); }
            catch (AggregateException e) { _log?.Invoke($"ladder: prefetch failed ({e.InnerException?.Message}); loading directly"); }
            p.Cts.Dispose();
            _pre = null;
            if (sw.Elapsed.TotalSeconds >= 1) _log?.Invoke($"  (ladder: waited {sw.Elapsed.TotalSeconds:F1}s for the prepared start vector)");
            string path = PrePath(k, idx);
            if (!File.Exists(path)) return null;
            var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
            _fillRes.Add(h);
            _usedPre.Add(path);
            return (start, dst) => ReadChunk(h, start, dst);
        }

        // ---- building a rung: residues -> mixed-radix digits, in place, with a per-state check

        private void Build(int k)
        {
            int j = _req[k] - 1;                 // digit count; residue j is the check
            File.WriteAllText(Marker(k, "converting"), "");
            var buildTimer = System.Diagnostics.Stopwatch.StartNew();
            var mods = Enumerable.Range(0, j + 1).Select(i => ModVal(_order[i])).ToArray();
            // inv[i] = (m_0 ... m_{i-1})^-1 mod m_i; red[t][i] = m_t mod m_i (all < m_i)
            var inv = new ulong[j];
            var red = new ulong[j + 1, j + 1];
            BigInteger prefix = BigInteger.One;
            for (int i = 0; i <= j; i++)
            {
                if (i > 0 && i < j) inv[i] = (ulong)ModInverse(prefix % mods[i], mods[i]);
                for (int t = 0; t < i; t++) red[t, i] = (ulong)(mods[t] % mods[i]);
                prefix *= mods[i];
            }
            var mv = mods.Select(x => x == BigInteger.One << 64 ? 0UL : (ulong)x).ToArray();   // 0 = 2^64
            var handles = Enumerable.Range(0, j + 1)
                .Select(i => File.OpenHandle(ResPath(k, i), FileMode.Open, i < j ? FileAccess.ReadWrite : FileAccess.Read)).ToArray();
            long bad = 0;
            try
            {
                long chunks = (_count + LadderRun.Chunk - 1) / LadderRun.Chunk;
                bool vec = _bits <= 16;   // products < 2^33: exact in double lanes
                var md = mods.Select(x => (double)x).ToArray();
                var redD = new double[j + 1, j + 1];
                for (int i = 0; i <= j; i++) for (int t = 0; t < i; t++) redD[t, i] = red[t, i];
                using var raws = new ThreadLocal<byte[][]>(() => Enumerable.Range(0, j + 1).Select(_ => new byte[ByteLen(LadderRun.Chunk)]).ToArray());
                using var dl = new ThreadLocal<double[][]>(() => Enumerable.Range(0, j + 2).Select(_ => new double[SubBlock]).ToArray());
                Parallel.For(0L, chunks, () => new ulong[j + 1][], (c, _, buf) =>
                {
                    long start = c * LadderRun.Chunk;
                    int len = (int)Math.Min(LadderRun.Chunk, _count - start);
                    if (vec)
                    {
                        // Packed chunk in, digits out in place; arithmetic in cache-sized sub-blocks.
                        var raw = raws.Value!;
                        var d = dl.Value!;
                        for (int i = 0; i <= j; i++) ReadRaw(handles[i], start, len, raw[i]);
                        long vb = 0;
                        for (int sb = 0; sb < len; sb += SubBlock)
                        {
                            int sl = Math.Min(SubBlock, len - sb);
                            for (int i = 0; i <= j; i++) UnpackD(raw[i], sb, sl, d[i]);
                            vb += GarnerVec(d, j, sl, md, redD, inv);
                            for (int i = 1; i < j; i++) PackD(d[i], sb, sl, raw[i]);
                        }
                        if (vb != 0) Interlocked.Add(ref bad, vb);
                        for (int i = 1; i < j; i++) WriteRaw(handles[i], start, len, raw[i]);
                        return buf;
                    }
                    for (int i = 0; i <= j; i++) ReadChunk(handles[i], start, (buf[i] ??= new ulong[LadderRun.Chunk]).AsSpan(0, len));
                    long localBad = 0;
                    for (int s = 0; s < len; s++)
                    {
                        // Garner: d_0 = r_0; d_i = (r_i - x_{i-1}) * inv_i mod m_i, x_{i-1} = d_0 + m_0 d_1 + ...
                        for (int i = 1; i <= j; i++)
                        {
                            ulong mi = mv[i];
                            ulong acc = buf[i - 1][s] % mi;
                            for (int t = i - 2; t >= 0; t--) acc = MulAddMod(acc, red[t, i], buf[t][s] % mi, mi);
                            ulong r = buf[i][s];
                            if (i == j) { if (acc != r) localBad++; break; }   // check residue
                            ulong diff = r >= acc ? r - acc : r + (mi - acc);
                            buf[i][s] = MulAddMod(diff, inv[i], 0, mi);
                        }
                    }
                    if (localBad != 0) Interlocked.Add(ref bad, localBad);
                    for (int i = 1; i < j; i++) WriteChunk(handles[i], start, buf[i].AsSpan(0, len));
                    return buf;
                }, _ => { });
            }
            finally
            {
                foreach (var h in handles) h.Dispose();
            }
            if (bad != 0)
            {
                _log?.Invoke($"ladder: rung {k} FAILED its check on {bad:N0} states (estimate too small or corrupt dump) -> discarded");
                DeleteRung(k);
                _failed.Add(k);
                return;
            }
            File.Delete(ResPath(k, j));
            for (int i = 0; i < j; i++)
                using (var h = File.OpenHandle(ResPath(k, i), FileMode.Open, FileAccess.ReadWrite)) WriteHeader(h, k, 1, _order[i]);
            File.WriteAllText(Marker(k, "ready"), $"digits={j}\n");
            File.Delete(Marker(k, "converting"));
            foreach (int lower in _ready.Where(x => x < k).ToArray()) DeleteRung(lower);
            _ready.Add(k);
            _log?.Invoke($"ladder: rung {k} ready ({j} digits, check passed on all {_count:N0} states, built in {buildTimer.Elapsed.TotalSeconds:F1}s); later runs start at row {k}");
        }

        private Action<long, Span<ulong>> MakeFill(int k, ulong target)
        {
            var reader = new RungReader(this, k, target);
            _fillRes.Add(reader);
            return reader.Eval;
        }

        /// <summary>Evaluates a ready rung mod one target modulus, chunk by chunk (thread-safe).</summary>
        private sealed class RungReader : IDisposable
        {
            private readonly ResidueLadder _l;
            private readonly int _j;
            private readonly ulong _t;                 // 0 = 2^64
            private readonly ulong[] _red;             // m_t mod target
            private readonly double[] _redD;
            private readonly SafeFileHandle[] _h;
            private readonly ThreadLocal<ulong[][]> _buf;
            private readonly ThreadLocal<byte[][]> _raw;
            private readonly ThreadLocal<double[][]> _d;

            public RungReader(ResidueLadder l, int k, ulong target)
            {
                _l = l;
                _j = int.Parse(File.ReadAllText(l.Marker(k, "ready")).Trim()["digits=".Length..]);
                int j = _j;
                BigInteger tv = l.ModVal(target);
                _t = tv == BigInteger.One << 64 ? 0UL : (ulong)tv;
                _red = Enumerable.Range(0, j).Select(t => (ulong)(l.ModVal(l._order[t]) % tv)).ToArray();
                _redD = _red.Select(x => (double)x).ToArray();
                _h = Enumerable.Range(0, j).Select(i => File.OpenHandle(l.ResPath(k, i), FileMode.Open, FileAccess.Read)).ToArray();
                _buf = new(() => Enumerable.Range(0, j).Select(_ => new ulong[LadderRun.Chunk]).ToArray());
                _raw = new(() => Enumerable.Range(0, j).Select(_ => new byte[l.ByteLen(LadderRun.Chunk)]).ToArray());
                _d = new(() => Enumerable.Range(0, j).Select(_ => new double[SubBlock]).ToArray());
            }

            public void Eval(long start, Span<ulong> dst)
            {
                int j = _j;
                if (_l._bits <= 16)
                {
                    // Keep the chunk packed; unpack and evaluate in cache-sized sub-blocks.
                    var raw = _raw.Value!;
                    var d = _d.Value!;
                    for (int i = 0; i < j; i++) _l.ReadRaw(_h[i], start, dst.Length, raw[i]);
                    for (int sb = 0; sb < dst.Length; sb += SubBlock)
                    {
                        int len = Math.Min(SubBlock, dst.Length - sb);
                        for (int i = 0; i < j; i++) _l.UnpackD(raw[i], sb, len, d[i]);
                        HornerVec(d, j, len, dst.Slice(sb, len), _redD, _t);
                    }
                    return;
                }
                var buf = _buf.Value!;
                for (int i = 0; i < j; i++) _l.ReadChunk(_h[i], start, buf[i].AsSpan(0, dst.Length));
                for (int s = 0; s < dst.Length; s++)
                {
                    ulong acc = _t == 0 ? buf[j - 1][s] : buf[j - 1][s] % _t;
                    for (int t = j - 2; t >= 0; t--) acc = MulAddMod(acc, _red[t], _t == 0 ? buf[t][s] : buf[t][s] % _t, _t);
                    dst[s] = acc;
                }
            }

            public void Dispose()
            {
                foreach (var h in _h) h.Dispose();
                _buf.Dispose(); _raw.Dispose(); _d.Dispose();
            }
        }

        // ---- vector kernels (12/16-bit moduli): exact integers in double lanes. All
        // intermediates are < 2^33, so x*(1/m) is off by < 1 and one correction suffices.
        // Each kernel is a pass over a whole chunk (digit outer, state inner), so the
        // states' modular chains are independent and the loop is throughput-bound.

        private static readonly int VC = Vector<double>.Count;
        private static readonly bool Avx2Fma = Avx2.IsSupported && Fma.IsSupported;

        private static int Padded(int len) => (len + VC - 1) / VC * VC;

        /// <summary>acc[s] = (acc[s] * mul + add[s]) mod m over [0, vl); with add = null, acc[s] = src mod m.</summary>
        private static unsafe void MulAddModPass(double[] acc, double mul, double[]? add, double[]? src, int vl, double m)
        {
            double inv = 1.0 / m;
            fixed (double* pa = acc, pd = add, ps = src)
            {
                int s = 0;
                if (Avx2Fma)
                {
                    var vm = Vector256.Create(m); var vi = Vector256.Create(inv); var vmul = Vector256.Create(mul);
                    for (; s + 4 <= vl; s += 4)
                    {
                        var x = ps != null ? Avx.LoadVector256(ps + s) : Fma.MultiplyAdd(Avx.LoadVector256(pa + s), vmul, Avx.LoadVector256(pd + s));
                        var r = Fma.MultiplyAddNegated(Avx.RoundToNegativeInfinity(Avx.Multiply(x, vi)), vm, x);   // x - floor(x/m)*m
                        r = Avx.Add(r, Avx.And(Avx.CompareLessThan(r, Vector256<double>.Zero), vm));
                        r = Avx.Subtract(r, Avx.And(Avx.CompareGreaterThanOrEqual(r, vm), vm));
                        Avx.Store(pa + s, r);
                    }
                }
                for (; s < vl; s++)
                {
                    double x = ps != null ? ps[s] : pa[s] * mul + pd[s];
                    double r = x - Math.Floor(x * inv) * m;
                    if (r < 0) r += m; else if (r >= m) r -= m;
                    pa[s] = r;
                }
            }
        }

        /// <summary>Garner in place on one sub-block: d[0..j) residues -> mixed-radix digits; d[j] is the
        /// check residue, d[j+1] scratch. Returns the number of states whose check fails.</summary>
        private static long GarnerVec(double[][] d, int j, int len, double[] md, double[,] red, ulong[] inv)
        {
            int vl = Padded(len);
            for (int i = 0; i <= j; i++) Array.Clear(d[i], len, vl - len);   // zero padding is self-consistent
            double[] acc = d[j + 1];
            long bad = 0;
            for (int i = 1; i <= j; i++)
            {
                // acc = x_{i-1} mod m_i, by Horner over the digits found so far
                MulAddModPass(acc, 0, null, d[i - 1], vl, md[i]);
                for (int t = i - 2; t >= 0; t--) MulAddModPass(acc, red[t, i], d[t], null, vl, md[i]);
                double[] r = d[i];
                if (i == j)
                {
                    for (int s = 0; s < len; s++) if (acc[s] != r[s]) bad++;
                    break;
                }
                // d_i = (r_i - acc) * inv_i mod m_i   (r_i - acc + m_i >= 0)
                for (int s = 0; s < vl; s++) acc[s] = r[s] - acc[s] + md[i];
                MulAddModPass(r, 0, null, acc, vl, md[i]);              // r = (r - acc) mod m_i
                for (int s = 0; s < vl; s++) acc[s] = r[s] * inv[i];  // < 2^32: exact
                MulAddModPass(r, 0, null, acc, vl, md[i]);              // d_i
            }
            return bad;
        }

        /// <summary>States per in-cache sub-block of a load (j digit arrays of 8 KB stay in L2).</summary>
        private const int SubBlock = 1024;

        /// <summary>dst = (d_0 + m_0 (d_1 + m_1 (...))) mod T per state; red[t] = m_t mod T. d[i][0..len) hold the
        /// digits; lanes up to Padded(len) are scratch.</summary>
        private static void HornerVec(double[][] d, int j, int len, Span<ulong> dst, double[] red, ulong T)
        {
            int vl = Padded(len);
            for (int i = 0; i < j; i++) Array.Clear(d[i], len, vl - len);
            double[] acc = d[j - 1];             // in place: digit j-1 becomes the accumulator
            MulAddModPass(acc, 0, null, acc, vl, T);
            for (int t = j - 2; t >= 0; t--) MulAddModPass(acc, red[t], d[t], null, vl, T);
            for (int s = 0; s < len; s++) dst[s] = (ulong)(long)acc[s];
        }

        /// <summary>(a*b + c) mod m for a, c &lt; m (m = 0 means 2^64).</summary>
        private static ulong MulAddMod(ulong a, ulong b, ulong c, ulong m)
        {
            if (m == 0) return unchecked(a * b + c);
            if (m <= 1UL << 32) return (a * b + c) % m;
            return (ulong)(((UInt128)a * b + c) % m);
        }

        // ---- file format: 64-byte header, then counters at _bits bits each

        private void WriteHeader(SafeFileHandle h, int k, int kind, ulong m)
        {
            Span<byte> hd = stackalloc byte[HeaderBytes];
            hd.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(hd, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(hd[4..], 1);
            BinaryPrimitives.WriteInt32LittleEndian(hd[8..], _n);
            BinaryPrimitives.WriteInt32LittleEndian(hd[12..], _bits);
            BinaryPrimitives.WriteInt32LittleEndian(hd[16..], k);
            BinaryPrimitives.WriteInt32LittleEndian(hd[20..], kind);
            BinaryPrimitives.WriteUInt64LittleEndian(hd[24..], m);
            BinaryPrimitives.WriteInt64LittleEndian(hd[32..], _count);
            RandomAccess.Write(h, hd, 0);
        }

        private int ByteLen(int len) => (int)((long)len * _bits / 8);   // len is even except for the last chunk of an odd count
        private long Offset(long start) => HeaderBytes + start * _bits / 8;

        private void WriteChunk(SafeFileHandle h, long start, ReadOnlySpan<ulong> v)
        {
            byte[] raw = ArrayPool<byte>.Shared.Rent(ByteLen(v.Length) + 2);
            Pack(v, raw);
            RandomAccess.Write(h, raw.AsSpan(0, (int)((v.Length * (long)_bits + 7) / 8)), Offset(start));
            ArrayPool<byte>.Shared.Return(raw);
        }

        /// <summary>Read the packed bytes of states [start, start+count) into raw.</summary>
        private void ReadRaw(SafeFileHandle h, long start, int count, byte[] raw)
        {
            int want = (int)((count * (long)_bits + 7) / 8);
            int got = 0;
            while (got < want)
            {
                int r = RandomAccess.Read(h, raw.AsSpan(got, want - got), Offset(start) + got);
                if (r <= 0) throw new IOException("ladder file truncated");
                got += r;
            }
        }

        /// <summary>Unpack states [first, first+count) of a raw chunk (first even) into doubles (12/16-bit).</summary>
        private void UnpackD(byte[] raw, int first, int count, double[] dst)
        {
            if (_bits == 12)
            {
                int o = first / 2 * 3;
                for (int i = 0; i < count; i += 2, o += 3)
                {
                    int b1 = raw[o + 1];
                    dst[i] = raw[o] | ((b1 & 0xF) << 8);
                    if (i + 1 < count) dst[i + 1] = (b1 >> 4) | (raw[o + 2] << 4);
                }
            }
            else
                for (int i = 0; i < count; i++) dst[i] = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(2 * (first + i)));
        }

        /// <summary>Pack doubles into states [first, first+count) of a raw chunk (first even; 12/16-bit).</summary>
        private void PackD(double[] src, int first, int count, byte[] raw)
        {
            if (_bits == 12)
            {
                int o = first / 2 * 3;
                for (int i = 0; i < count; i += 2, o += 3)
                {
                    int x = (int)src[i], y = i + 1 < count ? (int)src[i + 1] : 0;
                    raw[o] = (byte)x; raw[o + 1] = (byte)((x >> 8) | (y << 4)); raw[o + 2] = (byte)(y >> 4);
                }
            }
            else
                for (int i = 0; i < count; i++) BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(2 * (first + i)), (ushort)src[i]);
        }

        private void WriteRaw(SafeFileHandle h, long start, int count, byte[] raw)
            => RandomAccess.Write(h, raw.AsSpan(0, (int)((count * (long)_bits + 7) / 8)), Offset(start));

        private void ReadChunk(SafeFileHandle h, long start, Span<ulong> v)
        {
            byte[] raw = ArrayPool<byte>.Shared.Rent(ByteLen(v.Length) + 2);
            int want = (int)((v.Length * (long)_bits + 7) / 8);
            int got = 0;
            while (got < want)
            {
                int r = RandomAccess.Read(h, raw.AsSpan(got, want - got), Offset(start) + got);
                if (r <= 0) throw new IOException("ladder file truncated");
                got += r;
            }
            Unpack(raw, v);
            ArrayPool<byte>.Shared.Return(raw);
        }

        private void Pack(ReadOnlySpan<ulong> v, Span<byte> b)
        {
            switch (_bits)
            {
                case 12:
                    for (int i = 0, o = 0; i < v.Length; i += 2, o += 3)
                    {
                        uint x = (uint)v[i], y = i + 1 < v.Length ? (uint)v[i + 1] : 0;
                        b[o] = (byte)x; b[o + 1] = (byte)((x >> 8) | (y << 4)); b[o + 2] = (byte)(y >> 4);
                    }
                    break;
                case 16: for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(b[(2 * i)..], (ushort)v[i]); break;
                case 32: for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b[(4 * i)..], (uint)v[i]); break;
                default: for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(b[(8 * i)..], v[i]); break;
            }
        }

        private void Unpack(ReadOnlySpan<byte> b, Span<ulong> v)
        {
            switch (_bits)
            {
                case 12:
                    for (int i = 0, o = 0; i < v.Length; i += 2, o += 3)
                    {
                        v[i] = b[o] | ((uint)(b[o + 1] & 0xF) << 8);
                        if (i + 1 < v.Length) v[i + 1] = (uint)(b[o + 1] >> 4) | ((uint)b[o + 2] << 4);
                    }
                    break;
                case 16: for (int i = 0; i < v.Length; i++) v[i] = BinaryPrimitives.ReadUInt16LittleEndian(b[(2 * i)..]); break;
                case 32: for (int i = 0; i < v.Length; i++) v[i] = BinaryPrimitives.ReadUInt32LittleEndian(b[(4 * i)..]); break;
                default: for (int i = 0; i < v.Length; i++) v[i] = BinaryPrimitives.ReadUInt64LittleEndian(b[(8 * i)..]); break;
            }
        }
    }
}
