using Ai.Tlbx.MidTerm.Models.Git;

namespace Ai.Tlbx.MidTerm.Services.Git;

internal static class GitFileStatusMapBuilder
{
    internal static Dictionary<string, string> Build(GitStatusResponse status)
    {
        var result = new Dictionary<string, string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        AddEntries(result, status.Conflicted);
        AddEntries(result, status.Staged);
        AddEntries(result, status.Modified);
        AddEntries(result, status.Untracked);

        return result;
    }

    private static void AddEntries(Dictionary<string, string> map, IEnumerable<GitFileEntry> entries)
    {
        foreach (var entry in entries)
        {
            // Porcelain paths already use '/'; a backslash may be a literal Unix filename.
            var relativePath = entry.Path;
            if (string.IsNullOrEmpty(relativePath))
            {
                continue;
            }

            var badge = StatusToBadge(entry.Status);
            SetIfStronger(map, relativePath, badge);

            var slash = relativePath.LastIndexOf('/', StringComparison.Ordinal);
            while (slash > 0)
            {
                var directory = relativePath[..slash];
                SetIfStronger(map, directory, badge);
                slash = directory.LastIndexOf('/', StringComparison.Ordinal);
            }
        }
    }

    private static void SetIfStronger(Dictionary<string, string> map, string path, string badge)
    {
        if (!map.TryGetValue(path, out var existing) || GetPriority(badge) > GetPriority(existing))
        {
            map[path] = badge;
        }
    }

    private static string StatusToBadge(string status)
    {
        return status switch
        {
            "conflicted" or "unmerged" => "!",
            "deleted" => "D",
            "renamed" => "R",
            "copied" => "C",
            "type-changed" => "T",
            "added" => "A",
            "untracked" => "U",
            _ => "M"
        };
    }

    private static int GetPriority(string badge)
    {
        return badge switch
        {
            "!" => 5,
            "D" => 4,
            "R" => 3,
            "C" => 3,
            "T" => 3,
            "M" => 3,
            "A" => 2,
            "U" => 1,
            _ => 0
        };
    }
}
