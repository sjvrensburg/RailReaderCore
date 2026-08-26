#!/usr/bin/env python3
"""
Adds intermediate-tensor outputs to the Heron FP16 ONNX graph at a handful of
checkpoints spanning stem -> backbone -> encoder -> decoder, so
WebGpuDiag can compare CPU-EP vs WebGPU-EP activations layer-by-layer and
localize where the two execution providers start to diverge (issue #109).

Usage: python3 make_debug_model.py <src.onnx> <dst.onnx>
"""
import sys
import onnx

SRC, DST = sys.argv[1], sys.argv[2]

# Picked by inspecting the graph's Conv/LayerNormalization/GridSample node
# indices (evenly spaced through the backbone, then encoder start, then
# first/last decoder deformable-attention layer, then final decoder norm).
# Re-derive with:
#   m = onnx.load(SRC); [n.output for n in m.graph.node if n.op_type == "Conv"]
CHECKPOINTS = [
    "/model/model/backbone/model/embedder/embedder/embedder.0/convolution/Conv_output_0",   # stem
    "/model/model/backbone/model/encoder/stages.1/layers.3/layer/layer.1/convolution/Conv_output_0",  # ~25% backbone
    "/model/model/backbone/model/encoder/stages.3/layers.0/shortcut/shortcut.1/convolution/Conv_output_0",  # ~50% backbone
    "/model/model/decoder_input_proj.2/decoder_input_proj.2.0/Conv_output_0",  # end of backbone/proj
    "/model/model/encoder/encoder.0/layers.0/self_attn_layer_norm/LayerNormalization_output_0",  # encoder start
    "/model/model/decoder/layers.0/encoder_attn/GridSample_output_0",  # decoder layer 0 deformable attn
    "/model/model/decoder/layers.5/encoder_attn/GridSample_2_output_0",  # decoder layer 5 (last) deformable attn
    "/model/model/decoder/layers.5/final_layer_norm/LayerNormalization_output_0",  # last decoder layer output
]

m = onnx.load(SRC)
existing = {o.name for o in m.graph.output}
for name in CHECKPOINTS:
    if name in existing:
        continue
    m.graph.output.append(onnx.helper.make_empty_tensor_value_info(name))

onnx.save(m, DST, save_as_external_data=False)
print("saved", DST)
print("outputs:", [o.name for o in m.graph.output])
