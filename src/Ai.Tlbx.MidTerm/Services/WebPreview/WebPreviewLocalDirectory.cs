namespace Ai.Tlbx.MidTerm.Services.WebPreview;

/// <summary>Only regular files below an explicitly registered local program directory are served.</summary>
internal static class WebPreviewLocalDirectory
{
    internal static string? ResolveFile(string directory, string path)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var relative = path.Length == 0 ? "index.html" : path;
        if (Path.IsPathRooted(relative) || relative.Contains(':', StringComparison.Ordinal)) return null;
        var parts = relative.Replace('\\', '/').Split('/');
        var current = root;
        for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
            if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0) return null;
        foreach (var part in parts)
        {
            if (part is ".." or "." || part.Length == 0) return null;
            current = Path.Combine(current, part);
            if (!Path.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
        }
        return File.Exists(current) ? current : null;
    }
}
