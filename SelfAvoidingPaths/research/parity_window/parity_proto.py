"""Prototype of the parity-window schedule on the dense engine's state language.
States: tuple s[0..n-1], index = position (0 rightmost), symbols 0='.',1='(',2=')'.
Valid = Motzkin string read left->right (descending index) from height 1 (virtual '(' on the left) to 0."""
import sys, random, itertools
from collections import defaultdict
sys.setrecursionlimit(10000)
D,L,R=0,1,2
KNOWN={2:2,3:12,4:184,5:8512,6:1262816,7:575780564,8:789360053252,9:3266598486981642,10:41044208702632496496}

def partner_right(s,p):   # '(' at p -> matching ')' scanning right (indices decreasing)
    d=1
    for k in range(p-1,-1,-1):
        if s[k]==L: d+=1
        elif s[k]==R:
            d-=1
            if d==0: return k
    raise ValueError
def partner_left(s,p):    # ')' at p -> matching '(' scanning left (indices increasing); n = virtual
    d=1
    for k in range(p+1,len(s)):
        if s[k]==R: d+=1
        elif s[k]==L:
            d-=1
            if d==0: return k
    return None
def trans(s,p):
    """outcomes (list of new states) of the vertex update on pair (p,p-1); x=s[p] left, y=s[p-1] right"""
    x,y=s[p],s[p-1]
    out=[]
    def mk(**kw): pass
    if x==D and y==D:
        t=list(s); t[p]=L; t[p-1]=R
        return [s,tuple(t)]
    if (x==D)!=(y==D):
        t=list(s); t[p],t[p-1]=y,x
        return [s,tuple(t)]
    if x==L and y==L:
        q=partner_right(s,p-1); t=list(s); t[p]=t[p-1]=D; t[q]=L; return [tuple(t)]
    if x==R and y==R:
        q=partner_left(s,p)
        t=list(s); t[p]=t[p-1]=D
        if q is not None: t[q]=R
        return [tuple(t)]
    if x==R and y==L:
        t=list(s); t[p]=t[p-1]=D; return [tuple(t)]
    return []   # '()' closes a cycle
def occ(s): return [1 if c else 0 for c in s]
def fkey(s,a): return sum(1 for k in range(a+1) if s[k])&1      # prefix parity over [0..a]
def gkey(s,b): return sum(1 for k in range(b,len(s)) if s[k])&1  # suffix parity over [b..n-1]

def root(n):
    t=[D]*n; t[n-1]=R; return tuple(t)
def accept(n):
    t=[D]*n; t[0]=R; return tuple(t)
def sweep_plain(n):
    v={root(n):1}
    for row in range(n):
        for p in range(n-1,0,-1):
            nv=defaultdict(int)
            for s,c in v.items():
                for t in trans(s,p): nv[t]+=c
            v=nv
    return v.get(accept(n),0)

def sweep_parity(n):
    """class-partitioned sweep. key schedule: row r uses f_r for p>=r+2, switches to f_{r+1} before p=r+1
    (prefix drift; valid while r+1<=n-2). We record cross-class writes (must be 0) and moved states."""
    v={root(n):1}
    a=0; stats=[]
    cross=0; maxrow=0
    for row in range(n):
        for p in range(n-1,0,-1):
            if a+1<=n-2 and p==a+1:     # switch f_a -> f_{a+1} before this update
                # alternating labeling: machine = f_a ^ (a%2)
                moved=sum(1 for s in v if (fkey(s,a)^(a&1))!=(fkey(s,a+1)^((a+1)&1)))
                stats.append((row,p,len(v),moved,sum(1 for s in v if s[a+1]==D)))
                a+=1
            nv=defaultdict(int)
            for s,c in v.items():
                ks=fkey(s,a)
                for t in trans(s,p):
                    if fkey(t,a)!=ks: cross+=1
                    nv[t]+=c
            v=nv
    return v.get(accept(n),0),cross,stats

