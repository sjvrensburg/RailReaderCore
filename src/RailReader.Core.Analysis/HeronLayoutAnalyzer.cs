using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RailReader.Core;
using RailReader.Core.Analysis;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Docling Heron layout analyzer (RT-DETRv2, 17-class, 640×640). Implements
/// <see cref="ILayoutAnalyzer"/> as a sibling of the PP-DocLayoutV3-based
/// <see cref="LayoutAnalyzer"/>; consumers choose which to instantiate.
///
/// <para>
/// The ONNX export at <c>docling-project/docling-layout-heron-onnx</c> has
/// post-processing baked in (top-k, score sigmoid, box decode) and exposes
/// three named outputs: <c>labels</c>, <c>boxes</c>, <c>scores</c>. Inputs
/// are <c>images</c> (uint8, NCHW 1×3×640×640) and <c>orig_target_sizes</c>
/// (int64 <b>[W, H]</b>) — the model uses <c>orig_target_sizes</c> internally
/// to scale predicted boxes back into pixel space, so passing the caller's
/// rasterised <c>(pxW, pxH)</c> here yields boxes directly in that frame.
/// Note this is <em>opposite</em> to the HuggingFace
/// <c>RTDetrImageProcessor.post_process_object_detection(target_sizes=…)</c>
/// convention used by the PyTorch reference predictor — which takes <c>[H, W]</c>.
/// The mismatch was verified empirically against the published 0.4.0 model:
/// passing <c>[H, W]</c> transposes outputs, so boxes near the bottom of a
/// portrait page get clipped because the model thinks the canvas is wider
/// than tall (<a href="https://github.com/sjvrensburg/RailReaderCore/issues">issue link</a>).
/// </para>
///
/// <para>
/// Heron was trained with resize-and-distort to 640×640 (per its
/// <c>preprocessor_config.json</c>: <c>do_resize: true, do_pad: false</c>),
/// not letterbox. The existing renderer rasterises pages at <c>max(pxW, pxH) ==
/// InputSize</c>; this analyzer therefore bilinearly resizes the caller's
/// rasterised pixmap from <c>pxW × pxH</c> to exactly 640×640 (squishing
/// aspect ratio internally — predicted boxes still come back in
/// <c>pxW × pxH</c> space because we pass <c>orig_target_sizes</c>).
/// </para>
///
/// <para>
/// Reading order is not produced; the pipeline pairs Heron with an
/// <see cref="IReadingOrderResolver"/> (default
/// <see cref="XYCutPlusPlusResolver"/>) downstream.
/// </para>
/// </summary>
public sealed class HeronLayoutAnalyzer : ILayoutAnalyzer
{
    private const int ModelInputSize = 640;

    private readonly InferenceSession _session;
    private readonly LayoutModelCapabilities _capabilities;
#if DEBUG
    private bool _loggedIoShapes;
#endif
    private byte[]? _chwBuffer;

    public LayoutModelCapabilities Capabilities => _capabilities;

    static HeronLayoutAnalyzer() => OnnxRuntimeInitializer.Ensure();

    /// <summary>
    /// Loads the Docling Heron ONNX model.
    /// </summary>
    /// <param name="modelPath">Path to <c>model.onnx</c> for Heron.</param>
    /// <param name="capabilities">
    /// Optional capability override. Defaults to
    /// <see cref="DoclingHeronRoles.Capabilities"/>. Custom-trained Heron
    /// variants with a different class table can pass their own.
    /// </param>
    public HeronLayoutAnalyzer(string modelPath, LayoutModelCapabilities? capabilities = null)
    {
        _capabilities = capabilities ?? DoclingHeronRoles.Capabilities;

        var opts = new SessionOptions();
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        opts.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
        _session = new InferenceSession(modelPath, opts);

        RailReaderLogging.Logger.Debug($"[Heron ONNX] Input names: {string.Join(", ", _session.InputNames)}");
        RailReaderLogging.Logger.Debug($"[Heron ONNX] Output names: {string.Join(", ", _session.OutputNames)}");
    }

    public PageAnalysis RunAnalysis(byte[] rgbBytes, int pxW, int pxH, double pageW, double pageH,
        IReadOnlyList<CharBox>? charBoxes = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Resize the caller's RGB pixmap to 640×640, distorting aspect ratio.
        var chwBytes = PreprocessImage(rgbBytes, pxW, pxH, ModelInputSize, ref _chwBuffer);
        ct.ThrowIfCancellationRequested();

        var images = new DenseTensor<byte>(chwBytes, new[] { 1, 3, ModelInputSize, ModelInputSize });
        // orig_target_sizes is (W, H) for this Heron ONNX export — the baked-in
        // post-processor multiplies normalised box coords by orig_target_sizes
        // as [W, H, W, H], so x ends up scaled by element 0 and y by element 1.
        // This is opposite to the PyTorch RTDetrImageProcessor convention (which
        // takes [H, W]); the ONNX export wrapper transposes it. Empirically
        // verified: passing [H, W] produces detections only in the top portion
        // of portrait pages (y compressed by W/H, x clamped at H).
        var origSizes = new DenseTensor<long>(new long[] { pxW, pxH }, new[] { 1, 2 });

        List<NamedOnnxValue> inputs =
        [
            NamedOnnxValue.CreateFromTensor("images", images),
            NamedOnnxValue.CreateFromTensor("orig_target_sizes", origSizes),
        ];

        using var results = _session.Run(inputs);

        float mapScaleX = pxW > 0 ? (float)(pageW / pxW) : 1f;
        float mapScaleY = pxH > 0 ? (float)(pageH / pxH) : 1f;

        var rawBlocks = ExtractDetections(results, pxW, pxH, mapScaleX, mapScaleY);
        if (rawBlocks is null)
            return new PageAnalysis { Blocks = [], PageWidth = pageW, PageHeight = pageH };

        // RT-DETR is NMS-free by design but the 300-query output sometimes
        // produces near-duplicate detections; reuse the existing NMS for safety.
        LayoutAnalyzer.Nms(rawBlocks, LayoutConstants.NmsIouThreshold);
        LayoutAnalyzer.SuppressNestedBlocks(rawBlocks);

        return new PageAnalysis
        {
            Blocks = rawBlocks,
            PageWidth = pageW,
            PageHeight = pageH,
        };
    }

