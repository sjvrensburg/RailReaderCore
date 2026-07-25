using RapidOcrNet;

namespace RailReader.Core.Ocr.RapidOcr;

/// <summary>
/// Resolves a <see cref="RapidOcrModelSet"/>'s model files to absolute paths by probing
/// well-known install locations, mirroring <c>LayoutModelLocator</c>.
///
/// <para>
/// RapidOcrNet's presets carry paths relative to the <i>working directory</i>
/// (<c>models/v5/…</c>). That holds for a console app launched from its output folder and
/// breaks for a desktop app launched from a menu, a bundle, or anywhere else — so every set
/// is rebased onto the directories a model could actually have been installed to before it
/// reaches the engine.
/// </para>
/// <para>
/// The <c>RapidOcrNet</c> NuGet package ships the PP-OCRv5 Latin models and a build target
/// that copies them next to the consuming app's binaries, so
/// <see cref="LocateDefault"/> normally succeeds with no extra download. The PP-OCRv6 sets
/// are opt-in: fetch the files yourself and drop them in any probed location.
/// </para>
/// </summary>
public static class OcrModelLocator
{
    /// <summary>
    /// Resolves the bundled PP-OCRv5 Latin set. Returns null when any of its four files is
    /// missing, in which case the caller should run without OCR.
    /// </summary>
    public static RapidOcrModelSet? LocateDefault() => Locate(RapidOcrModelSet.PPOCRv5Latin);

    /// <summary>
    /// Returns a copy of <paramref name="models"/> with every path resolved to an existing
    /// file, or null if any of them could not be found. Paths that are already absolute and
    /// present are kept as-is, so a caller can pin specific files and still go through here.
    /// </summary>
    public static RapidOcrModelSet? Locate(RapidOcrModelSet models)
    {
        ArgumentNullException.ThrowIfNull(models);

        string? det = Resolve(models.DetModelPath);
        string? cls = Resolve(models.ClsModelPath);
        string? rec = Resolve(models.RecModelPath);
        string? keys = Resolve(models.KeysPath);
        if (det is null || cls is null || rec is null || keys is null) return null;

        return models with
        {
            DetModelPath = det,
            ClsModelPath = cls,
            RecModelPath = rec,
            KeysPath = keys,
        };
    }

    /// <summary>
    /// Probes for one model file. <paramref name="path"/> may be absolute (used directly if
    /// it exists) or relative to any of the probed roots.
    /// </summary>
    public static string? Resolve(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Path.IsPathRooted(path)) return File.Exists(path) ? path : null;

        foreach (var root in ProbeRoots())
        {
            if (root is null) continue;
            var candidate = Path.Combine(root, path);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static IEnumerable<string?> ProbeRoots()
    {
        // Beside the app's binaries: where the RapidOcrNet build target puts them.
        yield return AppContext.BaseDirectory;

        // Inside the RapidOcrNet package itself. Its model-copying target ships in `build/`
        // rather than `buildTransitive/`, so it only runs for projects that reference
        // RapidOcrNet *directly* — an app that references this package instead gets the
        // assembly but no models beside its binaries. Probing the package layout
        // (`<pkg>/lib/<tfm>/RapidOcrNet.dll` → `<pkg>/models/v5/…`) covers that case for
        // anything running against the NuGet cache, which is every non-published build.
        foreach (var root in RapidOcrNetPackageRoots())
            yield return root;
        // AppImage mount point.
        yield return Environment.GetEnvironmentVariable("APPDIR");
        // User-installed models, alongside where the layout models live.
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "railreader2");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } appData
            ? Path.Combine(appData, "railreader2")
            : null;

        // Working directory and a few levels up, for a `dotnet run` from a source tree.
        var cwd = Directory.GetCurrentDirectory();
        yield return cwd;
        for (int i = 0; i < 3 && cwd is not null; i++)
        {
            cwd = Path.GetDirectoryName(cwd);
            yield return cwd;
        }
    }

    /// <summary>
    /// Candidate roots derived from where the RapidOcrNet assembly was loaded from: its own
    /// directory, and the package root two levels above it (<c>lib/&lt;tfm&gt;</c>). Yields
    /// nothing under single-file or AOT publishing, where the assembly reports no location —
    /// there the models must have been copied out anyway.
    /// </summary>
    private static IEnumerable<string> RapidOcrNetPackageRoots()
    {
        string location;
        // IL3000: Location is empty under single-file publishing — which is exactly the
        // "yields nothing" case documented above, handled by the emptiness check below.
#pragma warning disable IL3000
        try { location = typeof(RapidOcrModelSet).Assembly.Location; }
#pragma warning restore IL3000
        catch { yield break; }

        if (string.IsNullOrEmpty(location)) yield break;

        var dir = Path.GetDirectoryName(location);
        if (dir is null) yield break;
        yield return dir;

        var tfmParent = Path.GetDirectoryName(dir);              // lib/
        var packageRoot = tfmParent is null ? null : Path.GetDirectoryName(tfmParent);
        if (packageRoot is not null) yield return packageRoot;
    }
}
