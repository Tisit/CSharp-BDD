# Meet-in-the-middle joins, symmetry, multi-vertex transfer and connectivity-DP theory for A007764

Scope: can anything beat a one-vertex-at-a-time noncrossing-frontier DP (dense ranked states, mod p + CRT) for corner-to-corner simple paths in an n x n grid? This file covers (a) joining forward/backward frontiers, (b) fused multi-vertex transfer, (c) pruning/symmetry, plus complexity lower bounds and separator methods.

Two things I checked myself are marked **[own computation]**: a small Python rank experiment on the join matrix (script at `C:/Users/SOVERE~1/AppData/Local/Temp/claude/D--projekty-CSharp-BDD/546484fa-4d05-4527-9e7b-807bbe3fe097/scratchpad/tl.py`, `tl2.py`). Everything else has a source or is marked as an inference.

**Context the report writer should know first:** A007764 is already known up to **n = 27**. H. Iwashita computed n=26 (Apr 2013) and n=27 (Nov 2013). R. G. Spaans computed n=23–25 (Feb 2013) with "state space compression (with rank/unrank) and dynamic programming (based in I. Jensen)", which is essentially the project's current design — [OEIS A007764](https://oeis.org/A007764). So n=24 is a re-derivation, not a new term. New terms start at n=28.

---

## Q1. Meet-in-the-middle: has anyone joined two frontier vectors, and what does the join cost?

### Takeaway
I found no published enumeration that joins two frontier state vectors for SAW/path counting. The relevant published idea is Clisby–Jensen's "prescriptive" transfer matrix. It is exactly the dual (Gram-transformed) form of the usual state vector, so a join between a descriptive vector and a prescriptive vector is a plain dot product. But producing a prescriptive vector costs a full half-sweep, so symmetry alone does not give 2x. The join matrix itself is **full rank over large primes** [own computation]. No low-rank shortcut exists for mod-p counting, and a naive join is far more expensive than the DP.

