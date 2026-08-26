using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RailReader.Core;
using RailReader.Core.Analysis.WebGpu;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

// Root-causes issue #109's GPU-vs-CPU layout-detection gap by comparing
// intermediate tensor values at several checkpoints through the Heron
// FP16 graph, running the SAME debug-instrumented ONNX file (extra outputs
// added at backbone/encoder/decoder checkpoints — see
// tools/webgpu-diag/make_debug_model.py) on CPU EP vs the WebGPU EP. Both
// runs consume byte-identical preprocessed input, so any divergence is
// attributable purely to the execution provider's kernel implementations.
//
// Usage: WebGpuDiag <pdf> <debugModelPath> [page=0]
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: WebGpuDiag <pdf> <debugModelPath> [page]");
    return 1;
}
string pdfPath = args[0], modelPath = args[1];
int page = args.Length > 2 ? int.Parse(args[2]) : 0;
const int ModelInputSize = 640;

RailReaderLogging.Logger = NullLogger.Instance;

if (!WebGpuAccelerator.IsAvailable)
{
    Console.Error.WriteLine("No WebGPU device found. Aborting.");
    return 1;
}
Console.Error.WriteLine($"WebGPU device: {WebGpuAccelerator.DeviceDescription}");

var factory = new SkiaPdfServiceFactory();
var svc = factory.CreatePdfService(pdfPath);
var (rgb, pxW, pxH) = svc.RenderPagePixmap(page, ModelInputSize);

// Simple bilinear resize to 640x640 CHW uint8 — doesn't need to be
// byte-identical to production's BilinearResampler; both EPs consume this
// exact same tensor, so only relative CPU-vs-GPU divergence matters here.
byte[] chw = ResizeToChw(rgb, pxW, pxH, ModelInputSize);
var images = new DenseTensor<byte>(chw, new[] { 1, 3, ModelInputSize, ModelInputSize });
var origSizes = new DenseTensor<long>(new long[] { pxW, pxH }, new[] { 1, 2 });
var inputs = new List<NamedOnnxValue>
{
    NamedOnnxValue.CreateFromTensor("images", images),
    NamedOnnxValue.CreateFromTensor("orig_target_sizes", origSizes),
};

using var cpuSession = new InferenceSession(modelPath);
Console.Error.WriteLine("CPU session loaded.");

WebGpuAccelerator.TryEnable(LayoutModelArchitecture.Heron);
var gpuOpts = new SessionOptions();
HeronLayoutAnalyzer.ConfigureSession?.Invoke(gpuOpts);
WebGpuAccelerator.Disable(LayoutModelArchitecture.Heron);
using var gpuSession = new InferenceSession(modelPath, gpuOpts);
Console.Error.WriteLine("GPU session loaded.");

using var cpuResults = cpuSession.Run(inputs);
using var gpuResults = gpuSession.Run(inputs);

var cpuByName = cpuResults.ToDictionary(r => r.Name, r => r);
var gpuByName = gpuResults.ToDictionary(r => r.Name, r => r);

Console.WriteLine($"{"output",-90} {"n",8} {"meanAbs",10} {"maxAbs",10} {"cosSim",8}");
foreach (var name in cpuByName.Keys)
{
    if (!gpuByName.TryGetValue(name, out var g)) { Console.WriteLine($"{name,-90} (missing on GPU side)"); continue; }
    var c = cpuByName[name];
    var (cf, ok1) = ToFloatArray(c);
    var (gf, ok2) = ToFloatArray(g);
    if (!ok1 || !ok2 || cf.Length != gf.Length)
    {
        Console.WriteLine($"{name,-90} (unreadable: cpu={c.Value?.GetType().FullName} gpu={g.Value?.GetType().FullName})");
        continue;
    }
    double sumAbs = 0, maxAbs = 0, dot = 0, cn = 0, gn = 0;
    for (int i = 0; i < cf.Length; i++)
    {
        double d = Math.Abs(cf[i] - gf[i]);
        sumAbs += d;
        if (d > maxAbs) maxAbs = d;
        dot += (double)cf[i] * gf[i];
        cn += (double)cf[i] * cf[i];
        gn += (double)gf[i] * gf[i];
    }
    double meanAbs = sumAbs / cf.Length;
    double cos = (cn > 0 && gn > 0) ? dot / (Math.Sqrt(cn) * Math.Sqrt(gn)) : double.NaN;
    Console.WriteLine($"{name,-90} {cf.Length,8} {meanAbs,10:E3} {maxAbs,10:E3} {cos,8:F5}");
}

return 0;

static byte[] ResizeToChw(byte[] rgbHwc, int srcW, int srcH, int target)
{
    var buf = new byte[3 * target * target];
    int plane = target * target;
    for (int y = 0; y < target; y++)
    {
        float sy = (y + 0.5f) * srcH / target - 0.5f;
        int y0 = Math.Clamp((int)MathF.Floor(sy), 0, srcH - 1);
        int y1 = Math.Clamp(y0 + 1, 0, srcH - 1);
        float fy = sy - y0;
        for (int x = 0; x < target; x++)
        {
            float sx = (x + 0.5f) * srcW / target - 0.5f;
            int x0 = Math.Clamp((int)MathF.Floor(sx), 0, srcW - 1);
            int x1 = Math.Clamp(x0 + 1, 0, srcW - 1);
            float fx = sx - x0;
            int dst = y * target + x;
            for (int ch = 0; ch < 3; ch++)
            {
                float p00 = rgbHwc[(y0 * srcW + x0) * 3 + ch];
                float p01 = rgbHwc[(y0 * srcW + x1) * 3 + ch];
                float p10 = rgbHwc[(y1 * srcW + x0) * 3 + ch];
                float p11 = rgbHwc[(y1 * srcW + x1) * 3 + ch];
                float top = p00 + (p01 - p00) * fx;
                float bot = p10 + (p11 - p10) * fx;
                float v = top + (bot - top) * fy;
                buf[ch * plane + dst] = (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
            }
        }
    }
    return buf;
}

static (float[] data, bool ok) ToFloatArray(DisposableNamedOnnxValue v)
{
    try
    {
        if (v.Value is Tensor<float> tf) return (tf.ToArray(), true);
        if (v.Value is Tensor<Microsoft.ML.OnnxRuntime.Float16> tfp16)
            return (tfp16.ToArray().Select(h => h.ToFloat()).ToArray(), true);
        if (v.Value is Tensor<long> tl) return (tl.ToArray().Select(x => (float)x).ToArray(), true);
        if (v.Value is Tensor<byte> tb) return (tb.ToArray().Select(x => (float)x).ToArray(), true);
        return ([], false);
    }
    catch
    {
        return ([], false);
    }
}
