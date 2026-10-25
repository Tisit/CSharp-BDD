# Transfer-matrix state reduction, pruning, memory and parallel techniques from lattice enumeration (Jensen, Guttmann, Enting, Conway, Bousquet-Mélou, Clisby), applied to A007764

Convention warning (used throughout): Bousquet-Mélou/Guttmann/Jensen (BGJ) and Guttmann/Jensen (GJ) use **L = number of edges per side**, so an "L x L square" has (L+1)x(L+1) vertices. **OEIS A007764 index n = L + 1.** For example, BGJ's L = 19 is our n = 20, and Iwashita's L = 26 is our n = 27. This comes from [GJ 2022](https://arxiv.org/abs/2208.06744): "the listing in the OEIS runs from 1 to 27, which in our notation is L = 0 to 26".

Primary sources read in full text (arXiv PDFs, extracted locally):
- [Jensen & Guttmann 1999, cond-mat/9905291](https://arxiv.org/abs/cond-mat/9905291) (J. Phys. A 32 4867)
- [Jensen 2003, cond-mat/0301468](https://arxiv.org/abs/cond-mat/0301468) (J. Phys. A 36 5731, DOI 10.1088/0305-4470/36/21/304)
- [BGJ 2005, cond-mat/0506341](https://arxiv.org/abs/cond-mat/0506341) (J. Phys. A 38 9159–9181)
- [Clisby & Jensen 2012, arXiv:1111.5877](https://arxiv.org/abs/1111.5877) (J. Phys. A 45 115202, per the abstract of Jensen 2013)
- [Jensen 2013, arXiv:1309.6709](https://arxiv.org/abs/1309.6709)
- [Guttmann & Jensen 2022, arXiv:2208.06744](https://arxiv.org/abs/2208.06744)
- [Conway, Enting & Guttmann 1993, hep-lat/9211062](https://arxiv.org/abs/hep-lat/9211062)
- [Jacobsen 2007, arXiv:0709.2322](https://arxiv.org/abs/0709.2322) (J. Phys. A 40 14667)
- [Conway 2017, arXiv:1610.09806](https://arxiv.org/abs/1610.09806)
- Iwashita et al., TCS-TR-A-13-64. I read a local copy of the PDF. The TR index is at [Hokkaido TCS TR list](http://www-alg.ist.hokudai.ac.jp/tra.html), but I did not re-verify the exact PDF URL.

---

## Q1. Jensen's pruning: how states that cannot complete are discarded, how the "minimum additional edges" is computed, and how much it cuts

### Takeaway
Jensen–Guttmann pruning is **length-based**. Each state carries the minimum number of steps already used (Ncur). A lower bound Nadd on the extra steps needed to finish is computed. The state is dropped if Ncur + Nadd > Nmax. For polygons this cut the state growth from about 3^Wmax to about 2^Wmax, which is roughly 3^{N/4} to about 1.2^N in perimeter N, and it reduced memory about 200x at N = 90.

This only works because those papers want the series **up to a maximum length**. A007764 counts walks of **all** lengths, so the length bound never triggers. BGJ themselves used no pruning to get C_L(1). The only kind of pruning that carries over is geometric ("cannot finish within the remaining columns"), and that has little effect at the peak column (see Inferences).

### Cited Findings
- **Mechanism.** "for each configuration we must keep track of the current minimum number of steps Ncur … Secondly, we calculate the minimum number of additional steps Nadd required to produce a valid polygon." Nadd has three parts: the steps needed to close the polygon, the steps needed to touch both the lower and the upper border, and the steps needed to extend at least W edges lengthwise. The state is discarded if Ncur + Nadd > 4Wmax + 2 — [Jensen & Guttmann 1999](https://arxiv.org/abs/cond-mat/9905291). The same description appears in [Jensen 2003](https://arxiv.org/abs/cond-mat/0301468).
- **Computing the closing cost (old, "left-connectivity" encoding), in three stages** — [Jensen & Guttmann 1999](https://arxiv.org/abs/cond-mat/9905291); [Clisby & Jensen 2012](https://arxiv.org/abs/1111.5877):
  1. Separate pieces: scan bottom to top, counting 1s (lower ends) and 2s (upper ends). Whenever the counts are equal, a separate piece ends, and its top 2-edge is joined to the next 1-edge above. Nadd grows by the distance between them, and both edges are removed.
  2. Prefix/suffix rule: configurations that start as {112…2} or end as {1…122} can safely have their two lower (or two upper) edges joined. Nadd grows by the distance, and the relabelling is applied.
  3. The remaining case is {111…222}. The minimum is found by trying every closing pattern: these were hand-coded for 6, 8 and 10 occupied edges (1, 2 and 5 generic cases, Catalan numbers), and a backtracking search handles 12 or more. Closing around intervening edges costs "an additional two horizontal steps".
  - The authors note this search "should ultimately grow exponentially with the number of non-zero edges", although in practice "the growth is quite slow".
- **Computing the closing cost (new "future-connectivity" encoding, Clisby–Jensen).** Each state records how edges *must* connect to the right of the line, not how they are connected on the left. Completion is then unique: "there is one and only one way of completing the SAP". The cost of joining loop ends at positions i and j is "simply j − i + 2l", where l is the nesting level. Pruning becomes O(W) — [Clisby & Jensen 2012](https://arxiv.org/abs/1111.5877). The trade-off is more complex updates: inserting a new loop can create O(W) targets, one for each reachable existing loop, although pruning keeps the average at O(1).
- **Effect on state growth.** "With the original algorithm the number of configurations required as Wmax increased grew asymptotically as 3^Wmax … the value of λ is reduced from λ = 3 to λ ≈ 2 with the improved algorithm" (their Fig. 3) — [Jensen & Guttmann 1999](https://arxiv.org/abs/cond-mat/9905291). Measured by perimeter: Enting's method is 3^{n/4} (≈1.316^n), and pruning "reduced the complexity to 1.2^n" — [Clisby & Jensen 2012](https://arxiv.org/abs/1111.5877).
- **Memory effect.** SAP from 70 to 90 terms "using at most 2Gb … with the original algorithm would have required at least 200 times as much memory, or close to half a terabyte" — [Jensen & Guttmann 1999](https://arxiv.org/abs/cond-mat/9905291). Hardware: 8-node AlphaServer 8400, about one CPU-week per prime.
- **Pruning also trims generating-function terms.** A configuration first contributes at order N = Ncur + Nadd, so only the first (Nmax − N)/2 terms are kept. At W = 24 (Nmax = 110) there were ≈8.1 × 10^8 configurations but only ≈2.1 × 10^9 nonzero terms, "on average … only about 2.5 non-zero terms per configuration" — [Jensen 2003](https://arxiv.org/abs/cond-mat/0301468).
- **Old vs new algorithm** (SAP, N = 98, their Table 1) — [Clisby & Jensen 2012](https://arxiv.org/abs/1111.5877):

  | Algorithm | Configurations | Terms | CPU time |
  |---|---|---|---|
  | Old | 94,861,519 | 251,027,714 | 173,408 s |
  | New | 93,020,737 | 244,009,381 | 145,454 s (−16%) |

  - The state count is almost unchanged. The saving is in pruning cost.
  - For SAW, a preliminary version saved "more than 60%" of time — [Clisby & Jensen 2012](https://arxiv.org/abs/1111.5877). Jensen 2013 confirms "very significant improvement in the running time" and says the gain "comes purely from the enhanced efficiency of the pruning algorithm" — [Jensen 2013](https://arxiv.org/abs/1309.6709).
- **Kink/vertex-state simplification** (future-connectivity encoding). Only the kink states '00', '10', '20' and '12' are needed, which amounts to "replacing the two kink edges by a vertex with four possible states". This gave a memory drop of ~15% and a net ~30% gain in running time. It is not valid on directed lattices — [Clisby & Jensen 2012](https://arxiv.org/abs/1111.5877).

### Inferences
- **Length pruning does not apply.** A007764 is C_L(1), the sum over all lengths. Nmax is effectively L² + 2L, so Ncur + Nadd > Nmax never prunes anything. BGJ's C_L(1) runs used plain unpruned TM (Q2).
- **Only "cannot complete within remaining columns" pruning carries over.**
  - Closing k nested arcs, and routing the free end past them to the far corner, needs about k further columns. Likewise, building nesting depth k needs about k columns behind the line.
  - At the middle column of an n x n grid, depth is already bounded by n/2 because there are only n positions. The geometric cut therefore removes (almost) nothing at the **peak**. It only removes states in the first and last few columns.
  - Typical Motzkin-path height at length n is O(√n), which is about 5 for n = 24. So only roughly the first and last ~√n columns are noticeably thinned.
  - Expected result: a modest wall-time saving, and **no saving of peak memory** with a dense per-column MPHF over all Motzkin states.
  - This is my reasoning, not a published measurement. It can be checked cheaply by counting nonzero slots per column for n ≈ 12–16 in the existing engine.
- The Clisby–Jensen "future-connectivity" idea gives O(W) exact completion costs, which matter only when there is a length cutoff. It is useful if the goal were C_L(x) truncated by length. It is not useful for A007764.

### Gaps
- I found no published measurement of what fraction of Motzkin states are reachable **and** completable at each column for the corner-to-corner problem.
- The Jensen–Guttmann 1999 text gives only qualitative detail of the "very many special cases" in the closing algorithm; the exact code was not published there.

---

## Q2. Bousquet-Mélou, Guttmann & Jensen (2005), "Self-avoiding walks crossing a square"

### Takeaway
BGJ used the Conway–Enting–Guttmann single-free-end TM: one vertex at a time, column by column, with states as 0 / free end / balanced 2–3 parentheses. They counted C_L(1) to **L = 19 (n = 20)**, and the full length polynomials C_L(x) to L = 17 (n = 18). Each run took about 72 h on 8 × 1 GHz Alpha CPUs per prime, with 10 × 30-bit primes. There was no pruning and no symmetry reduction. The only savings were for the length-polynomial version: keeping only terms of one parity and dropping terms below n_min.

### Cited Findings
- **State encoding.** "0" is empty, "1" the free end, "2" the lower end of a loop, "3" the upper end. There is "exactly one free end in any configuration". The 2s and 3s form "perfectly balanced parentheses". The initial state is a single '1' at position 0 (bottom-left corner). A walk is completed ("Res") only at the top-most vertex when no other edges are occupied — [BGJ 2005](https://arxiv.org/abs/cond-mat/0506341).
- **Update rules** (Table 1 of the paper). Inputs '11' never occur, and '23' is forbidden because it closes a loop. For '03'/'30', the loop end is not continued vertically if the edge below is a '2'. Joining two '2's (or two '3's) relabels the matching end. Joining a free end to a loop end turns the far end into the new free end — [BGJ 2005](https://arxiv.org/abs/cond-mat/0506341).
- **State count.**
  - After a completed column, the free end splits the line into two Motzkin paths, so the generating function of N_conf(M) is x·M(x)². Asymptotically N_conf(M) grows "like 3^M (up to a power of M)".
  - With a kink, the count exceeds N_conf(M) but "clearly is less than N_conf(M+1)".
  - Time is exponential in M and linear in L — [BGJ 2005](https://arxiv.org/abs/cond-mat/0506341).
- **Modular arithmetic.** Primes "p_i = 2^30 − r_i where r_i are distinct integers, less than 1000", combined by CRT — [BGJ 2005](https://arxiv.org/abs/cond-mat/0506341).
- **Length-polynomial extension.** Degree ≤ L(L+2). Only even (or only odd) terms are stored, and terms of degree below the minimal n_min needed to build the configuration are not stored — [BGJ 2005](https://arxiv.org/abs/cond-mat/0506341).
- **Results and resources.**
  - C_L(x) for all L ≤ 17. C_18(1) and C_19(1).
  - Cow-patch P_L(1) and transverse T_L(1) for L ≤ 19.
  - "Each run for a 19 × 19 lattice took about 72 hours using 8 processors of a multiprocessor 1 GHz Compaq Alpha computer. Ten such runs were needed" — [BGJ 2005](https://arxiv.org/abs/cond-mat/0506341).
- **Easy constraint variants.**
  - Forcing the walk through the centre: at that vertex, input '00' has only output '12', and output '00' is disallowed. The fraction through the centre is ≈0.793 at L = 10.
  - Hamiltonian paths (their Tables 6–8) — [BGJ 2005](https://arxiv.org/abs/cond-mat/0506341).
- **Growth constant.** λ = 1.744550 ± 0.000005 — [BGJ 2005](https://arxiv.org/abs/cond-mat/0506341). Refined to λ_S = 1.7445498 ± 0.0000012 in [GJ 2022](https://arxiv.org/abs/2208.06744).
- **Later history** — [GJ 2022](https://arxiv.org/abs/2208.06744):
  - Iwashita et al.: L = 20, 21.
  - R. Spaans: L = 22–24.
  - Iwashita: L = 25, 26 (n = 26, 27).
  - GJ 2022 wrote their own MPHF-based code and computed SAPs crossing a square and SAWs spanning a square to L = 26. They analysed but did not extend crossing SAWs; for those they used series extension ("approximate terms"), not new exact terms.

### Inferences
- BGJ's method is essentially the same algorithm our engine uses. The differences are storage (hash table vs MPHF), counter width (30-bit vs 12-bit residues) and parallelism (8 CPUs). No trick in BGJ reduces state count beyond the Motzkin bound.
- No "transfer along a diagonal" is used in any of these papers. A diagonal cut of a square crosses more edges than a column cut (see Q5).

### Gaps
- BGJ do not report the peak number of configurations or the memory used for L = 19.

---

## Q3. Parallelization and memory management: partitioning states, moving the cut, hash tables vs ranking/MPHF

### Takeaway
- **Parallelism.** The standard Jensen method uses the invariant that only kink edges change occupation. Data are partitioned by the occupation pattern of the lower half of the line, the top half of a column is processed independently on each processor, and then the data are redistributed by the upper-half pattern. This means two redistributions per column, with <10% overhead and near-perfect scaling to 64 CPUs.
- **Memory.** GJ 2022 replaced hash tables with Iwashita's Motzkin-split MPHF plus **in-place updating**: there is no second (tmp) array, and a movable "dividing position" makes in-place safe.
- Conway 2017 describes the general "signature invariant grouping", "binning" and "implicit boundary shape" tricks.

### Cited Findings
- **Invariant.** "any edge not directly involved in the update cannot change from being empty to being occupied and vice versa. That is only the kink edges can change their occupation status" — [Jensen 2003](https://arxiv.org/abs/cond-mat/0301468).
- **Algorithm.**
  1. With the line straight, distribute states so that the same lower-half occupation pattern goes to the same processor.
  2. Process the top half of the new column.
  3. Redistribute by the upper-half pattern.
  4. Process the bottom half.

  Load balance is done by greedy assignment of patterns sorted by frequency (most-frequent then least-frequent fill) — [Jensen 2003](https://arxiv.org/abs/cond-mat/0301468).
- **Scaling data** (SAP, Nmax = 98, W = 22) — [Jensen 2003](https://arxiv.org/abs/cond-mat/0301468):
  - 1 CPU: 33.4 h elapsed, 94.9M configurations, 202M terms, ≈3 GB.
  - 64 CPUs: 31 min elapsed, ≈1.4–1.5M configurations per CPU, <50 MB per CPU. The max/min difference is <10%.
  - Total CPU time stayed ≈33–35 h, even dropping slightly at 32–64 CPUs, which the authors attribute to cache effects as per-CPU memory shrank.
  - "the typical time to do a redistribution is the same as the average time taken per iteration" to move the kink once. At W = 24 there are 24 iterations and 2 redistributions per column, so the overhead is "smaller than 10%".
- **Resources, Jensen 2003 (SAP to 110).** At most 36 GB. Widths 23–25 took 40 CPUs and 8–10 h each per prime; ≈1500 CPU-hours per prime in total — [Jensen 2003](https://arxiv.org/abs/cond-mat/0301468).
- **Resources, Clisby & Jensen 2012 (SAP to 130).** ≈25,000 CPU-h, up to 1000 cores and up to 2.5 TB — [Clisby & Jensen 2012](https://arxiv.org/abs/1111.5877).
- **Resources, Jensen 2013 (SAW to 79 steps).** ≈16,500 CPU-h, up to 400 cores and up to 1 TB. Computing all moduli in the same run raised memory by about 50% — [Jensen 2013](https://arxiv.org/abs/1309.6709).
- **Hash tables in older work.** "The number of contributing configurations becomes very sparse in the total set of possible states … one uses a hash-addressing scheme" — [Jensen 2003](https://arxiv.org/abs/cond-mat/0301468). Jacobsen used two hash tables (time t and t + 1), keyed by the coding read as one big integer modulo a prime, and could store at most ~10^8 configurations — [Jacobsen 2007](https://arxiv.org/abs/0709.2322).
- **The MPHF (GJ 2022, after Iwashita)** — [GJ 2022](https://arxiv.org/abs/2208.06744):
  - The signature is split at m = ⌈(L+1)/2⌉ into a left part σ_L ∈ M^{(m)}_{(1)→h} and a reversed right part σ_R ∈ M^{(n)}_{(0)→h}.
  - Storage is sectioned by the height h. Φ(σ) = b_h + (I_L − 1)·|M^{(n)}_{(0),h}| + I_R, using lexicographic ranks (order ∅ < ( < )).
  - Signatures take 2 bits per state in a 64-bit word. The two lookup tables total "about 2^{L+2}", which is "insignificant".
- **Free end as an excess ')'.** The free end can be treated as an excess ')'. Then the state set is simply Motzkin paths from height 1 to 0 (OEIS A002026), |M| = M_{L+2} − M_{L+1} ~ 3^L, and the free end never needs to be tracked explicitly. Update rules (from the hexagonal-lattice write-up): "|∅" behaves like ")∅"; "|(" is equivalent to ")(" with the incoming ")" moving to the partner position; "(|" and "|)" are impossible — [GJ 2022](https://arxiv.org/abs/2208.06744).
- **In-place updating** — [GJ 2022](https://arxiv.org/abs/2208.06744):
  - Signatures are processed in order of height, then lexicographically, so each target is processed before its count is written, or source and target are updated simultaneously (the '∅(' / '(∅' swap pair).
  - The one unsafe case is ')(' → '∅∅' when the divider falls between ')' and '('. It is avoided by moving the **dividing position**, which need not equal the hash split lh.
  - The algorithm uses lt = lh − 1 and lb = lt − 1, splitting each column into three loops. Choosing dividers strictly below lh "is faster".
  - Iwashita notes that removing the tmp array halves memory — [Iwashita et al. TR-A-13-64, via TR index](http://www-alg.ist.hokudai.ac.jp/tra.html).
- **Iwashita state classes.** "Main" states number N_n = M_{n+1} − M_n; "blocked" states (current position occupied by a special marker) number M_n − M_{n−1}; the total is M_{n+1} − M_{n−1}. For n = 24 with 56-byte bignum counters, the count array alone would need "(M25 − M23) × 56 bytes = 413 gigabytes" — [Iwashita et al. TR-A-13-64](http://www-alg.ist.hokudai.ac.jp/tra.html) (local PDF read; URL of the specific PDF not re-verified).
- **Signature-invariant groups.** Split states into groups with no shared children and process each group with a small, cache-friendly hash map, then serialise. The groups can be kept ordered by bit-shifting the group id, so SSD storage of serialised groups is feasible even though "SSDs are too slow to be used as swap space for a giant hash map". Also useful on multiprocessors — [Conway 2017](https://arxiv.org/abs/1610.09806).
- **Implicit state parts.** The boundary shape and the number of processed sites are implicit in the iteration number and need not be stored per state — [Conway 2017](https://arxiv.org/abs/1610.09806).
- **Binning.** Choose an intermediate set of states, split it into groups, and redo the enumeration once per group, forcing it through that group. This trades time for memory, and is "rarely useful as the time penalty is usually significantly greater than the memory advantage" — [Conway 2017](https://arxiv.org/abs/1610.09806).
- **Two state sets in memory.** A TM usually needs "roughly twice" the peak state memory, because one set is being processed while the next is being produced — [Conway 2017](https://arxiv.org/abs/1610.09806). This is the cost that in-place updating removes.

### Inferences
- Our engine already has the GJ 2022/Iwashita design: dense MPHF, only "main" Motzkin states (our 5.85 × 10^9 at n = 24 equals M_25 − M_24 = 5.851 × 10^9 exactly), and modular counters. The remaining published levers are:
  1. In-place updating with a movable divider, if we still keep two arrays.
  2. Jensen's occupation-pattern partitioning. Under the kink-only-changes-occupancy invariant, each lower-half pattern forms an independent subproblem for the top half-column. This lets the working set be streamed from disk or SSD, pattern by pattern, with only two reshuffles per column. It is the route to n = 25 (1.66 × 10^10 states, ≈25 GB at 12 bits) on a machine with less RAM than that.
  3. Conway's binning, which trades time for peak memory.
- Because the engine is bandwidth-bound (per project memory), Jensen's observation that CPU time *fell* when the per-CPU working set shrank (3 GB → 50 MB) suggests that cache-blocking by occupation pattern could itself give a wall-clock gain on one machine. This is inference only.

### Gaps
- I could not open the GJ GitHub code (`IwanJensen/Self-avoiding-walks-and-polygons`, branch WCAS(H)); the fetch returned only repo metadata ([repo](https://github.com/IwanJensen/Self-avoiding-walks-and-polygons)). GJ 2022 does not describe the square-lattice update order ("which we won't describe here").
- GJ 2022 gives no run times or memory figures for the L = 26 square runs.

---

## Q4. Modular arithmetic, CRT and counter width in these works

### Takeaway
Every group computes modulo several primes and reconstructs with CRT. The moduli range from 16-bit (2^15 − r, to save memory in 1999) through 30-bit (2^30 − r) to 62-bit (2^62 − r, to save CPU on shared clusters). The choice depended explicitly on whether memory or CPU was the binding constraint.

### Cited Findings
- **Jensen & Guttmann 1999.** "In order to save memory we used primes of the form p_i = 2^15 − r_i so that the residues … could be stored using 16 bit integers"; "Up to 8 primes were needed" — [Jensen & Guttmann 1999](https://arxiv.org/abs/cond-mat/9905291).
- **BGJ 2005.** 2^30 − r_i, with r_i < 1000; 10 runs (primes) for L = 19 — [BGJ 2005](https://arxiv.org/abs/cond-mat/0506341).
- **Jensen 2003.** For SAP counts, moduli 2^62, 2^62 − 1 and 2^62 − 3 (note that 2^62 is not prime; only mutual coprimality is needed), because "CPU time was more of a immediate limitation than memory" and more memory went to configuration keys than to counts. For area moments, up to 6 primes of 2^30 − r_i. A sign-aware CRT bound is used: "p1 p2 ··· pk/2 > m" — [Jensen 2003](https://arxiv.org/abs/cond-mat/0301468).
- **Clisby & Jensen 2012.** The same three 62-bit moduli, run simultaneously in one pass — [Clisby & Jensen 2012](https://arxiv.org/abs/1111.5877).
- **GJ 2022.** "primes of the form p_k = 2^62 − r_k, such that p_k are the largest primes less than 2^62", stored in 64-bit arrays — [GJ 2022](https://arxiv.org/abs/2208.06744).
- **Jacobsen 2007.** Moduli 2^32, 2^31 − 1, 2^31 − 3, …; modular arithmetic works "because the weights … are constructed only by successive additions of positive integers" — [Jacobsen 2007](https://arxiv.org/abs/0709.2322).
- **Conway, Enting & Guttmann 1993.** A workstation with 256 MB reached SAW to 39 steps with complexity 3^{N/4} — [Conway, Enting & Guttmann 1993](https://arxiv.org/abs/hep-lat/9211062).

### Inferences
- Our 12-bit residues are narrower than anything in this literature, the narrowest published being 16-bit. That is consistent with our bandwidth-bound regime: the 1999 paper chose narrow counters for exactly the same reason.
- Running several moduli in one pass (Clisby–Jensen, Jensen 2013) cost about +50% memory but saved the per-pass state traversal. Whether that helps depends on whether the traversal or the counter I/O dominates in our engine.

### Gaps
- None of the papers report timing as a function of counter width.

---

## Q5. Enting's finite lattice method, decomposition, diagonal sweeps and meet-in-the-middle joins

### Takeaway
Enting's finite lattice method (FLM) builds **infinite-lattice** series from finite rectangles. It does not narrow the frontier for a fixed n x n domain, so it gives no gain for A007764. The published frontier optimisations are:
- choosing the orientation so the boundary is the short side;
- vertex-at-a-time kinked lines;
- building sub-portions and combining them, which is mentioned but only for FLM series.

I found **no** stat-mech paper that joins two half-sweeps (meet-in-the-middle) for crossing walks and resolves connectivity at the join.

### Cited Findings
- **FLM.** In the FLM, "the state then describes that boundary and connectivity information", and the boundary is a cross-section line with a kink. "The orientation is chosen to minimize the length of the boundary, and thus the number of states." "When enumerating size n objects, one generally has a maximum boundary length of roughly n/2" — [Conway 2017](https://arxiv.org/abs/1610.09806).
- **Sub-portions.** "A technique occasionally used in the finite lattice method … is to enumerate subportions of the objects … and construct the objects … from the subportions. This enables smaller lattices to be used, producing smaller boundary sizes and vast reductions in the number of states" — [Conway 2017](https://arxiv.org/abs/1610.09806). This is described generically. No join rule for connectivity is given there.
- **FLM for SAP.** SAP series come from rectangles W ≤ Wmax, W ≤ L ≤ 2Wmax − W + 1, with contributions from rectangles with L > W counted twice (the rectangle-level symmetry) — [Jensen 2003](https://arxiv.org/abs/cond-mat/0301468); [Jensen & Guttmann 1999](https://arxiv.org/abs/cond-mat/9905291).
- **The standard FLM text** is the chapter "Exact Enumerations" (Enting & Jensen) in Guttmann (ed.), *Polygons, Polyominoes and Polycubes*, LNP 775 (2009) — [Springer chapter page](https://link.springer.com/chapter/10.1007/978-1-4020-9927-4_7). I verified only its existence and authorship via the search index; I did not read the text.
- **Jacobsen's cross-check.** Jacobsen checks results by running both L1 × L2 and L2 × L1, which "leads to a completely different transfer process" — [Jacobsen 2007](https://arxiv.org/abs/0709.2322). This is used as validation, not for speed.

### Inferences (not found in literature — my analysis)
- **Diagonal sweep.** A staircase anti-diagonal cut through the middle of an n x n grid crosses about 2(n−1) edges, versus n for a column cut. So a diagonal sweep would square the state count (≈3^{2n}) and is worse.
- **Meet in the middle using 180° rotation symmetry.**
  - Rotating by 180° maps the A007764 problem onto itself (start and end swap, and the path is reversed). The backward state vector at column n−1−c is therefore the forward vector at column c, relabelled. One forward sweep to the middle gives both halves.
  - The join is **not** an inner product. For the same occupied set, the left arc system L (arcs plus free end from the start corner) and the right arc system R (arcs plus free end to the end corner) are compatible iff L ∪ R forms one simple path with no cycles. That is a meander-type bilinear form Σ_{L,R} F(L)·F(rot R)·[L∪R is a single path].
  - The number of compatible (L, R) pairs per occupation pattern can far exceed the number of states. A naive join would likely cost more than the second half of the sweep it replaces.
  - An efficient join would need a second TM-like pass over the middle frontier, e.g. contracting arcs one by one, and would probably cost as much as the half-sweep it saves.
  - No source found. Treat as speculative.
- **Up–down reflection.** Jensen's reflection symmetry (Q6) does not apply to a single corner-to-corner sweep: an up–down reflection moves the start corner to a different corner, so the reflected state belongs to a different problem.

### Gaps
- No published meet-in-the-middle or split-and-join implementation for crossing or spanning SAWs was found. Searches returned only the standard TM papers.
- The LNP 775 "Exact Enumerations" chapter text was not accessed, so any decomposition tricks it contains are unverified.

---

## Q6. Symmetry-based state reduction in Hamiltonian / SAW TM work (Jensen, Jacobsen, Kloczkowski & Jernigan, Pönitz & Tittmann)

### Takeaway
The only symmetry state reduction documented in this literature is Jensen's reflection of **completed-column** SAP states. States that are mirror images (for example {010122000} and {000112020}) have identical generating functions, which roughly halves the stored states at column boundaries. It needs a problem that is symmetric under up–down reflection, so it does not transfer directly to corner-to-corner paths. Jacobsen's Hamiltonian TM uses no state symmetry. Kloczkowski–Jernigan and Pönitz–Tittmann were not verified in detail.

### Cited Findings
- **Jensen's reflection symmetry.** "When a column has been completed the configuration are symmetric under reflection. That is the generating functions for the configurations such as, {010122000} and {000112020}, are identical. This symmetry also extends to the touching of the upper/lower borders" — [Jensen 2003](https://arxiv.org/abs/cond-mat/0301468). It "does not extend to the radius of gyration calculation".
- **Jacobsen's Hamiltonian chains, walks and circuits.**
  - The TM uses a canonical labelling of connectivity (positive entries in increasing order) and two hash tables.
  - Configurations are rejected at row completion or at the last row, which avoids special boundary cases.
  - The peak is "after transfering the next last vertex in the next last row", with ≤ ~10^8 configurations storable.
  - Reached L × L: chains L = 12, walks L = 17, circuits L = 20.
  - "limited by the memory rather than the CPU time". Counts in earlier references were given "modulo the symmetry group of the lattice", but Jacobsen does not use symmetry to reduce states.
  - [Jacobsen 2007](https://arxiv.org/abs/0709.2322); [IOP page](https://iopscience.iop.org/article/10.1088/1751-8113/40/49/003).
- **Kloczkowski & Jernigan (1998).** A TM for compact (Hamiltonian) SAWs on m × n strips, "attrition-free", generalised to irregular shapes and to Monte Carlo sampling — [J. Chem. Phys. 109, 5134 abstract](https://pubs.aip.org/aip/jcp/article-abstract/109/12/5134/183246/Transfer-matrix-method-for-enumeration-and). I read only the abstract, so any state-reduction details are unknown.
- **BGJ Hamiltonian paths** across a square: same TM, with Hamiltonian constraints — [BGJ 2005](https://arxiv.org/abs/cond-mat/0506341).

### Inferences
- For A007764, the up–down (or left–right) mirror of a partial state from the bottom-left start corresponds to a walk from the top-left corner. A symmetric-state merge would only be valid in a combined "both corner" formulation. Under 180° rotation, forward and backward vectors map onto each other (Q5), and that is the only exact symmetry available within a single sweep.

### Gaps
- Pönitz & Tittmann: I found no source in this session. Anything about their TM state reduction is **unverified**.
- Kloczkowski & Jernigan: full text not accessed.

---

## Q7. Asymptotic state-count growth and how pruning changes the base

### Takeaway
- **Unpruned (our case).** The number of noncrossing single-free-end states on n sweep positions is M_{n+1} − M_n ~ 3^n (OEIS A002026, Motzkin). The measured n → n+1 ratio at n = 20–27 is ≈2.81–2.86, approaching 3.
- **With length pruning (SAP).** The effective base drops from 3 to ≈2 per unit width, i.e. 3^{N/4} → ≈1.2^N in length.
- Since A007764 cannot use length pruning, the ~3^n base stands. Every +1 in n costs ×2.84 in states at n ≈ 24–25.

### Cited Findings
- **Motzkin count.** |M_{(1)→0}^{(L+1)}| = M_{L+2} − M_{L+1} (A002026) "≈ 3^L … This growth in storage is the main limitation on the maximum size" — [GJ 2022](https://arxiv.org/abs/2208.06744).
- **N_conf(M).** N_conf(M) has generating function x·M(x)²; "asymptotically N_conf(M) grows like 3^M (up to a power of M)" — [BGJ 2005](https://arxiv.org/abs/cond-mat/0506341).
- **Iwashita's total.** Main plus blocked states is M_{n+1} − M_{n−1}; CPU and memory "tripled when the graph becomes the next larger size"; a direct method would need "about 10 days and 700 gigabytes" for G_{23,23} — Iwashita et al. TR-A-12-60 / TR-A-13-64 ([TR index](http://www-alg.ist.hokudai.ac.jp/tra.html); local PDFs read).
- **Pruning lowers the base** from 3 to about 2 in Wmax — [Jensen & Guttmann 1999](https://arxiv.org/abs/cond-mat/9905291). Equivalently 3^{n/4} → 1.2^n in perimeter — [Clisby & Jensen 2012](https://arxiv.org/abs/1111.5877).
- **Conway, Enting & Guttmann.** SAW complexity is 3^{N/4} × poly(N) — [Conway, Enting & Guttmann 1993](https://arxiv.org/abs/hep-lat/9211062).
- **Where the peak falls.** With pruning, "the peak number of states actually encountered is highest at a smaller portion of n" — [Conway 2017](https://arxiv.org/abs/1610.09806).

Computed by me from the Motzkin recurrence (M_0 = M_1 = 1, (n+2)M_n = (2n+1)M_{n−1} + 3(n−1)M_{n−2}):

| n | main states M_{n+1} − M_n | main + blocked M_{n+1} − M_{n−1} |
|---|---|---|
| 22 | 7.29e8 | 9.87e8 |
| 23 | 2.063e9 | 2.792e9 |
| 24 | **5.851e9** (matches our engine) | 7.914e9 |
| 25 | 1.663e10 | 2.248e10 |
| 26 | 4.734e10 | 6.396e10 |
| 27 | 1.350e11 | 1.824e11 |

- Growth ratio from n to n+1 is 2.81 at n = 20 and 2.86 at n = 27.
- Implied dense storage at 12 bits per residue: n = 25 ≈ 25 GB, n = 26 ≈ 71 GB, n = 27 ≈ 203 GB (arithmetic, before any overhead).

### Inferences
- Reaching n = 25 on a machine with ~11–16 GB is not possible with a dense in-RAM array of all 1.66 × 10^10 states at 12 bits. The published routes are:
  1. **Distributed or partitioned processing** by lower- or upper-half occupation pattern (Jensen 2003). Only the active partition needs to be in RAM, with the rest streamed.
  2. **Narrower counters.** Already at 12 bits; 1999 used 16.
  3. **Binning at a mid-sweep cut** (Conway 2017), trading repeated runs for peak memory.
- Pruning of never-completing states will not change the ~3^n peak for this problem (see Q1 Inferences). This needs empirical confirmation by counting nonzero dense slots per column for n ≈ 12–16.

### Gaps
- There is no published measurement of the actual number of reachable-and-completable states per column for corner-to-corner walks versus the Motzkin bound.
- The growth of the pruned SAW (as opposed to SAP) state count is not given numerically in Jensen 2013's text; I only grepped it for key terms and did not read it in full.
