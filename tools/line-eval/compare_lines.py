#!/usr/bin/env python3
"""Scores our LineDetector output (ours.json from LineEval) against the Surya
line oracle (oracle.json from line_oracle.py).

For each non-atomic text block, the oracle lines whose centre falls inside the
block are the ground truth. A 1-D (Y-axis) coverage match is used: an oracle
line is matched to our line with the most Y-overlap if overlap >= 0.5 of the
oracle line height. Reports precision/recall/F1, line-count delta, and the worst
blocks. Born-digital (text layer) vs scanned pages are reported separately,
since char-clustering vs pixel-projection are different code paths.

Usage: compare_lines.py <ours.json> <oracle.json>
"""
import sys, json, statistics
from collections import defaultdict

ours = json.load(open(sys.argv[1]))["pages"]
oracle = json.load(open(sys.argv[2]))["pages"]

# Roles we DON'T score: atomic (one line by design) + margin furniture.
SKIP = {"Figure", "Chart", "Table", "Header", "Footer", "PageNumber", "Decoration"}

ora_by = {(p["pdf"], p["page"]): p for p in oracle}

def yband_ours(line):      # [y, h] -> (top, bottom)
    y, h = line; return (y - h / 2.0, y + h / 2.0)

def overlap(a, b):
    return max(0.0, min(a[1], b[1]) - max(a[0], b[0]))

def score_block(our_lines, gt_lines):
    """Greedy 1-D coverage match. Returns (tp, fp, fn, ious)."""
    our_bands = [yband_ours(l) for l in our_lines]
    gt_bands = [(y1, y2) for (_, y1, _, y2) in gt_lines]
    used = [False] * len(our_bands)
    tp = 0; ious = []
    for gt in gt_bands:
        gh = gt[1] - gt[0]
        if gh <= 0: continue
        best, bj = 0.0, -1
        for j, ob in enumerate(our_bands):
            if used[j]: continue
            ov = overlap(gt, ob)
            if ov > best: best, bj = ov, j
        if bj >= 0 and best >= 0.5 * gh:
            used[bj] = True; tp += 1
            union = max(gt[1], our_bands[bj][1]) - min(gt[0], our_bands[bj][0])
            ious.append(best / union if union > 0 else 0)
    fp = used.count(False)
    fn = len(gt_bands) - tp
    return tp, fp, fn, ious

def run(filter_text=None):
    TP = FP = FN = 0; ious = []; count_err = []; nblocks = 0
    worst = []
    for pg in ours:
        if filter_text is not None and pg["hasText"] != filter_text: continue
        key = (pg["pdf"], pg["page"])
        if key not in ora_by: continue
        gl = ora_by[key]["lines"]
        for blk in pg["blocks"]:
            if blk["role"] in SKIP: continue
            bx, by, bw, bh = blk["x"], blk["y"], blk["w"], blk["h"]
            gt = [L for L in gl
                  if bx <= (L[0] + L[2]) / 2 <= bx + bw and by <= (L[1] + L[3]) / 2 <= by + bh]
            if not gt: continue          # block region the oracle found no lines in
            nblocks += 1
            tp, fp, fn, bi = score_block(blk["lines"], gt)
            TP += tp; FP += fp; FN += fn; ious += bi
            count_err.append(abs(len(blk["lines"]) - len(gt)))
            f1 = (2 * tp / (2 * tp + fp + fn)) if (2 * tp + fp + fn) else 1.0
            worst.append((f1, len(blk["lines"]), len(gt), blk["role"], pg["pdf"], pg["page"]))
    prec = TP / (TP + FP) if TP + FP else 0
    rec = TP / (TP + FN) if TP + FN else 0
    f1 = 2 * prec * rec / (prec + rec) if prec + rec else 0
    label = {None: "ALL", True: "born-digital", False: "scanned"}[filter_text]
    print(f"\n=== {label} ===  blocks={nblocks}")
    if nblocks == 0: return
    print(f"line precision={prec:.3f} recall={rec:.3f} F1={f1:.3f}")
    print(f"mean |count delta|={statistics.mean(count_err):.2f}  "
          f"exact-count blocks={100*sum(1 for c in count_err if c==0)/len(count_err):.1f}%  "
          f"mean matched Y-IoU={statistics.mean(ious) if ious else 0:.3f}")
    if filter_text is None:
        print("worst blocks (F1, ours, gt, role, pdf p):")
        for w in sorted(worst)[:15]:
            print(f"  F1={w[0]:.2f} ours={w[1]:>2} gt={w[2]:>2} {w[3]:<9} {w[4][:38]} p{w[5]}")

run(None); run(True); run(False)
