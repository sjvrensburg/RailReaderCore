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
        if (string.IsNullOrEmpty(filename)) return null;

        foreach (var root in ProbeRoots())
        {
            if (string.IsNullOrEmpty(root)) continue;
            var candidate = Path.Combine(root, "models", filename);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    /// <summary>
    /// How far to climb when probing a directory's ancestors. A .NET build output sits three
    /// levels below its project (<c>bin/&lt;config&gt;/&lt;tfm&gt;</c>) and the project one or two
    /// below the root of the source tree, so anything less than five cannot see a
    /// <c>models/</c> directory at that root. Climbing costs one <c>File.Exists</c> per level, once
    /// per lookup, and can never pick a wrong directory — a root only matches when the file is
    /// present under it.
    /// </summary>
    private const int AncestorProbeDepth = 6;

    /// <summary>A directory and its ancestors, up to <see cref="AncestorProbeDepth"/> levels.</summary>
    private static IEnumerable<string> Ancestors(string? start, bool includeSelf = true)
    {
        var dir = string.IsNullOrEmpty(start) ? null : Path.GetFullPath(start);
        if (!includeSelf) dir = dir is null ? null : Path.GetDirectoryName(dir);

        for (int i = 0; i <= AncestorProbeDepth && !string.IsNullOrEmpty(dir); i++)
        {
            yield return dir;
            dir = Path.GetDirectoryName(dir);
        }
    }

    /// <summary>
    /// The directories searched for a <c>models/</c> subdirectory, in precedence order. Internal
    /// so a test can pin the reachability the layout depends on rather than only the lookups that
    /// happen to work on the machine running it. Kept deliberately parallel to
    /// <c>OcrModelLocator.ProbeRoots</c>, which solves the same problem for OCR packs; the two
    /// cannot share code because their packages do not reference each other, and the shared piece
    /// (pure path arithmetic that probes the filesystem) does not belong in <c>Core</c>.
    /// </summary>
    internal static IEnumerable<string?> ProbeRoots()
    {
        // Beside the app's binaries.
        yield return AppContext.BaseDirectory;

        // AppImage mount point.
        yield return Environment.GetEnvironmentVariable("APPDIR");

        // Same base directory as AppConfig.ConfigDir so the model is found wherever the app
        // stored it (%APPDATA% on Windows, ~/.config on Linux).
        yield return AppConfig.ConfigDir;

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "railreader2");

        // Working directory and its ancestors, for a `dotnet run` from a source tree.
        foreach (var root in Ancestors(Directory.GetCurrentDirectory()))
            yield return root;

        // The app's own directory and ITS ancestors. Not the same search as the one above: the
        // working directory is whatever the launcher chose, and for a binary started from its own
        // output folder — a test host, an IDE run, `dotnet bin/<config>/<tfm>/App.dll` — both
        // walks begin deep inside `bin/<config>/<tfm>` and only reach the source tree by
        // climbing. The OCR locator had exactly this gap, where it silently hid every
        // user-downloaded model pack from anything launched that way.
        foreach (var root in Ancestors(AppContext.BaseDirectory, includeSelf: false))
            yield return root;
    }
}
