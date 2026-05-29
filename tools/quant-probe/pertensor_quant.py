#!/usr/bin/env python3
"""
Decisive Heron quant experiment: full-graph static INT8 with PER-TENSOR
quantization (per_channel=False).

Why: the full-graph per-channel attempt broke the decoder (anchors_scale ->
Inf, zero detections). That run emitted many "Axis 1 is out-of-range for
weight '...norm.weight' with rank 1" warnings — i.e. per_channel=True tried to
quantize rank-1 / scalar tensors along axis 1, yielding zero/Inf scales.
Per-tensor avoids that axis problem AND quantizes the MatMul-heavy decoder
(where Heron's compute lives), so it can deliver real speed IF correct.

Two variants, both validity-gated (finite? detections? agreement vs FP32?):
  A) per-tensor, full graph
  B) per-tensor, full graph, excluding any node whose quant still goes non-finite
Calibrate pages 0-1, evaluate held-out pages 2-3, real RailDLA rasters.
"""
import os, glob, re, time, collections
import numpy as np
from PIL import Image
import onnx
from onnx import numpy_helper
import onnxruntime as ort
from onnxruntime.quantization import (
    quantize_static, CalibrationDataReader, QuantFormat, QuantType, CalibrationMethod,
)
from onnxruntime.quantization.shape_inference import quant_pre_process

RAST = "/home/stefan/RailReaderCore/tools/quant-probe/rasters"
OUT = "/home/stefan/RailReaderCore/tools/quant-probe/out"
FP32 = "/home/stefan/railreader2/experiments/docling-layout/heron.onnx"
PRE = os.path.join(OUT, "heron_pt_pre.onnx")
IN = 640; CONF = 0.4; NMS_IOU = 0.5; MIN_PX = 5.0
NAME_RE = re.compile(r"_p(\d+)_(\d+)x(\d+)\.rgb$")
os.makedirs(OUT, exist_ok=True)


def feed_of(p):
    m = NAME_RE.search(p); page, w, h = int(m.group(1)), int(m.group(2)), int(m.group(3))
    hwc = np.fromfile(p, np.uint8).reshape(h, w, 3)
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


def post(lab, bx, sc, w, h):
    d=[]
    for l,b,s in zip(lab,bx,sc):
        if not np.isfinite(s) or s<CONF: continue
        x1=max(0.,float(b[0])); y1=max(0.,float(b[1])); x2=min(float(w),float(b[2])); y2=min(float(h),float(b[3]))
        if (x2-x1)<MIN_PX or (y2-y1)<MIN_PX: continue
        d.append((int(l),float(s),[x1,y1,x2,y2]))
    d.sort(key=lambda t:-t[1]); keep=[True]*len(d)
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
    def __init__(s, feeds): s.it=iter(feeds)
    def get_next(s): return next(s.it, None)


def quantize(out_path, calib_feeds, exclude=None):
    quantize_static(
        PRE, out_path, Reader(list(calib_feeds)),
        quant_format=QuantFormat.QDQ, per_channel=False,
        activation_type=QuantType.QUInt8, weight_type=QuantType.QInt8,
        calibrate_method=CalibrationMethod.MinMax,
        nodes_to_exclude=exclude or [],
    )


def bad_inits(path):
    m=onnx.load(path); bad=[]
    for init in m.graph.initializer:
        a=numpy_helper.to_array(init)
        if a.dtype.kind=='f' and (np.isnan(a).any() or np.isinf(a).any()):
            bad.append(init.name)
    qc=collections.Counter(n.op_type for n in m.graph.node)
    return bad, qc


def evaluate(tag, path, s_fp, ev):
    s_q=sess(path)
    # validity probe on first eval page
    _,w,h,feed=ev[0]
    lq,bq,sq=run(s_q,feed)
    fin = np.isfinite(sq)
    print(f"  [{tag}] sample scores: max={ (np.max(sq[fin]) if fin.any() else float('nan')):.4f} "
          f"nan={int(np.isnan(sq).sum())} inf={int(np.isinf(sq).sum())}")
    agg=collections.Counter(); iou_sum=0.; drops=0
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
            if bj>=0 and best>=0.5: used[bj]=True; m+=1; iou_sum+=best
        agg["m"]+=m
        if len(rf) and m/len(rf)<0.9: drops+=1
    fp,q,m=agg["fp"],agg["q"],agg["m"]
    print(f"  [{tag}] EVAL fp={fp} int8={q} matched={m} recall={m/max(fp,1):.3f} "
          f"prec={m/max(q,1):.3f} meanIoU={iou_sum/max(m,1):.3f} pages<90%={drops}/{len(ev)}")
    # speed (min of 10)
    def t(s,feed):
        for _ in range(3): run(s,feed)
        xs=[time.perf_counter() for _ in range(0)]
        out=[]
        for _ in range(10):
            t0=time.perf_counter(); run(s,feed); out.append((time.perf_counter()-t0)*1000)
        return min(out)
    sp_fp=t(s_fp,ev[0][3]); sp_q=t(s_q,ev[0][3])
    print(f"  [{tag}] speed FP32={sp_fp:.1f}ms INT8={sp_q:.1f}ms speedup={sp_fp/sp_q:.2f}x  "
          f"size={os.path.getsize(path)/1e6:.0f}MB")
    return fp, q, m


def main():
    files=sorted(glob.glob(os.path.join(RAST,"*.rgb")))
    calib=[]; ev=[]
    for f in files:
        page,w,h,feed=feed_of(f)
        (calib if page<=1 else ev).append((os.path.basename(f),w,h,feed))
    print(f"calib={len(calib)} eval={len(ev)}")
    try: quant_pre_process(FP32, PRE, skip_symbolic_shape=False)
    except Exception: quant_pre_process(FP32, PRE, skip_symbolic_shape=True)

    s_fp=sess(FP32)

    # Variant A: per-tensor, full graph
    A=os.path.join(OUT,"heron_pertensor_int8.onnx")
    quantize(A, [c[3] for c in calib])
    badA, qcA = bad_inits(A)
    print(f"VARIANT A (per-tensor, full): size={os.path.getsize(A)/1e6:.0f}MB "
          f"bad_inits={len(badA)} QLinearConv={qcA.get('QLinearConv',0)} "
          f"QuantizeLinear={qcA.get('QuantizeLinear',0)} Conv={qcA.get('Conv',0)} MatMul={qcA.get('MatMul',0)}")
    if badA: print(f"  bad nodes (first 6): {badA[:6]}")
    evaluate("A", A, s_fp, ev)

    # Variant B: also exclude the known Inf-producing decoder nodes, only if A had bad inits
    if badA:
        B=os.path.join(OUT,"heron_pertensor_excl_int8.onnx")
        # exclude consumers of the bad scale tensors by name-substring matching node names
        m=onnx.load(PRE)
        excl=[n.name for n in m.graph.node
              if any(k in (n.name or "") for k in ("anchors_scale","add_2355")) or
                 any(any(k in (inp or "") for k in ("anchors_scale","add_2355")) for inp in n.input)]
        print(f"VARIANT B: excluding {len(excl)} decoder nodes near anchors_scale/add_2355")
        quantize(B, [c[3] for c in calib], exclude=excl)
        badB,_=bad_inits(B)
        print(f"  B bad_inits={len(badB)}")
        evaluate("B", B, s_fp, ev)

    print("done")


if __name__=="__main__":
    print(f"onnxruntime {ort.__version__}")
    main()