### Cited Findings
- Jensen's meander transfer matrix builds meanders "one bridge after the other"; the state is the connectivity of opened-but-not-closed arches above and below the river. Its growth rate is "much smaller" than previous algorithms. It reached closed meanders to n=48, open meanders to n=43 and semi-meanders to n=45 — [Jensen, J. Phys. A 33 (2000), cond-mat/0008178](https://arxiv.org/pdf/cond-mat/0008178); [IOP page](https://iopscience.iop.org/article/10.1088/0305-4470/33/34/301). This is the closest published "gluing of two noncrossing matchings computed sequentially". It counts all pairs uniformly. It does not contract two arbitrary weight vectors.
- Clisby & Jensen (2012) reverse what the TM state records: "keep track of what happened in the past, that is how loops are connected to the left, or prescribe what must happen in the future". In the prescriptive version "there is one and only one way of completing the SAP". Pruning becomes O(W), and configuration counts are comparable. The observed gain for square-lattice SAPs was about 16% at N=98 — [Clisby & Jensen, arXiv:1111.5877](https://arxiv.org/abs/1111.5877) (details from the [ar5iv full text](https://ar5iv.labs.arxiv.org/html/1111.5877)).
- For the SAW crossing a square, the standard TM (Bousquet-Mélou, Guttmann, Jensen 2005) uses edge states 0 = empty, 1 = free end, 2/3 = lower/upper end of a loop. It builds column by column, one vertex at a time. Boundary configurations grow like 3^M. A 19x19 run took "about 72 hours using 8 processors", times 10 runs for CRT — [BMGJ, cond-mat/0506341](https://ar5iv.labs.arxiv.org/html/cond-mat/0506341). No halving or join is described.
- Iwashita et al.'s record code (GGCount, n=26/27) is described as frontier DP with minimal perfect hash functions for state indexing, plus CRT. I saw no mention of a middle join — [GGCount README](https://github.com/kunisura/GGCount); tech report TCS-TR-A-13-64, Hokkaido Univ. 2013 ([cited via search results](https://www-alg.ist.hokudai.ac.jp/pub.html); not read in full).

### Formalising the join (inference plus own computation)
- Cut between two rows. Each half's state is: an occupancy set S of crossing edges, a noncrossing pairing of the occupied edges, and exactly one free end that is not nested under any arc (because it must reach its corner on the outer boundary). So |S| = k is odd.
- The join is block-diagonal over S. Within a block, entry(a,b) = 1 iff gluing a (below) to b (above) creates no closed loop. The two free ends are then automatically joined into the single start-to-end path.
- This block is exactly the Gram matrix of the Temperley–Lieb standard module with k points and one defect, evaluated at loop weight β = 0 (each loop weighted β; β=0 kills loops). Inference, from the TL definitions in [Ridout & Saint-Aubin, arXiv:1204.4505](https://arxiv.org/abs/1204.4505) and [Di Francesco, hep-th/9612026](https://arxiv.org/abs/hep-th/9612026).
- **[own computation] Ranks of this k-point, one-defect, β=0 join matrix:**

  | k | dim (Catalan) | rank mod 1e9+7 | rank mod 2 | rank mod 3 | det factorisation |
  |---|---|---|---|---|---|
  | 5 | 5 | 5 | 4 | 5 | 2 |
  | 7 | 14 | 14 | 8 | 14 | 2^6 |
  | 9 | 42 | 42 | 16 | 41 | 2^26·3 |
  | 11 | 132 | 132 | 32 | 122 | 2^100·3^10 |
  | 13 | 429 | 429 | 64 | 365 | 2^366·3^64 |
  | 15 | 1430 | 1430 | 128 | 1094 | (not computed) |

  - It is full rank over large primes and over Q.
  - Mod 2 the rank is exactly 2^((k-1)/2). This matches the GF(2) collapse of Cygan–Kratsch–Nederlof (below).
  - Mod 3 there is a modest rank drop.
  - The determinant contains only small primes (2 and 3 up to k=13). So every CRT prime of word size gives full rank.
- **Consequence (inference, a standard rank/communication argument):** any algorithm that expresses the count as Σ_i L_i(lower half)·R_i(upper half) across this cut needs at least rank-many terms. With full rank, that is about the number of distinct frontier states at the cut. The noncrossing frontier state space is therefore essentially minimal for exact or large-prime counting across a straight cut. Only unreachable or dead states and symmetry-equivalent states can be removed.
- **Naive join cost (inference):** the number of nonzero entries per block is the number of loop-free pairs, which is open-meander-like. Summed over S it is roughly (1 + ~3.5)^(n+1). At n=24 that is about 10^16 pair operations, versus about 5.85e9 states × ~600 vertex steps ≈ 3.5e12 for the whole DP. A dense or naive join is hopeless.

### Inferences (how a join could still pay off)
- **Symmetry makes the backward vector free.** A 180° rotation maps the backward frontier at the middle cut to the forward one, with position order reversed. So answer = Σ_S f_Sᵀ G_S R(f_S), where R reverses positions. The ceiling is a 2x saving (about n²/2 fewer vertex steps), and only if applying G costs much less than a half-sweep.
- **Prescriptive dual.** In Clisby–Jensen terms, G·f is exactly the prescriptive state vector: one entry per "future pairing that must be realised". Running a prescriptive DP over the first half gives G·f directly, and the join becomes a dot product with the mirrored descriptive f. But that is two half-sweeps, which is the same cost as one full sweep, so there is no gain. Directly applicable, but useless for speed.
- **"Zipper" conversion (speculative, not in the literature as far as I found).** Convert descriptive to prescriptive one cut position at a time, like one extra row sweep of k steps.
  - Hybrid state: positions ≤ j carry prescriptive (b-side) symbols and positions > j carry descriptive (a-side) symbols. It also records the planar pairing, through the glued left part, of the u open a-strands and v open b-strands crossing position j.
  - The extra pairing factor is about Catalan((u+v)/2). Typical open-strand counts are O(√k), so the hybrid state count may be only a moderate multiple (roughly 10–100x, unverified) of the frontier count.
  - Time: about k × (hybrid count), versus n²/2 × |states| for the second half. That could approach the 2x ceiling.
  - Memory: hybrid states exceed frontier states, which conflicts with the 11 GB limit.
  - Structurally this is the same as Jensen's meander TM (open arches above and below), applied to weighted vectors.
- **Change of basis via Gram–Schmidt / Jones–Wenzl (speculative, likely blocked).** Di Francesco diagonalises the meander Gram matrix with an upper-triangular change of basis P, giving det = (det P)^-2 ([Di Francesco](https://arxiv.org/html/hep-th/9612026v1)). The orthogonal basis involves denominators U_m(β), and U_m(0) = 0 for odd m. So that particular triangular basis probably does not exist at β=0, even though the one-defect, odd-k Gram matrix is nonsingular (my table). Flag this as unverified.

### Gaps
- I found no paper computing Σ f(a)g(b)[loop-free] for arbitrary weight vectors faster than a sweep. The zipper cost estimate is my own and unmeasured. A quick prototype (hybrid-state count at k ≈ 13–17) would settle it.
- I did not read the Iwashita tech report, so I cannot rule out that n=26/27 used a middle join or symmetry.

---

## Q2. Low-rank factorisations (rank-based approach, bases of perfect matchings): do they help counting mod p?

### Takeaway
No, not for counting modulo large primes. The rank-based and matching-basis speedups are decision or mod-2 results. Over Q and over large-prime fields the matchings connectivity matrix has rank about 4^k/poly(k) for general matchings. For the planar noncrossing case, my computation shows it is full rank.

### Cited Findings
- Cygan, Kratsch, Nederlof: the matchings connectivity matrix H_t has rank at most 2^(t/2−1) over GF(2), via an explicit factorisation with bases of matchings. This gives Hamiltonicity in (2+√2)^pw·n^O(1) time and counting Hamiltonian cycles **mod 2** in the same bounds. A (2+√2−ε)^pw algorithm would violate SETH — [arXiv:1211.1506](https://arxiv.org/abs/1211.1506) (STOC 2013; J. ACM 65(3), 2018 — [dblp](https://dblp.org/rec/conf/stoc/CyganKN13.html)).
- Bodlaender, Cygan, Kratsch, Nederlof: deterministic c^tw algorithms for weighted and counting connectivity problems. They use a rank-based approach (representative sets) and a determinant approach based on the matrix-tree theorem for counting — [arXiv:1211.1505](https://arxiv.org/abs/1211.1505); ICALP 2013; Inf. Comput. 243 (2015) 86–111 ([dblp](https://dblp.org/rec/conf/icalp/BodlaenderCKN13.html)).
- Curticapean, Lindzey, Nederlof: counting Hamiltonian cycles cannot be done in O*((6−ε)^pw) under SETH, which is tight against the O*(6^pw) algorithm of Bodlaender et al. Rank of the matchings connectivity matrix M_k: over Q it is 4^k/poly(k); over Z_p (p ≠ 2) it is Ω(1.97^k), and Ω(2.15^k) for some primes. "The modulus can affect the complexity in intricate ways" — [arXiv:1709.02311](https://arxiv.org/abs/1709.02311) (SODA 2018). One search snippet said "≈1.57^k" over Z_p. The fetched abstract says 1.97^k; trust the abstract.

### Inferences
- Rank-based pruning keeps a representative subset that preserves *existence* (or minimum weight), not counts. Counting needs the rank over the counting field, and for CRT primes that is full. **Not applicable** to exact A007764 beyond what the frontier DP already does.
- The GF(2) basis (my table: rank 2^((k-1)/2)) would give the parity of A007764(n) from far fewer states (about 2^(n/2)-ish per occupancy pattern). It is a curiosity only.
- Planar (noncrossing) states already exploit the structure that gives Catalan/Motzkin sizes, rather than the k^k-type counts of general partitions. That is the planar "Catalan structure" of [Dorn, Penninkx, Bodlaender, Fomin, Algorithmica 58 (2010)](https://link.springer.com/article/10.1007/s00453-009-9296-1).

### Gaps
- I found no published rank computation for the *noncrossing, one-defect, β=0* matrix. My small-k table is the only evidence of full rank. It is consistent with Ridout–Saint-Aubin's treatment of β=0, but I could not verify the specific claim "TL_n(0) is semisimple for n odd" from the abstract: a search snippet said so, the abstract does not ([arXiv:1204.4505](https://arxiv.org/abs/1204.4505)).

---

## Q3. Can the join be diagonalised (TL Gram / meander determinant)?

### Takeaway
The Gram determinant is known in closed form, and Gram–Schmidt gives a triangular diagonalisation for generic loop weight. At loop weight 0 the classical construction degenerates, and the determinant contains only tiny primes. Diagonalising does not reduce work unless the basis change itself is cheap, and no cheap (sweep-like) basis change is known.

### Cited Findings
- Di Francesco: det G_{2n}(q) = ∏_{m=1..n} U_m(q)^{a_{2n,2m}}, with a_{2n,2m} = c_{2n,2m} − c_{2n,2m+2}, and U_m(2cos θ) = sin((m+1)θ)/sin θ. The proof uses an upper-triangular Gram–Schmidt basis change: "basis 2 is orthogonal w.r.t. the bilinear form" — [hep-th/9612026](https://arxiv.org/html/hep-th/9612026v1); Comm. Math. Phys. 191 (1998) 543–583 ([Springer](https://link.springer.com/article/10.1007/s002200050277)).
- The TL standard-module bilinear form has a nontrivial radical only when q is a root of unity. β=0 (q = ±i) is "the delicate case" and is studied systematically — [Ridout & Saint-Aubin](https://arxiv.org/abs/1204.4505).

### Inferences
- With no defects (polygons, or the cycle analogue A140517), the matrix at β=0 is the q^0 coefficient of the meander Gram matrix. U_1(0) = 0, so it is singular for 2n ≥ 2. For paths (one defect, odd k) the matrix is nonsingular (my table).
- "Extract the loop-free part by polynomial interpolation in q" is possible, but each evaluation is a full-rank Catalan-dimension Gram product. That is no cheaper.

### Gaps
- There is no known fast (O(dim·poly k)) algorithm for multiplying by the TL Gram matrix at β=0. This would be new work.

---

## Q4. Symmetry reduction

### Takeaway
Reflection-canonical states only work when the reflection preserves the problem at every sweep step. For corner-to-corner with a row sweep, left–right reflection moves the start corner, so the usual "store only one of each mirror pair" trick does not apply mid-sweep. The usable symmetries are:
- the 180° rotation, at the middle join only;
- the diagonal reflection: A(n) = 2 × (paths whose first step is right).

### Cited Findings
- BMGJ exploit the square's symmetries only at the level of which rectangles to compute; the state space is 3^M with no mentioned mirror halving — [cond-mat/0506341](https://ar5iv.labs.arxiv.org/html/cond-mat/0506341). (I did not see an explicit statement either way on state mirroring.)
- The GGCount multithreaded version was summarised by the fetch model as "likely leveraging symmetry". That wording is the fetch model's speculation, **not** a README statement — [GGCount](https://github.com/kunisura/GGCount). Unverified.

### Inferences
- The diagonal symmetry fixes the first step. It removes about half the configurations only near the start corner; frontier counts in the middle are essentially unchanged. The saving is negligible.
- For the cycle problem (A140517) or problems symmetric under left–right reflection of the sweep, canonical mirror states give up to about 2x memory.

### Gaps
- I found no quantified mirror-reduction factor for corner-start frontiers in the TM literature.

---

## Q5. Multi-vertex (row) transfer vs single-site

### Takeaway
The literature standard is single-site ("move the kink one vertex at a time"). A full-row transfer matrix costs up to about |states|² per row, or |states|×(branching)^n, versus n·|states|·O(1) for single-site. Fusing two sites does not change the state count or arithmetic. It can only reduce memory passes, which matters here because the solver is bandwidth-bound.

### Cited Findings
- Polygons and walks are built "by successive moves of the kink in the boundary line, so that one vertex at a time is added". Jensen–Guttmann pruning gave an exponentially faster algorithm, and Clisby–Jensen improved on that — [Jensen & Guttmann, as summarised in arXiv:1607.02984](https://arxiv.org/pdf/1607.02984); [BMGJ](https://ar5iv.labs.arxiv.org/html/cond-mat/0506341).

### Inferences
- Fusing two sites (b) means each state produces up to about 3×3 successor contributions in one read/write pass, instead of two full passes. In a bandwidth-bound in-place dense layout this could approach 2x fewer bytes moved per vertex pair. The catch is that the intermediate (post-first-vertex) states must not need their own storage, and the in-place ranking order must allow safe scatter for two consecutive kink positions. There is no literature on this; it is an engineering matter.
- A "broken-line" / kink boundary is already what single-site TM uses. I found no literature showing a benefit from wider kinks.

### Gaps
- I found no published benchmark of 2-site fused vs 1-site transfer for SAW enumeration.

---

## Q6. Complexity bases and lower bounds

### Takeaway
The best known bounds parameterised by pathwidth are:

| Problem | Bound | Tight under SETH? |
|---|---|---|
| Hamiltonian cycle (decision) | (2+√2)^pw | yes |
| Hamiltonian cycle counting | 6^pw | yes |
| Hamiltonian cycle counting mod 2 | (2+√2)^pw | — |

For planar grids with a straight-line cut, the noncrossing state space (about 3^n Motzkin-type for SAWs) is below the general bounds. My rank computation suggests it cannot be shrunk further for large-prime counting across that cut.

### Cited Findings
- Hamiltonicity: (2+√2)^pw·n^O(1), with a SETH lower bound — [Cygan–Kratsch–Nederlof](https://arxiv.org/abs/1211.1506).
- Counting Hamiltonian cycles: O*(6^pw) (Bodlaender et al., ICALP 2013), with a matching SETH lower bound; counting mod p ≠ 2 has lower bounds of about O*(3.97^pw) — [Curticapean–Lindzey–Nederlof](https://arxiv.org/abs/1709.02311).
- SAW-crossing-square boundary configurations grow as 3^M — [BMGJ](https://ar5iv.labs.arxiv.org/html/cond-mat/0506341). The Iwashita et al. Simpath-style frontier reduces the state count to O(3^n) for grids, versus exponential in n² for backtracking (per a search summary of [Springer chapter](https://link.springer.com/chapter/10.1007/978-981-96-0668-9_2); not read in full).
- The number of walks grows as λ^(L²), with λ ≈ 1.744550 — [BMGJ](https://arxiv.org/abs/cond-mat/0506341); refined to λ = 1.7445498 ± 1.2e-6 by [Guttmann, Jensen, Owczarek, arXiv:2207.09731](https://arxiv.org/abs/2207.09731).

### Inferences
- The SETH lower bounds are for general graphs of bounded pathwidth and do not directly bound the planar grid. For grids, the relevant lower bound is the rank argument across a straight cut, which (my data) equals the number of noncrossing states. So idea (c) "prune dead states" can only remove unreachable or uncompletable states: for example, near the end of the sweep, states whose arc nesting depth exceeds the remaining rows. It cannot remove rank.

### Gaps
- I found no published theorem giving an exact lower bound on states for counting grid SAWs.

---

## Q7. Planar separators / divide and conquer

### Takeaway
Sphere-cut / planar-separator DPs give 2^O(√N) on N-vertex planar graphs using noncrossing (Catalan) states. For an n x n grid (N = n², treewidth and pathwidth about n) this is c^n again, with no better base than the frontier. Recursive divide-and-conquer can trade time for memory, but a join at every separator has the full-rank problem from Q1.

### Cited Findings
- Planar DP framework based on sphere-cut branch decompositions and noncrossing partitions (Catalan structures) — [Dorn, Penninkx, Bodlaender, Fomin, ESA 2005 / Algorithmica 2010](https://link.springer.com/article/10.1007/s00453-009-9296-1); Dorn, Fomin, Thilikos, "Catalan structures and dynamic programming in H-minor-free graphs", JCSS 78(5) (2012) (cited via search results only).

### Inferences
- For memory, a separator recursion (for example, splitting the grid into quadrants, each quadrant's boundary state being a noncrossing pattern on up to 2n boundary edges) makes the boundary alphabet larger than a single cut. That is worse than one straight cut of n+1 edges.
- The straight row cut is already the minimum separator for a square grid. **Not promising.**

### Gaps
- I found no implementation of separator-based exact SAW enumeration.

---

## Bottom line (for the report writer)
1. **Novelty:** n ≤ 27 is known (OEIS). The project's method matches Spaans' n=25 approach.
2. **(a) Middle join:** mathematically well defined as a TL one-defect Gram form at β=0.
   - It is full rank for large primes, so there is no low-rank trick.
   - Naive joining costs about 10^16 operations.
   - The Clisby–Jensen prescriptive dual turns the join into a dot product but costs a half-sweep.
   - The only route to the 2x ceiling is a cheap descriptive-to-prescriptive "zipper" (speculative, memory-hungry).
3. **(b) Fused 2-vertex transfer:** no theory gain. It is a plausible bandwidth gain up to about 2x, which is the most directly applicable idea.
4. **(c) Pruning:** only uncompletable states (depth > remaining rows near the end). The rank argument says the core state space is minimal.
5. **Theory:** CKN (2+√2)^pw and matching-basis tricks are decision or mod-2 only. Counting is tied to 6^pw in general graphs (CLN, SETH-tight), and mod-p ranks are still exponential.
