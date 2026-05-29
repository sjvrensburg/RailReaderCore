#!/usr/bin/env python3
"""
Diagnose WHY the INT8 Heron produces zero detections. Distinguishes:
  (A) NaN/Inf outputs from zero-scale quantization (broken quant -> fixable), vs
  (B) genuinely collapsed but finite scores (real accuracy loss).

Runs FP32 and INT8 on ONE real eval page, prints raw output stats. Also scans
the INT8 model's initializers for NaN/Inf weights (the divide-by-zero fallout).
"""
import os, glob, re
import numpy as np
from PIL import Image
import onnx
from onnx import numpy_helper
import onnxruntime as ort

RAST = "/home/stefan/RailReaderCore/tools/quant-probe/rasters"
FP32 = "/home/stefan/railreader2/experiments/docling-layout/heron.onnx"
INT8 = "/home/stefan/RailReaderCore/tools/quant-probe/out/heron_realcalib_int8.onnx"
IN = 640
NAME_RE = re.compile(r"_p(\d+)_(\d+)x(\d+)\.rgb$")


def feed_from(path):
    m = NAME_RE.search(path); page, w, h = int(m.group(1)), int(m.group(2)), int(m.group(3))
    hwc = np.fromfile(path, np.uint8).reshape(h, w, 3)
    img = Image.fromarray(hwc, "RGB").resize((IN, IN), Image.BILINEAR)
    chw = np.transpose(np.asarray(img, np.uint8), (2, 0, 1))[None, ...]
    return {"images": chw, "orig_target_sizes": np.array([[w, h]], np.int64)}


def sess(p):
    so = ort.SessionOptions(); so.log_severity_level = 3
    return ort.InferenceSession(p, so, providers=["CPUExecutionProvider"])


def stats(tag, path, feed):
    s = sess(path)
    names = [o.name for o in s.get_outputs()]
    r = dict(zip(names, s.run(names, feed)))
    print(f"  [{tag}] outputs: {names}")
    for n, v in r.items():
        v = np.asarray(v)
        nan = int(np.isnan(v).sum()); inf = int(np.isinf(v).sum())
        finite = v[np.isfinite(v)]
        mn = float(finite.min()) if finite.size else float('nan')
        mx = float(finite.max()) if finite.size else float('nan')
        print(f"    {n:20s} shape={list(v.shape)} dtype={v.dtype} nan={nan} inf={inf} finite_min={mn:.4g} finite_max={mx:.4g}")
    if "scores" in r:
        sc = np.asarray(r["scores"]).reshape(-1)
        fin = sc[np.isfinite(sc)]
        if fin.size:
            print(f"    scores: max={fin.max():.4f}  #>=0.4={int((fin>=0.4).sum())}  #>=0.1={int((fin>=0.1).sum())}")
        else:
            print(f"    scores: ALL non-finite (NaN/Inf)")


def scan_weights(path):
    m = onnx.load(path)
    bad = 0; total = 0
    worst = []
    for init in m.graph.initializer:
        arr = numpy_helper.to_array(init)
        if arr.dtype.kind != 'f':
            continue
        total += 1
        nan = np.isnan(arr).sum(); inf = np.isinf(arr).sum()
        if nan or inf:
            bad += 1
            if len(worst) < 12:
                worst.append((init.name, int(nan), int(inf), list(arr.shape)))
    print(f"  INT8 float initializers: {total}, with NaN/Inf: {bad}")
    for n, nan, inf, sh in worst:
        print(f"    BAD {n[:50]:50s} nan={nan} inf={inf} shape={sh}")


def main():
    files = sorted(glob.glob(os.path.join(RAST, "*.rgb")))
    page = next(f for f in files if "_p2_" in f)
    print(f"diagnostic page: {os.path.basename(page)}")
    feed = feed_from(page)
    print("FP32:")
    stats("fp32", FP32, feed)
    print("INT8:")
    if os.path.exists(INT8):
        stats("int8", INT8, feed)
        print("INT8 weight scan:")
        scan_weights(INT8)
    else:
        print(f"  MISSING {INT8}")


if __name__ == "__main__":
    print(f"onnxruntime {ort.__version__}")
    main()
    print("done")
