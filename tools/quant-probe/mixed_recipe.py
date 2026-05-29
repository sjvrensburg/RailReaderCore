#!/usr/bin/env python3
"""
Chase past backbone-only 2.95×: quantize Conv + encoder MatMuls, exclude the
DECODER (the anchor path that produced Inf scales in full-graph quant).

Heron has 109 MatMuls; backbone-only left all FP32. The encoder/AIFI MatMuls
(model.encoder.*) are safe — only model.decoder.* broke. Quantizing the encoder
too should add speed while staying correct.

Recipes, all validity-gated (finite + detections + agreement vs FP32, held-out):
  R0 backbone-only (Conv)                       — reference (already 2.95×)
  R1 Conv + all MatMul EXCEPT decoder MatMuls
  R2 Conv + all MatMul EXCEPT decoder MatMuls + decoder Adds  (wider exclude)

Decoder nodes identified as those whose name OR any input references a
'decoder' initializer/tensor. Calibrate pages 0-1, eval held-out 2-3.
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
PRE = os.path.join(OUT, "heron_mixed_pre.onnx")
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


def decoder_nodes(pre_path):
    m = onnx.load(pre_path)
    names=[]
    for n in m.graph.node:
        blob = (n.name or "") + "|" + "|".join(n.input)
        if "decoder" in blob:
            names.append(n.name)
    return names


def evaluate(tag, path, s_fp, ev):
    s_q = sess(path)
    _,w,h,feed = ev[0]
    _,_,sq = run(s_q, feed)
    fin = np.isfinite(sq)
    smax = float(np.max(sq[fin])) if fin.any() else float('nan')
    nan = int(np.isnan(sq).sum()); inf = int(np.isinf(sq).sum())
    if not fin.any() or smax < 0.1:
        print(f"  [{tag}] BROKEN: score max={smax:.4f} nan={nan} inf={inf} — skipping accuracy")
        return
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
    def t(s,feed):
        for _ in range(3): run(s,feed)
        out=[]
        for _ in range(10):
            t0=time.perf_counter(); run(s,feed); out.append((time.perf_counter()-t0)*1000)
        return min(out)
    spq=t(s_q,ev[0][3]); spf=t(s_fp,ev[0][3])
    print(f"  [{tag}] recall={m/max(fp,1):.3f} prec={m/max(q,1):.3f} meanIoU={iou_sum/max(m,1):.3f} "
          f"pages<90%={drops}/{len(ev)} | FP32={spf:.0f}ms INT8={spq:.0f}ms speedup={spf/spq:.2f}x "
          f"size={os.path.getsize(path)/1e6:.0f}MB")


def main():
    files=sorted(glob.glob(os.path.join(RAST,"*.rgb")))
    calib=[]; ev=[]
    for f in files:
        page,w,h,feed=feed_of(f)
        (calib if page<=1 else ev).append((os.path.basename(f),w,h,feed))
    print(f"calib={len(calib)} eval={len(ev)}")
    try: quant_pre_process(FP32, PRE, skip_symbolic_shape=False)
    except Exception: quant_pre_process(FP32, PRE, skip_symbolic_shape=True)

    dec = decoder_nodes(PRE)
    print(f"decoder nodes to exclude: {len(dec)}")
    s_fp=sess(FP32)
    cf=[c[3] for c in calib]

    # R1: Conv + MatMul everywhere EXCEPT decoder nodes
    R1=os.path.join(OUT,"heron_conv_encMatmul_int8.onnx")
    quantize_static(PRE, R1, Reader(list(cf)),
        quant_format=QuantFormat.QDQ, per_channel=True,
        activation_type=QuantType.QUInt8, weight_type=QuantType.QInt8,
        calibrate_method=CalibrationMethod.MinMax,
        op_types_to_quantize=["Conv","MatMul"],
        nodes_to_exclude=dec)
    qc=collections.Counter(n.op_type for n in onnx.load(R1).graph.node)
    print(f"R1 Conv+encMatMul(excl decoder): QLinearConv={qc.get('QLinearConv',0)} "
          f"QLinearMatMul={qc.get('QLinearMatMul',0)} MatMulInteger={qc.get('MatMulInteger',0)} "
          f"MatMul_left={qc.get('MatMul',0)}")
    evaluate("R1", R1, s_fp, ev)

    print("done")


if __name__=="__main__":
    print(f"onnxruntime {ort.__version__}")
    main()
