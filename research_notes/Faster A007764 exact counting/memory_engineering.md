# Memory and time engineering for large frontier / transfer-matrix DP (A007764, n=25-26 on a 16 GB, 6-core PC)

Conventions: "n" below follows Iwashita et al. (TCS-TR-A-13-64): an (n+1) x (n+1) vertex grid, i.e. n x n cells. Under this convention the number of "main" frontier states is N_n = M(0,1)(n,0), a generalized Motzkin count. Computed locally: N_24 = 5,850,674,704, which matches the user's "5.85e9 states for n=24" exactly. Main plus blocked states (N_n + N_{n-1}) is 7.91e9 for n=24. At 12 bits that is 11.05 GiB, which matches the user's 11.1 GB. Projections from the same formula (computed locally, not sourced):

| n | main states N_n | main+blocked | 12-bit | 8-bit | 4-bit |
|---|---|---|---|---|---|
| 24 | 5.85e9 | 7.91e9 | 11.0 GiB | 7.4 GiB | 3.7 GiB |
| 25 | 16.63e9 | 22.48e9 | 31.4 GiB | 20.9 GiB | 10.5 GiB |
| 26 | 47.34e9 | 63.96e9 | 89.4 GiB | 59.6 GiB | 29.8 GiB |
| 27 | 135.0e9 | 182.4e9 | 255 GiB | - | - |

Growth is about 2.84x per n. Bit lengths of the path counts, from Iwashita's Table 3 (computed locally): n=23 needs 425 bits, n=24 463 bits, n=25 502 bits, and n=26 about 540 bits by extrapolation (+39 bits per n).

## Q1. Several moduli per sweep (multi-residue) vs one modulus per sweep

### Takeaway
Jensen and Clisby's production transfer-matrix codes ran all CRT moduli in one pass. Three moduli cost about +15% time for 2x memory, which gave an overall 2.6x drop in total CPU time. The trick amortizes state decode, hashing and indexing, which is exactly the overhead that dominates here. For this project it pays only when RAM has room for k residues per state. So it helps for n ≤ 24, or inside an out-of-core/partitioned design; it cannot help n=25 in 16 GB by itself.

