using System.Collections.Concurrent;
using System.Numerics;

namespace SelfAvoidingPaths;

/// <summary>
/// Counts self-avoiding (non-intersecting) rook paths between opposite corners
/// of an n x n grid graph (OEIS A007764) using the frontier-based search /
/// SIMPATH technique of Knuth (TAOCP 7.1.4, ex. 225), in the memory-lean
/// "direct" form of Iwashita, Kawahara &amp; Minato (TCS-TR-A-12-60, 2012).
///
/// We sweep edges in a fixed order and keep only the *live frontier layer*:
/// a map from a compact `mate`-state to the number of partial paths that reach
/// it. Each layer is discarded as soon as its counts flow into the next, so
/// peak memory is O(#live states) rather than the size of the whole ZDD.
/// </summary>
public sealed class FrontierCounter
{
    // Special mate[] values. Otherwise mate[v] is either v itself (isolated /
    // untouched) or the id of the other open end of v's path fragment.
    private const int Interior = 0;   // vertex has degree 2, fully internal
    private const int Anchor = -1;    // open end whose fragment's far end is a
                                      // terminal that already reached degree 1

    private readonly int _n;
    private readonly int _vertexCount;
    private readonly int[] _edgeU;        // edge endpoints (1-based vertex ids)
    private readonly int[] _edgeV;
    private readonly int _edgeCount;
    private readonly bool[] _isTerminal;  // s and t
    private readonly int[] _firstEdge;    // 1-based edge index a vertex first appears
    private readonly int[] _lastEdge;     // 1-based edge index a vertex last appears
    private readonly int[][] _layerVerts; // frontier vertices after edge k (sorted)

    public long PeakStates { get; private set; }
    public long TotalStatesVisited { get; private set; }

    /// <summary>Total number of edges = number of frontier layers to sweep.</summary>
    public int EdgeCount => _edgeCount;

    public FrontierCounter(int n)
    {
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(n), "n must be >= 1");
        _n = n;
        _vertexCount = n * n;

