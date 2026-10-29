using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RailReader.Core.Services;

/// <summary>
/// One spelling per file, so a map keyed by path gives one file one entry. <see cref="Path.GetFullPath(string)"/>
/// does not canonicalise: on Windows the same file is reachable under different casing, an 8.3 short
/// name, a mapped drive vs its UNC path, a junction or a <c>subst</c> drive; on Unix through a symlink.
/// </summary>
internal static class CanonicalPath
{
    private const int MaxLinkHops = 40;   // Linux's SYMLOOP_MAX: a cycle gives up rather than spinning

    /// <summary>The file's canonical full path, or null when it doesn't exist or can't be resolved —
    /// callers fall back to <see cref="Path.GetFullPath(string)"/>.</summary>
    public static string? Of(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath)) return null;
            return OperatingSystem.IsWindows() ? FinalPathName(fullPath) : ResolveLinks(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Walks the path a component at a time, replacing each symlink by its target and starting
    /// again from the root, since the target may itself run through links. Everything before the
    /// component being examined is then link-free, so a <c>..</c> in a relative target is resolved
    /// against the real parent, as the kernel does — not lexically, as <c>GetFullPath</c> would.</summary>
    internal static string? ResolveLinks(string fullPath)
    {
        string path = fullPath;
        for (int hops = 0; ; hops++)
        {
            string root = Path.GetPathRoot(path)!;
            string current = root;
            string[] parts = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string? relink = null;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == ".") continue;
                if (parts[i] == "..") { current = Path.GetDirectoryName(current) ?? root; continue; }

                string next = Path.Join(current, parts[i]);
                if (new FileInfo(next).LinkTarget is { } target)
                {
                    relink = Path.Join(Path.IsPathRooted(target) ? target : Path.Join(current, target),
                        string.Join(Path.DirectorySeparatorChar, parts[(i + 1)..]));
                    break;
                }
                current = next;
            }
            if (relink is null) return current;
            if (hops == MaxLinkHops) return null;
            path = relink;
        }
    }

    /// <summary>The final path of a file or directory on Windows (<c>GetFinalPathNameByHandleW</c>):
    /// on-disk casing, long names, with junctions, symlinks and <c>subst</c> drives resolved.</summary>
    internal static string? FinalPathName(string fullPath)
    {
        // No access rights requested, so a file another process holds open without sharing still resolves;
        // backup semantics would also admit a directory.
        using SafeFileHandle handle = CreateFileW(ExtendedLengthPath(fullPath), 0,
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;

        var buffer = new char[512];
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length >= buffer.Length)   // too small: the return value is the size needed, terminator included
        {
            buffer = new char[length];
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        }
        if (length == 0 || length >= buffer.Length) return null;

        // FILE_NAME_NORMALIZED | VOLUME_NAME_DOS yields \\?\C:\… or, for a network path, \\?\UNC\server\share\….
        string path = new(buffer, 0, (int)length);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return LocalAdminShare(@"\\" + path[8..]);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
        return path;
    }

    // CreateFileW takes MAX_PATH (260) or more only with the \\?\ prefix, unless the process has opted in to
    // long paths (manifest and registry both), which a library can't assume. The prefix also turns off the
    // path normalisation, so it is added only to a full path.
    private static string ExtendedLengthPath(string path)
    {
        if (path.Length < 260 || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return path;
        path = Path.GetFullPath(path);
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    // A file opened through this machine's own administrative share (\\localhost\C$\…, or a drive mapped to it)
    // is the same file as C:\…, but its final path is the share's: the handle belongs to the network redirector.
    private static string LocalAdminShare(string uncPath)
    {
        string[] parts = uncPath[2..].Split('\\', 3);
        if (parts.Length < 2 || parts[1].Length != 2 || parts[1][1] != '$' || !char.IsAsciiLetter(parts[1][0])) return uncPath;
        string host = parts[0];
        if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && host != "127.0.0.1"
            && !host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return uncPath;
        return $"{char.ToUpperInvariant(parts[1][0])}:\\{(parts.Length == 3 ? parts[2] : "")}";
    }

    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, FileShare dwShareMode,
        IntPtr lpSecurityAttributes, FileMode dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, [Out] char[] lpszFilePath,
        uint cchFilePath, uint dwFlags);
}
