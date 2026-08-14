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

    /// <summary>
    /// How far to climb when probing a directory's ancestors. A .NET build output sits three
    /// levels below its project (<c>bin/&lt;config&gt;/&lt;tfm&gt;</c>) and the project one or two
    /// below the root of the source tree, so anything less than five cannot see a
    /// <c>models/</c> directory at that root. The climb costs one <c>File.Exists</c> per level per
    /// model file, once at startup, and can never pick a wrong directory — a root only matches
    /// when the exact file is present under it.
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
    /// The directories <see cref="Resolve"/> searches, in precedence order. Internal so a test can
    /// pin the reachability the layout depends on rather than only the resolutions that happen to
    /// work on the machine running it.
    /// </summary>
    internal static IEnumerable<string?> ProbeRoots()
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

        // Working directory and its ancestors, for a `dotnet run` from a source tree.
        foreach (var root in Ancestors(Directory.GetCurrentDirectory()))
            yield return root;

        // The app's own directory and ITS ancestors. Not the same search as the one above:
        // the working directory is whatever the launcher chose, and for a binary started from
        // its own output folder — a test host, an IDE run, `dotnet bin/…/App.dll` — both walks
        // start deep inside `bin/<config>/<tfm>` and only reach the source tree by climbing.
        // Missing this is what hid every opt-in v6 model set from the test suite: the bundled
        // v5 set resolved beside the binary, the downloaded v6 set sat unreachable at the root
        // of the same repository, and the tests that needed it silently skipped.
        foreach (var root in Ancestors(AppContext.BaseDirectory, includeSelf: false))
            yield return root;
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
