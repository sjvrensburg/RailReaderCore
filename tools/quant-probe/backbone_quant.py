#!/usr/bin/env python3
"""
Backbone-only static INT8 for Heron — the standard RT-DETR recipe.

Naive full-graph QDQ broke the decoder (anchors_scale -> Inf, scores collapse,
zero detections). Fix: quantize ONLY Conv ops (the CNN backbone, where the
compute and the speedup live) and leave the entire transformer decoder /
anchor path in FP32. This sidesteps the degenerate decoder-constant scales.

Validates the result for REAL viability before reporting anything:
  1. outputs finite (no NaN/Inf)
  2. produces detections, and score distribution resembles FP32
  3. detection agreement vs FP32 (recall / precision / IoU) on held-out pages
  4. speed vs FP32
Calibrate on pages 0-1, evaluate on pages 2-3 (held-out), real RailDLA rasters.
"""
import os, glob, re, time, statistics, collections
import numpy as np
from PIL import Image
import onnx
import onnxruntime as ort
from onnxruntime.quantization import (
    quantize_static, CalibrationDataReader, QuantFormat, QuantType, CalibrationMethod,
)
from onnxruntime.quantization.shape_inference import quant_pre_process

RAST = "/home/stefan/RailReaderCore/tools/quant-probe/rasters"
OUT = "/home/stefan/RailReaderCore/tools/quant-probe/out"
FP32 = "/home/stefan/railreader2/experiments/docling-layout/heron.onnx"
PRE = os.path.join(OUT, "heron_bb_pre.onnx")
INT8 = os.path.join(OUT, "heron_backbone_int8.onnx")
IN = 640
CONF = 0.4; NMS_IOU = 0.5; MIN_PX = 5.0
NAME_RE = re.compile(r"_p(\d+)_(\d+)x(\d+)\.rgb$")
os.makedirs(OUT, exist_ok=True)


def parse(path):
    m = NAME_RE.search(path)
    return int(m.group(1)), int(m.group(2)), int(m.group(3))


def feed_of(path):
    page, w, h = parse(path)
    hwc = np.fromfile(path, np.uint8).reshape(h, w, 3)
    img = Image.fromarray(hwc, "RGB").resize((IN, IN), Image.BILINEAR)
    chw = np.transpose(np.asarray(img, np.uint8), (2, 0, 1))[None, ...]
    return page, w, h, {"images": chw, "orig_target_sizes": np.array([[w, h]], np.int64)}


def sess(p):
    so = ort.SessionOptions(); so.intra_op_num_threads = 8
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    so.log_severity_level = 3
    return ort.InferenceSession(p, so, providers=["CPUExecutionProvider"])


def iou(a, b):
    x1=max(a[0],b[0]); y1=max(a[1],b[1]); x2=min(a[2],b[2]); y2=min(a[3],b[3])
    inter=max(0.,x2-x1)*max(0.,y2-y1)
    if inter<=0: return 0.
    ua=(a[2]-a[0])*(a[3]-a[1])+(b[2]-b[0])*(b[3]-b[1])-inter
    return inter/ua if ua>0 else 0.


def post(labels, boxes, scores, w, h):
    d=[]
    for lab,bx,sc in zip(labels,boxes,scores):
        if not np.isfinite(sc) or sc<CONF: continue
        x1=max(0.,float(bx[0])); y1=max(0.,float(bx[1]))
        x2=min(float(w),float(bx[2])); y2=min(float(h),float(bx[3]))
        if (x2-x1)<MIN_PX or (y2-y1)<MIN_PX: continue
        d.append((int(lab),float(sc),[x1,y1,x2,y2]))
    d.sort(key=lambda t:-t[1])
    keep=[True]*len(d)
    for i in range(len(d)):
        if not keep[i]: continue
        for j in range(i+1,len(d)):
            if keep[j] and iou(d[i][2],d[j][2])>NMS_IOU: keep[j]=False
    return [x for x,k in zip(d,keep) if k]


def run(s, feed):
    names=[o.name for o in s.get_outputs()]
    r=dict(zip(names,s.run(names,feed)))
    return (np.asarray(r["labels"]).reshape(-1),
            np.asarray(r["boxes"]).reshape(-1,4),
            np.asarray(r["scores"]).reshape(-1))


