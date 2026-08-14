using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// Where <see cref="LayoutModelLocator"/> looks. These pin the search itself rather than whether
/// a model happens to be installed on the machine running them: a model that is present but
/// unreachable is indistinguishable, to every caller, from one that was never downloaded.
/// </summary>
public class LayoutModelLocatorTests
{
    [Fact]
    public void Locator_ReachesASourceTreeRootFromABuildOutputDirectory()
    {
        // Every .NET build output sits at bin/<config>/<tfm> under its project, which sits under
        // the root of a source tree — five levels. A models/ directory at that root is where
        // scripts/download-model.sh puts a downloaded layout model, so it has to be reachable
        // from a binary whose working directory is its own output folder: a test host, an IDE
        // run, `dotnet bin/<config>/<tfm>/App.dll`. The sibling OCR locator had exactly this gap
        // and it was silent, because a second install location covered the common case.
        //
        // This pins the DEPTH of the climb, which is what was wrong. It cannot distinguish the
        // working-directory climb from the app-directory one, because a test host sets its
        // working directory to its own output folder and the two coincide; separating them would
        // mean mutating process-wide cwd while the rest of the suite runs in parallel, which
        // trades a real flake for a redundant assertion.
        var roots = LayoutModelLocator.ProbeRoots()
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r!)))
            .ToHashSet();

        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        for (int i = 0; i < 5; i++)
            dir = Path.GetDirectoryName(dir) ?? dir;

        Assert.Contains(dir, roots);
    }

    [Fact]
    public void Locator_KeepsAppLocalModelsAheadOfUserAndSourceTreeCopies()
    {
        // Precedence is load-bearing for a packaged app: one shipped beside its binaries must not
        // be overridden by a stale copy in the user directory or in whatever source tree the
        // process happens to be running from.
        var roots = LayoutModelLocator.ProbeRoots().Where(r => !string.IsNullOrEmpty(r)).ToList();

        Assert.Equal(Path.GetFullPath(AppContext.BaseDirectory), Path.GetFullPath(roots[0]!));

        int userIndex = roots.FindIndex(r => r!.Contains("railreader2", StringComparison.Ordinal));
        int cwdIndex = roots.FindIndex(r =>
            Path.GetFullPath(r!) == Path.GetFullPath(Directory.GetCurrentDirectory()));
        Assert.True(userIndex >= 0 && cwdIndex >= 0, "expected both a user-data root and the working directory");
        Assert.True(userIndex < cwdIndex, "user-installed models must be found before source-tree copies");
    }

    [Fact]
    public void FindModelPath_ReturnsNullForAFileNobodyHas()
    {
        Assert.Null(LayoutModelLocator.FindModelPath("definitely-not-a-real-model-a3f9.onnx"));
        Assert.Null(LayoutModelLocator.FindModelPath(""));
    }
}
