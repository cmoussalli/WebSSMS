namespace WebSSMS.Services;

public enum ServerPathStyle
{
    Windows,
    Posix
}

/// <summary>
/// Path handling for paths that belong to *SQL Server's* machine, which is not
/// necessarily this process's machine.
///
/// System.IO.Path is useless here: it always assumes the running host. Ask a
/// Windows-hosted web app to canonicalise the Linux path "/var/opt/mssql/backup"
/// and Path.GetFullPath cheerfully returns "C:\var\opt\mssql\backup" -- a path
/// that exists nowhere, silently pointing the transfer at the wrong file. So the
/// style is inferred from the path itself and every operation honours it.
/// </summary>
public static class ServerPath
{
    /// <summary>
    /// Windows paths are rooted at a drive letter or a UNC share; anything starting
    /// with a forward slash is treated as POSIX.
    /// </summary>
    public static ServerPathStyle DetectStyle(string path)
    {
        var trimmed = path.TrimStart();

        if (trimmed.StartsWith(@"\\") || trimmed.StartsWith("//"))
            return ServerPathStyle.Windows;

        if (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
            return ServerPathStyle.Windows;

        if (trimmed.StartsWith('/'))
            return ServerPathStyle.Posix;

        // Relative or unrecognised -- read it in the host's own terms and let
        // Normalize reject it for not being absolute.
        return OperatingSystem.IsWindows() ? ServerPathStyle.Windows : ServerPathStyle.Posix;
    }

    public static char SeparatorFor(ServerPathStyle style) =>
        style == ServerPathStyle.Windows ? '\\' : '/';

    /// <summary>
    /// True when this process can plausibly open the path itself, i.e. the path is
    /// written in the host OS's own style. A Windows app has no way to open
    /// "/var/opt/mssql/backup" on a Linux SQL Server, so it must not try.
    /// </summary>
    public static bool IsLocalToThisHost(string path)
    {
        var style = DetectStyle(path);
        return style == (OperatingSystem.IsWindows() ? ServerPathStyle.Windows : ServerPathStyle.Posix);
    }

    /// <summary>
    /// Resolves "." and ".." and normalises separators, without ever touching the
    /// file system. Returns null and sets <paramref name="error"/> on a path that is
    /// relative, malformed, or tries to climb above its own root.
    /// </summary>
    public static string? Normalize(string path, ServerPathStyle style, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "The path is empty.";
            return null;
        }

        var value = path.Trim();

        if (value.Any(char.IsControl))
        {
            error = "The path contains control characters.";
            return null;
        }

        return style == ServerPathStyle.Windows
            ? NormalizeWindows(value, out error)
            : NormalizePosix(value, out error);
    }

    private static string? NormalizeWindows(string value, out string? error)
    {
        error = null;
        value = value.Replace('/', '\\');

        string prefix;
        string remainder;

        if (value.StartsWith(@"\\"))
        {
            var parts = value[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                error = "A UNC path must include a server and a share, e.g. \\\\server\\share\\folder.";
                return null;
            }

            prefix = $@"\\{parts[0]}\{parts[1]}";
            remainder = string.Join('\\', parts.Skip(2));
        }
        else if (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':')
        {
            prefix = $"{char.ToUpperInvariant(value[0])}:";
            remainder = value[2..].TrimStart('\\');
        }
        else
        {
            error = "The path must be absolute, e.g. C:\\Backups or \\\\server\\share.";
            return null;
        }

        var segments = ResolveSegments(remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries), out error);
        if (segments == null) return null;

        foreach (var segment in segments)
        {
            if (segment.IndexOfAny(new[] { '<', '>', ':', '"', '|', '?', '*' }) >= 0)
            {
                error = $"'{segment}' contains characters that are not valid in a Windows path.";
                return null;
            }
        }

        return segments.Count == 0
            ? prefix + '\\'
            : prefix + '\\' + string.Join('\\', segments);
    }

    private static string? NormalizePosix(string value, out string? error)
    {
        error = null;

        if (!value.StartsWith('/'))
        {
            error = "The path must be absolute, e.g. /var/opt/mssql/backup.";
            return null;
        }

        var segments = ResolveSegments(value.Split('/', StringSplitOptions.RemoveEmptyEntries), out error);
        if (segments == null) return null;

        return '/' + string.Join('/', segments);
    }

    private static List<string>? ResolveSegments(IEnumerable<string> raw, out string? error)
    {
        error = null;
        var resolved = new List<string>();

        foreach (var segment in raw)
        {
            if (segment == ".") continue;

            if (segment == "..")
            {
                if (resolved.Count == 0)
                {
                    error = "The path climbs above its root.";
                    return null;
                }

                resolved.RemoveAt(resolved.Count - 1);
                continue;
            }

            resolved.Add(segment);
        }

        return resolved;
    }

    public static string GetFileName(string normalizedPath)
    {
        var index = normalizedPath.LastIndexOfAny(new[] { '\\', '/' });
        return index < 0 ? normalizedPath : normalizedPath[(index + 1)..];
    }

    public static string? GetDirectoryName(string normalizedPath)
    {
        var index = normalizedPath.LastIndexOfAny(new[] { '\\', '/' });
        if (index < 0) return null;

        // "/file" and "C:\file" -- the parent is the root itself.
        if (index == 0) return "/";
        if (index == 2 && normalizedPath[1] == ':') return normalizedPath[..3];

        return normalizedPath[..index];
    }

    public static string GetExtension(string normalizedPath)
    {
        var name = GetFileName(normalizedPath);
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? string.Empty : name[dot..];
    }

    public static string Combine(string directory, string name, ServerPathStyle style)
    {
        var separator = SeparatorFor(style);
        return directory.TrimEnd('\\', '/') + separator + name;
    }

    public static string TrimTrailingSeparator(string path)
    {
        // Never trim a bare root: "/" and "C:\" are already minimal.
        if (path.Length <= 1) return path;
        if (path.Length == 3 && path[1] == ':') return path;

        return path.TrimEnd('\\', '/');
    }

    public static StringComparison ComparisonFor(ServerPathStyle style) =>
        style == ServerPathStyle.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or sits inside it.</summary>
    public static bool IsWithin(string path, string root, ServerPathStyle style)
    {
        var comparison = ComparisonFor(style);
        var separator = SeparatorFor(style);

        var normalizedRoot = TrimTrailingSeparator(root);

        if (path.Equals(normalizedRoot, comparison)) return true;

        var prefix = normalizedRoot.EndsWith(separator)
            ? normalizedRoot
            : normalizedRoot + separator;

        return path.StartsWith(prefix, comparison);
    }
}
