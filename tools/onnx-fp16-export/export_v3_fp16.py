import torch, torch.nn as nn, onnx, time, sys
from transformers import PPDocLayoutV3ForObjectDetection

class V3Fp16Export(nn.Module):
    """Wraps PPDocLayoutV3ForObjectDetection (HF port of the Paddle PP-DocLayoutV3
    weights) to reproduce the [N,7] (classId, confidence, xmin, ymin, xmax, ymax,
    readingOrder) flat-tensor contract LayoutAnalyzer.cs expects from the original
    paddle2onnx export -- skipping the mask/polygon head (out_masks / cv2 polygon
    extraction) our C# consumer never reads. Reading-order decode mirrors
    PPDocLayoutV3ImageProcessor._get_order_seqs exactly; box/score decode mirrors
    its post_process_object_detection (global top-K over queries*classes, matching
    the reference implementation) rather than an ad-hoc per-query argmax -- the
    caller (LayoutAnalyzer.Nms) re-suppresses/thresholds downstream regardless, so
    this only needs to match the reference's selection, not add a new one.
    Backbone/encoder/decoder run in fp16; postprocessing arithmetic stays fp32.
    """
    def __init__(self, model, num_top_queries=300):
        super().__init__()
        self.model = model.half()
        self.num_top_queries = num_top_queries

    def forward(self, image, im_shape, scale_factor):
        pixel_values = image.half()
        outputs = self.model(pixel_values=pixel_values)
        logits = outputs.logits.float()
        pred_boxes = outputs.pred_boxes.float()
        order_logits = outputs.order_logits.float()

        # Reading order -- mirrors _get_order_seqs.
        order_scores = torch.sigmoid(order_logits)
        batch_size, seq_len, _ = order_scores.shape
        order_votes = order_scores.triu(diagonal=1).sum(dim=1) + \
            (1.0 - order_scores.transpose(1, 2)).tril(diagonal=-1).sum(dim=1)
        order_pointers = torch.argsort(order_votes, dim=1)
        order_seq = torch.empty_like(order_pointers)
        ranks = torch.arange(seq_len, device=order_pointers.device, dtype=order_pointers.dtype) \
            .expand(batch_size, -1)
        order_seq.scatter_(1, order_pointers, ranks)
        order_seq = order_seq.float()

        # Box decode -- mirrors post_process_object_detection.
        box_centers, box_dims = torch.split(pred_boxes, 2, dim=-1)
        top_left = box_centers - 0.5 * box_dims
        bottom_right = box_centers + 0.5 * box_dims
        boxes = torch.cat([top_left, bottom_right], dim=-1)
        img_h, img_w = im_shape.unbind(1)
        scale_fct = torch.stack([img_w, img_h, img_w, img_h], dim=1).to(boxes.dtype)
        boxes = boxes * scale_fct[:, None, :]
        # keep scale_factor a real graph input (C# always passes it) even though
        # its value is always [1,1] in this pipeline -- a no-op multiply.
        boxes = boxes * scale_factor[:, 0:1, None]

        num_classes = logits.shape[2]
        scores = torch.sigmoid(logits)
        scores, index = torch.topk(scores.flatten(1), self.num_top_queries, dim=-1)
        labels = (index % num_classes).float()
        box_index = index // num_classes
        boxes = boxes.gather(dim=1, index=box_index.unsqueeze(-1).repeat(1, 1, boxes.shape[-1]))
        order_seq = order_seq.gather(dim=1, index=box_index)

        det = torch.cat([labels.unsqueeze(-1), scores.unsqueeze(-1), boxes, order_seq.unsqueeze(-1)], dim=-1)
        return det[0]  # [num_top_queries, 7] -- caller always passes batch=1

def main():
    src_repo, out_path = sys.argv[1], sys.argv[2]
    t0 = time.time()
    device = "cuda" if torch.cuda.is_available() else "cpu"
    base = PPDocLayoutV3ForObjectDetection.from_pretrained(src_repo).to(device)
    base.eval()
    wrapper = V3Fp16Export(base).to(device).eval()
    print("model loaded", time.time() - t0, flush=True)

    dummy_image = torch.rand(1, 3, 800, 800, dtype=torch.float32, device=device)
    dummy_im_shape = torch.tensor([[800.0, 800.0]], dtype=torch.float32, device=device)
    dummy_scale_factor = torch.tensor([[1.0, 1.0]], dtype=torch.float32, device=device)

    torch.onnx.export(
        wrapper,
        (dummy_image, dummy_im_shape, dummy_scale_factor),
        out_path,
        input_names=["image", "im_shape", "scale_factor"],
        output_names=["det"],
        dynamic_axes={"image": {0: "batch"}, "im_shape": {0: "batch"}, "scale_factor": {0: "batch"}},
        opset_version=18,
        do_constant_folding=True,
    )
    print("exported", time.time() - t0, flush=True)

    m = onnx.load(out_path)
    onnx.checker.check_model(m)
    print("checked OK", time.time() - t0, flush=True)

    # torch.onnx's own constant folding leaves the (input-shape-static, since
    # only batch is dynamic) sinusoidal position-embedding subgraph as runtime
    # ops instead of baked constants. Left alone, ONNX Runtime's WebGPU EP
    # fails at session-init time ("Provider type for Cos node ... is not set")
    # -- a graph-partitioning bug, not a numerical one; CPU EP loads the
    # unsimplified graph fine. onnxsim's constant folding collapses ~4600
    # nodes to ~1200 and resolves it.
    from onnxsim import simplify
    m_sim, ok = simplify(m, overwrite_input_shapes={"image": [1, 3, 800, 800], "im_shape": [1, 2], "scale_factor": [1, 2]})
    if not ok:
        print("WARNING: onnxsim reported failure; the pre-simplify model is still valid on CPU EP but may not load on WebGPU EP.", flush=True)
    else:
        onnx.save(m_sim, out_path, save_as_external_data=False)
        print(f"simplified {len(m.graph.node)} -> {len(m_sim.graph.node)} nodes, saved", time.time() - t0, flush=True)

if __name__ == "__main__":
    main()
