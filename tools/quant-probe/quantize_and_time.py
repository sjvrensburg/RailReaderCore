#!/usr/bin/env python3
"""
Attempt dynamic INT8 quantization of V3 and Heron, then time FP32 vs INT8
in-process (same ORT native runtime the .NET binding uses).

Dynamic quant: weights -> INT8, activations quantized on the fly. Targets
MatMul/Gemm, needs NO calibration data. Best fit for transformer-ish graphs.
We run quant_pre_process first (shape inference + opt) — this is the step that
tends to choke on V3's dynamic-shape ops (Range/Where/ScatterND/GatherND).

Reports per model: pre_process ok?, quantize ok?, load ok?, fp32 vs int8
median ms (10 runs after 3 warmups), speedup, output size.
"""
import os, time, traceback, statistics
import numpy as np
import onnx
import onnxruntime as ort
from onnxruntime.quantization import quantize_dynamic, QuantType
from onnxruntime.quantization.shape_inference import quant_pre_process

OUTDIR = "/home/stefan/RailReaderCore/tools/quant-probe/out"
os.makedirs(OUTDIR, exist_ok=True)

MODELS = {
    "v3":    "/home/stefan/railreader2/models/PP-DocLayoutV3.onnx",
    "heron": "/home/stefan/railreader2/experiments/docling-layout/heron.onnx",
}

ELEM = {  # onnx TensorProto elem_type -> numpy
    1: np.float32, 2: np.uint8, 3: np.int8, 6: np.int32,
    7: np.int64, 9: np.bool_, 10: np.float16, 11: np.float64,
}


def make_inputs(path):
    """Build a realistic single-batch input dict from the model's declared
    input dtypes/shapes (dynamic dims -> 1)."""
    m = onnx.load(path)
    feeds = {}
    for vi in m.graph.input:
        name = vi.name
        et = vi.type.tensor_type.elem_type
        dt = ELEM.get(et, np.float32)
        dims = []
        for d in vi.type.tensor_type.shape.dim:
            dims.append(d.dim_value if d.HasField("dim_value") and d.dim_value > 0 else 1)
        # Heuristics for the two scalar/size inputs so the graph runs sanely.
        lname = name.lower()
        if "image" in lname and len(dims) == 4:
            # main image tensor
            if dt == np.uint8:
                feeds[name] = np.random.randint(0, 256, size=dims, dtype=np.uint8)
            else:
                feeds[name] = np.random.rand(*dims).astype(dt)
        elif "shape" in lname or "size" in lname:
            # im_shape / orig_target_sizes: fill with spatial size of image
            val = 800 if "v3" in path.lower() or "DocLayout" in path else 640
            arr = np.full(dims, val, dtype=dt)
            feeds[name] = arr
        elif "scale" in lname:
            feeds[name] = np.ones(dims, dtype=dt)
        else:
            feeds[name] = (np.random.rand(*dims).astype(dt) if dt in (np.float32, np.float16, np.float64)
                           else np.ones(dims, dtype=dt))
    return feeds


def session(path):
    so = ort.SessionOptions()
    so.intra_op_num_threads = 8          # fixed for stable A/B
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    so.log_severity_level = 3
    return ort.InferenceSession(path, so, providers=["CPUExecutionProvider"])


def time_model(path, feeds, warmup=3, runs=10):
    sess = session(path)
    out_names = [o.name for o in sess.get_outputs()]
    for _ in range(warmup):
        sess.run(out_names, feeds)
    ts = []
    for _ in range(runs):
        t0 = time.perf_counter()
        sess.run(out_names, feeds)
        ts.append((time.perf_counter() - t0) * 1000.0)
    return min(ts), statistics.median(ts)


def run(tag, path):
    print("=" * 72)
    print(f"{tag}  {path}")
    print(f"  fp32 size: {os.path.getsize(path)/1e6:.1f} MB")
    feeds = make_inputs(path)
    print(f"  inputs: {[(k, v.dtype.name, list(v.shape)) for k, v in feeds.items()]}")

    # FP32 baseline
    try:
        f_min, f_med = time_model(path, feeds)
        print(f"  FP32  min={f_min:.1f} ms  median={f_med:.1f} ms")
    except Exception as e:
        print(f"  FP32 RUN FAILED: {e}")
        return

    pre_path = os.path.join(OUTDIR, f"{tag}_pre.onnx")
    int8_path = os.path.join(OUTDIR, f"{tag}_int8.onnx")

    # Step 1: pre-process (shape inference + opt) — V3 expected to be fragile here
    pre_ok = False
    try:
        quant_pre_process(path, pre_path, skip_symbolic_shape=False)
        pre_ok = True
        print(f"  quant_pre_process: OK -> {os.path.getsize(pre_path)/1e6:.1f} MB")
    except Exception as e:
        print(f"  quant_pre_process: FAILED ({type(e).__name__}: {str(e)[:160]})")
        # fall back to quantizing the raw model directly
        pre_path = path

    # also try skip_symbolic_shape=True as a fallback if the strict pass failed
    if not pre_ok:
        try:
            quant_pre_process(path, int8_path + ".pretmp.onnx", skip_symbolic_shape=True)
            pre_path = int8_path + ".pretmp.onnx"
            print(f"  quant_pre_process(skip_symbolic=True): OK (fallback)")
        except Exception as e:
            print(f"  quant_pre_process(skip_symbolic=True): also FAILED ({type(e).__name__}: {str(e)[:120]})")

    # Step 2: dynamic INT8 quantization (weights INT8, MatMul-focused)
    try:
        quantize_dynamic(pre_path, int8_path, weight_type=QuantType.QInt8)
        print(f"  quantize_dynamic: OK -> {os.path.getsize(int8_path)/1e6:.1f} MB")
    except Exception as e:
        print(f"  quantize_dynamic: FAILED ({type(e).__name__}: {str(e)[:200]})")
        return

    # Count how many ops actually became quantized (DynamicQuantizeLinear / MatMulInteger)
    try:
        qm = onnx.load(int8_path)
        import collections
        c = collections.Counter(n.op_type for n in qm.graph.node)
        qsig = {k: c[k] for k in ("MatMulInteger", "ConvInteger", "DynamicQuantizeLinear",
                                  "QLinearMatMul", "QLinearConv", "MatMul", "Conv") if c.get(k)}
        print(f"  quantized-op signature: {qsig}")
    except Exception as e:
        print(f"  (op signature read failed: {e})")

    # Step 3: load + run + time INT8
    try:
        q_min, q_med = time_model(int8_path, feeds)
        print(f"  INT8  min={q_min:.1f} ms  median={q_med:.1f} ms")
        print(f"  >>> SPEEDUP (median): {f_med / q_med:.2f}x   (size {os.path.getsize(path)/1e6:.0f}->{os.path.getsize(int8_path)/1e6:.0f} MB)")
    except Exception as e:
        print(f"  INT8 RUN FAILED ({type(e).__name__}: {str(e)[:200]})")


if __name__ == "__main__":
    print(f"onnxruntime {ort.__version__}  providers={ort.get_available_providers()}")
    for tag, path in MODELS.items():
        try:
            run(tag, path)
        except Exception:
            print(f"  UNEXPECTED for {tag}:\n{traceback.format_exc()}")
    print("=" * 72)
    print("done")
