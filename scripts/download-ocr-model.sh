#!/bin/bash
# Download an additional (multilingual) OCR recognition model set for
# RailReader.Core.Ocr.RapidOcr.
#
# The RapidOcrNet NuGet package already bundles PP-OCRv5-Latin, which
# RapidOcrService uses by default with no download needed — but it only
# recognizes Latin-script text (railreader2#209). The PP-OCRv6 sets fetched
# here are RapidOCR's multilingual recognizers (Latin + CJK and more) in
# three size/accuracy tiers; pass the matching RapidOcrModelSet (see
# OcrModelRegistry in RailReader.Core.Ocr.RapidOcr) to RapidOcrService's
# constructor to use one once downloaded.
#
# Usage:
#   ./download-ocr-model.sh            # tiny (smallest, ~6 MB) — default
#   ./download-ocr-model.sh tiny       # PP-OCRv6 Tiny (~6 MB)
#   ./download-ocr-model.sh small      # PP-OCRv6 Small (~31 MB)
#   ./download-ocr-model.sh medium     # PP-OCRv6 Medium, most accurate (~138 MB)
#   ./download-ocr-model.sh all        # all three
set -e

MODEL_DIR="$(dirname "$0")/../models/v6"
mkdir -p "$MODEL_DIR"

WHICH="${1:-tiny}"

# URLs and SHA-256 hashes are sourced from RapidOCR's own model manifest
# (https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/default_models.yaml),
# the upstream project RapidOcrNet builds on — verified 2026-08-11 by downloading each
# file and independently re-hashing it, not by trusting the manifest text alone.
BASE="https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2"

verify_sha256() {
    local path="$1" expected="$2"
    local actual
    actual="$(sha256sum "$path" | cut -d' ' -f1)"
    if [ "$actual" != "$expected" ]; then
        echo "SHA-256 mismatch for $path" >&2
        echo "  expected: $expected" >&2
        echo "  actual:   $actual" >&2
        rm -f "$path"
        exit 1
    fi
}

download_file() {
    local url="$1" path="$2" sha256="$3" label="$4"
    if [ -f "$path" ]; then
        echo "$label already exists at $path"
        return
    fi
    echo "Downloading $label..."
    curl -L -o "$path" "$url"
    verify_sha256 "$path" "$sha256"
    echo "Downloaded to $path ($(du -h "$path" | cut -f1))"
}

download_tiny() {
    download_file "$BASE/onnx/PP-OCRv6/det/PP-OCRv6_det_tiny.onnx" \
        "$MODEL_DIR/PP-OCRv6_det_tiny.onnx" \
        "f42c0fbd294d95eac1a550e131b277dac97462c8025fa4b6c3cec1b7894bd3d5" \
        "PP-OCRv6 Tiny detector"
    download_file "$BASE/onnx/PP-OCRv6/rec/PP-OCRv6_rec_tiny.onnx" \
        "$MODEL_DIR/PP-OCRv6_rec_tiny.onnx" \
        "e16e242de5937ad92609223f19bc2aff3727ee40b095f996907c24749bad251b" \
        "PP-OCRv6 Tiny recognizer"
    download_file "$BASE/paddle/PP-OCRv6/rec/PP-OCRv6_rec_tiny/ppocrv6_tiny_dict.txt" \
        "$MODEL_DIR/ppocrv6_tiny_dict.txt" \
        "c5cbe34ef40c29c4df07ed012bf96569cb69a2d2a01a07027e9f13cb832bd9cd" \
        "PP-OCRv6 Tiny dictionary"
}

download_small() {
    download_file "$BASE/onnx/PP-OCRv6/det/PP-OCRv6_det_small.onnx" \
        "$MODEL_DIR/PP-OCRv6_det_small.onnx" \
        "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f" \
        "PP-OCRv6 Small detector"
    download_file "$BASE/onnx/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx" \
        "$MODEL_DIR/PP-OCRv6_rec_small.onnx" \
        "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884" \
        "PP-OCRv6 Small recognizer"
    download_file "$BASE/paddle/PP-OCRv6/rec/PP-OCRv6_rec_small/ppocrv6_dict.txt" \
        "$MODEL_DIR/ppocrv6_dict.txt" \
        "b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d" \
        "PP-OCRv6 Small/Medium dictionary"
}

download_medium() {
    download_file "$BASE/onnx/PP-OCRv6/det/PP-OCRv6_det_medium.onnx" \
        "$MODEL_DIR/PP-OCRv6_det_medium.onnx" \
        "92078b7355007ccfffcd4c8cd441a3afd4538904d06881b29a155e1e679907c2" \
        "PP-OCRv6 Medium detector"
    download_file "$BASE/onnx/PP-OCRv6/rec/PP-OCRv6_rec_medium.onnx" \
        "$MODEL_DIR/PP-OCRv6_rec_medium.onnx" \
        "eef444829dbbe18d7fea59a3f6eb75647518d2b3a9568d27c92e42940204894b" \
        "PP-OCRv6 Medium recognizer"
    # Medium shares its dictionary with Small (both key off ppocrv6_dict.txt).
    download_file "$BASE/paddle/PP-OCRv6/rec/PP-OCRv6_rec_medium/ppocrv6_dict.txt" \
        "$MODEL_DIR/ppocrv6_dict.txt" \
        "b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d" \
        "PP-OCRv6 Small/Medium dictionary"
}

case "$WHICH" in
    tiny|default)
        download_tiny
        ;;
    small)
        download_small
        ;;
    medium)
        download_medium
        ;;
    all)
        download_tiny
        download_small
        download_medium
        ;;
    *)
        echo "Unknown model: $WHICH" >&2
        echo "Usage: $0 [tiny|small|medium|all]" >&2
        exit 1
        ;;
esac
