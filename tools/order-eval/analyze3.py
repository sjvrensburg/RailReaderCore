import json, statistics, sys
d=json.load(open(sys.argv[1] if len(sys.argv)>1 else '/tmp/ordereval.json'))
usable=[r for r in d['pageRecords'] if r['DistinctRawOrder']>1]
FURN={'Header','Footer','PageNumber'}

def col_switches(seq,meta,mid):
    s=[]
    for bid in seq:
        m=meta[bid]
        if m['Role'] in FURN: continue
        if m['W']>0.6*(mid*2): continue
        s.append(0 if m['X']+m['W']/2<mid else 1)
    return sum(1 for i in range(1,len(s)) if s[i]!=s[i-1])

narrow_gutter=0; straddle_furn=0; both=0; neither=0; total_il=0
gutters=[]
for r in usable:
    meta={m['Id']:m for m in r['Meta']}
    maxr=max(m['X']+m['W'] for m in r['Meta']); minx=min(m['X'] for m in r['Meta']); mid=(minx+maxr)/2
    body=[m for m in r['Meta'] if m['Role'] not in FURN and m['W']<0.6*maxr]
    L=[m for m in body if m['X']+m['W']/2<mid]; R=[m for m in body if m['X']+m['W']/2>=mid]
    if len(L)<2 or len(R)<2: continue
    v3sw=col_switches(r['V3Seq'],meta,mid); oursw=col_switches(r['OursSeq'],meta,mid)
    if oursw-v3sw<3: continue
    total_il+=1
    # gutter width = right col min-left minus left col max-right
    gw=min(m['X'] for m in R)-max(m['X']+m['W'] for m in L)
    gutters.append(gw)
    ng = gw < 12
    # furniture (or any block) straddling the gutter midline band
    gmid=(min(m['X'] for m in R)+max(m['X']+m['W'] for m in L))/2 if gw>0 else mid
    sf=any(m['X']<gmid<m['X']+m['W'] for m in r['Meta'])  # ANY block straddles
    if ng and sf: both+=1
    elif ng: narrow_gutter+=1
    elif sf: straddle_furn+=1
    else: neither+=1

print(f"interleaving pages analyzed: {total_il}")
print(f"  narrow gutter (<12pt) ONLY:         {narrow_gutter}")
print(f"  gutter-straddling block ONLY:       {straddle_furn}")
print(f"  BOTH narrow gutter & straddler:     {both}")
print(f"  neither (other cause):              {neither}")
print(f"  => narrow gutter involved: {narrow_gutter+both} ({100*(narrow_gutter+both)/total_il:.0f}%)")
print(f"  => straddler involved:     {straddle_furn+both} ({100*(straddle_furn+both)/total_il:.0f}%)")
if gutters:
    gutters.sort()
    print(f"\ngutter width on interleaving pages: median={statistics.median(gutters):.1f}pt "
          f"p25={gutters[len(gutters)//4]:.1f} p75={gutters[3*len(gutters)//4]:.1f} "
          f"min={gutters[0]:.1f} max={gutters[-1]:.1f}")
    for thr in [6,8,10,12,14,16,20]:
        print(f"   gutter < {thr:2d}pt: {sum(1 for g in gutters if g<thr)} ({100*sum(1 for g in gutters if g<thr)/len(gutters):.0f}%)")
