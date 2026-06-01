import json, sys, collections, statistics

data = json.load(open(sys.argv[1]))
recs = data['pageRecords']
usable = [r for r in recs if r['DistinctRawOrder'] > 1]

def rank(seq): return {v: i for i, v in enumerate(seq)}

def discordant_pairs(r):
    rb = rank(r['OursSeq']); a = r['V3Seq']; out = []
    for i in range(len(a)):
        for j in range(i+1, len(a)):
            if rb[a[i]] > rb[a[j]]:
                out.append((a[i], a[j]))
    return out

print(f"pages={len(recs)} usable(V3 non-degenerate)={len(usable)}")
if not usable: sys.exit()
taus = [r['Tau'] for r in usable]
print(f"mean tau={statistics.mean(taus):.4f} median={statistics.median(taus):.4f}")
print(f"exact match={sum(1 for t in taus if t==0)} ({100*sum(1 for t in taus if t==0)/len(taus):.1f}%)")
for lo,hi in [(0,.02),(.02,.05),(.05,.1),(.1,.2),(.2,1.01)]:
    c=sum(1 for t in taus if (0<=t<=hi if lo==0 else lo<t<=hi))
    print(f"  tau ({lo:.2f},{hi:.2f}]: {c} ({100*c/len(taus):.1f}%)")

# page 0 (title pages) vs interior
t_title=[r['Tau'] for r in usable if r['Page']==0]
t_inner=[r['Tau'] for r in usable if r['Page']>0]
if t_title: print(f"\ntitle pages (p0): n={len(t_title)} mean tau={statistics.mean(t_title):.4f}")
if t_inner: print(f"interior pages:    n={len(t_inner)} mean tau={statistics.mean(t_inner):.4f}")

rolepair = collections.Counter(); role_involved=collections.Counter()
for r in usable:
    meta = {m['Id']: m for m in r['Meta']}
    for (a,b) in discordant_pairs(r):
        ra, rb = meta[a]['Role'], meta[b]['Role']
        rolepair[tuple(sorted((ra,rb)))]+=1
        role_involved[ra]+=1; role_involved[rb]+=1

print("\nTop role-pairs in disagreements:")
for k,v in rolepair.most_common(15): print(f"  {v:6d}  {k[0]} <-> {k[1]}")
print("\nRole involvement in disagreements:")
for k,v in role_involved.most_common(): print(f"  {v:6d}  {k}")

print("\n=== WORST 15 INTERIOR PAGES BY TAU ===")
for r in sorted([r for r in usable if r['Page']>0], key=lambda r:-r['Tau'])[:15]:
    print(f"\n--- {r['Dir']}/{r['Pdf']} p{r['Page']} tau={r['Tau']:.3f} n={r['NBlocks']} ---")
    meta={m['Id']:m for m in r['Meta']}
    v3rank=rank(r['V3Seq']); ourrank=rank(r['OursSeq'])
    for bid in r['V3Seq']:
        m=meta[bid]; flag='  <<DIFF' if v3rank[bid]!=ourrank[bid] else ''
        print(f"  v3#{v3rank[bid]:2d} ours#{ourrank[bid]:2d} {m['Role']:<9} x={m['X']:5.0f} y={m['Y']:5.0f} w={m['W']:4.0f} h={m['H']:4.0f} | {m['Text'][:38]}{flag}")
