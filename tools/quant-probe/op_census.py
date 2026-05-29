#!/usr/bin/env python3
"""
Op-type census for the layout-detection ONNX models, to assess quantization
fitness BEFORE attempting it. Reports, per model:
  - file size, IR version, opset(s), input/output shapes
  - op-type histogram (which ops dominate -> conv-heavy vs transformer-heavy)
  - a curated set of "quantization-hostile" ops baked into the graph
  - non-default op domains (custom ops: deformable conv etc. often live here)
  - quantizable-compute signal: Conv / MatMul / Gemm counts

Pure `onnx` only (no onnxruntime needed for this phase).
"""
import sys
import os
import collections
import onnx

# Ops that complicate INT8 quantization when present in the graph:
#  - they have no/poor INT8 CPU kernel, so QDQ must dequantize around them,
#    fragmenting the quantized region and eroding the speedup; or they break
#    the shape-inference pre-pass that static quantization requires.
HOSTILE = {
    "NonMaxSuppression": "NMS baked in graph; not quantizable, forces float boundary",
    "TopK": "selection op; stays float, QDQ boundary",
    "GridSample": "deformable/sampling; limited INT8 support",
    "RoiAlign": "ROI sampling; limited INT8 support",
    "NonZero": "dynamic-shape op; breaks symbolic shape inference",
    "Where": "often part of dynamic NMS/threshold loop",
    "Range": "dynamic-shape op; breaks shape inference",
    "Loop": "control flow; quantizer cannot descend cleanly",
    "If": "control flow; quantizer cannot descend cleanly",
    "ScatterND": "scatter; stays float",
    "GatherND": "gather; stays float",
    "RoiAlign": "ROI sampling; limited INT8 support",
}

MODELS = {
    "V3 (PP-DocLayoutV3)": "/home/stefan/railreader2/models/PP-DocLayoutV3.onnx",
    "Heron (RT-DETRv2)":   "/home/stefan/railreader2/experiments/docling-layout/heron.onnx",
    "PP-S (PicoDet)":      "/home/stefan/railreader2/experiments/pp-doclayout-s/pp_doclayout_s.onnx",
}


def shape_of(vi):
    dims = []
    for d in vi.type.tensor_type.shape.dim:
        dims.append(d.dim_value if d.HasField("dim_value") else (d.dim_param or "?"))
    return dims


def census(label, path):
    print("=" * 72)
    print(f"{label}")
    print(f"  path: {path}")
    if not os.path.exists(path):
        print("  MISSING")
        return
    print(f"  size: {os.path.getsize(path)/1e6:.1f} MB")
    m = onnx.load(path)
    print(f"  ir_version: {m.ir_version}")
    opsets = [(o.domain or '<default>', o.version) for o in m.opset_import]
    print(f"  opsets: {opsets}")

    g = m.graph
    print("  inputs:")
    for i in g.input:
        print(f"    {i.name}: {shape_of(i)}")
    print("  outputs:")
    for o in g.output:
        print(f"    {o.name}: {shape_of(o)}")

    counts = collections.Counter(n.op_type for n in g.node)
    domains = collections.Counter((n.domain or "<default>") for n in g.node)
    total = sum(counts.values())
    print(f"  total nodes: {total}")
    print(f"  op domains: {dict(domains)}")

    conv = counts.get("Conv", 0)
    matmul = counts.get("MatMul", 0) + counts.get("Gemm", 0)
    print(f"  >>> Conv={conv}  MatMul+Gemm={matmul}  "
          f"(conv-heavy if Conv dominates; transformer-heavy if MatMul dominates)")

    print("  top 20 op types:")
    for op, c in counts.most_common(20):
        print(f"    {c:5d}  {op}")

    print("  HOSTILE-to-quantization ops present:")
    any_hostile = False
    for op, why in HOSTILE.items():
        if counts.get(op):
            any_hostile = True
            print(f"    [{counts[op]:3d}] {op:18s} — {why}")
    if not any_hostile:
        print("    (none of the curated hostile ops found)")

    # Any op type that is NOT a standard well-quantized op and appears a lot
    # is worth eyeballing; print full distinct op-type set for the record.
    print(f"  distinct op types ({len(counts)}): {sorted(counts)}")


if __name__ == "__main__":
    for label, path in MODELS.items():
        census(label, path)
    print("=" * 72)
    print("done")
