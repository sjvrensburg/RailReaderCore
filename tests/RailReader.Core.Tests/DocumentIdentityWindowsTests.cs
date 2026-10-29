using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>
/// The Windows spellings of one file (railreader2#279, #161), the counterparts of the symlink tests in
/// <see cref="DocumentIdentityTests"/>. Each spelling must canonicalise to the on-disk path, keep the
/// file's id after its bytes change outside the app, and leave one entry in the map.
/// </summary>
public sealed class DocumentIdentityWindowsTests : IDisposable
{
    private readonly string _dir;
    private readonly List<Action> _undo = [];   // subst / net use, removed before the directory

    public DocumentIdentityWindowsTests()
    {
        _dir = DocumentIdentityTests.CanonicalDir(Directory.CreateTempSubdirectory("docid").FullName);
        DocumentIdentity.OverrideMapPathForTesting(Path.Combine(_dir, "ids.json"));
    }

    public void Dispose()
    {
        foreach (var undo in Enumerable.Reverse(_undo))
            try { undo(); } catch (Exception) { }
        DocumentIdentity.OverrideMapPathForTesting(null);
        Directory.Delete(_dir, true);
    }

    private string Write(string relative, string content)
    {
        string p = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    // The contract for every spelling: same key as the real path, same id before and after another tool
    // rewrote the file (so neither the path nor the new hash is known), one entry in the map.
    private static void AssertOneIdentity(string real, params string[] spellings)
    {
        Assert.Equal(real, CanonicalPath.Of(real));
        foreach (string s in spellings)
            Assert.Equal(real, CanonicalPath.Of(s));

        string id = DocumentIdentity.Resolve(real);
        foreach (string s in spellings)
        {
            Assert.Equal(id, DocumentIdentity.Peek(s));
            Assert.Equal(id, DocumentIdentity.Resolve(s));
        }
        for (int i = 0; i < spellings.Length; i++)
        {
            File.WriteAllText(real, $"rewritten by another tool, {i}");
            File.SetLastWriteTimeUtc(real, DateTime.UtcNow.AddMinutes(5 + i));
            Assert.Equal(id, DocumentIdentity.Lookup(spellings[i]));
            Assert.Equal(id, DocumentIdentity.Resolve(spellings[i]));
        }

        Assert.Equal([real], DocumentIdentity.KnownPaths(id));
        using var doc = JsonDocument.Parse(File.ReadAllText(DocumentIdentity.MapPath));
        Assert.Equal(real, Assert.Single(doc.RootElement.GetProperty("files").EnumerateObject()).Name);
    }

    [WindowsFact]
    public void CasingResolvesToTheOnDiskSpelling()
    {
        string real = Write(@"Docs\A.pdf", "cased");
        string lower = real.ToLowerInvariant(), upper = real.ToUpperInvariant();
        Assert.Equal(real, CanonicalPath.Of(lower));         // the on-disk casing, not the input's
        Assert.Equal(real, CanonicalPath.Of(upper));
        string dotted = Path.Combine(_dir, "Docs", "..", "docs", "a.pdf").Replace('\\', '/');
        AssertOneIdentity(real, lower, upper, dotted);
    }

    [WindowsFact]
    public void ALowerCaseDriveLetterHasOneEntry()
    {
        string real = Write(@"Docs\A.pdf", "drive letter");
        AssertOneIdentity(real, char.ToLowerInvariant(real[0]) + real[1..]);
    }

    [WindowsFact(WindowsNeeds.ShortNames)]
    public void AnEightDotThreeNameHasOneEntry()
    {
        string real = Write(@"Long Directory Name\Annotated Paper.pdf", "short name");
        string shortName = ShortPathName(real);
        Assert.NotEqual(real, shortName);
        AssertOneIdentity(real, shortName);
    }

    [WindowsFact]
    public void ASubstDriveHasOneEntry()
    {
        string real = Write(@"Docs\A.pdf", "subst");
        char letter = FreeDriveLetter();
        Run("subst.exe", $"{letter}:", _dir);
        _undo.Add(() => Run("subst.exe", $"{letter}:", "/D"));
        AssertOneIdentity(real, $@"{letter}:\Docs\A.pdf");
    }

    [WindowsFact(WindowsNeeds.AdminShare)]
    public void TheAdministrativeShareHasOneEntry()
    {
        string real = Write(@"Docs\A.pdf", "unc");
        AssertOneIdentity(real, $@"\\localhost\{real[0]}$\{real[3..]}", $@"\\localhost\{char.ToLowerInvariant(real[0])}$\{real[3..].ToLowerInvariant()}");
    }

    [WindowsFact(WindowsNeeds.AdminShare)]
    public void ADriveMappedToTheShareHasOneEntry()
    {
        string real = Write(@"Docs\A.pdf", "net use");
        char letter = FreeDriveLetter();
        Run("net.exe", "use", $"{letter}:", $@"\\localhost\{real[0]}$", "/persistent:no");
        _undo.Add(() => Run("net.exe", "use", $"{letter}:", "/delete", "/y"));
        AssertOneIdentity(real, $@"{letter}:\{real[3..]}");
    }

    [WindowsFact]
    public void AFileThroughAJunctionHasOneEntry()
    {
        string real = Write(@"Docs\A.pdf", "junction");
        string junction = Path.Combine(_dir, "Linked");
        // mklink is a cmd built-in; a single argument string keeps cmd's own quoting rules out of the way.
        RunRaw("cmd.exe", $"/c mklink /J \"{junction}\" \"{Path.Combine(_dir, "Docs")}\"");
        // A recursive delete takes a junction for a volume mount point and fails; a plain one removes the link.
        _undo.Add(() => Directory.Delete(junction));
        AssertOneIdentity(real, Path.Combine(junction, "A.pdf"));
    }

    [WindowsFact(WindowsNeeds.Symlinks)]
    public void AFileThroughASymlinkHasOneEntry()
    {
        string real = Write(@"Docs\A.pdf", "symlink");
        string fileLink = Path.Combine(_dir, "link.pdf");
        File.CreateSymbolicLink(fileLink, real);
        string dirLink = Path.Combine(_dir, "Shortcut");
        Directory.CreateSymbolicLink(dirLink, Path.Combine(_dir, "Docs"));
        AssertOneIdentity(real, fileLink, Path.Combine(dirLink, "A.pdf"));
    }

    [WindowsFact]
    public void AnEntryUnderAnOlderSpellingMovesToTheCanonicalKey()
    {
        // Maps written before canonicalisation keyed whatever spelling the file was opened by.
        string real = Write(@"Docs\A.pdf", "legacy");
        string lower = real.ToLowerInvariant();
        var info = new FileInfo(real);
        var map = new IdentityMap();
        map.Files[lower] = new FileEntry { Size = info.Length, MtimeTicks = info.LastWriteTimeUtc.Ticks, Id = "oldid" };
        map.Paths["oldid"] = [lower];
        JsonSidecar.Save(DocumentIdentity.MapPath, map, RailReaderJsonContext.Default.IdentityMap);
        DocumentIdentity.OverrideMapPathForTesting(DocumentIdentity.MapPath);

        Assert.Equal("oldid", DocumentIdentity.Peek(lower));
        Assert.Equal("oldid", DocumentIdentity.Resolve(lower));
        Assert.Equal([real], DocumentIdentity.KnownPaths("oldid"));
        using var doc = JsonDocument.Parse(File.ReadAllText(DocumentIdentity.MapPath));
        var files = doc.RootElement.GetProperty("files");
        Assert.Single(files.EnumerateObject());
        Assert.Equal("oldid", files.GetProperty(real).GetProperty("id").GetString());
    }

    [WindowsFact]
    public void AFileHeldOpenWithoutSharingStillCanonicalises()
    {
        string real = Write(@"Docs\A.pdf", "locked");
        string id = DocumentIdentity.Resolve(real);
        using var held = new FileStream(real, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(real, CanonicalPath.Of(real.ToLowerInvariant()));
        Assert.Equal(id, DocumentIdentity.Peek(real.ToLowerInvariant()));
    }

    [WindowsFact]
    public void ALongPathRoundTrips()
    {
        // Past MAX_PATH (CreateFileW needs the \\?\ prefix) and past the 512-char first buffer (the regrow).
        string segment = new('d', 100);
        string real = Write(Path.Combine(segment + "1", segment + "2", segment + "3", segment + "4", segment + "5", "Long.pdf"), "long");
        Assert.True(real.Length > 512, $"only {real.Length} chars");
        AssertOneIdentity(real, real.ToLowerInvariant(), real.Replace('\\', '/'));
    }

    private static char FreeDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'Z'; c >= 'H'; c--)
            if (!used.Contains(c)) return c;
        throw new InvalidOperationException("no free drive letter");
    }

