namespace RailReader.Core.Models;

/// <summary>
/// ONNX architecture family of a layout-detection model. Determines the
/// preprocessing, the input/output tensor contract, and which
/// <c>ILayoutAnalyzer</c> implementation can run the file. Deliberately
/// decoupled from the model <em>file</em> so a quantized or re-exported variant
/// of the same architecture reuses the same analyzer (e.g. FP32 and INT8 Heron
/// are both <see cref="Heron"/>).
/// </summary>
public enum LayoutModelArchitecture
{
    /// <summary>
    /// PP-DocLayoutV3 (PaddleDetection DETR-family). Inputs
    /// <c>im_shape</c>/<c>image</c>/<c>scale_factor</c>; emits a model-supplied
    /// reading-order column. 800×800 letterboxed input.
    /// </summary>
    PPDocLayoutV3,

    /// <summary>
    /// PP-DocLayout-S (PicoDet/GFL). Inputs <c>image</c>/<c>scale_factor</c>,
    /// NMS baked into the graph. Lightweight 480×480 model input.
    /// </summary>
    PPDocLayoutS,

    /// <summary>
    /// Docling Heron (RT-DETRv2). Inputs <c>images</c>/<c>orig_target_sizes</c>,
    /// post-processing baked into the graph. 640×640 distort-resized input.
    /// </summary>
    Heron,
}