def sample_states(n,count,rng):
    # N[h][m] = ways to finish m positions from height h ending at 0, never below 0
    N=[[0]*(n+2) for _ in range(n+3)]
    N[0][0]=1
    for m in range(1,n+1):
        for h in range(n+1):
            N[h][m]=N[h][m-1]*1  # '.'
            N[h][m]+=N[h+1][m-1]
            if h>0: N[h][m]+=N[h-1][m-1]
    out=[]
    for _ in range(count):
        h=1; sym=[]
        for pos in range(n):          # left -> right
            m=n-pos-1
            w=[N[h][m],N[h+1][m],N[h-1][m] if h>0 else 0]
            r=rng.random()*sum(w)
            c=0 if r<w[0] else (1 if r<w[0]+w[1] else 2)
            sym.append(c); h+= (c==1)-(c==2)
        out.append(tuple(reversed(sym)))
    return out,N

def count_with_dot_at(n,pos_idx,N):
    """number of valid states with '.' at index pos_idx (0=rightmost)"""
    # left->right DP over heights
    cur={1:1}
    for pos in range(n):
        idx=n-1-pos; nxt=defaultdict(int)
        for h,c in cur.items():
            nxt[h]+=c
            if idx!=pos_idx:
                nxt[h+1]+=c
                if h>0: nxt[h-1]+=c
        cur=nxt
    return cur.get(0,0)
def total_states(n):
    cur={1:1}
    for pos in range(n):
        nxt=defaultdict(int)
        for h,c in cur.items():
            nxt[h]+=c; nxt[h+1]+=c
            if h>0: nxt[h-1]+=c
        cur=nxt
    return cur.get(0,0)

if __name__=="__main__":
    mode=sys.argv[1]
    if mode=="verify":
        for n in range(2,10):
            plain=sweep_plain(n)
            par,cross,stats=sweep_parity(n)
            print(f"n={n} known={KNOWN[n]} plain={plain} parity={par} cross_class_writes={cross} ok={plain==KNOWN[n]==par}")
    elif mode=="stats":
        n=int(sys.argv[2]); par,cross,stats=sweep_parity(n)
        print("answer",par,"cross",cross)
        for row,p,tot,moved,dots in stats: print(f"row {row} switch@p={p}: live={tot} moved={moved} ({moved/tot:.3f}) dots-at-a+1={dots/tot:.3f}")
    elif mode=="sample":
        n=int(sys.argv[2]); cnt=int(sys.argv[3]); rng=random.Random(1)
        states,N=sample_states(n,cnt,rng)
        bad=0; tested=0; viol=defaultdict(int)
        for s in states:
            for p in range(n-1,0,-1):
                for t in trans(s,p):
                    tested+=1
                    for a in range(n-1):
                        if fkey(s,a)!=fkey(t,a):
                            viol[(p,a)]+=1
        offdiag=[k for k in viol if k[1]!=p-1 and k[1]+1!=k[0]]
        print(f"n={n} sampled {cnt} states, {tested} transitions")
        print("violations only at (p, a=p-1):", all(a+1==p for (p,a) in viol), "pairs seen:", sorted(viol)[:6],"...")
        # g keys: suffix parity invalid only at p==b
        v2=defaultdict(int)
        for s in states[:cnt//4]:
            for p in range(n-1,0,-1):
                for t in trans(s,p):
                    for b in range(1,n):
                        if gkey(s,b)!=gkey(t,b): v2[(p,b)]+=1
        print("suffix violations only at b==p:", all(b==p for (p,b) in v2))
    elif mode=="sizes":
        for n in map(int,sys.argv[2:]):
            _,N=sample_states(n,1,random.Random(0))
            tot=total_states(n)
            fr=[count_with_dot_at(n,i,N)/tot for i in range(n)]
            print(f"n={n} states={tot:,} P('.') per position idx0..: "+" ".join(f"{x:.3f}" for x in fr))
