#!/usr/bin/env python3
"""
Accuracy validation: static-INT8 Heron vs FP32 Heron on real academic pages.

Pipeline (faithful to HeronLayoutAnalyzer):
  raw 1024px-edge RGB raster (dumped by .NET PDFium) -> bilinear resize to
  640x640 uint8 CHW -> Heron ONNX (orig_target_sizes = raster [W,H]) -> boxes
  in raster pixel space -> conf>=0.5, min 5px, class-agnostic NMS@0.45.

Calibration: pages p0/p1 of each PDF.  Eval (held-out): pages p2/p3.
FP32 Heron is the reference; we measure how well INT8 reproduces it:
  - recall  = matched / FP32_count   (boxes INT8 kept)
  - prec    = matched / INT8_count   (boxes INT8 didn't invent)
  - mean IoU of matched pairs
  - class-flip rate among IoU-matched boxes
Matching is greedy by IoU, reported both same-class and class-agnostic.
"""
import os, glob, re, collections, statistics
import numpy as np
from PIL import Image
import onnx
import onnxruntime as ort
from onnxruntime.quantization import (
    quantize_static, CalibrationDataReader, QuantFormat, QuantType, CalibrationMethod,
)
from onnxruntime.quantization.shape_inference import quant_pre_process

RASTERS = "/home/stefan/RailReaderCore/tools/quant-probe/rasters"
OUTDIR = "/home/stefan/RailReaderCore/tools/quant-probe/out"
FP32 = "/home/stefan/railreader2/experiments/docling-layout/heron.onnx"
INT8 = os.path.join(OUTDIR, "heron_realcalib_int8.onnx")
MODEL_IN = 640
CONF = 0.5
NMS_IOU = 0.45
MIN_PX = 5.0
os.makedirs(OUTDIR, exist_ok=True)

NAME_RE = re.compile(r"_p(\d+)_(\d+)x(\d+)\.rgb$")


def load_raster(path):
    m = NAME_RE.search(path)
    page, w, h = int(m.group(1)), int(m.group(2)), int(m.group(3))
    arr = np.fromfile(path, dtype=np.uint8)
    if arr.size != w * h * 3:
        return None
    return page, w, h, arr.reshape(h, w, 3)


def to_feed(hwc, w, h):
    """640x640 uint8 NCHW images + int64 orig_target_sizes=[W,H] (raster dims)."""
    img = Image.fromarray(hwc, "RGB").resize((MODEL_IN, MODEL_IN), Image.BILINEAR)
    chw = np.transpose(np.asarray(img, dtype=np.uint8), (2, 0, 1))[None, ...]
    return {"images": chw, "orig_target_sizes": np.array([[w, h]], dtype=np.int64)}


def session(path):
    so = ort.SessionOptions()
    so.intra_op_num_threads = 8
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    so.log_severity_level = 3
    return ort.InferenceSession(path, so, providers=["CPUExecutionProvider"])


def iou(a, b):
    x1 = max(a[0], b[0]); y1 = max(a[1], b[1])
    x2 = min(a[2], b[2]); y2 = min(a[3], b[3])
    inter = max(0.0, x2 - x1) * max(0.0, y2 - y1)
    if inter <= 0:
        return 0.0
    ua = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / ua if ua > 0 else 0.0


def postprocess(labels, boxes, scores, w, h):
    """Mirror HeronLayoutAnalyzer: conf filter, clamp, min-size, class-agnostic NMS."""
    dets = []
    for lab, bx, sc in zip(labels, boxes, scores):
        if sc < CONF:
            continue
        x1 = max(0.0, float(bx[0])); y1 = max(0.0, float(bx[1]))
        x2 = min(float(w), float(bx[2])); y2 = min(float(h), float(bx[3]))
        if (x2 - x1) < MIN_PX or (y2 - y1) < MIN_PX:
            continue
        dets.append((int(lab), float(sc), [x1, y1, x2, y2]))
    dets.sort(key=lambda d: -d[1])
    keep = [True] * len(dets)
    for i in range(len(dets)):
        if not keep[i]:
            continue
        for j in range(i + 1, len(dets)):
            if keep[j] and iou(dets[i][2], dets[j][2]) > NMS_IOU:
                keep[j] = False
    return [d for d, k in zip(dets, keep) if k]


def run_model(sess, feed):
    outs = {o.name: o for o in sess.get_outputs()}
    names = list(outs)
    res = sess.run(names, feed)
    r = dict(zip(names, res))
    # Heron outputs: labels[B,300], boxes[B,300,4], scores[B,300]
    labels = np.asarray(r["labels"]).reshape(-1)
    boxes = np.asarray(r["boxes"]).reshape(-1, 4)
    scores = np.asarray(r["scores"]).reshape(-1)
    return labels, boxes, scores


