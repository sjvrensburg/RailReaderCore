using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace RailReader.Core.Services;

/// <summary>
/// Shared load/save for small config-dir-resident JSON files (source-gen serialised): the
/// "read → deserialize-or-default" / "create dir → write" boilerplate plus try/catch logging.
/// Used by <see cref="DocumentIdentity"/>; public so hosts can keep their own preference files the same way.
/// </summary>
public static class JsonSidecar
{
    /// <summary>Load <typeparamref name="T"/> from <paramref name="path"/>, returning
    /// <paramref name="fallback"/>() when the file is absent, empty, or unreadable.</summary>
    public static T Load<T>(string path, JsonTypeInfo<T> typeInfo, Func<T> fallback) where T : class
        => TryLoad(path, typeInfo, fallback, out var value) ? value : fallback();

    /// <summary>Like <see cref="Load{T}"/>, but tells an absent file (true, <paramref name="fallback"/>())
    /// apart from one that exists and could not be read or parsed (false, logged).</summary>
    public static bool TryLoad<T>(string path, JsonTypeInfo<T> typeInfo, Func<T> fallback, out T value) where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                value = fallback();
                return true;
            }
            if (JsonSerializer.Deserialize(ReadShared(path), typeInfo) is { } loaded)
            {
                value = loaded;
                return true;
            }
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error($"Failed to load {Path.GetFileName(path)}", ex);
        }
        value = fallback();
        return false;
    }

    /// <summary>Reads the whole file without blocking another process's <see cref="Save{T}"/>: on Windows,
    /// a reader opened without delete sharing makes the writer's rename over the file fail.</summary>
    internal static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Serialise <paramref name="value"/> to <paramref name="path"/>, creating its directory.
    /// Writes to a temp file and renames over the target so a crash or IO error mid-write can't leave
    /// a truncated/empty file in place.</summary>
    public static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        // Unique per writer, so two processes saving at once can't clobber each other's temp file.
        var tmpPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(value, typeInfo));
            File.Move(tmpPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error($"Failed to save {Path.GetFileName(path)}", ex);
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best effort */ }
        }
    }
}
