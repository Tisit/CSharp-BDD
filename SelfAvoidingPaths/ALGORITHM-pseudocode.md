# Pseudocode — how `sapaths` computes A007764(n)

A007764(n) is the number of self-avoiding rook paths between opposite corners
of an n × n grid graph. The tool has two engines. Both use the same idea, a
**frontier-based transfer-matrix sweep**: walk across the grid in a fixed
order and keep, for each *frontier state* (how the partial path fragments
cross the boundary between the processed and unprocessed parts), the number
of partial solutions that reach it.

| Engine | Source | Flags | Role |
|---|---|---|---|
| **Dense + CRT** (GGCount port) | `DenseCounter.cs` | `--dense`, `--mod`, `--crt`, `--bits` | primary; used for n = 20–24 |
| Frontier / SIMPATH (hash-table) | `FrontierCounter.cs`, `StateStore.cs` | default (no flag) | original engine; reference and out-of-core |

Sections 1–4 cover the dense engine. Section 5 covers the hash engine.

---

## 1. Top level

```
function A007764(n):
    if n == 1: return 1
    if mode == exact-limbs:  return DenseSweep(n, arithmetic = big fixed-width integers)
    if mode == mod m:        return DenseSweep(n, arithmetic = integers mod m)   // one residue
    if mode == crt:          return CountWithCrt(n, storeBits)                   // section 4
```

---

## 2. State representation: the dense perfect hash

### 2.1 Mate strings

The frontier has `w = n` positions. Position 0 is the rightmost position and
position n-1 is the leftmost. Each position holds one symbol, stored in 2 bits
of a `ulong`:

```
'.'  (N)  no path edge crosses the frontier here
'('  (L)  an arc opens here; its partner ')' is further right
')'  (R)  an arc closes here; its partner '(' is further left
```

Path fragments are noncrossing, so a valid state is a **balanced parenthesis
string with dots** (a Motzkin path) with one exception: it may contain exactly
**one unmatched ')'**, the "free end" of the fragment that starts at the corner s.
(In the codec this is a left-boundary height of 1 and a right-boundary height of 0.)

```
root state   : ')' at the leftmost position, all else '.'    (the path enters at s)
accept state : ')' at position 0, all else '.'               (the free end reached t)
```

### 2.2 Ranking: a minimal perfect hash

All valid strings are numbered 0 … StateCount-1 with no gaps, so counts can
live in a plain array indexed by rank. There is no hash table and no stored key.

```
Motzkin(w, h1, h2) = number of strings of width w that go from height h1 to height h2
                     without dipping below 0      ('(' = +1, ')' = -1, '.' = 0)
                   (memoised recursion: M(w-1,h1+1,h2) + M(w-1,h1,h2) + M(w-1,h1-1,h2))

Split each string into a LEFT half (width wL = n - ceil(n/2)) and a RIGHT half (width wR = ceil(n/2)).

Precompute:
    for each split height h:
        RightBlock[h] = list of all right halves that go from h down to 0
        codeR[rightId] = index of rightId within RightBlock[h]
    base = 0
    for each valid left half L (starts at height 1, ends at some height h):
        base = round base up to an even number      // keeps 12-bit packed blocks byte-disjoint
        codeLR[L] = base
        CodeTable += { Base = base, Size = |RightBlock[h]|, MateL = L, MateR = RightBlock[h] }
        base += |RightBlock[h]|
    StateCount = base

Encode(mate) = codeLR[leftHalf(mate)] + codeR[rightHalf(mate)]       // two lookups and one add
```

A second, smaller codec `wc` of width n-1 ranks the **deferred** states
(`DeferredCount` slots). Its right half is one position narrower than the main
codec's. This guarantees that parallel groups (§3.4) always write to different
deferred blocks.

---

## 3. The dense sweep (one pass over the grid)

### 3.1 Arrays

```
value[0 .. StateCount-1]        count for every main-codec state      (the whole "layer")
deferred[0 .. DeferredCount-1]  side array of width-(n-1) states, initially all 0
```

The update is **in place**: there is never a second copy of the layer, so peak
memory is about one layer plus the small deferred array.

