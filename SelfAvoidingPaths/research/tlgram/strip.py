# number of +-1 paths of length k from height 0 to height 1 staying in [0, H]
def strip(k, H):
    d = [1] + [0] * H
    for _ in range(k):
        e = [0] * (H + 1)
        for h, c in enumerate(d):
            if c:
                if h + 1 <= H: e[h + 1] += c
                if h - 1 >= 0: e[h - 1] += c
        d = e
    return d[1]
from math import comb
for k in range(1, 26, 2):
    j = (k - 1) // 2
    cat = comb(k + 1, j + 1) // (j + 2)
    print(k, "catalan", cat, " H=2:", strip(k, 2), " H=4:", strip(k, 4), (3**j + 1) // 2, " H=8:", strip(k, 8), " H=12:", strip(k, 12))