    private List<LayoutBlock>? ExtractDetections(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
        int pxW, int pxH, float mapScaleX, float mapScaleY)
    {
        long[]? labels = null;
        float[]? boxes = null;
        float[]? scores = null;
        int numDetections = 0;

        foreach (var r in results)
        {
            switch (r.Name)
            {
                case "labels" when r.Value is Tensor<long> tl:
                    labels = tl.ToArray();
                    if (tl.Dimensions.Length >= 2) numDetections = tl.Dimensions[1];
                    break;
                case "boxes" when r.Value is Tensor<float> tb:
                    boxes = tb.ToArray();
                    break;
                case "scores" when r.Value is Tensor<float> ts:
                    scores = ts.ToArray();
                    break;
            }

#if DEBUG
            if (!_loggedIoShapes && r.Value is Tensor<long> lt)
                RailReaderLogging.Logger.Debug($"[Heron ONNX] Output '{r.Name}': dims=[{string.Join(",", lt.Dimensions.ToArray())}]");
            else if (!_loggedIoShapes && r.Value is Tensor<float> ft)
                RailReaderLogging.Logger.Debug($"[Heron ONNX] Output '{r.Name}': dims=[{string.Join(",", ft.Dimensions.ToArray())}]");
#endif
        }
#if DEBUG
        _loggedIoShapes = true;
#endif

        if (labels is null || boxes is null || scores is null || numDetections == 0)
            return null;

        var classTable = Capabilities.Classes;
        var rawBlocks = new List<LayoutBlock>(numDetections);

        for (int i = 0; i < numDetections; i++)
        {
            float confidence = scores[i];
            int classId = (int)labels[i];
            int o = i * 4;
            float xmin = boxes[o];
            float ymin = boxes[o + 1];
            float xmax = boxes[o + 2];
            float ymax = boxes[o + 3];

            if (LayoutAnalyzer.TryBuildBlock(classId, confidence, xmin, ymin, xmax, ymax,
                    pxW, pxH, mapScaleX, mapScaleY, classTable, order: 0, out var block))
                rawBlocks.Add(block);
        }

        return rawBlocks;
    }

    /// <summary>
    /// Bilinear resize of an HWC RGB byte buffer to <paramref name="target"/>×
    /// <paramref name="target"/>, packed into CHW byte planes (model expects
    /// uint8 NCHW). Reuses the caller's persistent buffer to avoid per-page
    /// allocation.
    /// </summary>
    internal static byte[] PreprocessImage(byte[] rgbBytes, int srcW, int srcH, int target, ref byte[]? buffer)
    {
        int pixelCount = target * target;
        int needed = 3 * pixelCount;
        if (buffer is null || buffer.Length != needed)
            buffer = new byte[needed];

        // Guard against degenerate inputs — fall back to black.
        if (srcW <= 0 || srcH <= 0)
        {
            Array.Clear(buffer);
            return buffer;
        }

        // Map output (x, y) → source (srcX, srcY) so the LAST output pixel
        // samples at exactly (srcW-1, srcH-1).
        float xScale = srcW > 1 ? (float)(srcW - 1) / (target - 1) : 0f;
        float yScale = srcH > 1 ? (float)(srcH - 1) / (target - 1) : 0f;

        for (int y = 0; y < target; y++)
        {
            float fy = y * yScale;
            int y0 = (int)fy;
            int y1 = Math.Min(y0 + 1, srcH - 1);
            float wy = fy - y0;

            for (int x = 0; x < target; x++)
            {
                float fx = x * xScale;
                int x0 = (int)fx;
                int x1 = Math.Min(x0 + 1, srcW - 1);
                float wx = fx - x0;

                int o00 = (y0 * srcW + x0) * 3;
                int o01 = (y0 * srcW + x1) * 3;
                int o10 = (y1 * srcW + x0) * 3;
                int o11 = (y1 * srcW + x1) * 3;

                int dstIdx = y * target + x;
                for (int c = 0; c < 3; c++)
                {
                    float v =
                        rgbBytes[o00 + c] * (1 - wx) * (1 - wy) +
                        rgbBytes[o01 + c] * wx * (1 - wy) +
                        rgbBytes[o10 + c] * (1 - wx) * wy +
                        rgbBytes[o11 + c] * wx * wy;
                    int b = (int)(v + 0.5f);
                    buffer[c * pixelCount + dstIdx] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                }
            }
        }

        return buffer;
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