class Reader(CalibrationDataReader):
    def __init__(self, feeds): self.it=iter(feeds)
    def get_next(self): return next(self.it, None)


def main():
    files=sorted(glob.glob(os.path.join(RAST,"*.rgb")))
    calib=[]; ev=[]
    for f in files:
        page,w,h,feed=feed_of(f)
        (calib if page<=1 else ev).append((os.path.basename(f),w,h,feed))
    print(f"calib={len(calib)} eval={len(ev)}")

    try:
        quant_pre_process(FP32, PRE, skip_symbolic_shape=False)
    except Exception:
        quant_pre_process(FP32, PRE, skip_symbolic_shape=True)

    # KEY: quantize ONLY Conv. Decoder MatMul/Add/anchor consts stay FP32.
    quantize_static(
        PRE, INT8, Reader([c[3] for c in calib]),
        quant_format=QuantFormat.QDQ, per_channel=True,
        activation_type=QuantType.QUInt8, weight_type=QuantType.QInt8,
        calibrate_method=CalibrationMethod.MinMax,
        op_types_to_quantize=["Conv"],
    )
    print(f"backbone-INT8 written: {os.path.getsize(INT8)/1e6:.0f} MB "
          f"(FP32 {os.path.getsize(FP32)/1e6:.0f} MB)")

    # weight sanity
    from onnx import numpy_helper
    qm=onnx.load(INT8); bad=0
    for init in qm.graph.initializer:
        a=numpy_helper.to_array(init)
        if a.dtype.kind=='f' and (np.isnan(a).any() or np.isinf(a).any()): bad+=1
    qc=collections.Counter(n.op_type for n in qm.graph.node)
    print(f"  bad(NaN/Inf) initializers: {bad}   QLinearConv={qc.get('QLinearConv',0)} "
          f"ConvInteger={qc.get('ConvInteger',0)} Conv={qc.get('Conv',0)} MatMul={qc.get('MatMul',0)}")

    s_fp=sess(FP32); s_q=sess(INT8)

    # validity on one page
    name,w,h,feed=ev[0]
    lf,bf,sf=run(s_fp,feed); lq,bq,sq=run(s_q,feed)
    print(f"  sample page {name}: FP32 score max={np.nanmax(sf):.3f}  INT8 score max={np.nanmax(sq):.3f}  "
          f"INT8 nan={int(np.isnan(sq).sum())} inf={int(np.isinf(sq).sum())}")

    # agreement + drops
    agg=collections.Counter(); iou_sum=0.; flips=0; drops=0
    for name,w,h,feed in ev:
        rf=post(*run(s_fp,feed),w,h); rq=post(*run(s_q,feed),w,h)
        agg["fp"]+=len(rf); agg["q"]+=len(rq)
        used=[False]*len(rf); m=0
        for hc,hs,hb in rq:
            best,bj=0.,-1
            for j,(rc,rs,rb) in enumerate(rf):
                if used[j] or rc!=hc: continue
                v=iou(hb,rb)
                if v>best: best,bj=v,j
            if bj>=0 and best>=0.5:
                used[bj]=True; m+=1; iou_sum+=best
        agg["m"]+=m
        if len(rf) and m/len(rf)<0.9: drops+=1
    fp,q,m=agg["fp"],agg["q"],agg["m"]
    print("="*60)
    print(f"EVAL {len(ev)} pages  FP32={fp} INT8={q} matched(class-aware)={m}")
    print(f"  recall={m/max(fp,1):.3f} precision={m/max(q,1):.3f} meanIoU={iou_sum/max(m,1):.3f}")
    print(f"  pages with <90% recall: {drops}/{len(ev)}")

    # speed (paired, min of 10)
    def t(s,feed,n=10):
        for _ in range(3): run(s,feed)
        xs=[]
        for _ in range(n):
            t0=time.perf_counter(); run(s,feed); xs.append((time.perf_counter()-t0)*1000)
        return min(xs)
    name,w,h,feed=ev[0]
    fpt=t(s_fp,feed); qt=t(s_q,feed)
    print(f"  speed: FP32 {fpt:.1f}ms  INT8 {qt:.1f}ms  speedup={fpt/qt:.2f}x")
    print("="*60); print("done")


if __name__=="__main__":
    print(f"onnxruntime {ort.__version__}")
    main()
