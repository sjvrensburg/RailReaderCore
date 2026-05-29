#!/usr/bin/env python3
"""
Publish the backbone-only INT8 Heron ONNX to a new HF model repo:
  stefanj0/docling-layout-heron-int8-onnx
Reads the base model's license so the card is accurate; writes a model card;
uploads the .onnx.
"""
import os
from huggingface_hub import create_repo, upload_file, model_info

REPO = "stefanj0/docling-layout-heron-int8-onnx"
BASE = "docling-project/docling-layout-heron-onnx"
SRC = "/home/stefan/RailReaderCore/tools/quant-probe/out/heron_backbone_int8.onnx"
DST_NAME = "docling-layout-heron-int8.onnx"

# Discover the base model's license to inherit it honestly.
base_license = "other"
try:
    cd = model_info(BASE).cardData or {}
    base_license = cd.get("license", "other") or "other"
    print(f"base license: {base_license}")
except Exception as e:
    print(f"could not read base license ({e}); using 'other'")

card = f"""---
license: {base_license}
base_model: {BASE}
pipeline_tag: object-detection
library_name: onnxruntime
tags:
- document-layout-analysis
- object-detection
- onnx
- int8
- quantized
- rt-detr
- docling
---

# Docling Heron Layout — backbone-only INT8 (ONNX)

Static **INT8** ONNX quantization of
[`{BASE}`](https://huggingface.co/{BASE}) (Docling Heron, RT-DETRv2, 17-class
document-layout detection at 640×640). Produced for
[RailReaderCore](https://github.com/sjvrensburg/RailReaderCore).

## What this is
QDQ static quantization of **only the CNN backbone `Conv` ops**; the RT-DETR
transformer **decoder is left in FP32**.

## Why backbone-only
Quantizing the decoder drives two quantization scales
(`model.decoder.anchors_scale`, `add_2355_scale`) to `Inf`, collapsing all
detection scores → zero detections. Leaving the decoder FP32 avoids this, and
the backbone is where essentially all of the convolutional compute (and the
speedup) lives. Quantizing any MatMul (encoder or decoder) was measured to
either break the model or reduce recall with no speed gain.

## Validation
Versus the FP32 base on **88 held-out** real academic pages (detection
agreement, FP32 as reference):

| metric | value |
|---|---|
| recall | 0.990 |
| precision | 0.988 |
| mean IoU (matched) | 0.984 |
| pages with <90% recall | 1 / 88 |
| inference speedup (AVX-VNNI CPU, i7-12700H) | ~2.6–3× |

Visually faithful on dense and math-heavy pages.

> **Caveat:** agreement is measured against the FP32 Heron model, not labelled
> ground truth — i.e. this is *near-lossless versus the model you would
> otherwise ship*, not an independent mAP claim.

## I/O contract (identical to the base export)
- **Inputs:** `images` uint8 `[1,3,640,640]` NCHW; `orig_target_sizes` int64
  `[1,2]` = `[W, H]` (note: `[W, H]`, not `[H, W]`).
- **Outputs:** `labels` `[1,300]`, `boxes` `[1,300,4]` (xyxy in pixel space),
  `scores` `[1,300]`.

17 classes: caption, footnote, formula, list_item, page_footer, page_header,
picture, section_header, table, text, title, document_index, code,
checkbox_selected, checkbox_unselected, form, key_value_region.

## Recipe
```python
from onnxruntime.quantization import quantize_static, QuantFormat, QuantType, CalibrationMethod
quantize_static(
    pre_processed_fp32, out, calibration_reader,
    quant_format=QuantFormat.QDQ, per_channel=True,
    activation_type=QuantType.QUInt8, weight_type=QuantType.QInt8,
    calibrate_method=CalibrationMethod.MinMax,
    op_types_to_quantize=["Conv"],
)
```
Calibration: real rasterized academic pages. Full reproducible tooling:
`tools/quant-probe/` in RailReaderCore.

## License
Inherits the license of the base model
[`{BASE}`](https://huggingface.co/{BASE}).
"""

print(f"creating repo {REPO} ...")
create_repo(REPO, repo_type="model", exist_ok=True, private=False)

print("uploading model card ...")
upload_file(path_or_fileobj=card.encode("utf-8"), path_in_repo="README.md",
            repo_id=REPO, repo_type="model")

print(f"uploading {DST_NAME} ({os.path.getsize(SRC)/1e6:.0f} MB) ...")
upload_file(path_or_fileobj=SRC, path_in_repo=DST_NAME,
            repo_id=REPO, repo_type="model")

print(f"DONE: https://huggingface.co/{REPO}")
print(f"resolve URL: https://huggingface.co/{REPO}/resolve/main/{DST_NAME}")
