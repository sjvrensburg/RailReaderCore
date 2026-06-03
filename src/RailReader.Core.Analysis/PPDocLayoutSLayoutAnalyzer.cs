using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RailReader.Core;
using RailReader.Core.Analysis;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// PP-DocLayout-S layout analyzer (PicoDet/GFL, 23-class, 480×480 model input).
/// Implements <see cref="ILayoutAnalyzer"/> as a sibling of the
/// <see cref="LayoutAnalyzer"/> (PP-DocLayoutV3) and
/// <see cref="HeronLayoutAnalyzer"/> (Docling Heron) analyzers; consumers choose
/// which to instantiate. PP-DocLayout-S is the lightweight option (~4.7 MB ONNX
/// vs V3's ~50 MB / Heron's ~164 MB) and is the intended detector for future
/// web (WASM/ORT-Web) and mobile builds.
///
/// <para>
/// The ONNX export at <c>PaddlePaddle/PP-DocLayout-S</c> (PicoDet/GFL family)
/// exposes two inputs — <c>image</c> (float32 NCHW 1×3×480×480) and
/// <c>scale_factor</c> (float32 <c>[H_resize/H_orig, W_resize/W_orig]</c>) —
/// and two outputs:
/// <list type="bullet">
///   <item><description><c>multiclass_nms3_0.tmp_0</c> — <c>[M, 6]</c> detection rows
///   <c>[class_id, confidence, x1, y1, x2, y2]</c>, with NMS already baked in
///   at score_threshold=0.3.</description></item>
///   <item><description><c>multiclass_nms3_0.tmp_2</c> — scalar <c>num_dets</c>
///   giving the number of valid rows (the first output is padded out to its
///   maximum query count).</description></item>
/// </list>
/// Because <c>scale_factor</c> is passed as the resize-down ratio relative to
/// the caller's pixmap, the detection head divides predicted boxes by it
/// internally — output boxes come back already in the caller's
/// <c>pxW × pxH</c> coordinate frame. (Mirrors what the working Python
/// reference at <c>RailDLA/src/raildla/detector.py</c> does.)
/// </para>
///
/// <para>
/// Preprocessing per the model's <c>inference.yml</c>: bilinear resize to exactly
/// 480×480 <em>without</em> aspect-ratio preservation (<c>keep_ratio: false</c>),
/// then ImageNet normalisation (<c>mean=[0.485, 0.456, 0.406]</c>,
/// <c>std=[0.229, 0.224, 0.225]</c>), then HWC→CHW.
/// </para>
///
/// <para>
/// The caller is expected to rasterise the page at
/// <see cref="LayoutModelCapabilities.InputSize"/> = 1920 on the longest edge
/// (per <see cref="PPDocLayoutSRoles.InputSize"/>), <em>not</em> 480. Going
/// straight to 480 loses bibliography rows and small text on academic content;
/// downsizing from 1920 inside the analyzer preserves recall. This is the
/// load-bearing lesson from the Python <c>raildla</c> prototype.
/// </para>
///
/// <para>
/// Reading order is not produced; the pipeline pairs PP-S with an
/// <see cref="IReadingOrderResolver"/> (default <see cref="XYCutPlusPlusResolver"/>)
/// downstream, just like Heron.
/// </para>
/// </summary>
public sealed class PPDocLayoutSLayoutAnalyzer : ILayoutAnalyzer
{
    private const int ModelInputSize = PPDocLayoutSRoles.ModelInputSize;

    private static readonly float[] ImageNetMean = NormalizationConstants.ImageNetMean;
    private static readonly float[] ImageNetStd  = NormalizationConstants.ImageNetStd;

    private readonly InferenceSession _session;
    private readonly LayoutModelCapabilities _capabilities;
#if DEBUG
    private bool _loggedIoShapes;
#endif
    private float[]? _chwBuffer;

    public LayoutModelCapabilities Capabilities => _capabilities;

    /// <summary>
    /// Optional hook to customise the ONNX <see cref="SessionOptions"/> before
    /// the inference session is created — e.g. capping <c>IntraOpNumThreads</c>
    /// for a low-core target or registering a hardware execution provider.
    /// Invoked once per analyzer construction, after the defaults are applied.
    /// Null (the default) leaves the session at its built-in configuration.
    /// </summary>
    public static Action<SessionOptions>? ConfigureSession;

    static PPDocLayoutSLayoutAnalyzer() => OnnxRuntimeInitializer.Ensure();

