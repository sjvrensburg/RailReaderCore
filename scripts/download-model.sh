#!/bin/bash
# Download layout-analysis models from HuggingFace
#
# Usage:
#   ./download-model.sh             # download default model (Heron INT8, recommended)
#   ./download-model.sh heron-int8  # Docling Heron, backbone-only INT8 (~69 MB) — default
#   ./download-model.sh heron       # Docling Heron FP32 (~164 MB)
#   ./download-model.sh ppdoc       # PP-DocLayoutV3 FP32 (~125 MB)
#   ./download-model.sh pps         # PP-DocLayout-S only (lightweight, ~4.7 MB)
#   ./download-model.sh all         # all of the above
set -e

MODEL_DIR="$(dirname "$0")/../models"
mkdir -p "$MODEL_DIR"

WHICH="${1:-heron-int8}"

download_heron_int8() {
    # Docling Heron, backbone-only INT8 quantization (RT-DETRv2, 17-class,
    # 640x640). The recommended default: ~2.6-3x faster than FP32 PP-DocLayoutV3
    # on a VNNI-capable CPU, validated at ~0.99 detection agreement with FP32
    # Heron on held-out academic pages. Only the CNN backbone is quantized; the
    # RT-DETR decoder stays FP32 (full-graph quantization corrupts its anchor
    # scales). Quantized + published for RailReader; see tools/quant-probe.
    local path="$MODEL_DIR/docling-layout-heron-int8.onnx"
    if [ -f "$path" ]; then
        echo "Docling Heron INT8 already exists at $path"
        return
    fi
    local url="${HERON_INT8_ONNX_URL:-https://huggingface.co/stefanj0/docling-layout-heron-int8-onnx/resolve/main/docling-layout-heron-int8.onnx}"
    echo "Downloading docling-layout-heron-int8.onnx (~69 MB)..."
    curl -L -o "$path" "$url"
    echo "Downloaded to $path ($(du -h "$path" | cut -f1))"
}

download_ppdoc() {
    local path="$MODEL_DIR/PP-DocLayoutV3.onnx"
    if [ -f "$path" ]; then
        echo "PP-DocLayoutV3 already exists at $path"
        return
    fi
    echo "Downloading PP-DocLayoutV3.onnx..."
    curl -L -o "$path" \
        "https://huggingface.co/alex-dinh/PP-DocLayoutV3-ONNX/resolve/main/PP-DocLayoutV3.onnx"
    echo "Downloaded to $path ($(du -h "$path" | cut -f1))"
}

download_pps() {
    # PP-DocLayout-S (PicoDet/GFL, 23-class, 480x480 model input) — lightweight
    # ILayoutAnalyzer intended for web (WASM/ORT-Web) and mobile builds where
    # the 50 MB V3 model is too heavy. Hosted at stefanj0/PP-DocLayout-S-ONNX
    # since PaddlePaddle ship the checkpoint only in Paddle-native format
    # (.pdiparams + inference.json) — the .onnx there was produced locally via
    # paddle2onnx from that upstream checkpoint.
    local path="$MODEL_DIR/pp_doclayout_s.onnx"
    if [ -f "$path" ]; then
        echo "PP-DocLayout-S already exists at $path"
        return
    fi

    local url="${PP_S_ONNX_URL:-https://huggingface.co/stefanj0/PP-DocLayout-S-ONNX/resolve/main/pp_doclayout_s.onnx}"
    echo "Downloading pp_doclayout_s.onnx (~4.7 MB) ..."
    curl -L -o "$path" "$url"
    echo "Downloaded to $path ($(du -h "$path" | cut -f1))"
}

download_heron() {
    # Docling Heron FP32 (RT-DETRv2, 17-class, 640x640). The INT8 variant
    # (download_heron_int8) is preferred for desktop; this FP32 export is the
    # upstream reference / for accuracy comparison.
    local path="$MODEL_DIR/docling-layout-heron.onnx"
    if [ -f "$path" ]; then
        echo "Docling Heron already exists at $path"
        return
    fi
    echo "Downloading Docling Heron ONNX (~164 MB)..."
    curl -L -o "$path" \
        "https://huggingface.co/docling-project/docling-layout-heron-onnx/resolve/main/model.onnx"
    echo "Downloaded to $path ($(du -h "$path" | cut -f1))"
}

case "$WHICH" in
    heron-int8|heronint8|int8|default)
        download_heron_int8
        ;;
    ppdoc|pp|ppdoclayoutv3)
        download_ppdoc
        ;;
    pps|pp-s|ppdocs|ppdoclayouts)
        download_pps
        ;;
    heron|docling)
        download_heron
        ;;
    all)
        download_heron_int8
        download_ppdoc
        download_pps
        download_heron
        ;;
    both)
        download_ppdoc
        download_heron
        ;;
    *)
        echo "Unknown model: $WHICH" >&2
        echo "Usage: $0 [heron-int8|heron|ppdoc|pps|all]" >&2
        exit 1
        ;;
esac