        // Vertices numbered 1..n*n, row-major: id(r,c) = r*n + c + 1.
        // Edges: horizontal within a row, vertical between rows.
        var edges = new List<(int a, int b)>();
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                int id = r * n + c + 1;
                if (c + 1 < n) edges.Add((id, id + 1));      // right neighbour
                if (r + 1 < n) edges.Add((id, id + n));      // down neighbour
            }
        }
        // Lexicographic edge order (min endpoint, then max) keeps the frontier
        // to a narrow band of width ~ n+1 for a square grid.
        edges.Sort((x, y) =>
        {
            int xu = Math.Min(x.a, x.b), xv = Math.Max(x.a, x.b);
            int yu = Math.Min(y.a, y.b), yv = Math.Max(y.a, y.b);
            return xu != yu ? xu.CompareTo(yu) : xv.CompareTo(yv);
        });

        _edgeCount = edges.Count;
        _edgeU = new int[_edgeCount + 1]; // 1-based
        _edgeV = new int[_edgeCount + 1];
        for (int k = 1; k <= _edgeCount; k++)
        {
            _edgeU[k] = Math.Min(edges[k - 1].a, edges[k - 1].b);
            _edgeV[k] = Math.Max(edges[k - 1].a, edges[k - 1].b);
        }

        _isTerminal = new bool[_vertexCount + 1];
        _isTerminal[1] = true;                 // top-left corner  (s)
        _isTerminal[_vertexCount] = true;      // bottom-right corner (t)

        _firstEdge = new int[_vertexCount + 1];
        _lastEdge = new int[_vertexCount + 1];
        Array.Fill(_firstEdge, int.MaxValue);
        Array.Fill(_lastEdge, 0);
        for (int k = 1; k <= _edgeCount; k++)
        {
            foreach (int x in stackalloc[] { _edgeU[k], _edgeV[k] })
            {
                if (k < _firstEdge[x]) _firstEdge[x] = k;
                if (k > _lastEdge[x]) _lastEdge[x] = k;
            }
        }

        // Frontier after edge k = { v : firstEdge[v] <= k < lastEdge[v] }.
        _layerVerts = new int[_edgeCount + 1][];
        _posMap = new int[_edgeCount + 1][];
        _bits = new int[_edgeCount + 1];
        _keyBytes = new int[_edgeCount + 1];
        for (int k = 0; k <= _edgeCount; k++)
        {
            var list = new List<int>();
            for (int v = 1; v <= _vertexCount; v++)
                if (_firstEdge[v] <= k && k < _lastEdge[v])
                    list.Add(v);
            _layerVerts[k] = list.ToArray(); // already ascending

            // Position of each frontier vertex within layer k (for encoding at
            // an arbitrary layer during a lookahead skip). -1 for non-frontier.
            var pos = new int[_vertexCount + 1];
            Array.Fill(pos, -1);
            for (int i = 0; i < _layerVerts[k].Length; i++) pos[_layerVerts[k][i]] = i;
            _posMap[k] = pos;

            // Bit-packed key: each of the w frontier vertices holds one code in
            // {SELF, INTERIOR, ANCHOR, partner-position 0..w-1} => range [0, w+2],
            // so ceil(log2(w+3)) bits per vertex instead of a whole byte. With
            // PackKeys off, bits = 8 reproduces the plain byte-per-vertex layout.
            int w = _layerVerts[k].Length;
            int bits = 8;
            if (PackKeys) { bits = 1; while ((1 << bits) < w + 3) bits++; }
            _bits[k] = bits;
            _keyBytes[k] = (w * bits + 7) / 8;
        }
    }

    /// <summary>Bit-pack keys (memory-lean, ~13% slower in-core). Off = plain 1 byte/vertex.</summary>
    public static bool PackKeys = true;

    private readonly int[][] _posMap;   // _posMap[layer][vertex] = index in that layer's frontier
    private readonly int[] _bits;       // bits per frontier-vertex code, per layer
    private readonly int[] _keyBytes;   // packed key length in bytes, per layer
    private int MaxKeyBytes() { int m = 1; foreach (var b in _keyBytes) if (b > m) m = b; return m; }

    /// <summary>
    /// Number of self-avoiding s-t paths (A007764(n)). Each layer is expanded
    /// in parallel; source states are independent, so this scales with cores.
    /// The layer is a <see cref="StateStore"/> (hash-sharded, inline keys), so
    /// accumulation into the next layer contends only within a shard and does
    /// not allocate a key object per probe.
    /// </summary>
    /// <param name="onLayer">optional progress callback (edge index, live states)</param>
    /// <param name="maxDegreeOfParallelism">worker cap; defaults to all cores</param>
    /// <param name="outOfCore">spill layers larger than the budget to disk</param>
    /// <param name="budgetBytes">RAM budget that triggers spilling (out-of-core mode)</param>
    /// <param name="tempRoot">directory for spill files (defaults to the system temp)</param>
    /// <param name="checkpointDir">enable periodic checkpointing + auto-resume here</param>
    /// <param name="checkpointIntervalSec">seconds between checkpoints</param>
    /// <param name="stopAtLayer">debug: stop (leaving a checkpoint) after this layer</param>
    /// <param name="log">optional status callback (resume / checkpoint messages)</param>
    public BigInteger Count(Action<int, long, bool>? onLayer = null, int? maxDegreeOfParallelism = null,
        bool outOfCore = false, long budgetBytes = 0, string? tempRoot = null, bool compressSpill = false,
        string? checkpointDir = null, double checkpointIntervalSec = 300, int stopAtLayer = -1,
        Action<string>? log = null)
    {
        // n == 1: a single vertex, s == t, the empty path is the one path.
        if (_vertexCount == 1) return BigInteger.One;

        int dop = maxDegreeOfParallelism ?? Environment.ProcessorCount;
        var opts = new ParallelOptions { MaxDegreeOfParallelism = dop };

        // Fixed limb width for counts. Path counts grow ~0.21 * n^2 digits; a
        // 0.25 slope bound already carries a comfortable ~15-25% digit margin, so
        // no extra limb is added. The store's hard overflow guard is the backstop:
        // an underestimate aborts loudly rather than corrupting a result.
        double estBits = 0.25 * _vertexCount * 3.3219;   // digits -> bits
        int valWidth = Math.Max(2, (int)Math.Ceiling(estBits / 64.0));

        // A layer is spilled to disk when its source state count exceeds this
        // threshold; below it, it stays fully in RAM (fast path). The threshold
        // is derived from the actual per-state footprint (key bytes + limb bytes
        // + table overhead) times a factor ~3 that covers holding roughly two
        // layers plus arena/GC slack — so it uses the budget rather than a magic
        // constant. The factor keeps the true resident set comfortably under the
        // budget (measured ~2.5x raw at the peak), leaving headroom against OOM.
        long perStateEst = (MaxKeyBytes() + valWidth * 8L + 8) * 3;
        long threshold = outOfCore ? Math.Max(1024, budgetBytes / perStateEst) : long.MaxValue;
        string? oocDir = null;
        EventHandler? onProcExit = null;
        ConsoleCancelEventHandler? onCancel = null;
        if (outOfCore)
        {
            oocDir = Path.Combine(tempRoot ?? Path.GetTempPath(), "sap_ooc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(oocDir);

            // Best-effort cleanup on Ctrl-C / process exit, so an interrupted run
            // does not leave multi-GB spill files behind (a hard kill still can;
            // the startup sweep in Program catches those next time).
            string dir = oocDir;
            onProcExit = (_, _) => { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } };
            onCancel = (_, _) => onProcExit(null, EventArgs.Empty);
            AppDomain.CurrentDomain.ProcessExit += onProcExit;
            Console.CancelKeyPress += onCancel;
        }

        int[] posChild = new int[_vertexCount + 1];

        // Resume from a checkpoint if one exists for this exact problem; else
        // start from the root (empty frontier, one empty-key state, count 1).
        int startEdge = 1;
        ILayer current;
        var ckTimer = System.Diagnostics.Stopwatch.StartNew();
        var loaded = checkpointDir is null ? null : Checkpoint.TryLoad(checkpointDir, _n, _edgeCount);
        if (loaded is not null && loaded.ValWidth == valWidth)
        {
            current = Checkpoint.LoadLayer(loaded, threshold);
            startEdge = loaded.CompletedEdge + 1;
            PeakStates = loaded.PeakStates;
            TotalStatesVisited = loaded.TotalStates;
            log?.Invoke($"resumed from checkpoint: layer {loaded.CompletedEdge}/{_edgeCount}");
        }
        else
        {
            var root = new StateStore(_keyBytes[0], valWidth);
            Span<ulong> one = stackalloc ulong[valWidth];
            one[0] = 1UL;
            root.Accumulate(ReadOnlySpan<byte>.Empty, one);
            current = new RamLayer(root);
        }

        try
        {
            for (int k = startEdge; k <= _edgeCount; k++)
            {
                var ep = BuildEdge(k, posChild);
                bool spilled = current.Count > threshold;
                // Seed the next layer's shard capacity from this layer's per-shard
                // average. Consecutive frontier layers share the same hash, so the
                // distribution is near-identical; near the peak (layer k+1 ~= k) this
                // eliminates almost all arena Grow() reallocations — cutting LOH/gen2
                // GC churn without keeping arenas resident (peak RAM stays lean).
                int initCap = (int)Math.Min(int.MaxValue, current.Count / StateStore.ShardCount);
                ISink sink = spilled
                    ? new DiskSink(ep.ChildKeyBytes, valWidth, oocDir!, k, compressSpill)
                    : new RamSink(ep.ChildKeyBytes, valWidth, initCap);

                var src = current;
                Parallel.ForEach(Partitioner.Create(0, src.Partitions), opts,
                    () => new Worker(_vertexCount, ep.ChildKeyBytes),
                    (range, _, worker) =>
                    {
                        for (int p = range.Item1; p < range.Item2; p++)
                        {
                            src.Read(p, (key, val) => ExpandRecord(worker, key, val, ep, sink));
                            src.ReleasePartition(p);
                        }
                        return worker;
                    },
                    _ => { });

                ILayer next = sink.Finish();
                current.Dispose();
                current = next;

                long states = current.Count;
                TotalStatesVisited += states;
                if (states > PeakStates) PeakStates = states;
                onLayer?.Invoke(k, states, spilled);

                // Periodic checkpoint (not after the final layer — the run is done then).
                if (checkpointDir is not null && k < _edgeCount &&
                    ckTimer.Elapsed.TotalSeconds >= checkpointIntervalSec)
                {
                    Checkpoint.Write(checkpointDir, current, _n, _edgeCount, valWidth, k, PeakStates, TotalStatesVisited);
                    ckTimer.Restart();
                    log?.Invoke($"checkpoint saved: layer {k}/{_edgeCount}");
                }

                if (k == stopAtLayer)
                {
                    if (checkpointDir is not null)
                        Checkpoint.Write(checkpointDir, current, _n, _edgeCount, valWidth, k, PeakStates, TotalStatesVisited);
                    throw new StopRequested(k);
                }
            }

            // Final frontier is empty; the single accept state carries the answer.
            var ansLimbs = new ulong[valWidth];
            bool got = false;
            for (int p = 0; p < current.Partitions; p++)
                current.Read(p, (_, v) => { v.CopyTo(ansLimbs); got = true; });

            if (checkpointDir is not null) Checkpoint.Cleanup(checkpointDir); // run finished
            return got ? StateStore.ToBigInteger(ansLimbs) : BigInteger.Zero;
        }
        finally
        {
            current.Dispose();
            if (oocDir is not null)
                try { if (Directory.Exists(oocDir)) Directory.Delete(oocDir, recursive: true); } catch { }
            if (onProcExit is not null) AppDomain.CurrentDomain.ProcessExit -= onProcExit;
            if (onCancel is not null) Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>Per-worker scratch: decode buffer and reusable key-encode buffer.</summary>
    private sealed class Worker
    {
        public readonly int[] Mate;
        public readonly byte[] KeyBuf;
        public Worker(int vertexCount, int keyBufBytes)
        {
            Mate = new int[vertexCount + 1];
            for (int v = 0; v <= vertexCount; v++) Mate[v] = v;
            KeyBuf = new byte[keyBufBytes];
        }
    }

    /// <summary>Fixed per-edge data shared read-only across workers.</summary>
    private sealed class EdgeParams
    {
        public int[] ParentVerts = null!, ChildVerts = null!, PosChild = null!;
        public int U, W, ParentWidth, ChildWidth, ParentBits, ChildBits, ChildKeyBytes;
        public bool UIntro, WIntro, ULeave, WLeave;
    }

    private EdgeParams BuildEdge(int k, int[] posChild)
    {
        int u = _edgeU[k], w = _edgeV[k];
        int[] childVerts = _layerVerts[k];
        for (int i = 0; i < childVerts.Length; i++) posChild[childVerts[i]] = i;
        return new EdgeParams
        {
            ParentVerts = _layerVerts[k - 1],
            ChildVerts = childVerts,
            PosChild = posChild,
            U = u, W = w,
            ParentWidth = _layerVerts[k - 1].Length,
            ChildWidth = childVerts.Length,
            ParentBits = _bits[k - 1],
            ChildBits = _bits[k],
            ChildKeyBytes = _keyBytes[k],
            UIntro = _firstEdge[u] == k, WIntro = _firstEdge[w] == k,
            ULeave = _lastEdge[u] == k, WLeave = _lastEdge[w] == k,
        };
    }

    /// <summary>
    /// Expand one parent (key, count) into its ≤2 children, emitting each to the
    /// sink. Mirrors the SIMPATH mate transitions; mate[] is restored on exit.
    /// </summary>
    private void ExpandRecord(Worker worker, ReadOnlySpan<byte> key, ReadOnlySpan<ulong> cnt,
        EdgeParams ep, ISink sink)
    {
        int[] mate = worker.Mate;
        byte[] keyBuf = worker.KeyBuf;
        int[] parentVerts = ep.ParentVerts;
        int pw = ep.ParentWidth, u = ep.U, w = ep.W, pbits = ep.ParentBits;
        var childKey = keyBuf.AsSpan(0, ep.ChildKeyBytes);

        for (int i = 0; i < pw; i++)
            mate[parentVerts[i]] = Decode(GetCode(key, i, pbits), parentVerts[i], parentVerts);
        if (ep.UIntro) mate[u] = u;
        if (ep.WIntro) mate[w] = w;

        // child c = 0 : edge excluded
        if (LeaveValid(mate, u, w, ep.ULeave, ep.WLeave))
        {
            EncodeInto(mate, ep.ChildVerts, ep.PosChild, ep.ChildBits, childKey);
            sink.Add(childKey, cnt);
        }

        // child c = 1 : edge included
        if (TryInclude(mate, u, w, out _, out _))
        {
            if (LeaveValid(mate, u, w, ep.ULeave, ep.WLeave))
            {
                EncodeInto(mate, ep.ChildVerts, ep.PosChild, ep.ChildBits, childKey);
                sink.Add(childKey, cnt);
            }
        }

        // reset touched cells to isolated for the next source record
        for (int i = 0; i < pw; i++) mate[parentVerts[i]] = parentVerts[i];
        mate[u] = u; mate[w] = w;
    }

    // ============================ windowed lookahead ============================
    //
    // Applies the zero-suppress "node deletion" rule on the fly (Iwashita & Minato,
    // TCS-TR-A-13-69) with a bounded skip window W: when a produced child cannot
    // include the next edge (that branch is forced to the 0-terminal), the only
    // surviving branch is exclude, so we fast-forward along it — up to W edges —
    // and materialise the state at the layer where it actually branches. This
    // drops "pass-through" states from intermediate layers, lowering the peak.
    //
    // The sweep keeps a ring of W+1 layer sinks (children can land up to W layers
    // ahead). Each sink is RAM or disk (out-of-core) — the choice never affects
    // the counts, only footprint — so large lookahead runs spill like Count does.
    // Not combined with --checkpoint (the W+1 partial layers aren't checkpointed).

    /// <summary>Number of self-avoiding s-t paths, with a windowed lookahead of W (W>=0).</summary>
    public BigInteger CountLookahead(int window, Action<int, long, bool>? onLayer = null,
        int? maxDegreeOfParallelism = null,
        bool outOfCore = false, long budgetBytes = 0, string? tempRoot = null, bool compressSpill = false)
    {
        if (_vertexCount == 1) return BigInteger.One;
        int W = Math.Max(0, window);
        int dop = maxDegreeOfParallelism ?? Environment.ProcessorCount;
        var opts = new ParallelOptions { MaxDegreeOfParallelism = dop };
        int maxKeyBytes = MaxKeyBytes();

        double estBits = 0.25 * _vertexCount * 3.3219;
        int valWidth = Math.Max(2, (int)Math.Ceiling(estBits / 64.0));

        // A ring slot spills when its (proxy) size would push the W+1 live layers
        // over the budget; hence budget / (W+1) per slot.
        long perStateEst = (MaxKeyBytes() + valWidth * 8L + 8) * 3;
        long ringThreshold = outOfCore ? Math.Max(1024, budgetBytes / perStateEst / (W + 1)) : long.MaxValue;

        string? oocDir = null;
        EventHandler? onProcExit = null;
        ConsoleCancelEventHandler? onCancel = null;
        if (outOfCore)
        {
            oocDir = Path.Combine(tempRoot ?? Path.GetTempPath(), "sap_ooc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(oocDir);
            string dir = oocDir;
            onProcExit = (_, _) => { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } };
            onCancel = (_, _) => onProcExit(null, EventArgs.Empty);
            AppDomain.CurrentDomain.ProcessExit += onProcExit;
            Console.CancelKeyPress += onCancel;
        }

        ISink? NewSink(int layer, long proxyCount)
        {
            if (layer > _edgeCount) return null;
            // Seed shard capacity from the proxy size (the sibling layer just
            // completed) to avoid arena Grow() churn — see the note in Count().
            int initCap = (int)Math.Min(int.MaxValue, proxyCount / StateStore.ShardCount);
            return outOfCore && proxyCount > ringThreshold
                ? new DiskSink(_keyBytes[layer], valWidth, oocDir!, layer, compressSpill)
                : new RamSink(_keyBytes[layer], valWidth, initCap);
        }

        var root = new StateStore(_keyBytes[0], valWidth);
        Span<ulong> one = stackalloc ulong[valWidth];
        one[0] = 1UL;
        root.Accumulate(ReadOnlySpan<byte>.Empty, one);
        ILayer current = new RamLayer(root);
        int curIdx = 0;

        // ring[j] accumulates layer curIdx+1+j.
        var ring = new ISink?[W + 1];
        for (int j = 0; j <= W; j++) ring[j] = NewSink(1 + j, 1);

        try
        {
            while (curIdx < _edgeCount)
            {
                int[] resetSet = BuildResetSet(curIdx, W);
                var src = current;
                bool completingSpilled = ring[0] is DiskSink;

                Parallel.ForEach(Partitioner.Create(0, src.Partitions), opts,
                    () => new Worker(_vertexCount, maxKeyBytes),
                    (range, _, worker) =>
                    {
                        for (int p = range.Item1; p < range.Item2; p++)
                        {
                            src.Read(p, (key, val) => ExpandLookaheadRecord(worker, key, val, curIdx, ring, W, resetSet));
                            src.ReleasePartition(p);
                        }
                        return worker;
                    },
                    _ => { });

                ILayer completed = ring[0]!.Finish();   // layer curIdx+1 is now final
                current.Dispose();
                current = completed;
                curIdx++;

                long states = completed.Count;
                TotalStatesVisited += states;
                if (states > PeakStates) PeakStates = states;
                onLayer?.Invoke(curIdx, states, completingSpilled);

                // rotate the ring forward one layer (proxy size = the layer just done)
                for (int j = 0; j < W; j++) ring[j] = ring[j + 1];
                ring[W] = NewSink(curIdx + 1 + W, states);
            }

            var ansLimbs = new ulong[valWidth];
            bool got = false;
            for (int p = 0; p < current.Partitions; p++)
                current.Read(p, (_, v) => { v.CopyTo(ansLimbs); got = true; });
            return got ? StateStore.ToBigInteger(ansLimbs) : BigInteger.Zero;
        }
        finally
        {
            current.Dispose();
            if (oocDir is not null)
                try { if (Directory.Exists(oocDir)) Directory.Delete(oocDir, recursive: true); } catch { }
            if (onProcExit is not null) AppDomain.CurrentDomain.ProcessExit -= onProcExit;
            if (onCancel is not null) Console.CancelKeyPress -= onCancel;
        }
    }

    private int[] BuildResetSet(int curIdx, int W)
    {
        var set = new HashSet<int>(_layerVerts[curIdx]);
        for (int e = curIdx + 1; e <= Math.Min(curIdx + 1 + W, _edgeCount); e++)
        {
            set.Add(_edgeU[e]);
            set.Add(_edgeV[e]);
        }
        var arr = new int[set.Count];
        set.CopyTo(arr);
        return arr;
    }

    private void ExpandLookaheadRecord(Worker worker, ReadOnlySpan<byte> key, ReadOnlySpan<ulong> val,
        int curIdx, ISink?[] ring, int W, int[] resetSet)
    {
        int[] mate = worker.Mate;
        byte[] keyBuf = worker.KeyBuf;
        int e0 = curIdx + 1;
        int u0 = _edgeU[e0], w0 = _edgeV[e0];
        int[] parentVerts = _layerVerts[curIdx];
        int pw = parentVerts.Length;
        bool u0Leave = _lastEdge[u0] == e0, w0Leave = _lastEdge[w0] == e0;
        int pbits = _bits[curIdx];

        for (int i = 0; i < pw; i++)
            mate[parentVerts[i]] = Decode(GetCode(key, i, pbits), parentVerts[i], parentVerts);
        if (_firstEdge[u0] == e0) mate[u0] = u0;
        if (_firstEdge[w0] == e0) mate[w0] = w0;

        // c = 0 : exclude edge e0
        if (LeaveValid(mate, u0, w0, u0Leave, w0Leave))
            FastForwardEmit(mate, e0, val, ring, W, keyBuf);

        // c = 1 : include edge e0
        if (TryInclude(mate, u0, w0, out _, out _) && LeaveValid(mate, u0, w0, u0Leave, w0Leave))
            FastForwardEmit(mate, e0, val, ring, W, keyBuf);

        foreach (int v in resetSet) mate[v] = v;
    }

    /// <summary>
    /// Starting from a state already at layer <paramref name="startLayer"/>, skip
    /// forward over edges whose "include" branch is forced-reject (taking the only
    /// viable exclude branch), up to W edges, then emit into the matching ring slot.
    /// </summary>
    private void FastForwardEmit(int[] mate, int startLayer, ReadOnlySpan<ulong> val,
        ISink?[] ring, int W, byte[] keyBuf)
    {
        int t = startLayer, j = 0;
        while (j < W && t < _edgeCount)
        {
            int e = t + 1;
            int ue = _edgeU[e], we = _edgeV[e];
            if (_firstEdge[ue] == e) mate[ue] = ue;   // vertices entering at edge e
            if (_firstEdge[we] == e) mate[we] = we;

            if (IncludeForcedReject(mate, e, ue, we))
            {
                // exclude is the only option; drop the state if that is invalid too
                if (!LeaveValid(mate, ue, we, _lastEdge[ue] == e, _lastEdge[we] == e)) return;
                t++; j++;
            }
            else break; // include is viable here: materialise at layer t
        }

        EncodeAt(mate, t, keyBuf);
        ring[j]!.Add(keyBuf.AsSpan(0, _keyBytes[t]), val);
    }

    /// <summary>Would including edge e drive the state to the 0-terminal? (No net mutation.)</summary>
    private bool IncludeForcedReject(int[] mate, int e, int u, int w)
    {
        if (mate[u] == Interior || mate[w] == Interior) return true;
        int a = EndRef(mate, u), b = EndRef(mate, w);
        if (a == w && b == u) return true; // would close a cycle

        // tentatively include, test the leave condition, then restore
        int su = mate[u], sw = mate[w];
        int sa = a >= 1 ? mate[a] : 0, sb = b >= 1 ? mate[b] : 0;
        if (a >= 1) mate[a] = b >= 1 ? b : Anchor;
        if (b >= 1) mate[b] = a >= 1 ? a : Anchor;
        if (a != u) mate[u] = Interior;
        if (b != w) mate[w] = Interior;
        bool ok = LeaveValid(mate, u, w, _lastEdge[u] == e, _lastEdge[w] == e);
        if (a >= 1) mate[a] = sa;
        if (b >= 1) mate[b] = sb;
        mate[u] = su; mate[w] = sw;
        return !ok;
    }

    // Encode the frontier state at an arbitrary layer (used by lookahead skips).
    private void EncodeAt(int[] mate, int layer, byte[] dest)
        => EncodeInto(mate, _layerVerts[layer], _posMap[layer], _bits[layer], dest.AsSpan(0, _keyBytes[layer]));

    // ---- bit-packed key codec (bits per vertex-code, LSB-first within the key) ----

    private static void PutCode(Span<byte> dest, int i, int bits, int code)
    {
        int bitPos = i * bits, bytePos = bitPos >> 3, off = bitPos & 7;
        dest[bytePos] |= (byte)(code << off);
        if (off + bits > 8) dest[bytePos + 1] |= (byte)(code >> (8 - off));
    }

    private static int GetCode(ReadOnlySpan<byte> key, int i, int bits)
    {
        int bitPos = i * bits, bytePos = bitPos >> 3, off = bitPos & 7;
        int v = key[bytePos] >> off;
        if (off + bits > 8) v |= key[bytePos + 1] << (8 - off);
        return v & ((1 << bits) - 1);
    }

    // Map an encoded code back to a mate value for the given owning vertex.
    private static int Decode(int code, int vertex, int[] parentVerts) => code switch
    {
        0 => vertex,                       // SELF (isolated / untouched)
        1 => Interior,
        2 => Anchor,
        _ => parentVerts[code - 3],        // partner is a frontier vertex
    };

    private bool CanConnect(int[] mate, int x) => mate[x] != Interior;

    private int EndRef(int[] mate, int x)
    {
        int m = mate[x];
        if (m == x) return _isTerminal[x] ? Anchor : x; // isolated: terminal anchors, else itself
        if (m == Anchor) return Anchor;
        return m; // partner vertex id
    }

    /// <summary>
    /// Apply "edge included" to mate[] in place. Returns false (rejected)
    /// without mutating on a cycle / degree violation. On success, `a` and `b`
    /// receive the two outer-end references touched (vertex id, or Anchor(-1)),
    /// used by the caller to restore state.
    /// </summary>
    private bool TryInclude(int[] mate, int u, int w, out int a, out int b)
    {
        a = 0; b = 0;
        if (!CanConnect(mate, u) || !CanConnect(mate, w)) return false;

        a = EndRef(mate, u);
        b = EndRef(mate, w);

        // Closing a cycle among ordinary fragment ends is never a simple path.
        if (a == w && b == u) return false;

        // Wire the two outer ends together (Anchor ends have no cell to write;
        // they are recorded on the opposite real vertex).
        if (a >= 1) mate[a] = (b >= 1) ? b : Anchor;
        if (b >= 1) mate[b] = (a >= 1) ? a : Anchor;

        // u/w become interior unless they are themselves the outer end.
        if (a != u) mate[u] = Interior;
        if (b != w) mate[w] = Interior;
        return true;
    }

    /// <summary>
    /// Validity of vertices leaving the frontier after this edge. A leaving
    /// vertex must be interior (degree 2) or an untouched ordinary vertex
    /// (degree 0). A dangling open end, an unused terminal, or a stranded
    /// anchor is invalid.
    /// </summary>
    private bool LeaveValid(int[] mate, int u, int w, bool uLeave, bool wLeave)
    {
        if (uLeave && !LeaveOk(mate, u)) return false;
        if (wLeave && !LeaveOk(mate, w)) return false;
        return true;
    }

    private bool LeaveOk(int[] mate, int x)
    {
        int m = mate[x];
        if (m == Interior) return true;
        if (m == x && !_isTerminal[x]) return true; // isolated ordinary vertex
        return false;
    }

    // Encode the frontier state into a caller-provided buffer (no allocation),
    // packing each vertex-code into `bits` bits. `dest` is the packed key length.
    private void EncodeInto(int[] mate, int[] childVerts, int[] posChild, int bits, Span<byte> dest)
    {
        dest.Clear(); // codes are OR-ed in, so start from zero
        for (int i = 0; i < childVerts.Length; i++)
        {
            int v = childVerts[i];
            int m = mate[v];
            int code;
            if (m == v) code = 0;
            else if (m == Interior) code = 1;
            else if (m == Anchor) code = 2;
            else code = 3 + posChild[m];
            PutCode(dest, i, bits, code);
        }
    }
}
