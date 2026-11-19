"""Compare two order-eval dumps of the same boxes (e.g. two `reorder` runs).

    python3 tools/order-eval/compare.py BASE.json CANDIDATE.json [--top N]

Reports, for each side, against V3's own order (the reference):
  * body tau     -- mean Kendall-tau distance with Header/Footer/PageNumber excluded
  * exact        -- pages whose body order (furniture excluded) equals V3's
  * after-below  -- blocks ordered after the nearest block directly below them in
                    their column (x-overlapping), split headings / other; needs no
                    reference, so it is the cleanest regression signal (issue #223)
then lists the pages whose order changed, worst regression first.
"""
import json
import sys

FURNITURE = {'Header', 'Footer', 'PageNumber'}


def load(path):
    return {(r['Pdf'], r['Page']): r for r in json.load(open(path))['pageRecords']}


def tau(ref, seq, keep):
    a = [x for x in ref if x in keep]
    rank = {v: i for i, v in enumerate(x for x in seq if x in keep)}
    n = len(a)
    pairs = n * (n - 1) // 2
    if not pairs:
        return 0.0
    discord = sum(rank[a[i]] > rank[a[j]] for i in range(n) for j in range(i + 1, n))
    return discord / pairs


def after_below(page, seq):
    content = [m for m in page['Meta'] if m['Role'] not in FURNITURE]
    rank = {v: i for i, v in enumerate(seq)}
    counts = [0, 0, 0, 0]  # heading bad, heading total, other bad, other total
    for a in content:
        below = None
        for b in content:
            if b is a or not (a['X'] < b['X'] + b['W'] and b['X'] < a['X'] + a['W']):
                continue
            if b['Y'] < a['Y'] + a['H'] - 2:
                continue
            if below is None or b['Y'] < below['Y']:
                below = b
        if below is None:
            continue
        bad = rank[a['Id']] > rank[below['Id']]
        k = 0 if a['Role'] in ('Heading', 'Title') else 2
        counts[k] += bad
        counts[k + 1] += 1
    return counts


def main(argv):
    top = 20
    if '--top' in argv:
        i = argv.index('--top')
        top = int(argv[i + 1])
        del argv[i:i + 2]
    if len(argv) != 2:
        sys.exit(__doc__)
    base, cand = load(argv[0]), load(argv[1])
    keys = sorted(k for k in base if k in cand and base[k]['DistinctRawOrder'] > 1)
    if not keys:
        sys.exit('no pages in common')

    sums = {'base': [0.0, 0, [0, 0, 0, 0]], 'cand': [0.0, 0, [0, 0, 0, 0]]}
    changed = []
    for k in keys:
        taus = []
        for name, side in (('base', base), ('cand', cand)):
            r = side[k]
            keep = {m['Id'] for m in r['Meta'] if m['Role'] not in FURNITURE}
            t = tau(r['V3Seq'], r['OursSeq'], keep)
            taus.append(t)
            s = sums[name]
            s[0] += t
            s[1] += t == 0
            s[2] = [x + y for x, y in zip(s[2], after_below(r, r['OursSeq']))]
        if base[k]['OursSeq'] != cand[k]['OursSeq']:
            changed.append((taus[1] - taus[0], k, taus[0], taus[1]))

    n = len(keys)
    print(f'{n} pages')
    print(f'{"":6} {"body-tau":>9} {"exact":>6} {"headings after-below":>22} {"others after-below":>20}')
    for name in ('base', 'cand'):
        t, ex, (hb, hn, ob, on) = sums[name]
        print(f'{name:6} {t / n:9.4f} {ex:6} {f"{hb}/{hn}":>22} {f"{ob}/{on}":>20}')
    worse = sum(d > 0 for d, *_ in changed)
    better = sum(d < 0 for d, *_ in changed)
    print(f'changed {len(changed)} pages: {better} better, {worse} worse, {len(changed) - better - worse} same tau')
    for d, (pdf, page), t0, t1 in sorted(changed, reverse=True)[:top]:
        if d <= 0:
            break
        print(f'  worse {d:+.4f}  {t0:.3f} -> {t1:.3f}  {pdf} p{page}')


if __name__ == '__main__':
    main(sys.argv[1:])