A counter is one of:
* a fixed-width multi-limb integer (exact mode),
* `uint64 mod m`, `uint32 mod m`, or `uint16 mod m` (m = 0 means free wraparound mod 2^bits),
* a 12-bit packed counter mod m ≤ 4096 (1.5 bytes per state; the 12-bit configuration is what fits n = 24 in 16 GB of RAM).

All counter arithmetic is `+` (mod m in the modular modes).

### 3.2 Driver

```
function DenseSweep(n, arith):
    build codecs mc (width n) and wc (width n-1)
    value = 0; deferred = 0

    // Closed-form first and last rows (shortcut, enabled by default):
    // after row 0, exactly the 2^(n-1) "edge-row states" are reachable, each with count 1:
    //   reading from the left: some '.'s, then the free end ')', then any sequence of
    //   '.' and '(' '.'* ')' (non-nested arcs with only dots inside).
    // Before the last row, exactly those same states can be completed, each in exactly one way.
    E = EdgeRowStates(n)
    for s in E: value[Encode(s)] = 1
    firstRow = 1; lastRow = n - 1
    // (With SAP_FULL_SWEEP=1: start from the root state at row 0, sweep all n rows,
    //  and read the answer from the accept state.)

    for row = firstRow to lastRow - 1:
        for p = n-1 down to 1:          // the pair of frontier positions (p, p-1)
            Update(p)                   // one grid-vertex step (§3.3), applied to all states
        maybe checkpoint value + deferred (at most once every 10 min)

    return Σ_{s in E} value[Encode(s)]  // with the full sweep: value[Encode(accept)]
```

### 3.3 One vertex update: `Update(p)`

Look at the pair of symbols at positions `(p, p-1)`, written `[x y]`. Transitions
that produce a `[..]` pair (the vertex closes something off) are routed through
`deferred`. Transitions that keep a single strand passing through are done as an
in-place swap with the deferred slot as scratch.

```
function Update(p):
    for each state c (in code-block order; parallel over groups, §3.4):
        mate = decode(c)                      // = block.MateL | block.MateR[i]
        switch pair(mate, p):

        case [..]:                            // vertex unused, or a new arc "()" opens here
            d = wc.Encode(remove position p from mate)
            if value[c] != 0:
                value[Encode(mate with pair := "()")] += value[c]
            value[c] += deferred[d]           // absorb contributions emitted into this '..' state
            deferred[d] = 0

        case [.(] or [.)]:                    // one strand passes through: it may leave
                                              // on either side, so [.x] and [x.] mix
            d  = wc.Encode(remove position p from mate)
            cc = Encode(mate with pair swapped: [.(]→[(.] or [.)]→[).])
            if p == 1:
                deferred[d] += value[cc]
                value[cc]   += value[c]
                value[c]    += deferred[d]
                deferred[d]  = 0
            else:
                tmp          = value[c]
                value[c]    += value[cc]
                value[c]    += deferred[d]
                deferred[d]  = tmp

        case [((]:                            // two arcs meet. The partner of the inner '(' ...
            m2 = mate with pair := [..]
            q  = position of the ')' that matches the '(' at p-1     (scan right, depth counter)
            m2[q] = '('                       // ... becomes the new opener of the merged arc
            Emit(p, m2, c)

        case [))]:                            // mirror image of [((]
            m2 = mate with pair := [..]
            q  = position of the '(' that matches the ')' at p       (scan left, depth counter)
            m2[q] = ')'
            Emit(p, m2, c)

        case [)(]:                            // join two different fragments
            m2 = mate with pair := [..]
            Emit(p, m2, c)

        case [()]:                            // would close a cycle: not a simple path
            (no transition; the state dies)

function Emit(p, m2, c):
    if p == 1: value[Encode(m2)] += value[c]                       // row edge: write directly
    else:      deferred[wc.Encode(remove position p-1 from m2)] += value[c]
```

If the free end ')' is one of the two symbols, the same rules apply. The path
from s just keeps one unmatched closer, and it reaches t when that closer sits
at position 0 at the end of the sweep.

### 3.4 Lock-free parallelism