    private static void Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file);
        foreach (string a in args) psi.ArgumentList.Add(a);
        Run(psi);
    }

    private static void RunRaw(string file, string arguments) => Run(new ProcessStartInfo(file, arguments));

    private static void Run(ProcessStartInfo psi)
    {
        psi.UseShellExecute = false;
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();   // net use would otherwise wait on a credentials prompt
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000))
        {
            p.Kill(entireProcessTree: true);
            Assert.Fail($"{psi.FileName} {psi.Arguments}{string.Join(' ', psi.ArgumentList)} timed out");
        }
        Assert.True(p.ExitCode == 0,
            $"{psi.FileName} {psi.Arguments}{string.Join(' ', psi.ArgumentList)} exited {p.ExitCode}: {stdout.Result}{stderr.Result}");
    }

    internal static string ShortPathName(string path)
    {
        var buffer = new char[1024];
        uint length = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        return length == 0 || length >= buffer.Length ? path : new string(buffer, 0, (int)length);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string lpszLongPath, [Out] char[] lpszShortPath, uint cchBuffer);
}

public enum WindowsNeeds { Nothing, ShortNames, Symlinks, AdminShare }

/// <summary>
/// A [Fact] that runs on Windows only, and is skipped (with the reason) when the machine lacks what the
/// test needs. The administrative share is not optional where <c>RAILREADER_REQUIRE_ADMIN_SHARE</c> is set
/// (CI): there the test runs, and fails, rather than skip and let the check pass vacuously.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(WindowsNeeds needs = WindowsNeeds.Nothing)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only";
            return;
        }
        string? missing = needs switch
        {
            WindowsNeeds.ShortNames => ShortNamesMissing(),
            WindowsNeeds.Symlinks => SymlinksMissing(),
            WindowsNeeds.AdminShare when string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RAILREADER_REQUIRE_ADMIN_SHARE"))
                => AdminShareMissing(),
            _ => null,
        };
        if (missing is not null) Skip = missing;
    }

    private static string? ShortNamesMissing()
    {
        string probe = Path.Combine(Path.GetTempPath(), $"railreader short name probe {Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "");
            return Path.GetFileName(DocumentIdentityWindowsTests.ShortPathName(probe)) == Path.GetFileName(probe)
                ? $"8.3 short names are disabled on the temp volume ({Path.GetPathRoot(probe)}); see fsutil 8dot3name query"
                : null;
        }
        finally { File.Delete(probe); }
    }

    private static string? SymlinksMissing()
    {
        string dir = Directory.CreateTempSubdirectory("symprobe").FullName;
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir, "link"), Path.Combine(dir, "target"));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "creating a symlink needs Developer Mode or an elevated process";
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string? AdminShareMissing()
    {
        string share = $@"\\localhost\{Path.GetTempPath()[0]}$\";
        return Directory.Exists(share) ? null : $"{share} is not reachable (it needs an administrator); set RAILREADER_REQUIRE_ADMIN_SHARE to fail instead";
    }
}
