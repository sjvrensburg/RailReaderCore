import torch, torch.nn as nn, onnx, time, sys
from transformers import RTDetrV2ForObjectDetection

def center_to_corners_format(x):
    cx, cy, w, h = x.unbind(-1)
    return torch.stack([cx - 0.5*w, cy - 0.5*h, cx + 0.5*w, cy + 0.5*h], dim=-1)

class HeronFp16Export(nn.Module):
    """Mirrors lyuwenyu/RT-DETR's export_onnx.py wrapper (images/orig_target_sizes ->
    labels/boxes/scores, matching docling's published ONNX I/O contract) but runs the
    backbone/encoder/decoder in fp16 while keeping the box-decode/sigmoid/top-k
    postprocessing in fp32 for numerical stability -- the mixed-precision arithmetic
    that broke when patched onto an already-exported graph post-hoc instead works
    cleanly here because PyTorch's own tracer keeps every op's dtype consistent.
    """
    def __init__(self, model, num_top_queries=300):
        super().__init__()
        self.model = model.half()
        self.num_top_queries = num_top_queries

    def forward(self, images, orig_target_sizes):
        # images: uint8 [B,3,H,W] -- same raw-pixel input contract as the original graph
        pixel_values = images.to(torch.float32) / 255.0
        pixel_values = pixel_values.half()
        outputs = self.model(pixel_values=pixel_values)
        logits = outputs.logits.float()
        pred_boxes = outputs.pred_boxes.float()

        boxes = center_to_corners_format(pred_boxes)
        # NOTE: this export takes orig_target_sizes as [W, H], not HF's standard
        # [H, W] -- matching the existing docling heron.onnx export's (reverse-
        # engineered, see HeronLayoutAnalyzer.cs) convention so this is a drop-in
        # replacement with no C# call-site changes.
        img_w, img_h = orig_target_sizes.unbind(1)
        scale_fct = torch.stack([img_w, img_h, img_w, img_h], dim=1).to(boxes.dtype)
        boxes = boxes * scale_fct[:, None, :]

        num_classes = logits.shape[2]
        scores = torch.sigmoid(logits)
        scores, index = torch.topk(scores.flatten(1), self.num_top_queries, dim=-1)
        labels = index % num_classes
        index = index // num_classes
        boxes = boxes.gather(dim=1, index=index.unsqueeze(-1).repeat(1, 1, boxes.shape[-1]))
        return labels, boxes, scores

def main():
    src_repo, out_path = sys.argv[1], sys.argv[2]
    t0 = time.time()
    device = "cuda" if torch.cuda.is_available() else "cpu"
    base = RTDetrV2ForObjectDetection.from_pretrained(src_repo).to(device)
    base.eval()
    wrapper = HeronFp16Export(base).to(device).eval()
    print("model loaded", time.time() - t0, flush=True)

    dummy_images = torch.randint(0, 256, (1, 3, 640, 640), dtype=torch.uint8, device=device)
    dummy_sizes = torch.tensor([[640, 640]], dtype=torch.int64, device=device)

    torch.onnx.export(
        wrapper,
        (dummy_images, dummy_sizes),
        out_path,
        input_names=["images", "orig_target_sizes"],
        output_names=["labels", "boxes", "scores"],
        dynamic_axes={
            "images": {0: "batch"},
            "orig_target_sizes": {0: "batch"},
            "labels": {0: "batch"},
            "boxes": {0: "batch"},
            "scores": {0: "batch"},
        },
        opset_version=18,
        do_constant_folding=True,
    )
    print("exported", time.time() - t0, flush=True)

    m = onnx.load(out_path)
    onnx.checker.check_model(m)
    print("checked OK", time.time() - t0, flush=True)

if __name__ == "__main__":
    main()