    /// <summary>
    /// Loads the PP-DocLayout-S ONNX model.
    /// </summary>
    /// <param name="modelPath">Path to <c>pp_doclayout_s.onnx</c>.</param>
    /// <param name="capabilities">
    /// Optional capability override. Defaults to
    /// <see cref="PPDocLayoutSRoles.Capabilities"/>. Custom-trained PP-S
    /// variants with a different class table can pass their own.
    /// </param>
    public PPDocLayoutSLayoutAnalyzer(string modelPath, LayoutModelCapabilities? capabilities = null)
    {
        _capabilities = capabilities ?? PPDocLayoutSRoles.Capabilities;

        var opts = AnalyzerSessionOptions.Create(ConfigureSession);
        _session = new InferenceSession(modelPath, opts);

        RailReaderLogging.Logger.Debug($"[PP-S ONNX] Input names: {string.Join(", ", _session.InputNames)}");
        RailReaderLogging.Logger.Debug($"[PP-S ONNX] Output names: {string.Join(", ", _session.OutputNames)}");
    }

    public PageAnalysis RunAnalysis(byte[] rgbBytes, int pxW, int pxH, double pageW, double pageH,
        IReadOnlyList<CharBox>? charBoxes = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Resize the caller's RGB pixmap to 480×480 with bilinear + ImageNet norm.
        var chwData = PreprocessImage(rgbBytes, pxW, pxH, ModelInputSize, ref _chwBuffer);
        ct.ThrowIfCancellationRequested();

        var image = new DenseTensor<float>(chwData, new[] { 1, 3, ModelInputSize, ModelInputSize });

        // scale_factor = [ModelInputSize / pxH, ModelInputSize / pxW] — the
        // PaddleDetection GFL head divides predicted boxes by this internally,
        // so outputs come back in the caller's pxW × pxH coordinate frame.
        // Guard against degenerate pixmaps (1.0 means "no rescale" — boxes
        // would then be in model-input coords, but pxW/pxH<=0 won't happen
        // from the worker pipeline).
        float sH = pxH > 0 ? (float)ModelInputSize / pxH : 1f;
        float sW = pxW > 0 ? (float)ModelInputSize / pxW : 1f;
        var scaleFactor = new DenseTensor<float>(new[] { sH, sW }, new[] { 1, 2 });

        List<NamedOnnxValue> inputs =
        [
            NamedOnnxValue.CreateFromTensor("image", image),
            NamedOnnxValue.CreateFromTensor("scale_factor", scaleFactor),
        ];

        using var results = _session.Run(inputs);

        float mapScaleX = pxW > 0 ? (float)(pageW / pxW) : 1f;
        float mapScaleY = pxH > 0 ? (float)(pageH / pxH) : 1f;

        var rawBlocks = ExtractDetections(results, pxW, pxH, mapScaleX, mapScaleY);
        if (rawBlocks is null)
            return new PageAnalysis { Blocks = [], PageWidth = pageW, PageHeight = pageH };

        // NMS is baked into the ONNX graph at score_threshold=0.3, but reuse
        // the shared NMS for safety against near-duplicate detections (and to
        // honour LayoutConstants.NmsIouThreshold uniformly across analyzers).
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
        // Two outputs: a [M, 6] float detection tensor and a [1] / scalar int
        // count of valid rows (the detection tensor is padded out to its
        // top-K capacity, so respect num_dets).
        float[]? detectionData = null;
        int detRows = 0, detCols = 0;
        int numDets = -1;

        foreach (var r in results)
        {
            if (r.Value is Tensor<float> tf && tf.Dimensions.Length == 2 && tf.Dimensions[1] == 6)
            {
                detRows = tf.Dimensions[0];
                detCols = tf.Dimensions[1];
                detectionData = tf.ToArray();
            }
            else if (r.Value is Tensor<int> ti && ti.Length >= 1)
            {
                numDets = ti.GetValue(0);
            }
            else if (r.Value is Tensor<long> tl && tl.Length >= 1)
            {
                numDets = (int)tl.GetValue(0);
            }

#if DEBUG
            if (!_loggedIoShapes)
            {
                if (r.Value is Tensor<float> dbgF)
                    RailReaderLogging.Logger.Debug($"[PP-S ONNX] Output '{r.Name}': dims=[{string.Join(",", dbgF.Dimensions.ToArray())}]");
                else if (r.Value is Tensor<int> dbgI)
                    RailReaderLogging.Logger.Debug($"[PP-S ONNX] Output '{r.Name}': dims=[{string.Join(",", dbgI.Dimensions.ToArray())}] value={dbgI.GetValue(0)}");
                else if (r.Value is Tensor<long> dbgL)
                    RailReaderLogging.Logger.Debug($"[PP-S ONNX] Output '{r.Name}': dims=[{string.Join(",", dbgL.Dimensions.ToArray())}] value={dbgL.GetValue(0)}");
            }
#endif
        }
#if DEBUG
        _loggedIoShapes = true;
#endif

        if (detectionData is null)
            return null;

        // num_dets caps how many rows are valid; fall back to detRows if it
        // wasn't reported.
        int validRows = numDets >= 0 ? Math.Min(numDets, detRows) : detRows;
        var classTable = Capabilities.Classes;

        var rawBlocks = new List<LayoutBlock>(validRows);
        for (int i = 0; i < validRows; i++)
        {
            int off = i * detCols;
            int classId = (int)detectionData[off];
            float confidence = detectionData[off + 1];
            float xmin = detectionData[off + 2];
            float ymin = detectionData[off + 3];
            float xmax = detectionData[off + 4];
            float ymax = detectionData[off + 5];

            if (LayoutAnalyzer.TryBuildBlock(classId, confidence, xmin, ymin, xmax, ymax,
                    pxW, pxH, mapScaleX, mapScaleY, classTable, order: 0, out var block))
                rawBlocks.Add(block);
        }

        return rawBlocks;
    }

