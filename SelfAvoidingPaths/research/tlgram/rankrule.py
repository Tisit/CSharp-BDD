from math import comb

def D(k, r):  # dim of TL standard module W_{k,r}: k points, r defects
    if (k - r) % 2 or r > k:
        return 0
    a = (k - r) // 2
    return comb(k, a) - (comb(k, a - 1) if a > 0 else 0)

def vp(n, p):
    v = 0
    while n % p == 0:
        n //= p
        v += 1
    return v

def det_val(k, p):
    # p-adic valuation of det Gram(W_{k,1}) at delta=0 from
    # det = prod_j ([j+2]/[j])^{D(k,1+2j)}; at q=i only even j=2t survive, as (t+1)/t
    return sum(D(k, 1 + 4 * t) * (vp(t + 1, p) - vp(t, p)) for t in range(1, k))

def strip(k, p):  # +-1 paths 0->1 of length k inside [0, 2p-2]
    w = [1] + [0] * (2 * p - 2)
    for _ in range(k):
        n = [0] * len(w)
        for h, c in enumerate(w):
            if h > 0: n[h - 1] += c
            if h + 1 < len(w): n[h + 1] += c
        w = n
    return w[1]

def verlinde(j, p):  # multiplicity of 1 in (L_1 + 2*1)^{(x)j} in Ver_p (simples L_0..L_{p-2})
    w = [1] + [0] * (p - 2)
    for _ in range(j):
        n = [2 * c for c in w]
        for h, c in enumerate(w):
            if h > 0: n[h - 1] += c
            if h + 1 < len(w): n[h + 1] += c
        w = n
    return w[0]

print("k=17 det valuations v2, v3 (Smith form: 4724, 1581):", det_val(17, 2), det_val(17, 3))
tab = {9: (16, 41), 13: (64, 365), 15: (128, 1094), 17: (256, 3281, 4861),
       19: (512, 9842, 16778), 21: (1024, 29525, 58598)}
ok = True
for k, obs in tab.items():
    ps = (2, 3, 5)[:len(obs)]
    st = tuple(strip(k, p) for p in ps)
    ve = tuple(verlinde((k - 1) // 2, p) for p in ps)
    print(k, D(k, 1), "observed", obs, "strip", st, "verlinde", ve)
    ok &= st == obs == ve
print("all equal:", ok)
print("strip == verlinde for k<=201, p<=31:",
      all(strip(k, p) == verlinde((k - 1) // 2, p) for k in range(1, 202, 2) for p in (2, 3, 5, 7, 11, 13, 31)))
for p in (5, 7, 11, 13):
    print("first odd k with p | det:", p, next(k for k in range(1, 400, 2) if det_val(k, p) > 0), " 4p-3 =", 4 * p - 3)
from math import sin, cos, pi
def closed(k, p):
    j = (k - 1) // 2
    return round(2 / p * sum(sin(pi * s / p) ** 2 * (2 + 2 * cos(pi * s / p)) ** j for s in range(1, p)))
print("closed form == strip, k<=61, p<=31:",
      all(closed(k, p) == strip(k, p) for k in range(1, 62, 2) for p in (2, 3, 5, 7, 11, 13, 31)))
bad = [(k, p) for k in range(1, 62, 2) for p in (2, 3, 5, 7, 11, 13, 31) if closed(k, p) != strip(k, p)]
print("first mismatches:", bad[:3], "max relative error:",
      max(abs(closed(k, p) - strip(k, p)) / strip(k, p) for k, p in bad) if bad else 0)