### Cited Findings
- Clisby and Jensen, SAP to perimeter 130: moduli m0=2^62, m1=2^62-1, m2=2^62-3 (quoted as "262", "262 - 1" in the text extraction, i.e. 2^62). "We chose to perform the calculation for all mi in the same run. Effectively this doubles the memory requirement but only results in an increase in running time of some 15% (compared to a run using a single mi), that is, an overall decrease in total running time by a factor of about 2.6." Memory was not the constraint there; CPU time was. Resources: about 25,000 CPU hours, up to 1000 cores, up to 2.5 TB RAM. — [Clisby & Jensen, arXiv:1111.5877](https://arxiv.org/pdf/1111.5877)
- Clisby and Jensen, SAW to length 79: two moduli 2^62 and 2^62-1 in the same run. "increased the memory requirement by some 50% but only results in an increase in running time of some 15%". Memory grew by less than 2x because each state also stores a key. The metric-property run used 4 primes just under 2^30, all in a single run, to avoid overflow in multiplications. SAW run: about 16,500 CPU hours, up to 400 cores, 1 TB. — [Clisby & Jensen, arXiv:1309.6709](https://arxiv.org/pdf/1309.6709)
- Iwashita et al. "exchang[ed] space costs for time costs using the Chinese Remainder Theorem in the same way as [5]". They used one 64-bit modulus per run: 9 runs for n=25. — [Iwashita et al., TCS-TR-A-13-64 (2013)](https://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)
- oneesan-gpu (8x B300 GPU frontier DP for the same problem) runs its "required near-2^32 CRT moduli in one process", but sequentially, one residue after another with checkpointing between residues. It does not pack them into one sweep. n=27 needs "20 near-2^32 primes ... product has 640 bits". — [Kicky1618/oneesan-gpu README](https://github.com/Kicky1618/oneesan-gpu)
- Vector modular arithmetic literature: AVX-512 IFMA (VPMADD52) gives a 64-bit accumulator that makes lazy reduction easy in RNS/CRT code. Deferring reductions across sums works as long as the unreduced sum fits in the word. Small-prime Barrett (e.g. 7681, 12289, 65537) is standard in lattice cryptography. Crypto-oriented, and seen only at search-snippet level. — [arXiv:2410.18129](https://arxiv.org/pdf/2410.18129), [IACR ePrint 2025/1068](https://eprint.iacr.org/2025/1068.pdf), [arXiv:1602.01551](https://arxiv.org/pdf/1602.01551)

### Inferences
- The DP inner loop only adds: count[t] += count[s], plus in-place pair updates. It never multiplies, so Montgomery and Barrett are unnecessary. With k residues packed SWAR-style, e.g. 4 x 16-bit lanes in a uint64 or 16 x 16-bit lanes in AVX2, one add plus one conditional subtract per lane is enough. The subtract can be `min(x, x-p)` on unsigned lanes (`vpminuw`). This is a well-known trick, stated here as an engineering inference rather than a sourced claim.
- Cost model: at IPC ~3.1 the engine is decode/rank/branch-bound. If decode and index work is a fraction f of the per-state cost, k residues per sweep cost about 1 + (k-1)(1-f) of a single sweep. Jensen's +15% for 3 residues implies (1-f) ≈ 0.075 in his code. If this engine behaves similarly, 2 residues per sweep would nearly halve CRT wall-time.
- The memory constraint dominates. A 16 GB machine allows about 16 bits per state at n=24 (7.91e9 states), so the practical choices are 1 x 12-bit, 2 x 8-bit or 1 x 16-bit per sweep. At n=25 (22.5e9 states) there are about 5.7 bits per state, so no in-RAM multi-residue layout exists and the technique only helps inside Q3/Q4 designs.
- If "reaching" n=25/26 means reproducing values already in OEIS (Iwashita 2013), each residue run is an independent check against the known value. A few residues would confirm the number with overwhelming confidence, and the full CRT set (about 42 runs of 12 bits at n=25) is needed only for a from-scratch computation.

### Gaps
- No enumerative-combinatorics code was found that packs several small residues into SIMD lanes of one counter array. Jensen and Clisby ran multiple 62-bit moduli per state, but lane-packed small primes were not found in any paper.
- Jensen's polyomino cluster paper (Springer LNCS, 2003, DOI 10.1007/3-540-44863-2_21) was paywalled, and its moduli handling could not be read.

## Q2. Smaller counters (8-bit or less) with more CRT runs; RNS/lazy-reduction tricks

### Takeaway
Pure 8-bit counters cannot reach the required precision. The pairwise-coprime moduli ≤ 256 multiply to at most lcm(1..256) ≈ 2^363, but n=24 needs 463 bits and n=25 needs 502. 8-bit counters therefore have to be mixed with wider ones, and they save only 1/3 of memory relative to 12-bit. That is not enough to fit n=25 (20.9 GiB at 8-bit) in 16 GB.

### Cited Findings
- Bit lengths of the targets, from Iwashita's Table 3 path counts: n=24 is 463 bits and n=25 is 502 bits. — [Iwashita et al. TR](https://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf) (bit lengths computed locally from the printed values)
- Iwashita notes that using point symmetry, i.e. meeting at the middle and matching count(A) x (count(B)+count(C)), "reduces the memory usage by half because integer size can be halved when we only compute numbers for the upper half". The matching step, however, "might be at a disadvantage" in time growth. — [Iwashita et al. TR, §6.2](https://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)

### Inferences
- Coprime-modulus capacity by counter width, computed locally with log2 of lcm(1..2^b): 4-bit gives about 20 bits total, 8-bit 363 bits, 12-bit 5925 bits. Only widths of 9 bits or more can cover 500+ bits alone. A mixed plan would be the 54 primes < 256 (or prime powers), which give about 363 bits, plus about 12 runs at 12 bits (about 140 bits) for n=25.
- 8-bit counters also buy speed: byte-aligned loads and stores, no 12-bit nibble shifts, and twice the lanes per SIMD op compared with 16-bit. The speed gain, not the memory saving, may be the better argument for 8-bit at n ≤ 24.
- Point symmetry and modular arithmetic: with residues, the "halve the integer size" advantage vanishes, because the answer mod p still needs a full CRT modulus larger than the answer. The time advantage remains: sweep only to the middle row, then do a matching pass. The peak state count is at the middle, so peak memory is unchanged. Whether the current engine already stops at the midpoint is unknown here.

### Gaps
- No published measurement of 8-bit or sub-byte counter throughput in frontier DP was found.

## Q3. External-memory / out-of-core transfer matrix (SSD streaming)

### Takeaway
There is a working precedent. oneesan-gpu has an mmap external-store backend for exactly this problem, used for 64-bit residues with about 3.79 TiB of state files at n=26. Delayed duplicate detection (Korf) is the classic pattern: append, then sort or bucket, then merge sequentially. With a naive "one full read+write per vertex step" the I/O and SSD wear are prohibitive. With Jensen-style windowing, about 2-3 full passes per row instead of n, n=25 becomes I/O-feasible on an NVMe SSD (my estimate).

### Cited Findings
- oneesan-gpu's external backend "keeps the authoritative state in mmap files instead of sharding it entirely in HBM" and "accepts a 64-bit modulus". It uses crash-safe per-group undo journals and a durable completion bitmap, preallocates with posix_fallocate(), and recommends local NVMe/RAID. For n=26, "two 64-bit-residue external state files total roughly 3.79 TiB". No timings are reported. — [oneesan-gpu](https://github.com/Kicky1618/oneesan-gpu)
- Korf's delayed duplicate detection appends generated nodes to a file, sorts it, and removes duplicates in a single sequential scan. This avoids random disk access, and it "also speeds up searches that fit entirely in memory, by improving cache performance". The method gave the first complete BFS of the 2x7 sliding-tile puzzle and the 18-disk 4-peg Towers of Hanoi. — [Korf, IJCAI 2003 (ACM DL)](https://dl.acm.org/doi/10.5555/1630659.1630926); related: [Structured duplicate detection, Zhou & Hansen AAAI 2004](https://cdn.aaai.org/AAAI/2004/AAAI04-108.pdf); [External-memory BFS with DDD on the GPU (Springer)](https://link.springer.com/chapter/10.1007/978-3-642-20674-0_2)
- Recent work: Suzuki & Fukunaga, "Evaluation of Baseline Methods for IDD-based SSD External Memory Search" (SoCS 2026), studies how the OS page cache affects SSD-based search. Only the abstract was read, so no numbers are available. — [arXiv:2606.01840](https://arxiv.org/abs/2606.01840)

### Inferences (back-of-envelope, not sourced)
- The naive approach does one full in-place pass per vertex step. At n=25 12-bit, 31.4 GiB x 2 (read+write) x about 625 vertex steps is about 42 TB of I/O per residue, roughly 3.9 h at 3 GB/s. It writes about 21 TB per residue, about 880 TB over about 42 residues, which exceeds the TBW rating of typical consumer 1-2 TB NVMe drives (hundreds of TBW; general knowledge, unverified). Not viable.
- Windowed approach (see Q4): group states by the occupancy pattern outside a window of w frontier positions, and run w-1 consecutive vertex steps per group entirely in RAM. At about 2-3 redistributions per row (Jensen's figure), I/O is about 25 x 3 x 63 GB, or about 4.7 TB per residue: about 26 min at 3 GB/s, and about 100 TB written over the CRT set. Compute for n=25 is about 2.84 x 40 min ≈ 1.9 h per residue at the current rate. I/O would then be a minority cost, provided the redistribution itself (a bucket/sort by the new window) runs near sequential bandwidth.
- mmap with random access is the wrong model on 16 GB RAM, because of page-cache thrashing (cf. Suzuki & Fukunaga's topic). Explicit sequential streaming with large aligned buffers and double-buffering is preferred. This is an inference.

### Gaps
- No published out-of-core CPU transfer-matrix or frontier-DP runs with reported SSD throughput were found. oneesan-gpu reports no external-backend timing.

## Q4. Splitting the state space so each piece fits in RAM

### Takeaway
Both Jensen and Iwashita use the same invariant: a vertex update changes the occupancy (empty vs occupied) of only the positions being updated. Far positions may flip "(" to ")" but never become empty or occupied. Partitioning by the occupancy bits of all other positions therefore gives groups with zero cross-traffic for that step. Jensen extends the idea to a window, so that only 2-3 redistributions per added column are needed.

### Cited Findings
- Jensen and Clisby: "any edge not directly involved in the update cannot change from being empty to being occupied and vice versa... This invariant allows us to parallelise the algorithm in such a way that we can do the calculation completely independently on each processor with just two redistributions of the data set each time an extra column is added". With about 1000 processors they did 3 redistributions per column, to lengthen the invariant part for load balance. Redistribution took about 15% of CPU time, and MPI transfer itself took under 5%. — [arXiv:1111.5877](https://arxiv.org/pdf/1111.5877), same numbers in [arXiv:1309.6709](https://arxiv.org/pdf/1309.6709)
- Iwashita et al. §4.4: "except for modification of c_j and c_{j+1}, at most one position can be changed" (between the two paren types). They partition the states into 2^(n-2) groups "across which no transition occurs", based on the (n-2)-bit occupancy code of all positions except j and j+1. The leftmost m ≈ n/2 bits of that code are used for task allocation, "a convenient choice in combination with" the left-half/right-half state enumeration. — [Iwashita et al. TR](https://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)
- oneesan-gpu uses a "checkerboard-strip upper bound with row partition [9, 9, 9]" and GRIDFP_BOUNDED_PREFIX_K=6, which initializes the state directly after six rows. It shards states over 8 GPUs with full NVLink P2P, "peers=56". The exact semantics are not documented in the README. — [oneesan-gpu](https://github.com/Kicky1618/oneesan-gpu)

### Inferences
- Does the dense left+right rank allow partitioning by left-half prefix? Partly. The Iwashita index is base_h + (i_l-1) x |R_h| + i_r, so all states that share a left half l are contiguous (one block of |R_h| entries). Updates at positions j, j+1 inside the right half only touch the same l-block, apart from the rare far paren flip, which may cross into the left half. Updates inside the left half move between l-blocks but stay within the same right half. A two-phase scheme therefore follows the natural layout: process the right-half columns with the array chunked by l, then re-layout (transpose to r-major) for the left-half columns. That is roughly 2 redistributions per row, which matches Jensen's count. The paren-flip cross-traffic must be handled: the flipped position's occupancy stays the same, so an occupancy-keyed grouping remains closed, but a pure l-prefix grouping does not unless l encodes occupancy only.
- A cleaner out-of-core layout is to rank as (occupancy pattern outside the window) → group offset, then rank within the group. Within a group the far occupied positions have fixed positions and only the matching structure varies (Catalan-type counts per arc count), so groups can be sized to fit RAM.

### Gaps
- Jensen's 2003 polyomino-cluster paper (details of the parallel data decomposition) could not be read (paywall). The description above comes from the Clisby-Jensen summaries that cite it.

## Q5. Ranking and unranking of Motzkin / noncrossing states

### Takeaway
Iwashita's minimal perfect hash splits the 2n-bit code into left and right halves plus a height-h offset table. Both lookup tables are indexed directly by subcode, total size about 2^(n+1) entries, which is small relative to the count array. States are visited by a double loop over sorted left/right arrays, with no stored state list.

### Cited Findings
- φ(s) = L(l) + R(r), where L(l) = base_h + (i_l - 1) x M(m,h)(n,0), R(r) = i_r, and base_{h+1} = base_h + M(0,1)(m,h) x M(m,h)(n,0). States are 2-bit coded (empty=00, "("=01, ")"=10). "The total size of the two arrays is about 2^(n+1) and is small enough." Visiting states via the lstate x rstate_h double loop costs "almost the same" as a single sorted array but uses far less memory. In-place update (no tmp array) halves memory. — [Iwashita et al. TR §3-4](https://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)
- Measured gains: compared with their general-graph ZDD frontier code, "space and time efficiency has been improved five times and ten times respectively" sequentially, with "one another digit" of time from 12-core parallelism. Examples: n=20 took 61,570 s with the original code and 652 s with GGCount-PF. For n=25 (their convention): 9 runs x 18 h on 30 cores, 480 GB each, 64-bit moduli. n=26: a single run took 2 days and 1400 GB, and they estimated "three weeks" for the exact answer. — [Iwashita et al. TR, Tables 1-2, §5](https://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf); code: [kunisura/GGCount](https://github.com/kunisura/GGCount) (OpenMP; bignum or %modulus up to 64-bit)
- OEIS attribution: Iwashita computed terms 21, 26, 27 (2012-2013); Spaans computed 23-25 (Feb 2013) with "state space compression (with rank/unrank) and dynamic programming". — [OEIS A007764](https://oeis.org/A007764)

### Inferences
- The user's engine is already this design. Remaining gains lie in the decode/transition cost per state (IPC 3.1, instruction-bound): table-driven transitions keyed on the (c_j, c_{j+1}) pair and processed a whole r-block at a time, so that the inner loop over rstate_h is branch-light and vectorizable.

### Gaps
- Knuth's SIMPPATH-family programs were not checked in this pass. Spaans' implementation (thesis/code) was not located.

## Q6. Compression of count vectors

### Takeaway
Residues mod p are close to uniform, so entropy coding gains nothing on live states. The only compressible structure is identically-zero or unreachable states. Iwashita's exact state domain (a minimal perfect hash with no slack) already removes those.

### Cited Findings
- Iwashita's array holds exactly N_n + N_{n-1} entries (a minimal perfect hash), i.e. no empty slots. — [Iwashita et al. TR §3](https://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)

### Inferences
- Bit-packing at the counter width (12 bits) is the only "compression" worth doing, and the engine already does it.

### Gaps
- No measurements were sought or found.

## Q7. GPU implementations

### Takeaway
A dedicated GPU frontier-DP code exists for this exact sequence, oneesan-gpu, targeting 8 x NVIDIA B300. It stores uint32 residues. The n=27 state is 1939.9 GiB, which matches main+blocked = 520.7e9 states x 4 B (verified locally). On consumer GPUs only small n fit: n=18 runs in about 3 s on an RTX 3070 8 GB. For n ≥ 24 the state far exceeds consumer VRAM, so a GPU would need the Q3/Q4 partitioning to stream groups over PCIe.

### Cited Findings
- "the authoritative uint32 state is about 1939.9 GiB total, or about 242.5 GiB per GPU on 8 GPUs" (n=27). n=18 single GPU: about 169 MiB of state, "completes in about 3 seconds" on an RTX 3070. The README gives no wall-times for n=20-27. — [oneesan-gpu](https://github.com/Kicky1618/oneesan-gpu)
- GPU external-memory BFS with delayed duplicate detection exists in the heuristic-search literature (Edelkamp et al.). — [Springer LNCS chapter](https://link.springer.com/chapter/10.1007/978-3-642-20674-0_2) (abstract level only)

### Inferences
- A consumer GPU such as a 16 GB card at about 500-1000 GB/s VRAM bandwidth could process one Q4 group at a time. PCIe 4.0 x16 (about 25 GB/s practical) would, however, carry the full array 2-3 times per row: at n=25, 12-bit, about 25 x 3 x 63 GB / 25 GB/s ≈ 3.2 min of transfer per residue. This could beat the CPU's about 1.9 h estimate if the kernels are efficient. Unverified estimate.

### Gaps
- No published speedup numbers for GPU transfer-matrix lattice enumeration were found, and no GPU port of the Clisby-Jensen codes was found.

## Q8. Recent frontier-based search engineering (2013-2026)

### Takeaway
The main engineering line is Hokkaido/Minato: GGCount, TdZdd, and "Shared-Memory Parallel Frontier-Based Search" (WALCOM 2013). Most post-2015 work targets new constraint classes (colorful FBS, ZSDDs, graph partitions), not raw scale. The newest scale effort found is oneesan-gpu.

### Cited Findings
- "Shared-Memory Parallel Frontier-Based Search", WALCOM 2013, LNCS 7748 (DOI 10.1007/978-3-642-36065-7_17). The Springer page was not accessible, so the authors and speedup numbers are unverified. — [Springer](https://link.springer.com/chapter/10.1007/978-3-642-36065-7_17)
- Iwashita & Minato, "Efficient top-down ZDD construction techniques using recursive specifications", TCS-TR-A-13-69 (2013). Kawahara, Inoue, Iwashita, Minato, "Frontier-based search for enumerating all constrained subgraphs with compressed representation", IEICE Trans. Fundamentals 2017. Both are from search-result listings and were not read. — [Hokkaido lab publications](https://www-alg.ist.hokudai.ac.jp/pub.html)
- Later FBS variants: Colorful FBS (chordal/interval subgraphs), [Springer](https://link.springer.com/chapter/10.1007/978-3-030-34029-2_9); graph partitions with ZDD/ZTDD, [SEA 2018](https://drops.dagstuhl.de/entities/document/10.4230/LIPIcs.SEA.2018.21); ZSDD subgraph enumeration, [SEA 2020](https://drops.dagstuhl.de/entities/document/10.4230/LIPIcs.SEA.2020.9).

### Inferences
- For raw counting on grids, the specialized dense-array approach (Iwashita 2013) dominates generic ZDD/TdZdd: 5x space and 10x time over the general-graph code. Generic FBS libraries are unlikely to beat the current engine.

### Gaps
- TdZdd and Graphillion internals were not examined. No 2018-2026 paper on scaling frontier DP to more than 1e10 states on CPUs was found.