    /// <summary>
    /// Bilinear resize of an HWC RGB byte buffer to <paramref name="target"/>×
    /// <paramref name="target"/>, ImageNet-normalised, packed into CHW float
    /// planes (model expects float32 NCHW). Reuses the caller's persistent
    /// buffer to avoid per-page allocation.
    /// </summary>
    internal static float[] PreprocessImage(byte[] rgbBytes, int srcW, int srcH, int target, ref float[]? buffer)
    {
        int pixelCount = target * target;
        int needed = 3 * pixelCount;
        if (buffer is null || buffer.Length != needed)
            buffer = new float[needed];

        // Guard against degenerate inputs — fall back to (-mean/std) which is
        // what a black image would normalise to. Keeps inference well-defined
        // even on empty/zero-size pixmaps (shouldn't happen in practice).
        if (srcW <= 0 || srcH <= 0)
        {
            for (int c = 0; c < 3; c++)
            {
                float v = -ImageNetMean[c] / ImageNetStd[c];
                int planeOff = c * pixelCount;
                for (int i = 0; i < pixelCount; i++)
                    buffer[planeOff + i] = v;
            }
            return buffer;
        }

        // Map output (x, y) → source (srcX, srcY) so the LAST output pixel
        // samples at exactly (srcW-1, srcH-1) — matches PIL.Image.BILINEAR
        // which is what the Python reference uses.
        float xScale = srcW > 1 ? (float)(srcW - 1) / (target - 1) : 0f;
        float yScale = srcH > 1 ? (float)(srcH - 1) / (target - 1) : 0f;

        float invStdR = 1f / ImageNetStd[0];
        float invStdG = 1f / ImageNetStd[1];
        float invStdB = 1f / ImageNetStd[2];

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
                // R
                float r =
                    rgbBytes[o00]     * (1 - wx) * (1 - wy) +
                    rgbBytes[o01]     * wx * (1 - wy) +
                    rgbBytes[o10]     * (1 - wx) * wy +
                    rgbBytes[o11]     * wx * wy;
                buffer[dstIdx] = (r / 255f - ImageNetMean[0]) * invStdR;
                // G
                float g =
                    rgbBytes[o00 + 1] * (1 - wx) * (1 - wy) +
                    rgbBytes[o01 + 1] * wx * (1 - wy) +
                    rgbBytes[o10 + 1] * (1 - wx) * wy +
                    rgbBytes[o11 + 1] * wx * wy;
                buffer[pixelCount + dstIdx] = (g / 255f - ImageNetMean[1]) * invStdG;
                // B
                float b =
                    rgbBytes[o00 + 2] * (1 - wx) * (1 - wy) +
                    rgbBytes[o01 + 2] * wx * (1 - wy) +
                    rgbBytes[o10 + 2] * (1 - wx) * wy +
                    rgbBytes[o11 + 2] * wx * wy;
                buffer[2 * pixelCount + dstIdx] = (b / 255f - ImageNetMean[2]) * invStdB;
            }
        }

        return buffer;
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
