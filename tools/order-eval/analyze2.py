import json, sys, collections, statistics

data = json.load(open(sys.argv[1]))
usable = [r for r in data['pageRecords'] if r['DistinctRawOrder'] > 1]
FURNITURE = {'Header','Footer','PageNumber'}

def rank(seq): return {v:i for i,v in enumerate(seq)}

def tau_on(r, keep_ids):
    a=[x for x in r['V3Seq'] if x in keep_ids]
    rb=rank([x for x in r['OursSeq'] if x in keep_ids])
    n=len(a); d=0; t=0
    for i in range(n):
        for j in range(i+1,n):
            t+=1
            if rb[a[i]]>rb[a[j]]: d+=1
    return (d/t) if t else 0.0, n

def col_switches(seq, meta, midline):
    # number of times consecutive body blocks change side of the page midline
    sides=[]
    for bid in seq:
        m=meta[bid]
        if m['Role'] in FURNITURE: continue
        cx=m['X']+m['W']/2
        if m['W']>0.6*(midline*2): continue  # skip full-width spanners
        sides.append(0 if cx<midline else 1)
    return sum(1 for i in range(1,len(sides)) if sides[i]!=sides[i-1]), len(sides)

# --- furniture-excluded tau ---
body_taus=[]; full_taus=[]
interleave_pages=[]
two_col_pages=0
for r in usable:
    meta={m['Id']:m for m in r['Meta']}
    body_ids={m['Id'] for m in r['Meta'] if m['Role'] not in FURNITURE}
    bt,bn = tau_on(r, body_ids)
    if bn>=3:
        body_taus.append(bt); full_taus.append(r['Tau'])
    # column analysis
    maxright=max(m['X']+m['W'] for m in r['Meta'])
    minx=min(m['X'] for m in r['Meta'])
    midline=(minx+maxright)/2
    # is two-column? left & right both have >=2 body blocks
    lefts=[m for m in r['Meta'] if m['Role'] not in FURNITURE and m['X']+m['W']/2<midline and m['W']<0.6*maxright]
    rights=[m for m in r['Meta'] if m['Role'] not in FURNITURE and m['X']+m['W']/2>=midline and m['W']<0.6*maxright]
    if len(lefts)>=2 and len(rights)>=2:
        two_col_pages+=1
        v3sw,_=col_switches(r['V3Seq'],meta,midline)
        oursw,nb=col_switches(r['OursSeq'],meta,midline)
        # interleaving: ours switches columns substantially more than V3
        if oursw-v3sw>=3:
            interleave_pages.append((r,v3sw,oursw,nb))

print(f"usable pages={len(usable)}")
print(f"\n--- furniture-excluded (body-only) tau, pages with >=3 body blocks: {len(body_taus)} ---")
print(f"mean FULL tau={statistics.mean(full_taus):.4f}   mean BODY-only tau={statistics.mean(body_taus):.4f}")
print(f"body-only exact match={sum(1 for t in body_taus if t==0)} ({100*sum(1 for t in body_taus if t==0)/len(body_taus):.1f}%)")
for lo,hi in [(0,.02),(.02,.05),(.05,.1),(.1,.2),(.2,1.01)]:
    c=sum(1 for t in body_taus if (0<=t<=hi if lo==0 else lo<t<=hi))
    print(f"  body tau ({lo:.2f},{hi:.2f}]: {c} ({100*c/len(body_taus):.1f}%)")

print(f"\n--- column interleaving ---")
print(f"two-column pages: {two_col_pages}")
print(f"pages where OURS interleaves columns (ours switches >= v3+3): {len(interleave_pages)} "
      f"({100*len(interleave_pages)/max(two_col_pages,1):.1f}% of two-col)")
print("worst interleavers (v3 switches -> ours switches, body blocks):")
for r,v3sw,oursw,nb in sorted(interleave_pages,key=lambda x:-(x[2]-x[1]))[:12]:
    print(f"  {r['Tau']:.3f} v3sw={v3sw:2d} ours_sw={oursw:2d} nb={nb:2d}  {r['Dir']}/{r['Pdf'][:40]} p{r['Page']}")

# contribution of interleaving to overall tau
il_ids={id(r) for r,_,_,_ in interleave_pages}
il_tau=[r['Tau'] for r,_,_,_ in interleave_pages]
rest=[r['Tau'] for r in usable if id(r) not in il_ids]
if il_tau:
    print(f"\nmean tau on interleaving pages={statistics.mean(il_tau):.4f} vs rest={statistics.mean(rest):.4f}")
