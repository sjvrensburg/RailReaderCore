using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RailReader.Core;
using RailReader.Core.Analysis;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

public sealed class LayoutAnalyzer : ILayoutAnalyzer
{

    private readonly InferenceSession _session;
    private readonly LayoutModelCapabilities _capabilities;
#if DEBUG
    private bool _loggedOutputShapes;
#endif
    private float[]? _chwBuffer;

    public LayoutModelCapabilities Capabilities => _capabilities;

    static LayoutAnalyzer() => OnnxRuntimeInitializer.Ensure();

    /// <summary>
    /// Loads PP-DocLayoutV3 (or any model conforming to the same input/output
    /// contract: <c>im_shape</c>/<c>image</c>/<c>scale_factor</c> inputs,
    /// <c>[N, 6+]</c> detection tensor output, optional reading-order column).
    /// </summary>
    /// <param name="modelPath">Path to the ONNX file.</param>
    /// <param name="capabilities">
    /// Optional capability override — input size, class table, role mapping,
    /// reading-order availability. Use this to load a custom model trained
    /// with a different label space. Defaults to <see cref="PPDocLayoutV3Roles.Capabilities"/>.
    /// </param>
    public LayoutAnalyzer(string modelPath, LayoutModelCapabilities? capabilities = null)
    {
        _capabilities = capabilities ?? PPDocLayoutV3Roles.Capabilities;

        var opts = new SessionOptions();
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        // Suppress noisy NCHWc Conv kernel warnings while preserving genuine errors
        opts.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
        _session = new InferenceSession(modelPath, opts);

        RailReaderLogging.Logger.Debug($"[ONNX] Input names: {string.Join(", ", _session.InputNames)}");
        RailReaderLogging.Logger.Debug($"[ONNX] Output names: {string.Join(", ", _session.OutputNames)}");
    }

    public PageAnalysis RunAnalysis(byte[] rgbBytes, int pxW, int pxH, double pageW, double pageH,
        IReadOnlyList<CharBox>? charBoxes = null, CancellationToken ct = default)
    {
        int target = Capabilities.InputSize;

        ct.ThrowIfCancellationRequested();

        // Letterbox: place undistorted image at (0,0) in target×target canvas.
        // FitPageToTarget already ensures max(pxW, pxH) == target, so no resizing
        // is needed — just padding the shorter dimension with black pixels.
        // scale_factor = [1, 1] since the image pixels map 1:1 to canvas pixels.
        var chwData = PreprocessImage(rgbBytes, pxW, pxH, target, ref _chwBuffer);

        ct.ThrowIfCancellationRequested();

        var imShape = new DenseTensor<float>(new float[] { target, target }, new[] { 1, 2 });
        var image = new DenseTensor<float>(chwData, new[] { 1, 3, target, target });
        var scaleFactor = new DenseTensor<float>(new float[] { 1.0f, 1.0f }, new[] { 1, 2 });

        List<NamedOnnxValue> inputs =
        [
            NamedOnnxValue.CreateFromTensor("im_shape", imShape),
            NamedOnnxValue.CreateFromTensor("image", image),
            NamedOnnxValue.CreateFromTensor("scale_factor", scaleFactor),
        ];

        using var results = _session.Run(inputs);

        float mapScaleX = (float)(pageW / pxW);
        float mapScaleY = (float)(pageH / pxH);

        var rawBlocks = ExtractDetections(results, pxW, pxH, mapScaleX, mapScaleY);
        if (rawBlocks is null)
            return new PageAnalysis { Blocks = [], PageWidth = pageW, PageHeight = pageH };

        Nms(rawBlocks, LayoutConstants.NmsIouThreshold);
        SuppressNestedBlocks(rawBlocks);

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
        // Single pass: log output shapes (first call only) and find detection tensor.
        // Copy tensor data immediately since the results collection owns the memory.
        float[]? detectionData = null;
        int detRows = 0, detCols = 0;
        foreach (var r in results)
        {
            if (r.Value is not Tensor<float> t) continue;

            bool isDetection = detectionData is null && t.Dimensions.Length == 2 && t.Dimensions[1] >= 6;

            if (isDetection)
            {
                detRows = t.Dimensions[0];
                detCols = t.Dimensions[1];
                detectionData = t.ToArray();
            }

#if DEBUG
            if (!_loggedOutputShapes)
            {
                RailReaderLogging.Logger.Debug($"[ONNX] Output '{r.Name}': dims=[{string.Join(",", t.Dimensions.ToArray())}]");
                // Reuse detectionData if already copied, otherwise take a snapshot for preview
                var flat = isDetection ? detectionData! : t.ToArray();
                var preview = string.Join(", ", flat.Take(Math.Min(14, flat.Length)).Select(v => v.ToString("F2")));
                RailReaderLogging.Logger.Debug($"[ONNX]   First values: [{preview}]");
            }
#endif
        }
#if DEBUG
        _loggedOutputShapes = true;
#endif

        if (detectionData is null)
            return null;

        bool hasReadingOrder = detCols >= 7;
        var classTable = Capabilities.Classes;

        var rawBlocks = new List<LayoutBlock>();
        for (int i = 0; i < detRows; i++)
        {
            int off = i * detCols;
            int classId = (int)detectionData[off];
            float confidence = detectionData[off + 1];
            float xmin = detectionData[off + 2];
            float ymin = detectionData[off + 3];
            float xmax = detectionData[off + 4];
            float ymax = detectionData[off + 5];
            int modelOrder = hasReadingOrder ? (int)detectionData[off + 6] : 0;

            if (TryBuildBlock(classId, confidence, xmin, ymin, xmax, ymax,
                    pxW, pxH, mapScaleX, mapScaleY, classTable, modelOrder, out var block))
                rawBlocks.Add(block);
        }

        return rawBlocks;
    }

