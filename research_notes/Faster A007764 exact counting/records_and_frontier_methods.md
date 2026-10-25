# Record computations of OEIS A007764 and the frontier/ZDD methods behind them

Scratch copies of the primary PDFs used (text-extracted) were read in full: TCS-TR-A-12-60, TCS-TR-A-13-64, TCS-TR-A-13-69, arXiv:cond-mat/0506341 (BGJ 2005), arXiv:2208.06744 (Guttmann & Jensen 2022), Spaans' `A007764-fast.c`, Knuth's `simpath.w`.

## Q0. Indexing (confirm before comparing numbers)

### Takeaway
OEIS A007764(n) counts paths between opposite corners of an **n x n vertex** grid (n-1 x n-1 cells), offset 1, a(2)=2, a(3)=12. Every paper uses a different index: GGCount/TR 13-64 and Guttmann-Jensen use n or L = **number of cells per side** (so their n = OEIS n-1); Spaans' program uses the cell/"rook" dual. The dense-state count 5.85e9 we quote for n=24 is exactly M25 - M24, i.e. "main states" for a 24-vertex-wide frontier, so our n = OEIS n.

### Cited Findings
- OEIS name: "Number of nonintersecting (or self-avoiding) rook paths joining opposite corners of an n X n grid", %O 1,2; example a(2)=2 (UR, RU), a(3)=12 — [OEIS A007764 (text format)](https://oeis.org/search?q=id:A007764&fmt=text)
- TR 13-64 counts "paths connecting opposite corners of a (n+1)x(n+1) grid graph"; its Table 3 lists n=1 -> 2, n=25 -> the value OEIS gives for a(26) — [TCS-TR-A-13-64](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)
- Guttmann & Jensen: "Note that the listing in the OEIS runs from 1 to 27, which in our notation is L = 0 to 26" — [arXiv:2208.06744](https://arxiv.org/abs/2208.06744)
- Spaans' program: "when solving A007764, invoke with n+1,n+1"; comments say "n=24 required around 700 GB" in his own index (edge-grid n x n = OEIS n+1) — [Spaans A007764-fast.c](https://github.com/stubbscroll/OEIS/blob/master/A007764-fast.c)

### Inferences
- Frontier-state arithmetic (my computation from the Motzkin recurrence; w = vertices per row = OEIS n):
  | OEIS n (w) | main states M(w+1)-M(w) | GGCount main+blocked M(w+1)-M(w-1) | main @12-bit | bits of a(n) |
  |---|---|---|---|---|
  | 24 | 5.851e9 | 7.914e9 | 8.8 GB | 425 |
  | 25 | 1.663e10 | 2.248e10 | 24.9 GB | 463 |
  | 26 | 4.734e10 | 6.396e10 | 71.0 GB | 502 |
  | 27 | 1.350e11 | 1.824e11 | 202.5 GB | 543 |
  | 28 | 3.857e11 | 5.207e11 | 578.6 GB | ? |
  Growth ratio of main states is ~2.84-2.86x per n. It tends toward 3.
- These numbers match the published memory figures, which confirms the indexing. GGCount's 480 GB/run for OEIS 26 = 6.40e10 x 8 B (= 477 GiB). Its 1400 GB for OEIS 27 = 1.82e11 x 8 B (= 1359 GiB). Its "413 GB for a 24x24 graph with 56-byte integers" = 7.91e9 x 56 B.
- GGCount stores main + blocked states. Blocked states carry a special symbol at column j, so their total is 1.35x the main count. Our 5.85e9 figure (main only) suggests our encoding already avoids the blocked set, or handles it without storing it.

### Gaps
- None for indexing.

## Q1. Who computed which terms, when, on what hardware, at what cost

### Takeaway
All terms from n=21 to n=27 were computed in 2012-2013 with frontier (transfer-matrix) DP over noncrossing-parenthesis states. They ran on large shared-memory machines with 64-bit modular counters and CRT. Nothing beyond n=27 has been published: the OEIS b-file still ends at 27 (entry last edited Apr 16 2025), and Guttmann-Jensen (2022) stopped at the same size. The n=27 record used ~1.4 TB of RAM per modular run.

### Cited Findings
- OEIS history lines: computed to n=12 by John Van Rosendale (1981); n=13 by Knuth (Dec 07 1995); "Extended to n=20 by Mireille Bousquet-Mélou, A. J. Guttmann and I. Jensen"; "Extended to n=22 using ZDD technique based on Knuth's TAOCP (exercise 225 in 7.1.4) by H. Iwashita, J. Kawahara, and S. Minato, Sep 18 2012"; "Extended to n=25 using state space compression (with rank/unrank) and dynamic programming (based in I. Jensen) by Ruben Grønning Spaans, Feb 22 2013"; "Extended to n=26 by Hiroaki Iwashita, Apr 11 2013"; "Extended to n=27 by Hiroaki Iwashita, Nov 18 2013". The b-file covers n=1..27, and the entry revision is #143, Apr 16 2025 — [OEIS A007764](https://oeis.org/search?q=id:A007764&fmt=text), [b007764.txt](https://oeis.org/A007764/b007764.txt)
- **n <= 20, BGJ 2005:** transfer matrix following Conway-Enting-Guttmann, with modular arithmetic using primes p = 2^30 - r (r < 1000) and CRT. "Each run for a 19 x 19 lattice took about 72 hours using 8 processors of a multiprocessor 1 GHz Compaq Alpha computer. Ten such runs were needed". Here 19x19 is in cells, so OEIS n=20. Published as J. Phys. A 38 (2005) 9159-9181 — [arXiv:cond-mat/0506341](https://arxiv.org/abs/cond-mat/0506341)
- **n = 21, 22, Iwashita-Kawahara-Minato, TCS-TR-A-12-60 (Sep 18 2012):**
  - Method: Knuth's SIMPATH re-implemented to count directly without building the ZDD ("direct method"), for general graphs, single-threaded C++.
  - Hardware: Xeon E7-8837 2.67 GHz, 1 TB RAM.
  - G22,22 (OEIS 22): 284,117 s (~79 h) and 216,871 MB. G21,21: 91,944 s and 74,504 MB.
  - Their projection for G23,23 was "about 10 days and 700 gigabytes"; time and memory roughly triple per size step.
  - Source: [TCS-TR-A-12-60](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_12_60/tcstr_12_60.pdf)
- **n = 23, 24, 25, Ruben Spaans (Sep-Oct 2012; OEIS dated Feb 22 2013):**
  - Resources: "around a CPU-month on a machine with 4 eight-core CPUs and 1 TB RAM" at NTNU (Q2S); OEIS 25 "required around 700 GB".
  - Arithmetic: modulo primes just below 2^63, with CRT done externally.
  - Parallelism: pthreads with 30 threads gave a "22x speedup".
  - He noted that "a paper was under preparation, but has been on hold".
  - Source: [Spaans A007764-fast.c](https://github.com/stubbscroll/OEIS/blob/master/A007764-fast.c)
- **n = 26, Iwashita et al., GGCount-PF, TCS-TR-A-13-64 (Apr 26 2013):**
  - Per run: "9 runs with coprime moduli, of which each run took 18 hours on 30 CPU cores and used 480 gigabytes of memory".
  - Arithmetic: 64-bit modular arithmetic.
  - Hardware: Xeon E7-8837 2.67 GHz, 1.5 TB, SUSE.
  - The same runs also confirmed Spaans' n=23-25.
  - Source: [TCS-TR-A-13-64](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)
- **n = 27, Iwashita (Nov 18 2013):** TR 13-64 already reports "a successful run for n = 26 [cells; = OEIS 27] on the same machine using 2 days and 1400 gigabytes; it will take three weeks to get the exact answer" — [TCS-TR-A-13-64](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)
- **2022, Guttmann & Jensen:**
  - What they did: re-implemented the Iwashita et al. scheme (MPH plus in-place updates, OpenMP) and computed the square-lattice series "to lattice size L = 26" (= OEIS 27).
  - Scope: this covers polygons crossing a square and walks spanning a square. The walks-crossing count itself is credited to Iwashita/Spaans.
  - Beyond L = 26 they only produced *approximate* extra terms by "series extension".
  - Published as J. Phys. A: Math. Theor. 55 485201 (2022).
  - Source: [arXiv:2208.06744](https://arxiv.org/abs/2208.06744)
- GGCount single-machine timings (TR 13-64, Table 2, 12 cores, full 56-byte integers, no CRT; OEIS n = TR n + 1):

  | OEIS n | GGCount-PF time | memory |
  |---|---|---|
  | 22 | 3,244 s | 52,723 MB |
  | 23 | 5,695 s | 149,108 MB |
  | 24 | 21,313 s (5.9 h) | 422,634 MB |

  For OEIS 24, GGCount-SF sequential took 170,476 s and GGCount-SB sequential 554,160 s. The original general-graph code took more than a week — [TCS-TR-A-13-64](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)

### Inferences
- Cost of the record runs in core-hours:
  - OEIS 26: 9 x 18 h x 30 cores ≈ 4,900 core-h, at 480 GB/run.
  - OEIS 27: about 9 runs x 2 days x 30 cores ≈ 13,000 core-h, at 1.4 TB/run (my estimate from "three weeks").
  - Nine 64-bit moduli give ~567 bits, enough for a(27) (543 bits). That fits the "9 runs" figure.
- Our setup at OEIS 24 uses 11 GB, ~40 runs and ~1 day on 6 cores. That is ~40x less memory than GGCount's 423 GB, paid for with about 4-5x more runs. Per state-update our throughput is not obviously behind theirs.
- For OEIS 25 at 12 bits we would need ~25 GB (main states only), plus about 42 runs.
- None of the record holders used a distributed-memory or out-of-core method. All of them fit the whole count array in one machine's RAM.

### Gaps
- No write-up gives Nov-2013 run details for n=27 (actual number of moduli, wall time, machine). Only the TR's projection exists.
- Spaans' per-n timings are not published beyond "around a CPU-month" total.
- No evidence of anyone computing n=28 as of this search (Sept 2026). The OEIS b-file still ends at 27. A web search found nothing, but absence is not proven.

## Q2. GGCount (TCS-TR-A-13-64) internals, later versions, follow-ups

### Takeaway
GGCount's structure:
- **State model:** a per-vertex frontier over a 4-letter alphabet (empty, left end, right end, and a special "blocked" symbol at column j). State count is M(n+1) - M(n-1).
- **Ranking:** a minimal perfect hash (MPH) that splits each state into left and right halves by Motzkin height h, rank = base_h + i_L x |right set| + i_R, with two small lookup tables.
- **In-place update:** achieved by choosing hashes so that sources and targets share slots.
- **Parallelism:** by the invariant occupancy pattern.
- **Arithmetic:** 64-bit modular counters plus CRT.

No journal version was found. A conference talk (JCDCG^2 2013) exists.

### Cited Findings
- The frontier at vertex v(i,j) is {v(i,1..j), v(i-1,j+1..n)}. Vertical edges are forced, so branching happens only on horizontal edges. The endpoint connected to v(1,1) is always coded as a right endpoint — [TCS-TR-A-13-64](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)
- State counts: main states N_n = M(n+1) - M(n) (Motzkin paths from height 1 to 0); blocked states N_(n-1) = M(n) - M(n-1); total M(n+1) - M(n-1) — [TCS-TR-A-13-64](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)
- MPH (the "L + R" hash):
  - Rank = L(l) + R(r), with l the left m = floor(n/2) characters and r the rest, ordered by h.
  - base_(h+1) = base_h + |M(0,1)(m,h)| x |M(m,h)(n,0)|; L(l) = base_h + (i_l - 1) x |M(m,h)(n,0)|; R(r) = i_r.
  - States are 2-bit coded, and both halves index flat arrays directly, "about 2^(n+1)" entries in total.
  - Source: [TCS-TR-A-13-64](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)
- Enumeration is a double loop over `lstate` (l, h) and `rstate_h`, running in lexicographic order. It uses little memory and is about as fast as scanning a stored state array — [TCS-TR-A-13-64](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)
- In-place update: the hash functions satisfy φ_j(s) = φ_(j+1)(s) for main states, plus a shifted-blocked identity. Mutually dependent pairs are updated simultaneously. This removes the tmp array and halves memory — [TCS-TR-A-13-64](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)
- Parallelism:
  - A transition changes c_j and c_(j+1), plus at most one other symbol (a paren relabel). So the occupancy code g(c) (0 = empty, 1 = occupied) of the other n-2 positions partitions states into 2^(n-2) independent groups.
  - Tasks are assigned by the leftmost ~n/2 bits.
  - GGCount-PF used 12 cores, giving "one another digit" of speedup over sequential.
  - Source: [TCS-TR-A-13-64](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)
- Measured gains (Tables 1-2): versus the general SIMPATH-based counter, GGCount-SF is ~5x better in space and ~10x in time. SF was 3x faster than SB at slightly more memory — [TCS-TR-A-13-64](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)
- Counter width: 56-byte (448-bit) bignums in the benchmark tables. The records used "a 64-bit modular arithmetic version of GGCount-PF", following BGJ's CRT idea — [TCS-TR-A-13-64](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)
- A cycle-count variant (A140517) is also given. For n=25 cells it used 9 moduli, each run 9 h on 30 cores at 260 GB — [TCS-TR-A-13-64](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)
- A conference version exists: Iwashita, Nakazawa, Kawahara, Uno, Minato, "Fast computation of the number of paths in a grid graph", JCDCG^2 2013 (Sept 17-19, 2013). It is listed on Kawahara's publication page — [Kawahara publications](https://www.lab2.kuis.kyoto-u.ac.jp/jkawahara/publications.html)
- Follow-up TCS-TR-A-13-69 (Iwashita & Minato, Dec 17 2013), "Efficient Top-Down ZDD Construction Techniques Using Recursive Specifications":
  - Main ideas: "recursive specifications", LOOKAHEAD (skips configurations whose 1-child goes straight to 0, i.e. applies the ZDD zero-suppress rule on the fly), and "subsetting" with a Degree specification (a two-pass 2P method).
  - Speed and size: 1.1-2.9x speedup on ZDD construction, and 40-50% smaller peak ZDD.
  - G18,18 path ZDD: 6,398 s / 206 GB (1P) versus 2,274 s / 121 GB (2P+L).
  - These gains are for building ZDDs (node counts), not for counting-only DP.
  - Source: [TCS-TR-A-13-69](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_69/tcstr_13_69.pdf)
- TR 12-60 ("ZDD-Based Computation of the Number of Paths in a Graph"):
  - Its key trick is the "direct method": nodes and bignums are deleted as soon as their value is propagated, so memory is O(max level width) instead of the whole DAG.
  - They found "memory access has a significant impact on CPU time… Fine tuning of memory access have made our software several times faster."
  - Source: [TCS-TR-A-12-60](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_12_60/tcstr_12_60.pdf)
- On symmetry and meet-in-the-middle, TR 13-64 §6:
  - **Line (diagonal) symmetry:** doubling one first-step case "would not contribute to much computational reduction because the two cases are merged soon". Diagonal frontiers, which could exploit the symmetry fully, have faster state growth (their Fig. 8).
  - **Point (180°) symmetry:** a meet-in-the-middle scheme. Stop at mid-grid, then sum count(A) x (count(B) + count(C)) over matching main states, with B and C being the complement half turned over.
  - Effect: this "reduces the memory usage by half because integer size can be halved". But "Time growth of the matching process against n was larger than that of the basic algorithm in our preliminary experiments", so it was not used for the records.
  - Source: [TCS-TR-A-13-64](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)

### Inferences
- Under modular arithmetic, point-symmetry halving buys nothing on memory, because counter width is already set by the modulus. Its only benefit would be sweeping half the rows. The matching step is a sum over pairs of compatible half-states (a bilinear form per state class). If it could be organised as a sum over the state space, i.e. O(|states|) per modulus rather than a join, it could roughly halve the time per run. The TR only reports "preliminary" negative results, so this remains an open, testable idea for us.
- The occupancy-invariant partition (§4.4) also defines independent sub-arrays for each sweep step. That could support blocking for cache/NUMA, or out-of-core streaming, on memory-bound hardware.

### Gaps
- No journal version of TR 13-64 was found. researchmap lists Iwashita's papers only from 2016 on, and dblp returned an access-denied page. The content of the JCDCG^2 2013 talk and abstract was not retrieved.
- The TR does not give the per-step read/write access pattern or a bandwidth analysis.

## Q3. Methods of the other record holders (BGJ/Jensen, Spaans, Guttmann-Jensen 2022)

### Takeaway
Jensen's lineage uses a cell/edge-crossing transfer matrix with a "kink" in the boundary line. Before 2022 it used hash tables and distributed MPI. Spaans replaced the hash table with rank/unrank over Motzkin-type states (two arrays, striped mutexes, diagonal-symmetry halving). Guttmann-Jensen 2022 adopted Iwashita's MPH and in-place updates, and added the "free end = excess ')'" encoding. None of them used meet-in-the-middle, pruning of non-completable states, or disk/distributed memory for these records.

### Cited Findings
- BGJ 2005:
  - Encoding: signature symbols 0 (empty), 1 (free end), 2/3 (lower and upper arc ends).
  - Complexity: time and memory are ∝ the maximal number of boundary configurations, ~3^M.
  - Arithmetic: primes 2^30 - r and CRT.
  - Their fugacity (length-polynomial) version drops odd/even zero terms and terms below n_min. That is irrelevant to plain counting.
  - Source: [arXiv:cond-mat/0506341](https://arxiv.org/abs/cond-mat/0506341)
- Spaans:
  - Counters: 64-bit (primes < 2^63), with a compile option `BITS16` of 16-bit counters for "4 times as much memory" savings at about 4x less speed.
  - Arrays: two arrays (`prev` and `cur`), so no in-place update.
  - Concurrency: 2^21 striped pthread mutexes around each add.
  - State encoding: states ranked and unranked from a DP table ("the sequence dp[1][0][n] is actually A002026").
  - Symmetry: "force starting edge to go down. the exact number of paths is twice the answer we get".
  - Source: [Spaans A007764-fast.c](https://github.com/stubbscroll/OEIS/blob/master/A007764-fast.c)
- Guttmann & Jensen 2022:
  - Free end: the free end can never be enclosed by an arc, so it is treated as an "excess upper arc end". The state set is then Motzkin paths from height 1 to 0, |states| = M(L+2) - M(L+1) (A002026), with no separate free-end symbol.
  - Encoding: 2-bit code (empty = 00, ( = 10, ) = 01) in a 64-bit word. The halves are 32-bit, and the lookup tables total about 2^(L+2) entries.
  - Arithmetic: primes of the form 2^62 - r, stored in 64-bit ints.
  - Source: [arXiv:2208.06744](https://arxiv.org/abs/2208.06744)
- Guttmann & Jensen 2022, in-place and parallel details:
  - In-place updating is safe only for certain divider positions. They use three sub-sweeps per column with dividers l_t, l_b = l_t - 1, then l_t again, all strictly below l_h. Dividers below l_h were "faster" "because memory access is crucial".
  - OpenMP parallelism uses the occupancy invariant: split on the lower half's occupancy pattern, sweep the top half, then redistribute by the upper-half pattern.
  - Earlier Jensen codes used MPI on distributed memory.
  - Code is at github.com/IwanJensen/Self-avoiding-walks-and-polygons (not inspected).
  - Source: [arXiv:2208.06744](https://arxiv.org/abs/2208.06744)
- BGJ (2005) and Guttmann-Jensen (2022) give no runtime or memory for the 2022 runs — [arXiv:2208.06744](https://arxiv.org/abs/2208.06744)

### Inferences
- **Our encoding is already the most compact one.** Spaans' state count per array for OEIS 25 is consistent with M(27) - M(26) = 4.73e10: 4.73e10 x 8 B x 2 arrays ≈ 757 GB, which matches his "around 700 GB". That is the classic kink frontier with w+1 positions. The GJ/"excess )" form, and our 5.85e9 at OEIS 24, is the w-position main-state set, about 2.85x smaller per array. GGCount's main+blocked form sits in between at 1.35x main. So nobody in the record lineage used a smaller state space than we do. The record holders simply had 0.5-1.5 TB of RAM.
- The "pruning" that matters in Jensen's other work (SAP/SAW series truncated at a fixed length) does not apply to x = 1 total counts. No record computation pruned states as "cannot complete". In the square-grid frontier nearly all noncrossing states stay completable until the last rows. This is unverified; it would need to be measured by us.

### Gaps
- The 2022 GitHub code was not inspected for its square-lattice variant, e.g. whether it stores blocked states.
- The number of CRT runs and timings for the 2022 L=26 computations are not reported.

## Q4. Knuth: SIMPATH, TAOCP 7.1.4 and related programs

### Takeaway
SIMPATH (August 2008) builds an unreduced ZDD of all s-t simple paths using a byte-per-vertex `mate` table in a queue with a hash table. Its lasting tricks are the virtual source-target edge (so a path becomes one cycle) and discarding states that leave dangling ends. It has no grid-specific ranking and no counting-only mode. Counting-only was Iwashita's 2012 adaptation.

### Cited Findings
- Knuth's programs page lists: SIMPATH, SIMPATH-REDUCE, SIMPATH-EXAMPLE, SIMPATH-CYCLES, SIMPATH-HAM, SIMPATH-HAM-ANY and SIMPATH-DIRECTED-CYCLES (all August 2008). GRAYSPAN, SPSPAN, GRAYSPSPAN and SPGRAPH (August 2003) cover spanning trees — [Knuth programs](https://www-cs-faculty.stanford.edu/~knuth/programs.html)
- simpath.w state encoding: `mate[t]=t` means untouched, `mate[t]=u` means path-connected to u, and `mate[t]=0` means saturated. Workspace `mem[1<<27]` is a wrap-around queue, and `htable[1<<25]` is time-stamped — [simpath.w](https://www-cs-faculty.stanford.edu/~knuth/programs/simpath.w)
- The "slick?" trick: "imagine that an edge between the source and target has already been included. Then the final arc of a simple path will be an arc that completes a cycle" — [simpath.w](https://www-cs-faculty.stanford.edu/~knuth/programs/simpath.w)
- Pruning in `printstate`: when vertices leave the frontier, "incomplete junk mustn't be left hanging" (dangling path ends at exiting vertices make the branch go to 0). Removing the `mate[t]!=t` test restricts it to Hamiltonian paths — [simpath.w](https://www-cs-faculty.stanford.edu/~knuth/programs/simpath.w)
- OEIS and TR 13-64 identify the algorithm with TAOCP Vol. 4A §7.1.4, exercise 225 — [OEIS A007764](https://oeis.org/search?q=id:A007764&fmt=text); [TCS-TR-A-13-64](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)
- TR 13-69 timed Knuth's SIMPATH (main plus reduce): G18,18 took 4,935 s + 4,864 s. The "1P" re-implementation took 6,399 s — [TCS-TR-A-13-69](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_69/tcstr_13_69.pdf). Note: the table's row alignment in the PDF text is garbled, so the per-row mapping is uncertain.

### Inferences
- Knuth's virtual-edge trick is equivalent to GJ's "excess )" free-end encoding. We already rely on the same state set.

### Gaps
- The text of TAOCP exercise 225 and its answer, and any neighbouring exercises on grid-path counts or asymptotics, could not be accessed online. Nor could Knuth's "Things a Computer Scientist Rarely Talks About" (pp. 27-28, cited by OEIS). Whether they contain counting-only or symmetry tricks is **unverified**.

## Q5. Kawahara et al. frontier-based search (IEICE 2017) and other ZDD literature

### Takeaway
Kawahara, Inoue, Iwashita & Minato generalise SIMPATH into "frontier-based search" for constrained subgraphs with ZDD output. As far as could be verified (abstract only), it contributes generality, not grid-specific state reduction relevant to counting A007764.

### Cited Findings
- Citation: J. Kawahara, T. Inoue, H. Iwashita, S. Minato, "Frontier-Based Search for Enumerating All Constrained Subgraphs with Compressed Representation", IEICE Trans. Fundamentals E100-A(9):1773-1784, Sept 2017, doi:10.1587/transfun.E100.A.1773. The abstract frames it as generalising path/tree-specific algorithms "without losing their efficiency" — [J-STAGE](https://www.jstage.jst.go.jp/article/transfun/E100.A/9/E100.A_1773/_article)
- Other OEIS-cited ZDD literature: Minato, "Power of Enumeration…", IEICE Trans. Inf. & Syst. E100.D(8):1556-1562 (2017); Minato, "Motivating Problems and Algorithmic Solutions", in *Algorithmic Foundations for Social Advancement*, Springer 2025, pp. 17-29 — [OEIS A007764](https://oeis.org/search?q=id:A007764&fmt=text). Their contents were not retrieved.

### Gaps
- The full text of the IEICE 2017 paper could not be downloaded (J-STAGE returned HTML, not the PDF). Any state-reduction content there, e.g. degree/component arrays or precomputed frontier "exit" pruning, is **unverified**.

## Q6. Citation checks and discrepancies

### Takeaway
Most citations verify, with one bibliographic discrepancy in OEIS and several index-offset traps.

### Cited Findings
- The OEIS %D line cites Guttmann & Jensen 2022 as "J. Phys. A 55 012345", but arXiv lists "J. Phys. A: Math. Theor. 55 485201 (2022)". The OEIS number looks like a placeholder; use 485201 — [OEIS](https://oeis.org/search?q=id:A007764&fmt=text); contradicted by [arXiv:2208.06744](https://arxiv.org/abs/2208.06744)
- TR 13-64 says BGJ "presented the answers up to n = 19" (cells), while OEIS says "extended to n=20" (vertices). These are the same result — [TCS-TR-A-13-64](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf); [OEIS](https://oeis.org/search?q=id:A007764&fmt=text)
- The TR 12-60 abstract says "up to n = 21" (cells), which is OEIS 22 — [TCS-TR-A-12-60](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_12_60/tcstr_12_60.pdf)
- TR 13-64 was written in April 2013 and says n=25 (cells) was newly found, i.e. OEIS 26 of Apr 11 2013 — [TCS-TR-A-13-64](http://www-alg.ist.hokudai.ac.jp/~thomas/TCSTR/tcstr_13_64/tcstr_13_64.pdf)

### Inferences
- From everything found, the record lineage relied on four levers: (1) the minimal-state noncrossing encoding with MPH ranking, (2) in-place updates, (3) shared-memory parallelism over occupancy classes, and (4) 64-bit residues with CRT on 0.5-1.5 TB machines.
- Levers that were proposed but not exploited, and so are open to us:
  - point-symmetry meet-in-the-middle, rejected only on preliminary timing;
  - diagonal-frontier symmetry, judged unattractive because of state growth;
  - narrower residues, which Spaans had as a 16-bit option but used 64-bit for the records;
  - any out-of-core or distributed partitioning.

### Gaps
- No published source describes pruning of non-completable states, disk-based or distributed computation, or diagonal-symmetry exploitation for A007764 at n >= 21.
