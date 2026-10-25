# Parity-window prototype (2026-10-25)

Pure-Python check of the "parity key" idea for splitting the dense sweep into two independent
halves (two machines or two disk windows). Nothing here touches the engine.

- `literal.py`  line-by-line transliteration of `DenseCounter.UpdateBlockGeneric` on dict stores
  (value + deferred). Run in ascending tuple order it reproduces A007764 for n=3..12 (descending
  order does not: the in-place update is order dependent).
- `split_sim.py` two class-partitioned stores, key f_a = parity of occupancy over positions [0..a];
  the literal update runs independently inside each class; before p = a+1 (where f_a is invalid)
  the layer, including the pending deferred entries, is re-keyed to f_{a+1} (wrap f_{n-2} -> f_0).
  Counts entries that change machine. `python split_sim.py 9 11` (needs ~1 min for n=11).
- `parity_proto.py` atomic-transition helpers (`trans`), state counting (`sizes`) and a transition
  sampler (`sample 19 20000`). The atomic per-update model alone does NOT reproduce OEIS (the
  engine pipelines emissions through the deferred array), so use `literal.py` for sweeps.

Results: see RESEARCH-BRIEF.md, section "Beyond one machine's RAM".
