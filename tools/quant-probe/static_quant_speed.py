#!/usr/bin/env python3
"""
STATIC (QDQ) INT8 quantization speed gate for V3 and Heron.

Dynamic quant was the wrong tool (conv-heavy models -> ConvInteger reference
kernel, no VNNI -> slower). Static QDQ produces QLinearConv/QLinearMatMul which
hit the MLAS U8S8 VNNI path on this Alder Lake CPU (avx_vnni present).

This script answers the SPEED question only: does static INT8 beat FP32 here?
Calibration uses synthetic document-like images — fine for kernel timing
(kernel speed is value-independent); NOT a statement about accuracy. Accuracy
validation comes later, with real rasterized pages, IF speed pans out.

Activation=QUInt8, weight=QInt8 (the U8S8 combo VNNI accelerates), per-channel.
"""
import os, time, statistics, collections, traceback
import numpy as np
import onnx
import onnxruntime as ort
from onnxruntime.quantization import (
    quantize_static, CalibrationDataReader, QuantFormat, QuantType, CalibrationMethod,
)
from onnxruntime.quantization.shape_inference import quant_pre_process

OUTDIR = "/home/stefan/RailReaderCore/tools/quant-probe/out"
os.makedirs(OUTDIR, exist_ok=True)

MODELS = {
    "v3":    ("/home/stefan/railreader2/models/PP-DocLayoutV3.onnx", 800, "float32"),
    "heron": ("/home/stefan/railreader2/experiments/docling-layout/heron.onnx", 640, "uint8"),
}
N_CALIB = 24


def doc_like_image(size, dtype):
    """Mostly-white page with sparse dark rectangles (text-ish). float32 in
    [0,1] for V3, uint8 [0,255] for Heron."""
    img = np.ones((size, size, 3), dtype=np.float32)  # white
    rng = np.random.default_rng()
    for _ in range(rng.integers(20, 60)):
        y, x = rng.integers(0, size - 20, 2)
        h, w = rng.integers(4, 18), rng.integers(20, 200)
        img[y:y + h, x:min(x + w, size)] = rng.uniform(0.0, 0.3)
    if dtype == "uint8":
        return (img * 255).astype(np.uint8)
    return img.astype(np.float32)


def make_feed(path, size, img_dtype):
    """Full input dict matching the model's declared inputs."""
    m = onnx.load(path)
    feed = {}
    for vi in m.graph.input:
        name = vi.name; lname = name.lower()
        et = vi.type.tensor_type.elem_type
        if "image" in lname:
            hwc = doc_like_image(size, "uint8" if et == 2 else "float32")
            chw = np.transpose(hwc, (2, 0, 1))[None, ...]  # NCHW
            feed[name] = chw
        elif "shape" in lname or "size" in lname:
            dt = np.int64 if et == 7 else np.float32
            feed[name] = np.array([[size, size]], dtype=dt)
        elif "scale" in lname:
            feed[name] = np.ones((1, 2), dtype=np.float32)
    return feed


class Reader(CalibrationDataReader):
    def __init__(self, path, size, img_dtype, n):
        self.data = [make_feed(path, size, img_dtype) for _ in range(n)]
        self.i = iter(self.data)

    def get_next(self):
        return next(self.i, None)


def session(path):
    so = ort.SessionOptions()
    so.intra_op_num_threads = 8
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    so.log_severity_level = 3
    return ort.InferenceSession(path, so, providers=["CPUExecutionProvider"])


def time_model(path, feed, warmup=3, runs=12):
    sess = session(path)
    outs = [o.name for o in sess.get_outputs()]
    for _ in range(warmup):
        sess.run(outs, feed)
    ts = []
    for _ in range(runs):
        t0 = time.perf_counter(); sess.run(outs, feed); ts.append((time.perf_counter() - t0) * 1000)
    return min(ts), statistics.median(ts)


def run(tag, path, size, img_dtype):
    print("=" * 72)
    print(f"{tag}  {path}")
    feed = make_feed(path, size, img_dtype)
    print(f"  inputs: {[(k, v.dtype.name, list(v.shape)) for k, v in feed.items()]}")

    f_min, f_med = time_model(path, feed)
    print(f"  FP32  min={f_min:.1f}  median={f_med:.1f} ms  (size {os.path.getsize(path)/1e6:.0f} MB)")

    pre = os.path.join(OUTDIR, f"{tag}_pre_static.onnx")
    try:
        quant_pre_process(path, pre, skip_symbolic_shape=False)
        print("  pre_process: OK (strict symbolic)")
    except Exception as e:
        print(f"  pre_process strict FAILED ({type(e).__name__}); retrying skip_symbolic")
        quant_pre_process(path, pre, skip_symbolic_shape=True)
        print("  pre_process: OK (skip_symbolic fallback)")

    q = os.path.join(OUTDIR, f"{tag}_static_int8.onnx")
    try:
        quantize_static(
            pre, q, Reader(path, size, img_dtype, N_CALIB),
            quant_format=QuantFormat.QDQ,
            per_channel=True,
            activation_type=QuantType.QUInt8,
            weight_type=QuantType.QInt8,
            calibrate_method=CalibrationMethod.MinMax,
        )
        print(f"  quantize_static: OK -> {os.path.getsize(q)/1e6:.0f} MB")
    except Exception as e:
        print(f"  quantize_static FAILED ({type(e).__name__}: {str(e)[:240]})")
        return

    qm = onnx.load(q)
    c = collections.Counter(n.op_type for n in qm.graph.node)
    sig = {k: c[k] for k in ("QLinearConv", "QLinearMatMul", "ConvInteger", "MatMulInteger",
                             "QuantizeLinear", "DequantizeLinear", "Conv", "MatMul", "GridSample")
           if c.get(k)}
    print(f"  quantized-op signature: {sig}")

    try:
        q_min, q_med = time_model(q, feed)
        print(f"  INT8  min={q_min:.1f}  median={q_med:.1f} ms")
        print(f"  >>> STATIC SPEEDUP (median): {f_med / q_med:.2f}x")
    except Exception as e:
        print(f"  INT8 RUN FAILED ({type(e).__name__}: {str(e)[:200]})")


if __name__ == "__main__":
    print(f"onnxruntime {ort.__version__}")
    for tag, (path, size, dt) in MODELS.items():
        try:
            run(tag, path, size, dt)
        except Exception:
            print(f"UNEXPECTED {tag}:\n{traceback.format_exc()}")
    print("=" * 72); print("done")
