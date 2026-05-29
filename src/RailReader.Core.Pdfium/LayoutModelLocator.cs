namespace RailReader.Core.Services;

/// <summary>
/// Locates a layout-detection ONNX model file on disk by probing well-known
/// install locations. Returns null if not found anywhere; the caller should
/// fall back to layout-less behaviour.
///
/// <para>
/// <b>0.9.0 behaviour change:</b> the parameterless <see cref="FindModelPath()"/>
/// now probes for <see cref="LayoutModelRegistry.Default"/> (the backbone-INT8
/// Docling Heron model, <c>docling-layout-heron-int8.onnx</c>) — previously it
/// probed for <c>PP-DocLayoutV3.onnx</c>. Consumers that fed the result into the
/// V3 <c>LayoutAnalyzer</c> must switch to <c>LayoutAnalyzerFactory</c> (or pass
/// the V3 descriptor explicitly), because the returned file is now a Heron model
/// and only <c>HeronLayoutAnalyzer</c> can run it.
/// </para>
/// </summary>
public static class LayoutModelLocator
{
    /// <summary>Probes for the default model (<see cref="LayoutModelRegistry.Default"/>).</summary>
    public static string? FindModelPath() => FindModelPath(LayoutModelRegistry.Default.FileName);

    /// <summary>Probes for the file named by <paramref name="descriptor"/>.</summary>
    public static string? FindModelPath(LayoutModelDescriptor descriptor)
        => FindModelPath(descriptor.FileName);

    /// <summary>
    /// Probes the well-known install locations for a model file named
    /// <paramref name="filename"/> (e.g. <c>"docling-layout-heron-int8.onnx"</c>).
    /// Returns the absolute path of the first hit, or null.
    /// </summary>
    public static string? FindModelPath(string filename)
    {
        var candidates = new List<string?>
        {
            Path.Combine(AppContext.BaseDirectory, "models", filename),
            Environment.GetEnvironmentVariable("APPDIR") is { } appDir
                ? Path.Combine(appDir, "models", filename) : null,
            // Same base directory as AppConfig.ConfigDir so the model is found
            // wherever the app stored it (%APPDATA% on Windows, ~/.config on
            // Linux, ~/Library/Application Support on macOS).
            Path.Combine(AppConfig.ConfigDir, "models", filename),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "railreader2", "models", filename),
            Path.Combine("models", filename),
        };

        // Walk up from CWD
        for (int i = 1; i <= 3; i++)
        {
            var walkUp = string.Concat(Enumerable.Repeat("../", i));
            candidates.Add(Path.Combine(walkUp, "models", filename));
        }

        foreach (var path in candidates)
        {
            if (path is not null && File.Exists(path))
                return Path.GetFullPath(path);
        }
        return null;
    }
}
