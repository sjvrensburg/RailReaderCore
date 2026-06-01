#!/usr/bin/env python3
"""Surya text-line-detection oracle: renders corpus pages and dumps line boxes
(in PDF page points, top-left origin) to JSON, to score our LineDetector against.

Usage:
  line_oracle.py <out.json> <pdf-dir> [<pdf-dir> ...]
Env: LINEEVAL_MAXPAGES (default 6), LINE_RENDER_SCALE (px per point, default 2.5)
"""
import os, sys, glob, json
import pypdfium2 as pdfium
from PIL import Image
from surya.detection import DetectionPredictor

out_path = sys.argv[1]
dirs = sys.argv[2:]
maxpages = int(os.environ.get("LINEEVAL_MAXPAGES", "6"))
scale = float(os.environ.get("LINE_RENDER_SCALE", "2.5"))  # px per point

pdfs = []
for d in dirs:
    pdfs += glob.glob(os.path.join(d, "**", "*.pdf"), recursive=True)
pdfs.sort()
print(f"{len(pdfs)} PDFs; maxpages/doc={maxpages} scale={scale}", file=sys.stderr)

det = DetectionPredictor()

def line_boxes(img):
    res = det([img])[0]
    out = []
    for b in res.bboxes:
        x1, y1, x2, y2 = b.bbox
        out.append([x1 / scale, y1 / scale, x2 / scale, y2 / scale])  # -> page points
    return out

pages = []
for di, path in enumerate(pdfs, 1):
    try:
        doc = pdfium.PdfDocument(path)
    except Exception as e:
        print(f"[{di}] OPEN FAIL {os.path.basename(path)}: {e}", file=sys.stderr)
        continue
    n = min(len(doc), maxpages)
    print(f"[{di}/{len(pdfs)}] {os.path.basename(path)} ({len(doc)}p, dump {n})", file=sys.stderr)
    for p in range(n):
        try:
            page = doc[p]
            w_pt, h_pt = page.get_size()
            pil = page.render(scale=scale).to_pil().convert("RGB")
            boxes = line_boxes(pil)
            pages.append({
                "dir": os.path.basename(os.path.dirname(path)),
                "pdf": os.path.basename(path), "page": p,
                "pw": w_pt, "ph": h_pt, "lines": boxes,
            })
        except Exception as e:
            print(f"    page {p} ERR: {e}", file=sys.stderr)
    doc.close()

with open(out_path, "w") as f:
    json.dump({"pages": pages}, f)
print(f"Wrote {out_path} ({len(pages)} pages)", file=sys.stderr)