def match(ref, hyp, class_aware):
    """Greedy IoU match hyp->ref. Returns (#matched, sum_iou, #classflip)."""
    used = [False] * len(ref)
    matched = 0; sum_iou = 0.0; flips = 0
    for hc, hs, hb in hyp:
        best, bj = 0.0, -1
        for j, (rc, rs, rb) in enumerate(ref):
            if used[j]:
                continue
            if class_aware and rc != hc:
                continue
            v = iou(hb, rb)
            if v > best:
                best, bj = v, j
        if bj >= 0 and best >= 0.5:
            used[bj] = True; matched += 1; sum_iou += best
            if ref[bj][0] != hc:
                flips += 1
    return matched, sum_iou, flips


class CalibReader(CalibrationDataReader):
    def __init__(self, feeds):
        self.it = iter(feeds)

    def get_next(self):
        return next(self.it, None)


def main():
    files = sorted(glob.glob(os.path.join(RASTERS, "*.rgb")))
    print(f"rasters found: {len(files)}")
    calib_feeds, eval_items = [], []
    for f in files:
        r = load_raster(f)
        if r is None:
            print(f"  bad size, skip {os.path.basename(f)}"); continue
        page, w, h, hwc = r
        feed = to_feed(hwc, w, h)
        if page <= 1:
            calib_feeds.append(feed)
        else:
            eval_items.append((os.path.basename(f), w, h, feed))
    print(f"calibration tensors: {len(calib_feeds)}   eval tensors: {len(eval_items)}")
    if not calib_feeds or not eval_items:
        print("INSUFFICIENT DATA"); return

    # Re-quantize Heron with REAL page calibration.
    pre = os.path.join(OUTDIR, "heron_realcalib_pre.onnx")
    try:
        quant_pre_process(FP32, pre, skip_symbolic_shape=False)
    except Exception:
        quant_pre_process(FP32, pre, skip_symbolic_shape=True)
    quantize_static(
        pre, INT8, CalibReader(list(calib_feeds)),
        quant_format=QuantFormat.QDQ, per_channel=True,
        activation_type=QuantType.QUInt8, weight_type=QuantType.QInt8,
        calibrate_method=CalibrationMethod.MinMax,
    )
    print(f"INT8 (real calib) written: {os.path.getsize(INT8)/1e6:.0f} MB")

    s_fp = session(FP32)
    s_q = session(INT8)

    agg = collections.Counter()
    ca_iou_sum = 0.0; sa_iou_sum = 0.0; flips_total = 0
    per_page = []
    for name, w, h, feed in eval_items:
        rf = postprocess(*run_model(s_fp, feed), w, h)
        rq = postprocess(*run_model(s_q, feed), w, h)
        m_sa, iou_sa, _ = match(rf, rq, class_aware=False)
        m_ca, iou_ca, flips = match(rf, rq, class_aware=True)
        agg["fp"] += len(rf); agg["q"] += len(rq)
        agg["match_sa"] += m_sa; agg["match_ca"] += m_ca
        sa_iou_sum += iou_sa; ca_iou_sum += iou_ca; flips_total += flips
        per_page.append((name, len(rf), len(rq), m_ca))

    fp, q = agg["fp"], agg["q"]
    msa, mca = agg["match_sa"], agg["match_ca"]
    print("=" * 64)
    print(f"EVAL pages: {len(eval_items)}   FP32 dets: {fp}   INT8 dets: {q}")
    print("-- class-AGNOSTIC (localization only) --")
    print(f"  recall={msa/fp:.3f}  precision={msa/max(q,1):.3f}  meanIoU(matched)={sa_iou_sum/max(msa,1):.3f}")
    print("-- class-AWARE (localization + class) --")
    print(f"  recall={mca/fp:.3f}  precision={mca/max(q,1):.3f}  meanIoU(matched)={ca_iou_sum/max(mca,1):.3f}")
    print(f"  class flips among loc-matched: {flips_total}")
    print("-- per-page (name, fp, int8, matched-class-aware) --")
    for name, nf, nq, m in per_page:
        flag = "" if (nf and m / nf >= 0.9) else "  <-- drop"
        print(f"  {name[:48]:48s} fp={nf:3d} q={nq:3d} m={m:3d}{flag}")
    print("=" * 64)
    print("done")


if __name__ == "__main__":
    print(f"onnxruntime {ort.__version__}")
    main()