```
groupWidth = wL - 2
for each bitmask g in 0 .. 2^groupWidth - 1 (densest first, one group per grab from a shared counter):
    enumerate the left halves whose fixed positions follow g      // bit = 1 → paren, bit = 0 → '.'
    (the two "ungrouped" positions next to p are free to be any symbol)
    for each such left half: run Update(p) on its code block
```

One update never turns a paren into '.' (or the reverse) except at the two
ungrouped positions. So every state written belongs to the same group as the
state that wrote it, and different workers never touch the same counter. No
locks are needed. Block bases are even-aligned, so 12-bit counters that share a
byte always belong to the same worker.

### 3.5 Performance specialisations (same semantics as §3.3)

* **p < wR** (pair inside the right half): every transition depends only on
  `(p, split height, right-half index)`. The engine precomputes a table entry
  `{kind, value-offset, deferred-offset}` per right mate and streams it. This
  removes the parenthesis-matching scans and half of the hash lookups.
* **p > wR** (pair inside the left half): the case is the same for every state
  in a block. Each block runs a single-case loop, and blocks with no transition
  are skipped.
* **p == wR** (pair straddles the split): handled by dedicated "straddle" tables,
  with an AVX2 kernel for 12-bit storage.
* States with a zero count skip their scattered writes.

---

## 4. Exact value from modular runs: CRT campaign (`--crt`)

Each sweep stores counts modulo m in `storeBits` bits per state. Several sweeps
with pairwise-coprime moduli are then combined with the Chinese Remainder
Theorem.

```
function CountWithCrt(n, storeBits):
    need     = 2^ceil(0.223 · n² · log2(10))        // estimated size of the answer
    moduli   = []; residues = []; product = 1
    resume (moduli, residues) from sapcrt_<n>_b<bits>.state if present

    NextModulus():
        first modulus: 2^storeBits (free wraparound)
        then: odd candidates descending from 2^storeBits - 1, skipping any not coprime to all chosen moduli

    // 1. collect residues until their product covers the estimate
    while product < need:
        m = NextModulus()
        r = DenseSweep(n, arith = mod m)
        append (m, r); product *= m; append the pair to the state file

    result = CRT(moduli, residues)

    // 2. verify with held-out moduli (≥ ~24 bits' worth of checks)
    checksNeeded = ceil(24 / storeBits)
    passed = 0
    while passed < checksNeeded:
        m = NextModulus(); r = DenseSweep(n, mod m)
        if result mod m == r: passed += 1; continue

        if product ≤ healLimit:                     // the estimate was too small
            fold (m, r) into the CRT set; result = CRT(...); passed = 0; continue

        // past the size bound, so a stored residue may be corrupt (e.g. a RAM bit-flip)
        r2 = DenseSweep(n, mod m)
        if r2 != r: continue                        // the check sweep itself glitched; retry
        samples = previous passed checks + (m, r) + fresh samples until there are checksNeeded
        bad = the unique k such that CRT(all residues except k) agrees with every sample
        if there is no unique k: abort ("not repairable")
        residues[bad] = DenseSweep(n, mod moduli[bad]); result = CRT(...); passed = 0

    assert result is even                           // A007764(n) is even for n ≥ 2
    delete the state file and checkpoints
    return result

function CRT(moduli, residues):                    // incremental Garner-style combination
    x = r0; M = m0
    for i = 1 .. k-1:
        t = ((ri - x) mod mi) · inverse(M mod mi, mi) mod mi
        x += M · t; M *= mi
    return x
```

Variants:

* **`--crt-known`**: checks every residue against the published value as it
  arrives, re-running any mismatch once. It stops as soon as `product > known`
  and does no held-out runs. This re-derives a published value; it is not an
  independent computation.
* **`--crt-part K/N` and `--crt-merge`**: every machine derives the same
  deterministic modulus list (sized up front, plus held-out check moduli).
  Part K runs the entries i with `i mod N == K-1`. The merge step runs CRT and
  checks the result against the held-out residues.

* **`--ladder [dir]`** (residue ladder, `DenseCounter.Ladder.cs`): later runs
  start mid-grid instead of at row 1. See §4.1.

### 4.1 Residue ladder

The counts at row boundary k are much smaller than the answer: the largest has
about 0.77·n·(k−1) bits (measured with `SAP_ROWBITS=1` at n = 12–18), about 47% of
a(n)'s bits at mid-grid. So the first few runs already determine the exact vector
at boundary k, and every later run can start there.

