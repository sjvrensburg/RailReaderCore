#!/bin/bash
# Download ONNX models from HuggingFace
#
# Usage:
#   ./download-model.sh             # download default model (PP-DocLayoutV3)
#   ./download-model.sh ppdoc       # PP-DocLayoutV3 only
#   ./download-model.sh heron       # Docling Heron only
#   ./download-model.sh all         # both
set -e

MODEL_DIR="$(dirname "$0")/../models"
mkdir -p "$MODEL_DIR"

WHICH="${1:-ppdoc}"

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

download_heron() {
    # Docling Heron (RT-DETRv2, 17-class, 640x640) — alternative ILayoutAnalyzer.
    # Distributed separately from the desktop installer at the user's request;
    # download manually and either drop it into ./models alongside the PP
    # model, or point AppConfig at the file directly (the desktop app picks
    # which analyzer to wire up — see railreader2).
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
    ppdoc|pp|ppdoclayoutv3)
        download_ppdoc
        ;;
    heron|docling)
        download_heron
        ;;
    all|both)
        download_ppdoc
        download_heron
        ;;
    *)
        echo "Unknown model: $WHICH" >&2
        echo "Usage: $0 [ppdoc|heron|all]" >&2
        exit 1
        ;;
esac
