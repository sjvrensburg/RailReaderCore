#!/usr/bin/env python3
"""
Fetch a sample of the SynFinTabs financial-table dataset (ethanbradley/synfintabs)
and write a compact JSON the C# TableRowEval harness can score against.

SynFinTabs ships, per example: the table image, the table bbox, and a full
row -> cell -> word hierarchy with bounding boxes. All bboxes are
[left, top, right, bottom] in image-pixel coordinates (verified against the
data), which maps directly onto RailReader's CharBox/BBox. That makes the
dataset a turnkey ROW oracle for table line detection — no Surya needed.

We keep only what the row scorer needs:
  - table_bbox: [l, t, r, b]
  - rows:       [[l, t, r, b], ...]   ground-truth semantic rows
  - words:      [[l, t, r, b], ...]   every word box, flattened across cells
                (synthesised into CharBoxes to drive LineDetector)

Usage:
  python3 fetch_synfintabs.py [--n 200] [--split test] [--out data/synfintabs-test.json]
"""
import argparse
import json
import os
import urllib.request

API = "https://datasets-server.huggingface.co/rows"
DATASET = "ethanbradley/synfintabs"
PAGE = 100  # datasets-server max rows per request


def fetch_page(split, offset, length):
    url = f"{API}?dataset={DATASET}&config=default&split={split}&offset={offset}&length={length}"
    with urllib.request.urlopen(url, timeout=60) as r:
        return json.load(r)["rows"]


def compact(example):
    words = []
    cells = []
    for ri, row in enumerate(example["rows"]):
        for cell in row["cells"]:
            cw = [w["bbox"] for w in cell["words"]]
            words.extend(cw)
            # Only word-bearing cells are navigable targets. Record the cell's
            # word-ink span [minLeft, maxRight] (what the detector actually sees,
            # vs the padded grid bbox) plus the grid bbox, tagged with its row.
            if cw:
                cells.append({
                    "row": ri,
                    "bbox": cell["bbox"],
                    "wspan": [min(b[0] for b in cw), max(b[2] for b in cw)],
                    "nwords": len(cw),
                })
    return {
        "id": example["id"],
        "table_bbox": example["bbox"],
        "rows": [row["bbox"] for row in example["rows"]],
        "cells": cells,
        "words": words,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--n", type=int, default=200, help="number of examples")
    ap.add_argument("--split", default="test", choices=["train", "validation", "test"])
    ap.add_argument("--out", default=None)
    args = ap.parse_args()

    out = args.out or os.path.join(os.path.dirname(__file__), "data", f"synfintabs-{args.split}.json")
    os.makedirs(os.path.dirname(out), exist_ok=True)

    examples = []
    offset = 0
    while len(examples) < args.n:
        length = min(PAGE, args.n - len(examples))
        rows = fetch_page(args.split, offset, length)
        if not rows:
            break
        for r in rows:
            examples.append(compact(r["row"]))
        print(f"  fetched {len(examples)}/{args.n}")
        # Advance by what the API actually returned, not what we asked for: a short
        # (but non-empty) page would otherwise skip the un-returned rows.
        offset += len(rows)

    with open(out, "w") as f:
        json.dump({"examples": examples}, f)
    print(f"Wrote {out} ({len(examples)} examples)")


if __name__ == "__main__":
    main()