```
req(k) = #moduli whose product exceeds 2^(0.78·n·(k-1)+2), plus 1 check

plan (once, in run 1 after its 2nd row; persisted for resume):
    tRow = measured row time; R, W = measured disk read/write speed (or --ladder-disk)
    choose ≤ 3 rung rows minimizing a time model of the whole campaign under the disk budget:
        rows swept + dumps (S/W each) + rung builds (max(Garner CPU, I/O))
        + loads (prefetched: S/R + stall if preparing took longer than the previous sweep)
    no rung beats plain sweeps by > 1% → plain sweeps

for each CRT run (modulus m, position i in the deterministic modulus list):
    start = highest ready rung (or row 1)
    if start > 1:
        value = prepared file for (start, i) if the background thread made one,
                else Horner(digits of rung start) mod m                // deferred is empty at a row boundary
        background: prepare the start vector for run i+1 (one file of residues) while this run sweeps
    sweep rows start .. n-2; at each rung k > start with i < req(k): dump value (residues mod m)
    after the run, for each rung whose req(k) residues are all on disk:
        per state: Garner → mixed-radix digits (in place), check against the extra residue
        any mismatch → discard the rung (undersized estimate or corrupt dump)
        else → rung ready; delete lower rungs' files

held-out verification, isolation and repair runs always sweep from row 1
```

Measured at n=20 (`--crt-known --bits 12`): 175 s vs 222 s for plain sweeps (0.79×).

---

## 5. The original engine: SIMPATH frontier search with hash tables

This is Knuth's SIMPATH in the memory-lean "direct" form of Iwashita–Kawahara–Minato (2012).

```
function FrontierCount(n):
    vertices 1..n² in row-major order; s = 1, t = n²
    edges sorted lexicographically by (min endpoint, max endpoint)    // frontier width ≈ n+1
    frontier[k] = { v : firstEdge(v) ≤ k < lastEdge(v) }

    mate[v] meanings:  v (isolated / untouched) | INTERIOR (degree 2)
                       | ANCHOR (open end whose fragment reaches a terminal) | u (other open end)

    layer = { emptyKey → 1 }
    for k = 1 .. |E|:  (u, w) = edge k
        next = sharded hash map key → count        // 256 shards, one lock per shard; spills to disk with --ooc
        parallel for each (key, cnt) in layer:
            mate = decode(key); initialise u and w as isolated if this is their first edge
            // child 0: edge excluded
            if LeaveOk(u, w): next[encode(mate)] += cnt
            // child 1: edge included
            if mate[u] != INTERIOR and mate[w] != INTERIOR:
                a = EndRef(u); b = EndRef(w)        // isolated terminal → ANCHOR; isolated vertex → itself
                if not (a == w and b == u):         // the edge would close a cycle
                    link a <-> b                    // an ANCHOR end is recorded on the other side
                    mark u and w INTERIOR unless they are themselves the outer ends
                    if LeaveOk(u, w): next[encode(mate)] += cnt
        layer = next                                // the old layer is freed shard by shard

    return the count of the single remaining (empty-key) state

LeaveOk(x) for a vertex leaving the frontier after this edge:
    mate[x] == INTERIOR  or  (mate[x] == x and x is not a terminal)
```

A single ANCHOR marker is enough because at most one s-anchored and one
t-anchored open end can exist at the same time. The path is complete when two
anchored ends join.

Options for this engine: bit-packed keys, multi-limb counts added in place,
`--lookahead W` (skips pass-through states using the zero-suppression rule),
`--ooc` (hash-partitioned spill files with a dedup pass per partition), and
`--checkpoint`.

---

### References

* Iwashita, Nakazawa, Kawahara, Uno, Minato, *Efficient Computation of the
  Number of Paths in a Grid Graph with Minimal Perfect Hash Functions*,
  TCS-TR-A-13-64 (2013). GGCount is the basis of the dense engine.
* Iwashita, Kawahara, Minato, TCS-TR-A-12-60 (2012), the "direct" frontier method.
* Knuth, TAOCP 4A §7.1.4, exercise 225 (SIMPATH).
