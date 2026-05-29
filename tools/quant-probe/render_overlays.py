#!/usr/bin/env python3
"""
Visual accuracy eyeball: FP32 vs backbone-INT8 Heron detections, side by side,
drawn on the real page image. Lets a human judge real quality, not just
FP32-agreement numbers.

For a handful of held-out eval pages (incl. math/table-heavy ones), runs both
models with the production HeronLayoutAnalyzer post-processing, draws boxes
coloured by class with a {class:score} label, and writes a side-by-side PNG:
  left = FP32 (reference), right = INT8 (backbone-only).
Output: tools/quant-probe/overlays/*.png
"""
import os, re, glob
import numpy as np
from PIL import Image, ImageDraw, ImageFont
import onnxruntime as ort

RAST = "/home/stefan/RailReaderCore/tools/quant-probe/rasters"
OUT = "/home/stefan/RailReaderCore/tools/quant-probe/overlays"
FP32 = "/home/stefan/railreader2/experiments/docling-layout/heron.onnx"
INT8 = "/home/stefan/RailReaderCore/tools/quant-probe/out/heron_backbone_int8.onnx"
IN = 640; CONF = 0.4; NMS_IOU = 0.5; MIN_PX = 5.0
NAME_RE = re.compile(r"_p(\d+)_(\d+)x(\d+)\.rgb$")
os.makedirs(OUT, exist_ok=True)

CLASS = ["caption","footnote","formula","list_item","page_footer","page_header",
         "picture","section_header","table","text","title","document_index","code",
         "checkbox_selected","checkbox_unselected","key_value_region","other","form","code_block"]

# distinct-ish colours per class id
PALETTE = [
    (230,25,75),(60,180,75),(0,130,200),(245,130,48),(145,30,180),
    (70,240,240),(240,50,230),(210,245,60),(250,190,212),(0,128,128),
    (220,190,255),(170,110,40),(255,250,200),(128,0,0),(170,255,195),
    (128,128,0),(255,215,180),(0,0,128),(128,128,128),
]

# pick a diverse, deterministic subset of held-out pages (p2/p3)
WANT = [
    "generalized-kernel-regularized-least-squares_ann",  # math/stats
    "14-ar-ma-models",                                   # equations
    "1-s2.0-S0038092X16302201-main",                     # dense, many blocks
    "arxiv-2501.10084",                                  # very dense
    "energies-17-02346",                                 # tables/figures
    "TimeBERT",                                          # mixed
]


def load(path):
    m = NAME_RE.search(path); page, w, h = int(m.group(1)), int(m.group(2)), int(m.group(3))
    hwc = np.fromfile(path, np.uint8).reshape(h, w, 3)
    return page, w, h, hwc


def feed(hwc, w, h):
    img = Image.fromarray(hwc, "RGB").resize((IN, IN), Image.BILINEAR)
    chw = np.transpose(np.asarray(img, np.uint8), (2, 0, 1))[None, ...]
    return {"images": chw, "orig_target_sizes": np.array([[w, h]], np.int64)}


def sess(p):
    so = ort.SessionOptions(); so.log_severity_level = 3
    return ort.InferenceSession(p, so, providers=["CPUExecutionProvider"])


def iou(a,b):
    x1=max(a[0],b[0]);y1=max(a[1],b[1]);x2=min(a[2],b[2]);y2=min(a[3],b[3])
    inter=max(0.,x2-x1)*max(0.,y2-y1)
    if inter<=0: return 0.
    ua=(a[2]-a[0])*(a[3]-a[1])+(b[2]-b[0])*(b[3]-b[1])-inter
    return inter/ua if ua>0 else 0.


def detect(s, hwc, w, h):
    names=[o.name for o in s.get_outputs()]
    r=dict(zip(names, s.run(names, feed(hwc, w, h))))
    lab=np.asarray(r["labels"]).reshape(-1)
    bx=np.asarray(r["boxes"]).reshape(-1,4)
    sc=np.asarray(r["scores"]).reshape(-1)
    d=[]
    for l,b,s_ in zip(lab,bx,sc):
        if not np.isfinite(s_) or s_<CONF: continue
        x1=max(0.,float(b[0]));y1=max(0.,float(b[1]));x2=min(float(w),float(b[2]));y2=min(float(h),float(b[3]))
        if (x2-x1)<MIN_PX or (y2-y1)<MIN_PX: continue
        d.append((int(l),float(s_),[x1,y1,x2,y2]))
    d.sort(key=lambda t:-t[1]); keep=[True]*len(d)
    for i in range(len(d)):
        if not keep[i]: continue
        for j in range(i+1,len(d)):
            if keep[j] and iou(d[i][2],d[j][2])>NMS_IOU: keep[j]=False
    return [x for x,k in zip(d,keep) if k]


def draw(hwc, dets, title):
    img = Image.fromarray(hwc, "RGB").convert("RGB")
    dr = ImageDraw.Draw(img)
    try: font = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", 13)
    except Exception: font = ImageFont.load_default()
    for cid, sc, (x1,y1,x2,y2) in dets:
        col = PALETTE[cid % len(PALETTE)]
        dr.rectangle([x1,y1,x2,y2], outline=col, width=3)
        name = CLASS[cid] if cid < len(CLASS) else str(cid)
        lbl = f"{name}:{sc:.2f}"
        tb = dr.textbbox((0,0), lbl, font=font)
        tw, th = tb[2]-tb[0], tb[3]-tb[1]
        dr.rectangle([x1, max(0,y1-th-4), x1+tw+4, y1], fill=col)
        dr.text((x1+2, max(0,y1-th-3)), lbl, fill=(255,255,255), font=font)
    band = 26
    out = Image.new("RGB", (img.width, img.height+band), (255,255,255))
    out.paste(img, (0, band))
    ImageDraw.Draw(out).text((6,5), title, fill=(0,0,0), font=font)
    return out


def main():
    files = sorted(glob.glob(os.path.join(RAST, "*.rgb")))
    by_stem = {}
    for f in files:
        page,_,_,_ = load(f)
        if page in (2,3):
            base = os.path.basename(f)
            stem = base[:base.rfind("_p")]
            by_stem.setdefault(stem, f)  # first held-out page per stem

    chosen = [(s, by_stem[s]) for s in WANT if s in by_stem]
    # top up to >=4 if some weren't found
    for s, f in by_stem.items():
        if len(chosen) >= 6: break
        if s not in dict(chosen): chosen.append((s, f))

    s_fp, s_q = sess(FP32), sess(INT8)
    print(f"rendering {len(chosen)} pages")
    for stem, f in chosen:
        page,w,h,hwc = load(f)
        df = detect(s_fp, hwc, w, h)
        dq = detect(s_q, hwc, w, h)
        left = draw(hwc, df, f"FP32  ({len(df)} dets)")
        right = draw(hwc, dq, f"INT8 backbone  ({len(dq)} dets)")
        gap = 12
        canvas = Image.new("RGB", (left.width+right.width+gap, max(left.height,right.height)), (180,180,180))
        canvas.paste(left, (0,0)); canvas.paste(right, (left.width+gap,0))
        name = re.sub(r'[^A-Za-z0-9._-]','_', stem)[:48]
        outp = os.path.join(OUT, f"{name}_p{page}.png")
        canvas.save(outp)
        print(f"  {os.path.basename(outp)}  fp={len(df)} int8={len(dq)}")
    print(f"done -> {OUT}")


if __name__ == "__main__":
    print(f"onnxruntime {ort.__version__}")
    main()
