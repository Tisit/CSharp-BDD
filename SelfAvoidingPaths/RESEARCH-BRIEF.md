# Research brief: faster exact counts of A007764

Handoff for a fresh research session (written 2026-09-24). Goal, current state,
measured costs, invariants, rejected ideas, open directions.

## Goal

- **Near term:** exact A007764(24) in one overnight run (8-10 h) on the current desktop
  (i5-9600K, 6 cores / 6 threads, 16 GB RAM). Current estimate is about 25 h, so this
  needs about 2.5-3x.
- **Research:** a genuinely better algorithm (fewer states, fewer sweeps or cheaper
  sweeps) that would also push toward n = 25+ on commodity hardware. The known
  record frontier is n = 27 (Iwashita et al.); reference values up to n = 27 are
  in `Program.cs` (`known`), so every n <= 27 auto-verifies.

## Current engine (DenseCounter.cs)

A C# port of GGCount (Iwashita, Nakazawa, Kawahara, Uno, Minato, TCS-TR-A-13-64):

- **Frontier states** are noncrossing parenthesis strings (`.`, `(`, `)`) over the
  n positions of the sweep line, one grid vertex updated at a time (row-major,
  n-1 updates per row, n*(n-1) per sweep).
- **Minimal perfect hash** ranks every state into a dense counter array (no hash
  table): state = left half + right half, `code = codeLR[left] + codeR[right]`.
  Blocks = all states sharing a left half, contiguous in the array.
- **In-place update** with a *deferred* array (width n-1) holding values in flight
  between consecutive updates, so no second full array.
- **Modular counting + CRT:** each sweep counts mod m in 12/32/64-bit storage;
  independent sweeps over coprime moduli are combined with the CRT and verified
  with extra moduli (self-healing, single-fault repair). Exact limb mode exists
  but only for small n.
- **Parallelism:** left-half patterns are grouped (group bits fix '.' vs paren),
  groups run on a shared-counter work queue; lock-free by construction.
- **Per-update specializations:** `UpdateBlockRight` (pair in the right half,
  table-driven, per-kind passes), `UpdateBlockLeft` (pair in the left half, case
  is block-invariant, per-height offset tables), `UpdateBlockGeneric` (the one
  straddling position).

## Cost model (measured)

