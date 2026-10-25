"""E3: live-state census for the A007764 frontier sweep.

Vertex-at-a-time frontier DP over explicit state sets (independent of the C#
engine). The cut after vertex (i, j) is, in planar order,
    [down(i,0) .. down(i,j), right(i,j), down(i-1,j+1) .. down(i-1,n-1)]
(n+1 positions; symbols 0 '.', 1 '(', 2 ')', 3 free end = the start's fragment).
Forward pass: reachable states per step (with path counts, to check OEIS).
Backward pass: a state is completable if some successor is completable.
Live = reachable and completable.

At row boundaries the cut is the n down-edges of the row, i.e. exactly the dense
engine's mate string (free end read as ')', left height 1), so live counts there
are compared with the dense slot count M(n+1) - M(n) and grouped by the dense
codec's left half (blocks) to see whether dead states fill whole blocks.

usage: python e3_live_states.py N [N ...]
"""
import sys
from functools import lru_cache

N_, L_, R_, F_ = 0, 1, 2, 3


def get(s, k):
    return (s >> (2 * k)) & 3


def put(s, k, v):
    return (s & ~(3 << (2 * k))) | (v << (2 * k))


def partner(s, k, width):
    """Position of the paren matching the one at k."""
    v = get(s, k)
    d = 1
    if v == L_:
        q = k
        while True:
            q += 1
            w = get(s, q)
            if w == L_:
                d += 1
            elif w == R_:
                d -= 1
                if d == 0:
                    return q
    else:
        q = k
        while True:
            q -= 1
            w = get(s, q)
            if w == R_:
                d += 1
            elif w == L_:
                d -= 1
                if d == 0:
                    return q


FINAL = -1


def successors(s, n, i, j):
    """States after processing vertex (i, j) from cut s (pair at positions j, j+1).
    Position j holds right(i, j-1) (the left input), j+1 holds down(i-1, j) (the up input);
    outputs: position j = down(i, j), position j+1 = right(i, j)."""
    width = n + 1
    a, b = get(s, j), get(s, j + 1)
    has_down, has_right = i < n - 1, j < n - 1
    start = (i == 0 and j == 0)
    end = (i == n - 1 and j == n - 1)
    base = put(put(s, j, 0), j + 1, 0)
    out = []
    if a == 0 and b == 0:
        if start:
            if has_down:
                out.append(put(base, j, F_))
            if has_right:
                out.append(put(base, j + 1, F_))
        elif not end:
            out.append(base)
            if has_down and has_right:
                out.append(put(put(base, j, L_), j + 1, R_))
    elif a == 0 or b == 0:
        x = a | b
        if end:
            if x == F_ and base == 0:
                out.append(FINAL)
        else:
            if has_down:
                out.append(put(base, j, x))
            if has_right:
                out.append(put(base, j + 1, x))
    else:
        if end or start:
            return out
        if a == L_ and b == R_:
            return out                      # adjacent matched pair: closing it makes a cycle
        t = base
        if a == F_ or b == F_:
            o = j + 1 if a == F_ else j     # the non-free input
            t = put(t, partner(s, o, width), F_)
        elif a == L_ and b == L_:
            t = put(t, partner(s, j + 1, width), L_)
        elif a == R_ and b == R_:
            t = put(t, partner(s, j, width), R_)
        # a == R_, b == L_: the outer partners already form '(' ... ')'
        out.append(t)
    return out


def row_start(s, n):
    """End of row: drop the (empty) right edge at position n, put an empty one at position 0."""
    assert get(s, n) == 0
    return (s & ((1 << (2 * n)) - 1)) << 2


@lru_cache(maxsize=None)
def motzkin(w, h1, h2):
    if w == 0:
        return 1 if h1 == h2 else 0
    if h1 < 0 or h2 < 0 or w < abs(h1 - h2):
        return 0
    return motzkin(w - 1, h1 + 1, h2) + motzkin(w - 1, h1, h2) + motzkin(w - 1, h1 - 1, h2)


def dense_layout(n):
    """Dense codec geometry for width n: (wR, wL, hC, total slots, left-string -> block size)."""
    wR = (n + 1) // 2
    wL = n - wR
    hC = min(1 + wL, 0 + wR)
    total = sum(motzkin(wL, 1, h) * motzkin(wR, h, 0) for h in range(hC + 1))
    return wR, wL, hC, total


def left_key(s, n, wL):
    """Dense left half of a row-boundary cut (columns 0..wL-1, free end as ')'), and its split height."""
    h = 1
    key = 0
    for c in range(wL):
        v = get(s, c)
        if v == F_:
            v = R_
        key = key * 3 + v
        h += 1 if v == L_ else -1 if v == R_ else 0
    return key, h


def run(n):
    steps = [(i, j) for i in range(n) for j in range(n)]
    layers = []                          # layers[k] = dict state -> count, BEFORE step k
    cur = {0: 1}
    for k, (i, j) in enumerate(steps):
        if j == 0 and i > 0:
            cur = {row_start(s, n): c for s, c in cur.items()}
        layers.append(cur)
        nxt = {}
        for s, c in cur.items():
            for t in successors(s, n, i, j):
                nxt[t] = nxt.get(t, 0) + c
        cur = nxt
    paths = cur.get(FINAL, 0)

    live_next = {FINAL}
    live = [None] * len(steps)
    for k in range(len(steps) - 1, -1, -1):
        i, j = steps[k]
        lv = set()
        for s in layers[k]:
            if any(t in live_next for t in successors(s, n, i, j)):
                lv.add(s)
        live[k] = lv
        if j == 0 and i > 0:
            # layers[k] is the post-row_start form; map back to the pre-shift form for step k-1
            live_next = {s >> 2 for s in lv}
        else:
            live_next = lv

    wR, wL, hC, total = dense_layout(n)
    print(f"n={n}  paths={paths}  dense slots/update={total}")
    work_reach = work_live = 0
    for k, (i, j) in enumerate(steps):
        if j == n - 1:
            continue                     # the dense engine has n-1 updates per row
        work_reach += len(layers[k + 1]) if k + 1 < len(steps) else 0
        work_live += len(live[k + 1]) if k + 1 < len(steps) else 0
    print(f"  mid-row (all steps): reachable/dense {work_reach / (total * n * (n - 1)):.3f}"
          f"  live/dense {work_live / (total * n * (n - 1)):.3f}")
    print("  row  reachable  live   live/dense  liveBlocks/blocks  liveBlockSlots/dense")
    nblocks = sum(motzkin(wL, 1, h) for h in range(hC + 1))
    for i in range(1, n):
        k = i * n                         # state before the first vertex of row i == row-boundary cut
        reach = {s >> 2 for s in layers[k]}
        lv = {s >> 2 for s in live[k]}
        blocks = {}
        for s in lv:
            key, h = left_key(s, n, wL)
            blocks[key] = motzkin(wR, h, 0)
        print(f"  {i:3d}  {len(reach):9d}  {len(lv):6d}  {len(lv) / total:9.3f}"
              f"  {len(blocks) / nblocks:17.3f}  {sum(blocks.values()) / total:20.3f}")


if __name__ == "__main__":
    for arg in sys.argv[1:]:
        run(int(arg))
