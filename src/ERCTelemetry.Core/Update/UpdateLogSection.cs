namespace ERCTelemetry.Core.Update;

/// <summary>Extracts the UPDATELOG.md section for one version — pure string logic, kept in
/// Core so it is unit-testable; the WPF UpdateLogWindow renders the returned markdown.</summary>
public static class UpdateLogSection
{
    /// <summary>The section of <paramref name="markdown"/> under the `## &lt;version&gt;` heading,
    /// up to the next `## ` (or EOF); null when the version has no entry. The version match is
    /// word-boundary aware: `## 0.4.2` does not match `## 0.4.20`.</summary>
    public static string? Extract(string markdown, string version)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsVersionHeading(lines[i], version))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return null;
        }

        var end = lines.Length;
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("## ", StringComparison.Ordinal))
            {
                end = i;
                break;
            }
        }

        return string.Join("\n", lines[start..end]).Trim();
    }

    /// <summary>True when the line is a `## &lt;version&gt;` heading and the version is not a
    /// prefix of a longer version token (e.g. `## 0.4.2` must not match `## 0.4.20`).</summary>
    private static bool IsVersionHeading(string line, string version)
    {
        var prefix = $"## {version}";
        if (line.StartsWith(prefix, StringComparison.Ordinal) is false)
        {
            return false;
        }

        return line.Length == prefix.Length || char.IsLetterOrDigit(line[prefix.Length]) is false;
    }
}