    private static float[] PreprocessImage(byte[] rgbBytes, int origW, int origH, int target, ref float[]? buffer)
    {
        // PP-DocLayoutV3 uses mean=[0,0,0] std=[1,1,1] (no ImageNet normalization)
        // Letterbox: place image at (0,0) in target×target canvas with black padding.
        // FitPageToTarget ensures max(origW, origH) == target, so pixels copy 1:1.
        int pixelCount = target * target;
        int needed = 3 * pixelCount;
        if (buffer is null || buffer.Length != needed)
            buffer = new float[needed];
        else
            Array.Clear(buffer);
        var chwData = buffer;

        int copyW = Math.Min(origW, target);
        int copyH = Math.Min(origH, target);

        for (int y = 0; y < copyH; y++)
        {
            int srcRow = y * origW;
            for (int x = 0; x < copyW; x++)
            {
                int srcIdx = (srcRow + x) * 3;
                int dstIdx = y * target + x;
                chwData[dstIdx] = rgbBytes[srcIdx] / 255.0f;                     // R
                chwData[pixelCount + dstIdx] = rgbBytes[srcIdx + 1] / 255.0f;    // G
                chwData[2 * pixelCount + dstIdx] = rgbBytes[srcIdx + 2] / 255.0f; // B
            }
        }
        return chwData;
    }

    /// <summary>
    /// Shared post-detection construction: validates confidence and class id,
    /// clamps the box to the pixmap, rejects detections smaller than
    /// <see cref="LayoutConstants.MinDetectionSizePx"/>, and scales the
    /// pixel-space box into page-space via <paramref name="mapScaleX"/>/
    /// <paramref name="mapScaleY"/>.
    /// </summary>
    internal static bool TryBuildBlock(
        int classId, float confidence,
        float xmin, float ymin, float xmax, float ymax,
        int pxW, int pxH, float mapScaleX, float mapScaleY,
        IReadOnlyList<LayoutClassDescriptor> classTable, int order,
        out LayoutBlock block)
    {
        block = default!;
        if (confidence < LayoutConstants.ConfidenceThreshold) return false;
        if (classId < 0 || classId >= classTable.Count) return false;

        float x = Math.Max(xmin, 0);
        float y = Math.Max(ymin, 0);
        float w = Math.Min(xmax, pxW) - x;
        float h = Math.Min(ymax, pxH) - y;
        if (w < LayoutConstants.MinDetectionSizePx || h < LayoutConstants.MinDetectionSizePx) return false;

        block = new LayoutBlock
        {
            BBox = new BBox(x * mapScaleX, y * mapScaleY, w * mapScaleX, h * mapScaleY),
            Role = classTable[classId].Role,
            ClassId = classId,
            Confidence = confidence,
            Order = order,
        };
        return true;
    }

    internal static float Iou(BBox a, BBox b)
    {
        float x1 = Math.Max(a.X, b.X);
        float y1 = Math.Max(a.Y, b.Y);
        float x2 = Math.Min(a.X + a.W, b.X + b.W);
        float y2 = Math.Min(a.Y + a.H, b.Y + b.H);

        float inter = Math.Max(x2 - x1, 0) * Math.Max(y2 - y1, 0);
        float areaA = a.W * a.H;
        float areaB = b.W * b.H;
        float union = areaA + areaB - inter;
        return union <= 0 ? 0 : inter / union;
    }

    internal static void Nms(List<LayoutBlock> blocks, float threshold)
    {
        blocks.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
        var keep = new bool[blocks.Count];
        Array.Fill(keep, true);

        for (int i = 0; i < blocks.Count; i++)
        {
            if (!keep[i]) continue;
            for (int j = i + 1; j < blocks.Count; j++)
            {
                if (!keep[j]) continue;
                if (Iou(blocks[i].BBox, blocks[j].BBox) > threshold)
                    keep[j] = false;
            }
        }

        RemoveFlagged(blocks, keep);
    }

    /// <summary>
    /// Removes blocks that are fully contained within a larger block.
    /// Handles cases like inline_formula detected inside a text block,
    /// which would otherwise create redundant navigation targets.
    /// </summary>
    internal static void SuppressNestedBlocks(List<LayoutBlock> blocks)
    {
        const float margin = 2f; // tolerance in page points
        var keep = new bool[blocks.Count];
        Array.Fill(keep, true);

        for (int i = 0; i < blocks.Count; i++)
        {
            if (!keep[i]) continue;
            for (int j = 0; j < blocks.Count; j++)
            {
                if (i == j || !keep[j]) continue;

                var outer = blocks[i].BBox;
                var inner = blocks[j].BBox;

                bool contained = inner.X >= outer.X - margin &&
                    inner.Y >= outer.Y - margin &&
                    inner.X + inner.W <= outer.X + outer.W + margin &&
                    inner.Y + inner.H <= outer.Y + outer.H + margin;

                if (contained && inner.W * inner.H < outer.W * outer.H)
                    keep[j] = false;
            }
        }

        RemoveFlagged(blocks, keep);
    }

    private static void RemoveFlagged(List<LayoutBlock> blocks, bool[] keep)
    {
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            if (!keep[i]) blocks.RemoveAt(i);
        }
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