Sweep cost is **instruction-bound** (VTune, n=21 12-bit: Retiring 71 %, Memory
Bound 2.6 %, IPC about 3.1). So:

    campaign time ~ (#CRT runs) x (states x updates x instructions/state-update)
    #CRT runs      ~ answer_bits / counter_bits   (+ 1-2 verification runs)

| n | dense states | deferred | 12-bit RAM | answer size |
|---|---:|---:|---:|---|
| 19 | 32.7 M | 11.7 M | 0.06 GB | |
| 21 | 258 M | 91.7 M | 0.5 GB | 97 digits |
| 23 | 2.06 G | 0.73 G | 3.9 GB | 117 digits |
| 24 | 5.85 G | 2.06 G | 11.1 GB | ~128 digits (~40 runs at 12-bit) |
| 25 | 16.6 G | 5.85 G | ~32 GB | |
| 26 | 47.3 G | 16.6 G | ~90 GB | |

States grow about 2.84x per n. Measured sweeps (2026-09-24, all optimizations):

- n=19 single sweep: 32-bit about 6.6-7.8 s, 12-bit about 9.9 s.
- n=21 single sweep: 32-bit about 69 s (160 s in July).
- CRT n=18 (32-bit, end to end): 18.8 s (53 s this morning).
- n=24 projection: about 40 min per 12-bit sweep x about 40 runs, so about 1 day.

`sapaths --dense-info <n>` gives state counts and RAM; `--crt-plan <n> --bits b`
lists the runs.

## Invariants (break these and results are silently wrong)

1. **NN-before-emitter ordering.** Within one update, an emitted state (LL/RR/RL
   join, `..` at (p,p-1)) targets the same deferred slot the NN state `c == m2`
   consumes (`c += d; d = 0`). The NN consumer must run first. The right-mate
   enumeration order (`.`, `)`, `(` from the top) and UpdateGroup's R-before-L
   order guarantee it; `UpdateBlockRight` runs passes NN, Swap, Emit, Escape.
   Negative test: reversing block order mismatches OEIS from n=8.
2. **Even-aligned block bases** (12-bit): two counters sharing a byte are always in
   the same block, and one block is only touched by one worker.
3. **Deferred codec's right width is one less** than the main codec's, so states
   from different groups hit different deferred blocks (lock-freedom).

## Tried and rejected (with evidence)

- Software prefetch: slower (per-element recompute; memory already saturated).
- SIMD gather/scatter: no contiguous loop to vectorize.
- Zero-block skipping: n=19 is 97 % nonzero after row 5, 99.99 % after row 9;
  row time is flat regardless, so the upper bound is about 11 % (n=19) and about
  8-10 % (n=24), and safe skipping would need per-block deferred tracking.
- Lock/GC/segment-heap tuning (hash engine era): no wall-time effect.
- Remaining micro-options judged small: left/right split retune, large pages
  (12-bit bounds checks: done 2026-09-24, 1.05x).
- (2026-09-24, literature survey: `reports/Faster A007764 exact counting.md`)
  - Compacting the deferred array (E2): it is ~100 % live after every update from row 5
    on (`SAP_DEFERRED_CENSUS`), so 16-bit n=24 (14.74 GiB) cannot fit 16 GB.
  - Planar 12-bit layout (low-byte array + nibble array): ~9 % slower than packed.
    Store forwarding is not the 12-bit cost; instruction count per packed access is.
  - Pruning dead states (E3, `research/e3_live_states.py`): every row-boundary state
    is live except rows 1-4 (unreachable; see zero-block skipping) and the last row.
    The first/last rows became closed form instead (see below).
  - Fused two-vertex passes (E4): a cache-hot right update costs 89-93 % of a cold
    one, so locality is worth <= ~5 % of a sweep, before the escape-ordering cost
    (RR escapes deposit into other blocks' deferred slots between the two updates).
  - Head-row block skipping with per-block deferred tracking (prototyped, reverted):
    a "maybe nonzero" byte per value block and per deferred block, static write sets
    flagged before each block, skip when the whole read set is unflagged; verified
    exact (skip check, OEIS n=4..18). With row 0 already closed form, it skipped only
    ~2 % of slot-updates (n=12-19), and the exact block-level ceiling is ~3.6-3.8 %:
    reachability spreads within a row, and a block turns nonzero with any one state.
    Wall time at n=19 12-bit: 7.10 vs 7.13 s (no gain; zero blocks were already cheap).
  - Meet in the middle: Iwashita dropped it (TR 13-64 s6.2); the join matrix has full
    rank mod every CRT prime and a naive join costs ~10^16 ops at n=24.
    Superseded 2026-10-16: the join is sparse (open meanders, ~5e11 MACs at n=24 after
    the rotation symmetry), see "Meet in the middle" below: 1.80x at n=22.

## Done 2026-09-24 (n=19 12-bit sweep 10.07 -> ~7.1 s, 1.41x; n=24 projection ~17.5-18 h)

- `--bits 16` (native store): 16-bit sweeps at 32-bit speed (0.65x the 12-bit time).
- Scalar Load/Store/Sum path (each counter decoded once per update): 1.11x at 12-bit.
- `UpdateBlockStraddle` for p == RightWidth (was 15-19 % of a sweep): 1.10-1.15x.
- 12-bit bounds checks off by default (`SAP_BOUNDS=1`): 1.05x.
- First and last rows closed form (`EdgeRowStates`, 2^(n-1) states each with
  count / completion 1): skips 2 of n rows, 1.10x at n=19 (`SAP_FULL_SWEEP=1` = old).
- Research hooks: `SAP_PTIME=1` (time per update position), `SAP_DEFERRED_CENSUS=<csv>`.
- 12-bit AVX2 unpack/pack (`Packed12Simd`, d85d71d243): each block's value range and
  its deferred blocks (D(B) for right updates, up to 3 for left and straddle updates)
  are unpacked to plain ushorts, the loops run there, and everything is packed back.
  1.59x over the scalar 12-bit path at n=19 (`SAP_NO_SIMD=1` = old). The 12-bit sweep
  now runs at about 16-bit speed (~4.6-5.0 s at n=19, from 10.07 s this morning).
  Projection for n=24 at 12-bit: ~12 h.
- Lazy modular reduction in the unpacked buffers (7433143715): plain adds, one vector
  reduce per pack (bounds < 5m, limit 16m). 1.21x. n=19 12-bit sweep ~4.5-4.8 s; the n=18
  12-bit CRT campaign takes 30 s (65 s at the start of 2026-09-24). n=24 projects to ~11 h.
- `--crt-known` (35d4e7836b): for n <= 27, every residue is checked against the known a(n);
  the campaign stops once the product exceeds it and needs no held-out runs. n=24 12-bit:
  40 -> 36 runs. Not an independent computation.
- Pre-decoded, pass-ordered right-update entries `RtEntry` (9aeea30d13): 1.10x sweep,
  1.21x on right positions. n=19 12-bit sweep ~4.25 s. Projection for n=24 12-bit
  `--crt-known`: 36 runs x ~16 min = ~9.5 h (normal mode ~10.5 h).
- **Calibrated 2026-10-03:** one real n=24 12-bit sweep (`--mod 4093 --bits 12 24`) took
  671 s (11.2 min), peak 11.34 GB; residue 408 matches OEIS. So n=24 `--crt-known` is
  ~6.7 h (36 runs) and normal `--crt` ~7.1-7.5 h, plus checkpoint writes. Both are
  overnight runs. The machine must be otherwise idle: with SQL Server, VS, Steam etc.
  open the first attempt thrashed the pagefile (working set 0.3 GiB of 11.7).
- VTune v2 (2026-10-04, n=21 12-bit): still instruction-bound (Retiring 71 %, Memory
  Bound 9 %, DRAM 4.9 %), ~17 instructions per state-update (55 in September).
  UpdateBlockRight12 35 %, LeftSwap12 12 %, LeftEmit12 12 %, EscapeAll 10 %, unpack/pack ~15 %.
- Canonical deferred layout + absolute offset tables + branch-free left emits
  (b53ce2babe): left positions 1.11x, sweep 1.06x. Expect n=24 at ~10.5 min/sweep.
- Rejected: escapes into unpacked target blocks (no gain; whole-block unpack/pack costs
  as much as the sparse packed writes it replaces).

## Residue ladder (2026-10-04, `--ladder`, DenseCounter.Ladder.cs)

- Idea: the row-boundary vector at row k has max entries of ~0.77·n·(k−1) bits
  (`SAP_ROWBITS=1`, exact mode: n=12 45/98 bits at mid-grid, n=16 85/181, n=18 110/232),
  so after req(k) runs it is fully determined. Later runs start at row k from that vector
  reduced mod their own modulus. One extra residue checks every state when a rung is built.
  Not meet-in-the-middle: there is no join, and the sweep code is unchanged.
- Model (n=24, 36 runs): 1 rung at row 13 → 0.74× work, ~167 GB disk; within 90 GB the best
  is 1 rung at row 6 → 0.83×, 79 GB. Up to n=23 there's room for 3 rungs (~0.65×).
- Measured n=20 `--crt-known --bits 12`: 222 / 224 s plain vs 178 / 173 s ladder (0.79×).
  Overheads at n=19: rung build ≤ 1 s, load 0.13-0.25 s and dump ~0.09 s per run (sweep 2.4 s).
- Kernel lessons: the first load path was slower than the rows it saved. Two fixes were
  needed: (1) pass-structured AVX2/FMA modular arithmetic in double lanes (state-inner
  loops, no dependent chain per state); (2) unpack the packed chunk in 1024-state
  sub-blocks. The first version expanded 14 × 512 KB double arrays per thread and was
  DRAM-bound.
- Test hooks: `SAP_LADDER_SHORT=<bits>` undersizes rungs (the check must reject them),
  `SAP_LADDER_TIMING=1` prints per-run load and dump times.
- n=22 on D: (SATA 860 EVO, ~0.5 GB/s), rungs 7/12/16: 2,559 s vs ~2,370 s plain (estimated).
  No gain: each load read every digit file (up to 24 GB per run, ~45 s on SATA), plus
  50 dumps (~110 s) and builds (230 s). An I/O-aware model reproduced it (2,505 s).
- Fix (same day): the planner is now a time model fed by the measured row time (2nd row
  of run 1) and the measured disk speeds (512 MB unbuffered test, or `--ladder-disk R,W`).
  It picks no rungs when none pays off. A background thread prepares the next run's start
  vector into one file during the current sweep (prefetched load at n=19: 0.04 s vs
  0.1-0.28 s direct). The plan is persisted (`sapladder_<n>_b<bits>.plan`) for resume.
- n=22 on the test box (N100, 4 cores; NVMe 2.34 R / 1.99 W GB/s), `--crt-known --bits 12`,
  disk-aware plan + prefetch: rungs 4/9/14, predicted 1.66 h vs 2.29 h plain (0.73×).
  Measured 6,361 s = 1.77 h, OEIS match (plain baseline not measured; 0.77× vs the model's
  plain). No prefetch waits.
  - Prefetched loads: 2.5-3.6 s. Direct loads (first run after each new rung): 9.7 / 28 / 44 s,
    about 2× the model.
  - Builds: 23 / 72 / 140 s (model ~10 / 59 / 148 s).
  - Dumps: ~2.8 s each, CPU-bound (model 0.55 s).
  The model is ~7% optimistic on this slow CPU. Recalibrate the direct-load and dump costs
  from these numbers.
- n=23 on the test box (2026-10-04/05), `--crt-known --bits 12 --ladder-gb 120`: rungs 3/8/14,
  predicted 5.73 h vs 7.82 h plain (0.73×). Measured 22,174 s = 6.16 h (+7.5%; 0.79× of the
  modelled plain), OEIS match, all rung checks passed, no prefetch waits, peak RAM 4.6 GB.
  - Builds: 48 / 155 / 447 s (model ~19 / 147 / 487 s).
  - Direct loads: 27 / 63 / 118 s, ~2× the model.
  - Prefetched loads: 10.5-13 s each.
  - Dumps: ~7.5 s each (model 1.5 s), CPU-bound on the N100.
  Both n=22 and n=23 on the box overran by ~7%.
- Recalibrated (2026-10-05) from the per-run timings of both campaigns. The model now
  reproduces n=22 within +0.3% and n=23 within -1.0% (was -6% / -7%).
  - The 2nd row, which the planner times, is ~5% faster than the average row
    (`RowTimeFactor` 1.05).
  - Direct load: 0.15 rows/digit.
  - Prefetched load: a constant 0.28 rows.
  - The prefetch thread slows the concurrent sweep by 0.06 rows/digit (+2% at j=4, +14-17%
    at j=19-20).
  - Dump: 0.18 rows.
  - Build: (0.015 j^2 + 0.22 j) rows.
  - The full-sweep runs at the start of each campaign measure plain cost directly
    (14.28 s/row at n=22, 43.2 s/row at n=23). Against that the ladder achieved 0.74× (n=22)
    and 0.75× (n=23).
- Next: rerun n=22 on C: (NVMe) and on D: with the new planner, plus a measured plain
  baseline; then n=23/24. Possible gain: combine 3 digits per Horner step (12-bit: < 2^53).

## Width throughput re-measured (2026-10-04, n=19 single sweep)

| machine | 12-bit (SIMD) | 16-bit | 32-bit | 12-bit SAP_NO_SIMD |
|---|---:|---:|---:|---:|
| dev (i5-9600K, 6 cores) | 3.1-3.3 s | 4.6-5.0 s | 5.1-5.2 s | |
| test box (N100, 4 cores) | 8.0-8.2 s | 11.5 s | 11.3 s | 20.7 s |

- The 12-bit path is now the fastest on both machines, about 1.45× faster than 16/32-bit.
  The earlier "16-bit at 0.65× the 12-bit time" is stale: since then only the 12-bit path got
  the unpack-to-local-buffers, `RtEntry` and layout-table work.
- N100 scaling (1 → 4 cores): 12-bit 3.7×, 32-bit 3.96×, 16-bit 4.06×. It is compute-bound,
  not limited by the single memory channel.
- Per CRT bit, 32-bit is still cheapest (dev 0.16 s/bit vs 0.27 s/bit at 12-bit), where RAM
  allows it.
- Tried and rejected (2026-10-04): giving the 32-bit store the 12-bit path's kernels.
  - Generic `UpdateBlockRightU<TIo,T>` and friends, with `IBlockIo<T>` (Io12 = ushort,
    Io32 = ulong buffers, widening copy in, subtract-ladder reduce out). Residues matched
    the old build, and 12-bit was unchanged (3.05-3.13 s vs 3.14-3.42 s, noise). But 32-bit
    got slower: 7.7 s vs 5.1 s; R 4.05 vs 2.92 s, L 3.49 vs 2.07 s (`SAP_PTIME`). The
    whole-block copy in and out costs more than it saves when counters are cheap to access
    in place.
  - In-place right update on the 32-bit store (`RtEntry`, branch-free reduce per add, no
    copy): R ~2.6-2.8 s vs ~3.1 s, but the sweep was 5.07 s vs 5.17 s (noise).
  - Why 12-bit is faster: both widths scale the same (dev 1 → 6 cores: 32-bit 4.04×,
    12-bit 4.19×), and 32-bit is already 1.63× slower on one core (21.6 vs 13.3 s). So it's
    per-core cost, not bandwidth, and the 12-bit techniques don't remove it.
  - Experiment source kept outside the repo (session scratchpad); code reverted.
- Per CRT bit, the existing 32-bit path is still the cheapest wherever it fits in RAM.

## Non-research levers toward "overnight"

- **RAM.** 64 GB allows `--bits 32` at n=24: 15 runs instead of 40, about 7 h.
- **Split campaigns (built):** `--crt-part K/N` on N machines, then
  `--crt-merge <state files>`. The runs are independent, so this scales linearly.
- A 16-bit storage mode (easy) would give 27 runs on a 32 GB machine.

## Memory: overhead audit and how others handled it (2026-10-16)

**Overhead audit** (`SAP_MEMTRACE` hook, kept as a patch outside the repo). At n=24, 12-bit,
the peak of 11.34 GiB is 11.06 GiB of counters plus ~0.28 GiB of other memory. The other memory is:
- Codecs: raw-id-indexed `long[]` arrays, 526 MiB committed, ~85 MiB actually touched.
- Transition tables: ~42 MiB, the same at n=23 and n=24.
- Sweep scratch and the runtime: ~60 MiB.
- `--ladder`: a flat ~20 MiB over n=18-20, not growing with the state count.

CRT mode was building the codecs twice (dc3af36f49 fixed it; n=20 peak private memory went from 281 to 246 MiB).
What remains is < 1 % each.

**Packing the counters further:** not possible losslessly. Residues mod ~4093 are uniform, so
12-bit storage is 99.99 % efficient, and the arrays are ~100 % nonzero (E2), so there is nothing
to compress. Only the width can change, which trades RAM against runs: at n=24,
11-bit is 10.1 GiB with ~39 runs and 13-bit is 12.0 GiB with ~33 runs.

**Similar problems in the literature:** none stored counters more compactly. Each traded
memory for disk or for time.
- Legal Go positions, L19 (Tromp et al., 2015): border-state transfer matrix, CRT over 9 moduli
  of the form 2^64 - small. Each cell step stream-merges sorted (state, count) files through a
  RAM buffer of bounded size (`golegal`: `jtinsert`/`dumpstates`). Total ~30 PB of disk I/O and
  ~10 months of runs; the recommended machine is 15 TB scratch + 192 GB RAM. Here this is the
  rejected full-pass out-of-core design (~300 TB of I/O per campaign). Only an occupancy-windowed
  variant could work.
- Rubik's cube, 26 moves (Kunkle and Cooperman, 2007/09): a ranked array kept in RAM at 4 bits
  per coset, with updates sent to disk buckets by hash range (delayed duplicate detection) and
  merged one RAM-sized bucket at a time. This shrinks their update buffer, not the array. For
  us, an update (index + residue) is larger than the slot it changes.
- Hamiltonian cycles in grids (Pettersson, 2014): a ranked vector, with CRT over prime powers
  <= 256 so that one byte per counter is enough. The n=26 run still used 4 B/counter: 160 GB and
  ~3 core-years. Here lcm(1..256) ~ 2^363 < a(24) ~ 2^425, so 8-bit needs extra runs at a wider
  width. That is ~7.4 GiB but ~1.6-2x the campaign time.
- Self-avoiding walks and polygons (Jensen; Guttmann and Jensen, 2022): 62-bit primes
  normally, and 15-bit primes "to save memory". The 2022 paper says the in-place update needs
  "no temporary storage", but the published code covers only the hexagonal lattice
  (`hexsaw_rhombus_cross_omp.f90`, one `WalkCount` array). The square-lattice code is not
  public. My inference, not checked: an edge-based kinked boundary on the square lattice has n+1
  edges mid-row, so it would need more states than our main + deferred, not fewer.

Sources: [Tromp, legal Go positions](https://tromp.github.io/go/legal19.html),
[golegal](https://github.com/tromp/golegal);
[Kunkle & Cooperman, JSC 2009](https://www.ccs.neu.edu/home/gene/papers/jsc09.pdf);
[Pettersson, EJC 21(4) P4.7](https://www.combinatorics.org/ojs/index.php/eljc/article/view/v21i4p7);
[Guttmann & Jensen, arXiv:2208.06744](https://arxiv.org/abs/2208.06744),
[code](https://github.com/IwanJensen/Self-avoiding-walks-and-polygons/tree/WCAS(H)).
Earlier survey: `reports/Faster A007764 exact counting.md`.

## Join matrix mod small primes, and is there a recursion? (2026-10-16)

**Join (connection) matrix.** For one occupancy class of k crossing edges at a straight cut,
G[a,b] = 1 if gluing top pattern a to bottom pattern b gives a single path and no loop
(the one-defect Temperley-Lieb Gram matrix at loop weight 0). The free end is never inside an
arc, so G is Catalan((k+1)/2) square. Its rank lower-bounds the state count of any cut-based
method. If it drops mod p, a mod-p^e DP could run in a smaller quotient space, because the
kernel is closed under transitions. Tool: `research/tlgram` (local Smith form; `rank` mode).

| k | size | rank mod 2 | mod 3 | mod 5 | mod 7 |
|---:|---:|---:|---:|---:|---:|
| 9 | 42 | 16 | 41 | full | full |
| 13 | 429 | 64 | 365 | full | full |
| 15 | 1,430 | 128 | 1,094 | full | full |
| 17 | 4,862 | 256 | 3,281 | 4,861 | full |
| 19 | 16,796 | 512 | 9,842 | 16,778 | full |
| 21 | 58,786 | 1,024 | 29,525 | 58,598 | not run |

- **Observed rule:** rank mod p = the number of +-1 paths of length k from height 0 to height 1
  that stay within [0, 2p-2] (`research/tlgram/strip.py`). It holds for every entry above. It was
  fitted on k <= 17, and k=19 and k=21 were predicted before they were computed. Special cases:
  mod 2 = 2^j and mod 3 = (3^j+1)/2, with k = 2j+1; these satisfy r(j+1) = 2r(j) and
  r(j+1) = 3r(j) - 1. So p divides det G iff (k+1)/2 > 2p-2. The first case is k=17 for p=5,
  which corrects the earlier "only 2 and 3", found at k <= 15. This looks like the dimension of
  a simple module of the Temperley-Lieb algebra at q = i in characteristic p (mixed
  characteristic). I have not checked it against the published theory, which predicts further
  corrections at larger k; none appear up to k=21.
  **Update 2026-10-18: the rule is a theorem (no corrections at any k); see "Rank rule proved" below.**
- **Smith form (k <= 17):** the 2- and 3-adic valuations are almost all 1 (at k=17,
  2-adic: 4,488 x 1, 118 x 2; 3-adic: 1,581 x 1). So the quotient saves a fixed ~1 bit per state
  mod 2^e and ~0.5 bit mod 3^e, about 1.5 bits per state over a whole campaign (< 0.4 % of
  a(24)'s 425 bits). It would also need a basis with sparse transitions, which is known only for
  mod 2 (Cygan-Kratsch-Nederlof). For CRT moduli near 4096 the rule gives full rank up to
  k ~ 16,000, so the frontier is minimal for every modulus we use. Not worth engineering.

**Rank rule proved (2026-10-18).** Check script: `research/tlgram/rankrule.py`.
- **G is a Temperley-Lieb Gram matrix.** G is the Gram matrix of the TL standard (cell) module
  W(k,1) (k points, 1 defect) at loop weight delta = 0, i.e. q = i, quantum characteristic l = 2.
  With one defect on each side, the defects always join, so <a,b> = delta^(#loops), which is 1 iff
  there is no loop. That is J. The size C(k,(k-1)/2) - C(k,(k-3)/2) = Catalan((k+1)/2) agrees.
- **Determinant (Di Francesco / Westbury / Ridout-Saint-Aubin form).**
  det G(k,1) = prod_j ([j+2]_q/[j]_q)^dim W(k,1+2j). At q = i the odd-j factors are +-1, and an even
  j = 2t factor tends to +-(t+1)/t. So v_p(det) = sum_t dim W(k,1+4t) (v_p(t+1) - v_p(t)).
  This reproduces the k=17 Smith forms exactly (2-adic total 4,724 = 4,488 + 2x118; 3-adic 1,581),
  and gives p | det iff k >= 4p-3 (17 for p=5, 25 for p=7), matching the table.
- **Rank.** Over a field of characteristic p, rank G = dim of the simple head D(k,1) of W(k,1)
  (cellular algebra; the form is nonzero). Then three known facts:
  1. dim D(k,m) = multiplicity of the indecomposable tilting module T(m) in V^(x)k for quantum SL2
     (Andersen, arXiv:1709.00248, Prop. 6.2).
  2. Steinberg tensor product (same paper, Prop. 5.2 and (5.2)): T(1 + 2 lambda) = St (x)
     T_cl(lambda)^[Frobenius], with St = T(1) at l = 2 and T_cl classical SL2 tilting in char p.
     By characters, St^(x)3 = St (x) X^[F] with X = T_cl(1) + 2 T_cl(0), so
     V^(x)(2j+1) = St (x) (X^(x)j)^[F], and rank G = multiplicity of T_cl(0) in X^(x)j.
  3. Classical char p: the tiltings T_cl(mu) with mu >= p-1 are negligible. The quotient is the
     Verlinde category Ver_p (simples L_0..L_{p-2}, truncated Clebsch-Gordan fusion), and the
     multiplicity of T_cl(0) equals the multiplicity of the unit after the monoidal quotient.
  So rank G = the number of walks of length j = (k-1)/2 from 0 to 0 on {0..p-2}, with steps +-1
  and a weight-2 "stay" step. Pairing the steps of a +-1 walk on [0, 2p-2] (odd heights 2mu+1)
  gives exactly the observed strip rule. Closed form (diagonalize the path graph):
      rank_p(k) = (2/p) sum_{s=1}^{p-1} sin^2(pi s/p) (4 cos^2(pi s/(2p)))^((k-1)/2)
  It grows like (2 cos(pi/(2p)))^k: sqrt2 for p=2 (2^j), sqrt3 for p=3 ((3^j+1)/2), and tends to 2
  (Catalan growth, full rank) as p grows. Verified: strip = Verlinde walk for k <= 201,
  p <= 31; closed form = strip up to float rounding; all table entries reproduced.
- **Is it new?** As representation theory, no: it is a short corollary of Andersen (2019) plus
  Ver_p. The strip model is the mixed Verlinde quotient at level l*p (Sutton-Tubbenhauer-Wedrich-
  Zhu, arXiv:2105.07724). Spencer (arXiv:2011.01328) and Martin-Senecal-Spencer (arXiv:2601.17445)
  treat TL in positive and mixed characteristic diagrammatically. I found no statement of this
  closed form for delta = 0 or for the open-meander matrix. On the CS side, rank-based algorithms
  (Cygan-Kratsch-Nederlof; Curticapean-Lindzey-Nederlof, arXiv:1709.02311) use the non-planar
  matchings matrix, whose rank over Z_p (p odd) is only lower-bounded (Omega(1.979^k)) and left
  open. The planar (noncrossing) analogue is exactly solvable through TL theory, and no planar-
  algorithm paper I found uses this (e.g. Caroppo-Da Lozzo-Di Battista-Vihrovs, arXiv:2610.11944,
  counts meanders, not ranks). Fair summary: a known-theory result with a possibly new
  combinatorial and algorithmic reading; a short note at most, not a main result.
- **For this project:** no speedup. CRT moduli near 4096 give full rank up to k ~ 16,000.
  A mod-3 DP in the quotient would have about sum_k C(n,k) 3^(k/2) = (1+sqrt3)^n = 2.73^n states
  instead of about 3^n, so it only helps for a(n) mod 3 (and needs an explicit sparse basis).

**Recursion in n.**
- No linear recurrence with polynomial coefficients exists, of any order. Such (P-recursive)
  sequences grow at most like (n!)^C K^n. But ln a(n) ~ n^2 ln(lambda): the second difference of
  ln a(n) over the 27 known terms converges to 1.11298 = ln(lambda^2), so lambda = 1.74455, matching
  the literature. So a(n+1) is not a fixed polynomial-coefficient combination of earlier terms.
- What does exist: for a strip of fixed width w, the count in the length L has a rational
  generating function (the transfer matrix), so it satisfies a linear recurrence of order about
  #states(w). The square is the diagonal w = L of a non-rational bivariate series, so there is no
  fixed recurrence. Each n needs the wider transfer matrix, which is exactly the frontier.
- Not ruled out, but no evidence: q-holonomic-type recurrences (coefficients in lambda^n), or
  nonlinear ones. 27 terms cannot test them. Guttmann-Jensen extend the sequence only
  approximately, from asymptotic fits.
- **Congruences:** a(n) is even for n >= 2. Proof: the diagonal reflection fixes both corners,
  and a path fixed by it would have every vertex on the diagonal, where no two vertices are
  adjacent. So a(n)/2 counts the paths whose first step goes right. Beyond that, 2-adic
  valuations are 1-6 with no pattern, and a(n) mod 8, 9, 5, 7 for n <= 27 show no periodicity.

## Meet in the middle (2026-10-16, `--mitm`, DenseCounter.Mitm.cs)

For even n, `--mitm` (with `--mod` or `--crt`) sweeps only rows 1..n/2-1 and then joins the
middle-row vector with itself. The 180-degree rotation swaps the corners, so the completion
count of a middle state from below equals the forward count of its rotation. With rot =
reverse the positions and swap '(' and ')' (the free end stays the free end):
a(n) = sum f(s) * J(s, rot(u)) * f(u), over states with occ(s) == reverse(occ(u)). J = 1 iff
the top arcs and the bottom arcs form one path from free end to free end, with no loop.

- **Structure.** Inside an occupancy class of k crossings, J depends only on the k-symbol
  paren patterns (Catalan((k+1)/2) of them). Its nonzeros are the open meanders, A005316(k).
  `sapaths --meanders 23` reproduces A005316 for every odd k <= 23 (678,390,116 at k=23,
  16 s to generate on 6 cores). The pair list is shared by all C(n,k) occupancy patterns, so
  the join is a sparse-times-dense product: rows = paren patterns, columns = occupancy patterns.
- **The rotation halves the join again.** The term for P equals the term for reverse(P)
  (rotate the whole picture). For even n, no mask with odd popcount is its own reverse, so
  only P < reverse(P) is evaluated and the result doubled. Pair work is
  sum_k C(n,k)·A005316(k)/2: 2.2e9 MACs at n=20, 3.3e10 at n=22, 5.1e11 at n=24.
- **Kernel.** For each batch of occupancy columns, gather G[s][j] = f(s on P_j) and
  H[r][j] = f(r on reverse(P_j)) as 16-bit cells. Each cell costs a pdep and an Encode.
  Over all classes, every state is gathered exactly once. Then T = sum_s G[s]·(sum over the
  partners r of s of H[r]): partner sums are adds in 32-bit AVX2 lanes, with one 32x32->64
  multiply per (s, j). Partners come from a DFS over the bottom pattern with an O(1)
  path-endpoint union/undo. They are stored per s up to k=21 (294 MB at k=21) and regenerated
  per s for k=23, which has only 12 column patterns at n=24. Small classes (few rows, many
  columns) run their column batches in parallel; large ones split rows. The deferred array
  is empty at the row boundary and is freed before the join. The join's scratch (<= ~330 MB
  at n=24) is far below its 3.1 GB, so peak memory does not grow. Other widths (16/32/64-bit,
  2^k) use a scalar 64-bit path.
- **Verified:** residues match OEIS for n=2..18 at 12-bit (4093 and 2^12), 16-bit, 32-bit
  (prime and 2^32) and 64-bit (prime and 2^64). Also checked with forced on-the-fly lists
  (`SAP_MITM_FLY=1`), tiny batches (`SAP_MITM_BATCH_KB=1`), `SAP_FULL_SWEEP=1` and
  `--cores 1`. Plain and mitm residues agree at n=18/20/22. `--selftest --dense --crt --bits 12
  --mitm` passed 40/40, plus `--cores 1` and 32-bit. Full `--crt --bits 12 --mitm` campaigns
  match OEIS at n=14, 18 and 20.
- **Measured** (12-bit, mod 4093, 6 cores, interleaved; sweep = sum of `SAP_PTIME`, join from
  `SAP_MITM_TIMING=1`; total includes ~0.5 s of table setup):

  | n | plain sweep | mitm sweep + join | total plain -> mitm | speedup |
  |---:|---:|---:|---:|---:|
  | 18 | 1.26 s | 0.98 + 0.12 s | 1.45 -> 1.28 s | 1.13x |
  | 20 | 6.40 s | 3.37 + 0.72 s | 6.95 -> 4.45 s | 1.56x |
  | 22 | 56.8 s | 27.2 + 4.3 s | 60.0 -> 33.3 s | **1.80x** |

  32-bit prime at n=20 (scalar join): 16.4 -> 10.5 s total (1.56x). The half sweep is exactly
  half the rows. The join is 11 % of a plain sweep at n=20 and 7.5 % at n=22. At n=22 its
  4.3 s is about 2.0 s gathers (random reads through the codec into the value array), 1.6 s
  building the k=21 partner lists, and under 1 s of pair arithmetic.
- **n=24 projection (not run):** half sweep ~671 s x 27.2/56.8 ~ 320 s. Join: gathers scale
  with states (x8 -> ~16 s), k=21 lists ~1.6 s, k=23 generated on the fly (~16 s + work), pair
  arithmetic x15 -> ~10 s. That is ~45-60 s, so ~370-380 s per residue against 671 s:
  **~1.8x**. A 36-run `--crt-known` campaign would drop from ~6.7 h to ~3.7 h. Peak memory
  should stay at the plain sweep's 11.3 GB, but this is not measured at n=24 yet.
- **Not yet:** `--ladder` together with `--mitm` (rejected for now; rungs and the plan must lie
  in the top half), `--crt-part` with `--mitm`, and odd n (needs a staircase cut).
  Checkpoints work unchanged: one written at row r <= n/2 resumes either mode.

## Beyond one machine's RAM: pooling and windowed out-of-core (2026-10-25, analysis only, nothing built)

Question: can two 16 GB machines (desktop + N100 testbox) pool RAM for ONE sweep, or can a windowed
out-of-core (OOC) sweep reach n=25? Findings of a read-only design study (numbers are model
estimates, calibrated on the 671 s n=24 12-bit sweep and ~3.05x per +1 n; not measured at n>=25).

- **n=25 needs ~34 GB at 12-bit, not 25 GB.** The 24.9 GB figure in the research notes is the main
  array only. A sweep also holds the deferred array: (16.63e9 + 5.85e9) x 1.5 B = 33.7 GB (the
  table above already says ~32 GB). Two 16 GB boxes leave ~27 GB usable, so even pooling would
  need a 9-bit store (55 runs instead of 39).
- **Parity schedule (new): ~1 redistribution per row instead of 2.** Every transition preserves
  the parity of occupancy over the pair {p, p-1}. So f_a = parity of occupancy over positions
  [0..a] is invariant for every update except p = a+1. Switch f_a -> f_{a+1} just before
  p = a+1; it then stays valid for n-2 updates, i.e. one switch per row (the switch position drifts
  by one each row, and the wrap f_{n-2} -> f_0 costs ~0.5 of the array once per n-2 rows). The
  engine's own groups key on left-half occupancy only, which covers right updates only
  (DenseCounter.cs ~1508-1515, 1800-1862). Suffix parities need no separate treatment: every
  state has odd total occupancy (free end + paired arcs), so g_b = 1 xor f_{b-1}.
- **Prototype result (research/parity_window/, 2026-10-25).**
  - *Correctness.* A line-by-line Python transliteration of `UpdateBlockGeneric` (value + deferred)
    was split into two class stores. The literal update ran independently inside each class, with
    the layer, pending deferred entries included, re-keyed at every switch. It reproduces A007764
    for n=3..12 with **zero** cross-class entries (n=10..12 checked against the engine's own
    output). The invariance was also checked exhaustively over all 76 valid states at n=6 and on
    524,699 sampled transitions at n=19 (violations only at (p, a=p-1), as predicted).
  - *The atomic per-update model alone is NOT the engine.* Applying the pair transitions atomically
    gives 13 instead of 12 at n=3. The engine pipelines join emissions through the deferred array
    (they skip the next update's open/swap branches), and the in-place update is order dependent
    (descending state order gives wrong counts). Any partitioned design must keep the engine's
    per-block order, and the deferred array has to be partitioned along with the layer.
  - *Corrected traffic.* The first estimate (0.36 of the array per row, "2.8x less than
    Jensen's scheme") counted only the main array. Pending deferred entries at a switch all have
    '.' at the switch position, so they all move under the alternating labelling that makes the
    main array move only 0.352. Total volume is then 0.352 + deferred/main (0.358) ~ 0.71 of main;
    the plain labelling instead moves the occupied states: **0.648 of the main array and no
    pending entries**, the better choice. That is ~1.6x less than the two-redistribution scheme
    (~1.0), not 2.8x. P('.') is the same at every position (0.357 at n=19, 0.353 at n=24, 0.352
    at n=25), so every drift step costs the same.
  - *n=19 numbers.* 32,652,735 main states (matches the 32.7M above); one switch moves 20.99M
    states = 31.5 MB at 12-bit, 23.6 MB at 9-bit, per row. Desktop compute is ~0.25 s/row for the
    whole array (~0.31 s for half on the N100), so on GbE (~115 MB/s per direction, ~15.7 MB each
    way) the transfer is ~0.14 s/row, not the bottleneck there. At n=25 9-bit: 12.1 GB per row
    (6.05 GB each way, ~53 s) against ~112 s/row for the N100's half: ~165 s/row without overlap,
    ~58 h per campaign (55 runs), or ~39 h if transfer overlaps compute. Still well above the ~22 h
    of one 48 GB box, so the verdict below stands.
- **Pooling over the network: rejected.** At n=25 9-bit about 4.5 GB each way per row = 40 s/row
  on full-duplex GbE (testbox is on Wi-Fi today, GbE port unplugged: ~180 s/row), ~910 s of
  transfer per sweep on top of ~2,600 s compute on the slower N100, x55 runs = ~50 h (~100 h on
  Wi-Fi), versus ~22 h with 48 GB in one box. Needs a 9-bit store, per-row codecs, in-place
  re-ranking and a two-machine failure model. n=26/27 stay out of reach for 2 x 16 GB.
- **Windowed OOC on one machine (the "only variant that could work" noted in the memory section).**
  Keys made of 3 interval parities are invalid at only 3 consecutive values of p: 8 chunks, the
  largest ~21% of the array (7 GB at n=25 12-bit), ~1.13 windows per row. Lay the array out on disk
  as 2D cells (key1, key2) and update in place; disk = the array itself. Per row ~T read + ~T
  written (minus ~10 GB resident), ~27 GB/row at n=25 12-bit against ~89 s compute per row, so it
  overlaps on NVMe at ~1 GB/s. The earlier ~300 TB figure was a full pass per vertex (n^2 passes);
  per-row windows cut it ~25x. Under OOC, wider counters win (I/O per answer bit is
  width-independent, 32-bit compute per bit is ~1.7x cheaper):

  | n=25 option | RAM | wall | disk written |
  |---|---|---|---|
  | 48-64 GB desktop, 12-bit in RAM (no code) | 32 GiB | ~22 h (~18 h --ladder) | none |
  | 128 GB, 32-bit in RAM | 90 GB | ~14-15 h | none |
  | OOC NVMe, 12-bit | ~12 GB | ~25-27 h | ~24 TB |
  | OOC NVMe, 32-bit | ~12 GB | ~16-17 h | ~31 TB (90 GB disk) |

  SSD wear ~8-10% of a 980 PRO's 300 TBW per n=25 campaign. n=26 (96 GB, fits on C:) ~100 TB per
  campaign (~50 TB with --mitm); n=27 needs 274 GB disk and ~300 TB: not realistic.
- **Other options checked and dead:** remote RAM as swap (Windows cannot page to SMB; GbE is ~25x
  slower than NVMe); mitm (the join needs the whole forward vector, so RAM does not drop); 8-bit
  moduli (lcm(1..256) ~ 2^363 < a(24) ~ 2^425; 9-bit 55 runs, 10-bit 48, 12-bit 39); linear
  decomposition of the start vector (every row-boundary state is live from row 5, so sub-sweeps do
  not shrink) - only the occupancy/parity windows above are closed subsets.
- **Where it would plug in (if ever built):** codec region (to ~:833) with a key dimension;
  BuildRightEntries / BuildStraddleTables / BuildAbsLLTables (~1497-1504) per key; sweep loop
  (~1584-1607) gets a window loop; a file-backed chunked Packed12Store (~:376); the deferred array
  must join the partition at mid-row switches; reuse the disk probe + prefetch thread from
  DenseCounter.Ladder.cs. Risks: deferred lock-freedom (invariant 3), race re-testing, SSD wear.
- **Verdict:** buying RAM (2 x 16 GB DDR4-3200 -> 48 GB, if the Z390 board takes it) is the cheapest
  route to n=25; windowed OOC with the parity schedule is the only software route and costs weeks.
  The useful two-machine setup remains independent `--crt-part` runs. Unverified: usable RAM per
  16 GB box (~13 GB assumed), board RAM limit (SMBIOS says 64 GB), the testbox's single SODIMM slot
  (32 GB max, still short of 34 GB).

## Research directions (hypotheses, cheapest evaluation first)

Evaluate every idea at n <= 20 by **state counts and sweep work** before any
engineering; only then prototype.

1. **Meet in the middle / symmetry.** DONE 2026-10-16 (`--mitm`, 1.80x at n=22; see above).
   Original note: sweep to the middle row only and join the
   forward frontier with the backward one; the 180-degree rotation maps the start
   corner to the end corner, so the backward counts may equal the forward counts
   up to a state mapping. Halves the rows; the hard part is the join (the two
   half-frontiers' connections must form one path with no cycle, so it's not a
   plain dot product). Check whether the published record computations used this.
2. **Pruning dead states.** The dense codec includes every noncrossing string,
   including states that can never reach the end corner in the remaining rows
   (e.g. too many open arcs for the rows left, parity constraints). Fewer states
   need a codec that ranks only live states. The hash engine's `--lookahead`
   (Iwashita & Minato TCS-TR-A-13-69 node deletion) is related prior art in this
   repo (`FrontierCounter.CountLookahead`).
3. **Fewer passes per row.** Process two vertices per pass over the array (fused
   transitions) to cut per-state overhead; the in-place/deferred scheme makes this
   non-trivial.
4. **Literature survey** (first step): Iwashita et al. (TCS-TR-A-13-64, and the
   n=26/27 records); Knuth, TAOCP 7.1.4 (SIMPATH / frontier ZDDs); Bousquet-Melou,
   Guttmann, Jensen, "Self-avoiding walks crossing a square" (J. Phys. A, 2005),
   the transfer-matrix work behind A007764 asymptotics; Jensen's transfer-matrix
   pruning for self-avoiding polygons. (Citations from memory; verify.)

## Working rules

- Code is versioned with Fossil: `D:\aplikacje\fossil.exe`, repo
  `D:\projekty\CSharp-BDD\CSharp-BDD.fossil`; commit with `--no-warnings` after each
  verified step.
- Verify with `--selftest`, `--selftest --dense --crt [--bits 12]`, OEIS matches at
  n=17/18, and residue equality against the previous build.
- **Races show up only on repetition:** run `--selftest --dense --crt --bits 12` about
  40 times (and once with `--cores 1` to tell a race from a logic bug). A whole-block
  race (d85d71d243) failed only ~5 % of single runs.
- Any code that touches a block as a whole range (unpack/pack, vector add) must first
  check that the block exists (`BlockOf`). `EncodeL`/`CodeLRAt` of a raw left the codec
  doesn't contain silently returns block 0, which belongs to another group.
- A/B wall-clock with interleaved repeated runs (background load on this machine);
  benchmark with a prime modulus (e.g. 4294967291), not 2^32.
- VTune hardware sampling needs an admin prompt: `vtune_uarch.cmd`.
- Long runs (multi-minute, multi-GB) are launched by the user, not the agent.
