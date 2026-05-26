#!/bin/bash
# Download layout-analysis models from HuggingFace
#
# Usage:
#   ./download-model.sh             # download default model (PP-DocLayoutV3)
#   ./download-model.sh ppdoc       # PP-DocLayoutV3 only
#   ./download-model.sh pps         # PP-DocLayout-S only (lightweight, ~4.7 MB)
#   ./download-model.sh heron       # Docling Heron only
#   ./download-model.sh lightgbm    # huridocs LightGBM (token type + paragraph; ~few hundred KB)
#   ./download-model.sh all         # all of the above
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

download_lightgbm() {
    # huridocs's fast text-only DLA pipeline. Two LightGBM models, each
    # in LightGBM's plain text-dump format (loadable via lgb.Booster on
    # Python side, via LightGBMNet.Tree.OvaPredictor / BinaryPredictor
    # on .NET). Total ~few hundred KB combined. License: Apache-2.0
    # (trained on DocLayNet, CDLA-Permissive-1.0).
    local base="https://huggingface.co/HURIDOCS/pdf-document-layout-analysis/resolve/main"
    for f in token_type_lightgbm.model paragraph_extraction_lightgbm.model; do
        local path="$MODEL_DIR/$f"
        if [ -f "$path" ]; then
            echo "$f already exists at $path"
            continue
        fi
        echo "Downloading $f..."
        curl -L -o "$path" "$base/$f"
        echo "Downloaded to $path ($(du -h "$path" | cut -f1))"
    done
}

case "$WHICH" in
    ppdoc|pp|ppdoclayoutv3)
        download_ppdoc
        ;;
    pps|pp-s|ppdocs|ppdoclayouts)
        download_pps
        ;;
    heron|docling)
        download_heron
        ;;
    lightgbm|lgbm|huridocs)
        download_lightgbm
        ;;
    all)
        download_ppdoc
        download_pps
        download_heron
        download_lightgbm
        ;;
    both)
        download_ppdoc
        download_heron
        ;;
    *)
        echo "Unknown model: $WHICH" >&2
        echo "Usage: $0 [ppdoc|pps|heron|lightgbm|all]" >&2
        exit 1
        ;;
esac
