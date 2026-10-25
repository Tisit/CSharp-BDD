# Self-Avoiding Corner-to-Corner Rook Paths (OEIS A007764)

Counts the number of **non-intersecting (self-avoiding) rook paths joining
opposite corners of an *n × n* grid graph** — [OEIS A007764](https://oeis.org/A007764).

The grid is *n × n* **vertices** ((n−1)×(n−1) cells). Terminals are the
top-left and bottom-right corners. A "path" is a simple path (no vertex
visited twice) between them.

```
n=2 -> 2      n=5 -> 8512          n=8  -> 789360053252
n=3 -> 12     n=6 -> 1262816       n=9  -> 3266598486981642
n=4 -> 184    n=7 -> 575780564     n=10 -> 41044208702632496804
```

## Method: frontier-based search (SIMPATH), memory-lean form

This is **not** a plain BDD/apply encoding. Counting simple paths hinges on
forbidding detached cycles, which has no cheap boolean formula for an
apply-based BDD library. Instead we use the decision-diagram technique that was
purpose-built for this problem:

- **Knuth's SIMPATH** (TAOCP 4A, 7.1.4 ex. 225): a *frontier-based search* that
  builds a ZDD of all s–t paths edge by edge, tracking how the "frontier"
  vertices are partially connected via a `mate` table.
- **Iwashita, Kawahara & Minato, TCS-TR-A-12-60 (2012)** — the paper that
  *extended A007764* to n=22. Their §4.2 "direct" method never materializes the
  ZDD: it keeps only the **live frontier layer** (a map `mate-state → path count`)
  and discards each layer as its counts flow to the next. Peak memory is
  `O(#live states)`, not the diagram size.

### Implementation notes (`FrontierCounter.cs`)

- **Edge order**: vertices row-major, edges lexicographic by endpoint → frontier
  width ≈ `n+1` (minimal for a square), which is what keeps the state count down.
- **`mate` state** per frontier vertex: *isolated*, *interior* (degree 2), a
  *partner* (the other open end of its path fragment), or **anchor**.
- **Single anchor marker for terminals**: rather than keeping s and t in the
  frontier the whole run, a "used" terminal collapses to interior and its
  fragment's open end is marked `ANCHOR`. Because only one s-anchored and one
  t-anchored open end can ever coexist, a *single* marker is provably enough; the
  path completes exactly when two anchored ends join.
- **Compact key**: 1 byte per frontier vertex → small hashable state.
- **Parallel layer expansion**: source states are independent, so each layer is
  expanded across all cores. The layer is **hash-sharded** (256 shards, per-shard
  lock) so workers rarely contend and memory stays ~1×.

### Storage engine (`StateStore.cs`) — profiling-driven

The layer store was rewritten after profiling showed the naive
`Dictionary<byte[], BigInteger>` spent most of its budget on transient garbage.
Each shard is now an **open-addressing table over two contiguous arenas**:

- **Keys probed as `Span<byte>`**, copied into the key arena only on a genuine
  insert — eliminates the `byte[]`-per-probe allocation (most probes hit an
  existing state and discarded the key immediately).
- **Bit-packed keys.** Each frontier vertex's mate-code is one of ≤ `w+3` values,
  so it takes `ceil(log2(w+3))` bits (~5 at n≈19) instead of a whole byte. This
  is a **memory-for-time trade**: ~9% less peak RAM (n=15: 0.22 → 0.20 GB) and
  smaller spill records, but ~13% more in-core time from the pack/unpack codec.
- **Counts stored as fixed-width little-endian `ulong` limbs** inline in a value
  arena and **added in place** — no allocation per merge and no per-value heap
  object (unlike `BigInteger`, whose magnitude is a separate `uint[]`). Limb
  width is sized tightly from `n` (a 0.25-digits/vertex bound carries a ~15–25%
  margin over the empirical 0.21 slope), backed by a hard overflow guard so an
  underestimate aborts loudly rather than corrupting. Converted to `BigInteger`
  once, at the end.
- **Early shard free**: each source shard is released the moment it is consumed,
  so peak ≈ one layer rather than two.
- **Capacity seeding**: each new layer's shards start sized from the *previous*
  layer's per-shard average instead of from 8 and doubling up. Consecutive
  frontier layers share the same hash, so the distribution is near-identical;
  near the peak (layer *k+1* ≈ *k*) this removes almost all arena `Grow()`
  reallocations. Measured at n=14: total allocation **10.6 → 6.2 GB (−42%)** and
  gen2 GCs roughly halved. It is *allocation/GC hygiene*, not a speedup —
  wall-time and peak RAM are unchanged, because Server GC collects concurrently
  on spare cores while the bandwidth-bound main loop is the true bottleneck.

Net effect at n=17 vs. the original: peak working set **6.4 GB → 1.02 GB**
(6.3×), time **942 s → 366 s** (2.6×), ~65 bytes/state.

## Build & run

```
dotnet build -c Release
dotnet run   -c Release -- <n>          # count for one grid size
dotnet run   -c Release -- --table 16   # n = 1..16
dotnet run   -c Release -- --selftest   # verify vs known A007764 values
dotnet run   -c Release -- --fast <n>   # faster, but ~2x peak RAM (see below)
dotnet run   -c Release -- --ooc --maxram 6 <n>   # out-of-core: bounded RAM (see below)
dotnet run   -c Release -- --cores 4 <n>          # cap worker threads (default: all cores)
```

### Out-of-core mode (`--ooc`) — break the RAM wall

Since memory triples per step, RAM eventually caps n. `--ooc` removes that cap by
**spilling large layers to disk**, keeping RAM bounded regardless of n
([OutOfCore.cs](OutOfCore.cs)):

- A layer is 256 hash partitions. Small layers stay in RAM (fast path); a layer
  whose source exceeds the `--maxram` budget is built **on disk** instead.
- The disk sink routes each child record to a partition file by `hash(childkey)`,
  so **all duplicates of a state land in the same file**. A per-partition
  **dedup pass** then loads one file at a time into an in-RAM table and sums —
  each partition's *distinct* set fits easily, so RAM never holds a whole layer.
- Spill files live under the system temp dir (or `--ooc <dir>`) and are deleted
  as each layer is consumed. Cleanup runs on normal exit *and* on Ctrl-C; a
  startup sweep removes any spill dir left by a hard-killed run (untouched > 1 h).
- **`--compress-spill`** Deflate-compresses spill files. It only helps when a run
  is genuinely I/O-bound (very large n or a slow disk); at small/medium n on a
  fast disk it is *slower* (Deflate CPU > I/O saved), so it is off by default.
  The `[ram]/[disk]` marker on the progress line shows which layers are spilling.

Trade-off: it is **I/O-bound** — roughly ~5–7× slower than the in-RAM path (disk
throughput, and ~4× layer-size I/O per step). Use it only when a layer would not
fit in RAM. Example (validated): n=15 forced fully to disk (budget 0.1 GB)
produced the exact 48-digit result with peak RAM ~0.27 GB.

### Checkpoint / resume (`--checkpoint`) — survive interruptions

Long runs (hours at high n) can be made crash-safe ([Checkpoint.cs](Checkpoint.cs)):

```
dotnet run -c Release -- --ooc --maxram 6 --checkpoint D:\ck --checkpoint-every 600 19
```

- Every `--checkpoint-every` seconds (default 300), the current completed layer is
  written to the checkpoint dir as 256 partition files plus a manifest **written
  last as the atomic commit marker** (interrupted writes never corrupt the last
  good checkpoint; commit is write-temp-then-rename).
- **Auto-resume**: re-run the *same command* and it loads the latest checkpoint
  matching this `(n, edgeCount)` and continues from the next layer (into RAM if
  small, else aliasing the checkpoint files on disk without copying). Checkpoint
  data is deleted automatically once the run completes.
- Works with any mode (RAM or `--ooc`). Validated: stopping n=12 at layer 130 and
  resuming reproduces the exact OEIS value, in both RAM and out-of-core modes.

### Memory-lean vs. `--fast` (a real trade-off)

Growing each layer's shards incrementally keeps peak memory low but churns
allocations; recycling those arenas through an array pool (`--fast`) removes the
churn but keeps the memory resident, which undoes the early-free peak reduction.
You can't have both, so it's a switch — **default is memory-lean** (reaches the
highest n); `--fast` is for when RAM is ample and you want wall-clock speed.

| n=16          | default (memory-lean) | `--fast` (pooled) |
|---------------|-----------------------|-------------------|
| peak RAM      | **0.51 GB**           | 0.77 GB           |
| time          | 113 s                 | **94 s**          |
| allocated/GCs | 127 GB / 3157         | 18.5 GB / 232     |

### Windowed lookahead (`--lookahead <W>`) — a stronger speed knob

Applies the zero-suppress "node deletion" rule on the fly (Iwashita & Minato,
TCS-TR-A-13-69) with a bounded skip window `W`: when a produced state cannot
include the next edge (that branch is forced to the 0-terminal), the only viable
branch is exclude, so the state is **fast-forwarded** along it — up to `W` edges —
and materialised at the layer where it actually branches. Pass-through states
are never stored, so ~40–50% fewer states are processed → **up to ~2× faster**.

The sweep keeps a **ring of `W+1` layer accumulators** (a state can land up to
`W` layers ahead), so in RAM-only mode peak RAM *rises* — a speed-for-memory
trade. Measured at n=15 (RAM):

| W | 0 (off) | 1 | 2 | 4 |
|---|---------|---|---|---|
| time | 40 s | 33 s | 27 s | **20.5 s** |
| peak RAM | **0.22 GB** | 0.31 | 0.31 | 0.38 |

**Combines with `--ooc`.** Each ring slot is independently RAM or disk (the choice
never affects the counts), so `--lookahead W --ooc --maxram B` keeps RAM bounded
*and* processes ~40–50% fewer states — i.e. ~40–50% less spill/dedup I/O than plain
`--ooc`. Measured at n=15 (budget 0.05 GB, forced spilling):

| n=15 out-of-core         | peak states | time     |
|--------------------------|-------------|----------|
| plain `--ooc`            | 936,771     | 228 s    |
| `--lookahead 4 --ooc`    | 474,706     | **115 s** (−50%) |

This is the intended path for large runs: fewer states → less disk traffic →
~2× faster, with RAM still capped. Correct at every window and with forced
spilling, validated vs. OEIS. Not combined with `--checkpoint` (the `W+1` partial
layers aren't checkpointed).

### Controlling parallelism (`--cores <N>`)

By default a layer is expanded across **all logical cores**. `--cores <N>` caps
the worker count at `N` (clamped to `[1, ProcessorCount]`); with no flag it stays
at the maximum. This is mainly a measurement/throttling knob — useful for leaving
headroom for other work, or for seeing the parallel-scaling curve directly.

That curve flattens well before core count, because the frontier sweep is
**memory-bandwidth-bound**, not compute-bound: each state transition is a random
probe into a multi-GB hash table, and beyond a few threads they all queue on the
same memory controller. Measured at n=14 on a 6-core desktop:

| cores | 1 | 2 | 4 | 6 |
|-------|---|---|---|---|
| time    | 44.6 s | 23.6 s | 16.1 s | **11.4 s** |
| speedup | 1.00× | 1.89× | 2.76× | 3.90× |
| efficiency | 100% | 94% | 69% | 65% |

Near-linear to 2 cores, then diminishing — 6 cores delivers ~3.9× of a possible
6×. This is intrinsic to the workload, so the flag is for control, not for finding
extra speed (the real speed knobs are `--lookahead` and `--fast`).

### Dense engine (`--dense`, `--mod`, `--crt`) — the record-holder's method

A second, independent engine ([DenseCounter.cs](DenseCounter.cs)) ports the method
that extended A007764 to **n = 26/27** (Iwashita et al., TCS-TR-A-13-64, 2013;
reference code [GGCount](https://github.com/kunisura/GGCount)):

- **Minimal perfect hash.** A mate state is a noncrossing parenthesis string;
  all *valid* strings are ranked into consecutive integers by precomputed
  Motzkin-number code tables (`encode = codeLR[left] + codeR[right]` — two
  lookups and an add). Counts live in a **dense array indexed by rank**: no
  keys, no hashing, no dedup — a duplicate child *is* the same array slot.
- **In-place update.** The array is transformed one grid vertex at a time with
  a small width-(w−1) "deferred" side array — peak memory ≈ one layer, not two.
- **Lock-free parallelism.** Codes are grouped by the '.'-vs-paren pattern of
  the left-half positions; one vertex update provably never crosses groups, so
  groups run on all cores with zero locks.
- **Modular counting + CRT.** `--mod <m>` runs the sweep with counts mod m
  (`--mod 2^64` / `2^32` / `max` are free wraparound). `--crt` runs several
  pairwise-coprime moduli and combines them with the Chinese Remainder Theorem
  into the exact count — the lowest-RAM exact mode.
- **Storage width (`--bits 12|32|64`).** Modular counts are stored in 64-bit
  (default), 32-bit, or **12-bit packed** slots. Narrower storage cuts per-run
  RAM proportionally at the cost of proportionally more CRT runs (total work is
  invariant: it always moves states × answer-bits). `--bits 32` (4 B/state) is
  what fits n=23 in 16 GB; **`--bits 12` (1.5 B/state, odd moduli near 4096,
  chunked past the 2.1 B array-element limit) is what fits n=24**. Lock-free
  parallelism survives sub-byte packing because code-block bases are even-aligned
  and the deferred codec's split guarantees cross-group writes land in different
  blocks — so counters sharing a byte always belong to one worker.
  `--dense-info <n>` reports state counts + projected RAM per width without running.
- **Hoisted perfect-hash encodes (≈1.5× faster sweeps).** Within one code
  block the left half of the mate string is fixed, so the `codeLR` half of
  every encode is block-invariant, and `codeR[MateR[i]] == i` by construction
  makes "right half unchanged" encodes free. `UpdateBlock` therefore dispatches
  to specialized forms: for updates in the right half of the frontier the
  block-invariant lookups are hoisted out of the inner loop (2-lookup encodes
  become ≤ 1); for updates in the left half the *case itself* is block-invariant,
  so each block runs a tight single-case loop — and blocks whose left pair
  admits no transition skip outright. Zero-count states also skip their
  random-access writes (adding zero is a no-op). This matters most exactly
  where runs are longest: the rank tables reach ~200 MB at n=23 (far past L3),
  and halving the lookups halves the cache misses they cause. Measured
  (6-core, vs. pre-hoist): n=19 `--mod 2^32` 30.5 → **22 s** (1.4×); n=21
  `--mod 2^32` 244 → **160 s** (1.52×); n=17 exact 8.7 → 7.3 s; n=18 exact
  33 → 26.5 s. The straddling update position keeps the generic path, and all
  modes (limb/64/32/12-bit) revalidate against OEIS.
- **Crash-safe CRT campaigns (two layers).** *Between* sweeps, each completed
  run's `(modulus, residue)` is appended to `sapcrt_<n>_b<bits>.state`; an
  interrupted campaign resumes it automatically (finished runs never lost).
  *Within* a sweep, the engine checkpoints `_value`+`_deferred` at row
  boundaries to `sapckpt_<n>_b<bits>_m<modulus>.bin` (atomic temp-then-rename, so a
  crash mid-write can't corrupt a good checkpoint), and a restart resumes that
  sweep from its last checkpointed row. Checkpoints are **rate-limited to one per
  10 minutes** (`SAP_CKPT_EVERY_SEC` overrides): a full dump is ~10 GB at n ≥ 23,
  so writing every row would add ~15–20% sweep time on a SATA SSD and terabytes
  of write wear per campaign — a crash now costs at most ~10 min of recompute. All state files are deleted on
  success. Checkpointing activates only when a state file is in use (i.e. `--crt`).
- **Single-fault self-repair.** A verification mismatch *past* the size-estimate
  bound means a recorded residue is corrupt — a real risk on non-ECC RAM over a
  multi-day run. Instead of discarding the campaign, the engine confirms the fault
  is reproducible (ruling out a transient bad check sweep), then isolates the one
  bad residue by leave-one-out CRT against independent check samples, re-runs only
  that modulus, and continues. A 5-day total loss becomes a ~1-sweep repair. If it
  can't attribute the inconsistency to a single residue, it still aborts rather
  than return a wrong answer.
- **Self-verifying result.** The answer-size estimate only *starts* the campaign
  (0.223 digits/vertex — tighter than the old 0.25, saving runs); after CRT
  reconstruction, extra runs with fresh moduli (≥ ~24 bits' worth) must reproduce
  the value. An undershot estimate self-heals (the failed check folds into the
  CRT set). A free parity sanity check (A007764(n) is even for n ≥ 2) runs at the end.

```
dotnet run -c Release -- --dense 18        # exact, dense engine
dotnet run -c Release -- --crt 20          # exact via modular runs + CRT
dotnet run -c Release -- --mod 2^64 20     # one residue (for distributed CRT)
```

Measured against the hash engine (same machine, exact counts):

| n  | hash engine (best) | dense exact       | speedup | RAM ratio |
|----|--------------------|-------------------|---------|-----------|
| 15 | ~37 s / 0.22 GB    | **0.9 s** / <0.1 GB | ~40×  | —         |
| 16 | ~113 s / 0.51 GB   | **3.3 s** / 0.09 GB | ~34×  | 5.7×      |
| 17 | ~366 s / 1.02 GB   | **8.7 s** / 0.20 GB | ~42×  | 5.1×      |
| 18 | ~20 min / 3.29 GB  | **33 s** / 0.63 GB  | ~36×  | 5.2×      |
| 19 | (out of reach)     | **112 s** / 1.69 GB | —     | —         |

And past the exact engine's RAM ceiling, via `--crt` (8 bytes/state per run):

| n  | mode        | digits | time      | peak RAM |
|----|-------------|--------|-----------|----------|
| 20 | dense + CRT | 88     | ~11.5 min | 2.58 GB  |
| 21 | dense + CRT | 97     | ~31 min   | 2.63 GB  |
| 22 | dense + CRT | 107    | ~1 h 48 m | 7.41 GB  |
| 23 | dense + CRT (`--bits 32`) | 117 | 8.7 h¹ | 11.40 GB |

¹ 13 CRT runs + 1 verification run (~28 min/sweep post-hoist, product ~2^415)
on a machine that was also in desktop use; ~6.5 h expected on an idle box.

**n = 20–23 were computed exactly on a 6-core / 16 GB desktop** — all
match OEIS. For perspective, the original 2012 paper computed n=22 using
**217 GB and ~3.3 days on a 1 TB machine**; the dense + CRT engine reproduces
that 107-digit value in **under 2 hours on 7.4 GB** (7 coprime runs, product
~2^447).

**n=23 has been computed on this machine** with `--crt --bits 32` (see table
above: 14 sweeps at ~28 min each, 8.7 h wall on a non-idle machine, 11.4 GB
peak — every intermediate residue and the final 117-digit value match OEIS).
A lower-RAM alternative is `--crt --bits 12` at n=23: **3.9 GB**/run, ~35 runs,
est. ~2 h (scaled from the n=24 sweep below, not measured) — light enough to coexist with
normal desktop use. **n=24** is reachable in-RAM with `--crt --bits 12` (peak 11.34 GB; one
real sweep measured at 671 s on 2026-10-03, so `--crt-known --bits 12` takes ~36 runs ≈ 6.7 h
and plain `--crt` ~7.5 h; the full campaign has not been run yet, and it needs an otherwise
idle machine) —
the 12-bit packed chunked store sidesteps both the RAM wall and the array-element
limit, making disk-based out-of-core (~300 TB of I/O either engine) unnecessary.
The ladder beyond this machine, same engine: n=25 ≈ 32 GiB (48–64 GB box),
n=26 ≈ 96 GiB, n=27 ≈ 273 GiB — i.e. the published n=26/27 record hardware
(terabyte-class in 2013) is now a rentable large-memory VM. CRT peak RAM stays ~flat (each run is 8 bytes/state regardless of the
answer's size); only the *number* of runs grows (6 coprime moduli here, product
~2^383). The dense state space is also *smaller* than the hash engine's (n=17:
4.18 M codes vs 7.19 M live keyed states) — the parenthesis alphabet needs no
anchor markers. Why so much faster: no hash probing (the perfect-hash lookup is
two sequential-friendly array reads), no per-state key copies, no dedup pass, no
allocation in the sweep, and lock-free parallelism. Validated against OEIS for
n = 1–19 (exact) and n = 20–21 (CRT), plus modular residues at n = 15.

Limits: mate strings are packed in one `ulong`, so **n ≤ 32**; the dense array
covers all valid states including unreachable ones (accepted by the original
authors — the slack is small); no out-of-core/checkpoint support (RAM-only).

### Memory monitoring

Because memory is the ceiling, the run is instrumented:

- **Live per-layer progress** is printed to **stderr** (throttled to ~4×/sec),
  updating in place:
  ```
  [n=16] layer  240/480  states=   1,830,441  RAM=  0.71 GB  t=  95.3s
  ```
  showing the current frontier layer, number of live states, working-set RAM,
  and elapsed time — so you can watch RAM climb and judge how close you are to
  the ceiling in real time.
- **Peak live states** and **peak working-set RAM** are reported at the end.
- **`--maxram <GB>`** caps the run: if the working set crosses the cap after a
  layer, the run **aborts cleanly** (reporting the layer and live-state count)
  instead of being OOM-killed by the OS. Example:
  ```
  dotnet run -c Release -- --maxram 24 18
  ```
  The check runs after each completed layer, so it is approximate (a single
  layer can still spike ~3× before the next check) — set the cap with headroom.

## Measured scaling (6-core desktop, .NET 10, post-optimization)

| n  | paths (digits) | peak live states | peak working set | time    |
|----|----------------|------------------|------------------|---------|
| 15 | 48             | 936,771          | 0.22 GB          | ~37 s   |
| 16 | 55             | 2,589,102        | 0.51 GB          | ~113 s  |
| 17 | 62             | 7,186,227        | 1.02 GB          | ~366 s  |

Peak states (and thus memory **and** time) grow ≈ **3× per +1 in n** — exactly
the ratio reported in the paper. **Memory is the ceiling, not time.**

### Reaching higher n

Because cost triples per step, the largest feasible n is set almost entirely by
RAM. After the storage rewrite (peak ≈ 1.02 GB at n=17, ~65 bytes/state):

| RAM    | approx. reachable n |
|--------|---------------------|
| 8 GB   | ~18–19              |
| 32 GB  | ~20                 |
| 64 GB  | ~20–21              |
| 256 GB | ~22                 |

For reference, the paper reached **n=22** (G₂₂,₂₂, a 107-digit count) using
217 GB and ~3.3 days single-threaded on a 1 TB machine.

To push further on the same hardware:
- **The dense engine (`--dense`/`--crt`) is now the first choice** — ~40× faster
  and ~5× less RAM than the hash engine, and `--crt` cuts per-run RAM to
  8 bytes/state. Confirmed on a 6-core / ~8 GB desktop: n=19 exact dense
  (112 s), **n=20 and n=21 via `--crt`** (~11.5 / ~31 min, 2.6 GB). n=22 is the
  next realistic probe (~1.5 h, RAM still ~4 GB — CRT keeps it bounded).
- **More RAM** is still the highest-leverage change (the 3×/level wall is intrinsic).
- Run a **single n** (not `--table`) so nothing else is resident.
- **`--ooc` removes the RAM cap entirely** (spills to disk, hash engine only) at
  ~5–7× the time — the way to reach n beyond what any in-RAM mode can hold.
- Two *speed* knobs for the hash engine that cost RAM: **`--lookahead <W>`**
  (up to ~2× faster) and **`--fast`** (~1.4× faster). See below.

## Verification

`--selftest` checks n = 1..8 against the known sequence. Runs here have also
been confirmed against the paper's Table 2 for n up to 16 (48- and 55-digit
values match exactly).
