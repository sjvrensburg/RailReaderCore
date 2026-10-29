using System.Text.Json;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

/// <summary>Content-based document identity, shared by every host keying the annotation sidecar (#159).</summary>
public sealed class DocumentIdentityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("docid").FullName;

    public DocumentIdentityTests() => DocumentIdentity.OverrideMapPathForTesting(Path.Combine(_dir, "ids.json"));

    public void Dispose()
    {
        DocumentIdentity.OverrideMapPathForTesting(null);
        Directory.Delete(_dir, true);
    }

    private string Write(string name, string content)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void MovedAndCopiedFilesKeepTheirId()
    {
        string a = Write("a.pdf", "same bytes");
        string id = DocumentIdentity.Resolve(a);
        string moved = Path.Combine(_dir, "moved.pdf");
        File.Move(a, moved);
        string copy = Write("copy.pdf", "same bytes");
        Assert.Equal(id, DocumentIdentity.Resolve(moved));
        Assert.Equal(id, DocumentIdentity.Resolve(copy));
    }

    [Fact]
    public void DifferentContentGetsADifferentId()
        => Assert.NotEqual(DocumentIdentity.Resolve(Write("a.pdf", "one")), DocumentIdentity.Resolve(Write("b.pdf", "two")));

    [Fact]
    public void RewritingInPlaceKeepsTheIdAndTheNewBytesAreAnAlias()
    {
        string a = Write("a.pdf", "original");
        string id = DocumentIdentity.Resolve(a);
        File.WriteAllText(a, "original plus an annotation");
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(id, DocumentIdentity.Resolve(a));
        string copyOfRewritten = Write("c.pdf", "original plus an annotation");
        Assert.Equal(id, DocumentIdentity.Resolve(copyOfRewritten));
    }

    [Fact]
    public void AnUnchangedFileIsNotRehashedAndPeekNeedsNoFile()
    {
        string a = Write("a.pdf", "x");
        string id = DocumentIdentity.Resolve(a);
        Assert.Equal(id, DocumentIdentity.Peek(a));
        File.Delete(a);
        Assert.Equal(id, DocumentIdentity.Peek(a));
        Assert.Null(DocumentIdentity.Peek(Path.Combine(_dir, "never.pdf")));
    }

    [Fact]
    public void TheMapSurvivesARestart()
    {
        string a = Write("a.pdf", "persist me");
        string id = DocumentIdentity.Resolve(a);
        DocumentIdentity.OverrideMapPathForTesting(DocumentIdentity.MapPath);
        Assert.Equal(id, DocumentIdentity.Peek(a));
        Assert.Contains(Path.GetFullPath(a), DocumentIdentity.KnownPaths(id));
    }

    [Fact]
    public void LookupFindsAMovedFileByContentWithoutWritingTheMap()
    {
        string a = Write("a.pdf", "known bytes");
        string id = DocumentIdentity.Resolve(a);
        string map = DocumentIdentity.MapPath;
        var mapWritten = File.GetLastWriteTimeUtc(map);

        string moved = Path.Combine(_dir, "moved.pdf");
        File.Move(a, moved);
        Assert.Equal(id, DocumentIdentity.Lookup(moved));
        Assert.Null(DocumentIdentity.Peek(moved));           // not recorded
        Assert.Equal(mapWritten, File.GetLastWriteTimeUtc(map));

        Assert.Null(DocumentIdentity.Lookup(Write("other.pdf", "unknown bytes")));
        Assert.Null(DocumentIdentity.Lookup(Path.Combine(_dir, "missing.pdf")));
    }

    [Fact]
    public void LookupNeverCreatesTheMap()
    {
        Assert.Null(DocumentIdentity.Lookup(Write("a.pdf", "fresh")));
        Assert.False(File.Exists(DocumentIdentity.MapPath));
    }

    [Fact]
    public void TheMapFileFormatIsStable()
    {
        // Other hosts read this file; its shape is part of the contract.
        string a = Write("a.pdf", "format");
        string id = DocumentIdentity.Resolve(a);
        string full = Path.GetFullPath(a);

        using var doc = JsonDocument.Parse(File.ReadAllText(DocumentIdentity.MapPath));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        var entry = root.GetProperty("files").GetProperty(full);
        Assert.Equal(id, entry.GetProperty("id").GetString());
        Assert.Equal(new FileInfo(full).Length, entry.GetProperty("size").GetInt64());
        Assert.True(entry.TryGetProperty("mtime_ticks", out _));
        Assert.Equal(id, root.GetProperty("aliases").GetProperty(id).GetString());
        Assert.Equal(full, root.GetProperty("paths").GetProperty(id)[0].GetString());
    }

    // Stands in for the app, in another process, recording a document in the map.
    private void AppRecords(string pdfPath, string id)
    {
        string full = Path.GetFullPath(pdfPath);
        var map = new IdentityMap();
        map.Files[full] = new FileEntry { Size = new FileInfo(full).Length, MtimeTicks = 1, Id = id };
        map.Aliases[id] = id;
        map.Paths[id] = [full];
        JsonSidecar.Save(DocumentIdentity.MapPath, map, RailReaderJsonContext.Default.IdentityMap);
    }

    [Fact]
    public void RefreshSeesDocumentsTheAppRecordedSinceTheMapWasRead()
    {
        string a = Write("a.pdf", "opened later");
        Assert.Null(DocumentIdentity.Lookup(a));             // map read (absent) and cached

        AppRecords(a, "appid");
        Assert.Null(DocumentIdentity.Lookup(a));             // still the cached map
        Assert.True(DocumentIdentity.Refresh());
        Assert.Equal("appid", DocumentIdentity.Lookup(a));
    }

    [Fact]
    public void RefreshKeepsTheLoadedMapWhenTheFileIsUnreadable()
    {
        string a = Write("a.pdf", "known");
        AppRecords(a, "appid");
        Assert.True(DocumentIdentity.Refresh());

        File.WriteAllText(DocumentIdentity.MapPath, "{ not json");
        Assert.False(DocumentIdentity.Refresh());
        Assert.Equal("appid", DocumentIdentity.Lookup(a));
    }

    [Fact]
    public void RefreshTreatsAMissingMapAsEmpty()
    {
        string a = Write("a.pdf", "known");
        AppRecords(a, "appid");
        Assert.True(DocumentIdentity.Refresh());

        File.Delete(DocumentIdentity.MapPath);
        Assert.True(DocumentIdentity.Refresh());
        Assert.Null(DocumentIdentity.Peek(a));
    }

    [Fact]
    public void RefreshRefusesInTheProcessThatOwnsTheMap()
    {
        string a = Write("a.pdf", "owned");
        string id = DocumentIdentity.Resolve(a);
        File.WriteAllText(DocumentIdentity.MapPath, "{}");   // whatever is on disk must not replace memory
        Assert.Throws<InvalidOperationException>(() => DocumentIdentity.Refresh());
        Assert.Equal(id, DocumentIdentity.Peek(a));
    }

    [Fact]
    public void AnUnreadableMapIsKeptBeforeTheFirstSaveOverwritesIt()
    {
        File.WriteAllText(DocumentIdentity.MapPath, "{ not json");
        DocumentIdentity.Resolve(Write("a.pdf", "after corruption"));
        Assert.Equal("{ not json", File.ReadAllText(DocumentIdentity.MapPath + ".unreadable"));
    }

    [Fact]
    public void APathHoldingADifferentDocumentLeavesTheOldIdsKnownPaths()
    {
        string a = Write("a.pdf", "first doc");
        string first = DocumentIdentity.Resolve(a);
        string b = Write("b.pdf", "second doc");
        string second = DocumentIdentity.Resolve(b);
        File.Copy(b, a, overwrite: true);
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(second, DocumentIdentity.Resolve(a));
        Assert.DoesNotContain(Path.GetFullPath(a), DocumentIdentity.KnownPaths(first));
    }

    [Fact]
    public void SidecarAnnotationsOfAMovedFileResolveToTheSameStorage()
    {
        // The app keys the sidecar by Peek after resolving at open; any other host by ReadOnlyResolver.
        string a = Write("a.pdf", "sidecar bytes");
        DocumentIdentity.Resolve(a);
        string moved = Path.Combine(_dir, "renamed.pdf");
        File.Copy(a, moved);

        var app = new AnnotationService { DocumentKeyResolver = DocumentIdentity.Peek };
        var other = new AnnotationService { DocumentKeyResolver = DocumentIdentity.ReadOnlyResolver };
        string stored = app.GetStoragePath(a);
        Assert.NotEqual(AnnotationService.GetInternalPath(a), stored);
        Assert.Equal(stored, other.GetStoragePath(a));
        Assert.Equal(stored, other.GetStoragePath(moved));
        DocumentIdentity.Resolve(moved);                     // the app opening the renamed copy
        Assert.Equal(stored, app.GetStoragePath(moved));
    }
}
