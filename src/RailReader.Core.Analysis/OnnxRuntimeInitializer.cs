using System.Runtime.InteropServices;

namespace RailReader.Core.Analysis;

/// <summary>
/// Pre-loads the OnnxRuntime native library before OnnxRuntime's own static
/// initializer runs. <see cref="NativeLibrary.TryLoad(string)"/> caches the
/// handle so the subsequent P/Invoke inside OnnxRuntime finds it already
/// loaded — no resolver conflict.
///
/// <para>
/// (<see cref="NativeLibrary.SetDllImportResolver"/> can only be called once
/// per assembly and OnnxRuntime registers its own, so we must not use it.)
/// </para>
///
/// <para>
/// All three analyzers in this assembly trigger this from their static
/// constructor via <see cref="Ensure"/>. Windows is a no-op — the runtime's
/// own DLL search already finds <c>onnxruntime.dll</c> next to the assembly.
/// </para>
/// </summary>
internal static class OnnxRuntimeInitializer
{
    private static int _initialized;

    /// <summary>
    /// Idempotent. Safe to call from multiple analyzer static ctors — the
    /// underlying <see cref="NativeLibrary"/> handle cache makes repeated
    /// loads cheap, and the interlocked guard skips the filesystem probes
    /// after the first successful run.
    /// </summary>
    public static void Ensure()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        string ext, fallbackRid;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            ext = ".dylib";
            fallbackRid = "osx-arm64";
        }
        else
        {
            ext = ".so";
            fallbackRid = "linux-x64";
        }

        string libName = $"libonnxruntime{ext}";
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory,
                "runtimes", RuntimeInformation.RuntimeIdentifier, "native", libName),
            Path.Combine(AppContext.BaseDirectory,
                "runtimes", fallbackRid, "native", libName),
            Path.Combine(AppContext.BaseDirectory, libName),
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out _))
                return;
        }
    }
}
