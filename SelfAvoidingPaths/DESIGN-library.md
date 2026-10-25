# Design sketch: turning the counter into a reusable frontier-search engine

Goal: separate the **generic frontier engine** from the **problem-specific state
logic** and the **accumulation algebra**, without giving up the performance that
lets it reach high `n`.

Three collaborators, three responsibilities:

| Piece            | Owns                                                        | Varies per…      |
|------------------|------------------------------------------------------------|------------------|
| `FrontierEngine` | layered edge sweep, frontier enter/leave, sharded parallel dedup, memory monitoring | nothing (fixed)  |
| `IFrontierSpec`  | what a state *means*; transitions; reject/accept            | problem family   |
| `ISemiring`      | how partial results combine (count / probability / min-cost)| analysis type    |

The key performance decision: **both plug-ins are `struct` with generic
constraints**, so the JIT monomorphizes `FrontierEngine<TSpec, TSemiring, …>` and
inlines the hot calls — no virtual dispatch, no boxing per state. This is how
TdZdd stays fast in C++; the C# equivalent is struct generics + `Span`.

---

## 1. What stays in the engine (already generic today)

Everything in [FrontierCounter.cs](FrontierCounter.cs) *except* five methods is
already problem-agnostic:

- graph build + edge ordering (ctor)
- `_firstEdge/_lastEdge/_layerVerts` — frontier enter/leave (ctor)
- the `for k = 1..edgeCount` sweep, `NewShards`, the `Parallel.ForEach`
  over shards, `Accumulate` (sharded lock + `CollectionsMarshal`)
- `PeakStates`, memory monitoring, the `onLayer` hook

The engine loop becomes (schematically):

```csharp
public TVal Count<TSpec, TSemiring, TVal>(TSpec spec, TSemiring ring, …)
    where TSpec : struct, IFrontierSpec
    where TSemiring : struct, ISemiring<TVal>
{
    // seed root
    var current = NewShards();
    spec.WriteRoot(rootKeyBuffer, Layer(0));
    current[Shard(rootKey)][rootKey] = ring.One;

    for (int k = 1; k <= _edgeCount; k++)
    {
        var step = BuildEdgeStep(k);              // engine: verts, u/w, intro/leave
        var next = NewShards();

        Parallel.ForEach(Partitioner.Create(0, Shards), opts, range =>
        {
            var worker = spec.CreateWorker();     // per-thread scratch (the int[] mate)
            var sink   = new ShardSink(next, locks, ring); // where children land
            for (int s = range.Item1; s < range.Item2; s++)
                foreach (var (key, val) in current[s])
                    spec.Expand(worker, key, in step, val, ref sink);
        });

        current = next; RecordPeak(current); onLayer?.Invoke(k, Count(current));
    }
    return Read(current, EmptyKey) ?? ring.Zero;
}
```

The only thing that changed vs. today: the body of the inner `foreach` is now a
call to `spec.Expand(...)`, and the value type is `TVal` instead of hardcoded
`BigInteger`.

---

## 2. The spec interface

```csharp
/// One transition step's fixed data (engine computes once per edge).
public readonly ref struct EdgeStep
{
    public readonly int[] ParentVerts;   // frontier vertices of the parent layer
    public readonly int[] ChildVerts;    // frontier vertices of the child layer
    public readonly int[] PosChild;      // vertex -> index in ChildVerts
    public readonly int U, V;            // edge endpoints (min, max)
    public readonly bool UIntro, VIntro; // endpoint enters the frontier here
    public readonly bool ULeave, VLeave; // endpoint leaves the frontier here
}

/// Receives the children an Expand produces. A struct so it inlines; the engine
/// supplies one that applies the semiring weight and accumulates into a shard.
public interface IChildSink
{
    // took == true  -> edge included on this branch
    void Emit(bool took, ReadOnlySpan<byte> childKey);
}

public interface IFrontierSpec
{
    /// Bytes per state key for a given layer (path: ChildVerts.Length, 1B/vertex).
    int KeyLength(int frontierSize);

    /// Encode the initial (empty-frontier) state.
    void WriteRoot(Span<byte> dest, int layer0Size);

    /// Per-worker mutable scratch (the decoded working representation).
    TWorker CreateWorker<TWorker>() where TWorker : new();  // or a concrete assoc. type

    /// Expand one parent state into 0, 1, or 2 children via `sink`.
    void Expand<TSink>(object worker, ReadOnlySpan<byte> parentKey,
                       in EdgeStep step, ref TSink sink) where TSink : IChildSink;
}
```

(The `TWorker` handling above is simplified; in the real version the spec would
declare an associated worker type, e.g. `IFrontierSpec<TWorker>`, so there's no
`object`. Kept loose here to show intent.)

---

## 3. The semiring interface

```csharp
public interface ISemiring<TVal>
{
    TVal Zero { get; }              // additive identity (rejected / empty)
    TVal One  { get; }              // multiplicative identity (root)
    TVal Add(TVal a, TVal b);       // merge two branches into the same state
    TVal Times(TVal a, TVal edgeWeight); // extend along a decision
}
```

The same `SimplePathSpec` then yields different analyses purely by swapping the
ring — **no change to the combinatorial logic**:

```csharp
struct CountRing     : ISemiring<BigInteger>  // (+, ×), weights = 1  -> A007764
struct ReliabilityRing : ISemiring<double>    // take: ×p, skip: ×(1-p) -> network reliability
struct ShortestRing  : ISemiring<long>        // (min, +), weight = edge length -> shortest simple path
struct ExistsRing    : ISemiring<bool>        // (∨, ∧) -> feasibility
```

Where do edge weights come from? The engine asks the ring for the weight of
each branch: counting → `One` for both; reliability → `p` / `1−p`; shortest →
`edgeLen` / `0`. So `Times` is applied by the engine at `sink.Emit`, keeping the
spec value-agnostic.

---

## 4. The path spec is almost a copy-paste of today's five methods

Everything path-specific in `FrontierCounter.cs` moves into `SimplePathSpec`
**unchanged in logic**:

| Today (private in FrontierCounter)     | Becomes (in SimplePathSpec)         |
|----------------------------------------|-------------------------------------|
| consts `Interior`, `Anchor`            | private consts of the spec          |
| `Decode(byte, vertex, parentVerts)`    | decode into `worker.mate`           |
| `EndRef`, `CanConnect`, `TryInclude`   | private helpers (verbatim)          |
| `LeaveValid`, `LeaveOk`                | private helpers (verbatim)          |
| `Encode(mate, childVerts, posChild)`   | write child key into a `Span<byte>` |

```csharp
public struct SimplePathSpec : IFrontierSpec<PathWorker>
{
    const int Interior = 0, Anchor = -1;
    readonly bool[] _isTerminal;   // set from the graph (corner s, corner t)

    public int KeyLength(int frontierSize) => frontierSize;         // 1 byte / vertex
    public void WriteRoot(Span<byte> dest, int _) { /* empty frontier: dest is length 0 */ }

    public PathWorker CreateWorker(int vertexCount)
    {
        var m = new int[vertexCount + 1];
        for (int v = 0; v <= vertexCount; v++) m[v] = v;            // all isolated
        return new PathWorker { Mate = m };
    }

    public void Expand<TSink>(PathWorker w, ReadOnlySpan<byte> key,
                              in EdgeStep step, ref TSink sink) where TSink : IChildSink
    {
        var mate = w.Mate;
        // ---- decode parent (was: the decode loop in Count) ----
        for (int i = 0; i < step.ParentVerts.Length; i++)
        {
            int v = step.ParentVerts[i];
            mate[v] = Decode(key[i], v, step.ParentVerts);
        }
        if (step.UIntro) mate[step.U] = step.U;
        if (step.VIntro) mate[step.V] = step.V;

        // ---- child c = 0 (edge excluded) ----  [verbatim from Count]
        if (LeaveValid(mate, step))
            sink.Emit(false, Encode(mate, step, w.KeyScratch));

        // ---- child c = 1 (edge included) ----  [verbatim from Count]
        if (TryInclude(mate, step.U, step.V, out _, out _) && LeaveValid(mate, step))
            sink.Emit(true, Encode(mate, step, w.KeyScratch));

        // ---- reset scratch ----                [verbatim from Count]
        for (int i = 0; i < step.ParentVerts.Length; i++) mate[step.ParentVerts[i]] = step.ParentVerts[i];
        mate[step.U] = step.U; mate[step.V] = step.V;
    }

    // EndRef / CanConnect / TryInclude / LeaveValid / LeaveOk / Encode / Decode
    // are pasted from FrontierCounter with zero logic changes.
}

public sealed class PathWorker { public int[] Mate; public byte[] KeyScratch; }
```

That's the whole path-specific surface. Note the **anchor/terminal trick lives
entirely inside the spec** — the engine never hears the word "terminal."

---

## 5. Second spec to prove the abstraction earns its keep

A *cycle* spec (count/enumerate simple cycles) differs from the path spec only
in the terminal handling: there are no terminals, and a state is accepted when
the last fragment closes into exactly one cycle. `SpanningTreeSpec` tracks
component ids instead of endpoints. Neither needs to touch the engine.

If the first two specs drop in without engine edits, the seam is right.

---

## 6. Honest cost / risk ledger

- **Perf:** must be struct-generic + `Span`/ref-struct sink, or you pay per-state
  boxing and lose ~1–2 in reachable `n`. This is the make-or-break decision.
- **State size:** path needs 1 byte/vertex; specs that track component ids or
  degrees need more. `KeyLength` + a `Span<byte>` contract covers it, but the
  encoding budget is per-spec design work.
- **Variable ordering:** the grid gets a free narrow frontier. For general
  graphs, frontier width ≈ pathwidth, so ordering quality *is* the performance
  story — a whole sub-problem the engine should expose as a pluggable strategy.
- **Correctness surface grows:** each spec needs a conformance test (small graphs
  vs. brute force). Non-negotiable once logic is pluggable.

## 7. Minimum viable library

1. `Graph` + edge-ordering utilities (ordering strategy pluggable).
2. `FrontierEngine<TSpec, TSemiring, TVal>` — sweep, sharding, parallel, monitoring.
3. `SimplePathSpec` + one more (`CycleSpec` or `SpanningTreeSpec`).
4. `CountRing` + `ReliabilityRing`.
5. A spec-conformance test harness (brute force on graphs up to ~6 vertices).

Effort: a focused refactor, not a project — the current code already sits on the
seam. The risk is entirely in holding the performance line through the generics.
