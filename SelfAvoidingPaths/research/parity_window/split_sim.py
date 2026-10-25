"""Two-machine class-partitioned sweep using the literal engine update (literal.py).
Key f_a = parity of occupancy over positions [0..a]; valid for every update except p=a+1."""
from literal import *
import sys
def occ_prefix(s,a): return sum(1 for k in range(a+1) if s[k])&1
def run(n, full=True, verbose=False):
    S=sorted(s for s in itertools.product(range(3),repeat=n) if valid(s))
    key=lambda s,a: occ_prefix(s,a)
    a=0; lam=0
    V=[{},{}]; P=[{},{}]   # machine -> value, deferred
    r=root(n); V[key(r,a)^lam][r]=1
    last_p=None            # last update index applied (pending expands with insert at last_p-1)
    stats=[]; cross=0
    nrows=n
    for row in range(nrows):
        for p in range(n-1,0,-1):
            if p==a+1:   # current key invalid for this update: switch
                b=a+1 if a+1<=n-2 else 0
                # entries: value states and pending (expanded with insert at last_p-1)
                ent=[]   # (machine, kind, key, count, fullstate)
                for m in (0,1):
                    for s,c in V[m].items():
                        if c: ent.append((m,'V',s,c,s))
                    for k,c in P[m].items():
                        if c:
                            q=(last_p-1) if last_p is not None else p
                            ent.append((m,'P',k,c,k[:q]+(D,)+k[q:]))
                best=None
                for l2 in (0,1):
                    movedV=sum(1 for m,kd,k,c,f in ent if kd=='V' and (key(f,b)^l2)!=m)
                    movedP=sum(1 for m,kd,k,c,f in ent if kd=='P' and (key(f,b)^l2)!=m)
                    if best is None or movedV+movedP<best[0]: best=(movedV+movedP,movedV,movedP,l2)
                _,movedV,movedP,l2=best
                nV=sum(1 for e in ent if e[1]=='V'); nP=sum(1 for e in ent if e[1]=='P')
                stats.append((row,p,a,b,nV,movedV,nP,movedP))
                newV=[{},{}]; newP=[{},{}]
                for m,kd,k,c,f in ent:
                    nm=key(f,b)^l2
                    (newV if kd=='V' else newP)[nm][k]=c
                V,P=newV,newP; a=b; lam=l2
            # run the literal update independently inside each class
            for m in (0,1):
                order=[s for s in S if key(s,a)^lam==m]
                literal_update(n,p,V[m],P[m],order)
            # check support stays inside own class (value states and expanded pending)
            for m in (0,1):
                for s,c in V[m].items():
                    if c and (key(s,a)^lam)!=m: cross+=1
                for k,c in P[m].items():
                    if c:
                        f=k[:p-1]+(D,)+k[p-1:]
                        if (key(f,a)^lam)!=m: cross+=1
            last_p=p
    ans=V[0].get(accept(n),0)+V[1].get(accept(n),0)
    return ans,cross,stats
if __name__=="__main__":
    for n in range(3,int(sys.argv[1])+1):
        ans,cross,stats=run(n)
        print(f"n={n} answer={ans} known={KNOWN.get(n)} match={ans==KNOWN.get(n)} cross_class_entries={cross} switches={len(stats)}")
    if len(sys.argv)>2:
        n=int(sys.argv[2]); ans,cross,stats=run(n)
        print("n=",n,"switch table: row p a->b | live main moved (frac) | pending live moved")
        for row,p,a,b,nV,mV,nP,mP in stats:
            print(f"row {row:2d} p={p:2d} {a:2d}->{b:2d} | {nV:6d} {mV:6d} ({mV/max(nV,1):.3f}) | {nP:6d} {mP:6d}")
