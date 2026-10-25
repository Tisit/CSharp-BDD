# literal transliteration of DenseCounter.UpdateBlockGeneric on dict stores
from parity_proto import *
import itertools, sys
def shrink(s,k): return s[:k]+s[k+1:]          # remove index k (tuple indexed by position)
def valid(s):
    h=1
    for c in reversed(s):
        h+=(c==L)-(c==R)
        if h<0: return False
    return h==0
def literal_update(n,p,value,deferred,order):
    for c in order:
        mate=c
        pair=(mate[p],mate[p-1])
        v=value.get(c,0)
        x,y=pair
        if x==D and y==D:
            d=shrink(mate,p)
            if v:
                m2=list(mate); m2[p]=L; m2[p-1]=R; m2=tuple(m2)
                value[m2]=value.get(m2,0)+v
            value[c]=value.get(c,0)+deferred.get(d,0); deferred[d]=0
        elif x==D and y!=D:       # [.x] -> [x.]
            d=shrink(mate,p); m2=list(mate); m2[p],m2[p-1]=y,x; cc=tuple(m2)
            if p==1:
                deferred[d]=deferred.get(d,0)+value.get(cc,0)
                value[cc]=value.get(cc,0)+value.get(c,0)
                value[c]=value.get(c,0)+deferred[d]; deferred[d]=0
            else:
                tmp=value.get(c,0)
                value[c]=value.get(c,0)+value.get(cc,0)+deferred.get(d,0)
                deferred[d]=tmp
        elif x==L and y==L:
            m2=list(mate); m2[p]=m2[p-1]=D; q=partner_right(mate,p-1); m2[q]=L; emit(p,tuple(m2),c,value,deferred)
        elif x==R and y==R:
            m2=list(mate); m2[p]=m2[p-1]=D; q=partner_left(mate,p)
            if q is not None: m2[q]=R
            emit(p,tuple(m2),c,value,deferred)
        elif x==R and y==L:
            m2=list(mate); m2[p]=m2[p-1]=D; emit(p,tuple(m2),c,value,deferred)
def emit(p,m2,c,value,deferred):
    if p==1: value[m2]=value.get(m2,0)+value.get(c,0)
    else:
        k=shrink(m2,p-1); deferred[k]=deferred.get(k,0)+value.get(c,0)
def expand(n,p,value,deferred):
    E=defaultdict(int)
    for s,c in value.items():
        if c: E[s]+=c
    for k,c in deferred.items():
        if c:
            full=k[:p-1]+(D,)+k[p-1:]; E[full]+=c
    return E
if __name__=="__main__":
    for n in range(3,8):
        S=sorted(s for s in itertools.product(range(3),repeat=n) if valid(s))
        for name,order in (("asc",S),("desc",S[::-1])):
            value={root(n):1}; deferred={}
            e_cmp=True
            Eprev=dict(value)
            for row in range(n):
                for p in range(n-1,0,-1):
                    literal_update(n,p,value,deferred,order)
                    E=expand(n,p,value,deferred)
                    A=defaultdict(int)
                    for s,c in Eprev.items():
                        for t in trans(s,p): A[t]+=c
                    A={k:v for k,v in A.items() if v}
                    if dict(E)!=A: e_cmp=False
                    Eprev=dict(E)
            print(n,name,"answer",value.get(accept(n),0),"known",KNOWN[n],"E==atomic at every step:",e_cmp)
