using System.Text;

namespace CineVault.Services;

public sealed record RobotsDecision(bool Allowed, string? Agent = null, string? Rule = null);

public static class RobotsPolicy
{
    private sealed record Rule(bool Allow, string Pattern);
    private sealed record Group(List<string> Agents, List<Rule> Rules);
    public static RobotsDecision Evaluate(string text, Uri url, string productToken = "CineVault")
    {
        var groups = new List<Group>();
        List<string> agents = []; List<Rule> rules = []; var hasRules = false;
        foreach (var raw in text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var parts = raw.Split('#')[0].Trim().Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            if (parts[0].Equals("user-agent", StringComparison.OrdinalIgnoreCase))
            {
                if (hasRules) { groups.Add(new(agents, rules)); agents = []; rules = []; hasRules = false; }
                agents.Add(parts[1]);
            }
            else if (agents.Count > 0 && (parts[0].Equals("allow", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("disallow", StringComparison.OrdinalIgnoreCase)))
            {
                hasRules = true;
                if (parts[1].StartsWith('/') && parts[1].Length <= 16384)
                    rules.Add(new(parts[0].Equals("allow", StringComparison.OrdinalIgnoreCase), parts[1]));
            }
        }
        if (agents.Count > 0) groups.Add(new(agents, rules));
        var applicable = groups.Where(g => g.Agents.Any(a => a.Equals(productToken, StringComparison.OrdinalIgnoreCase))).ToArray();
        var agent = productToken;
        if (applicable.Length == 0) { applicable = groups.Where(g => g.Agents.Contains("*")).ToArray(); agent = "*"; }
        var path = Normalize(url.PathAndQuery);
        var matches = applicable.SelectMany(g => g.Rules).Where(r => Matches(r.Pattern, path))
            .OrderByDescending(r => Encoding.UTF8.GetByteCount(Normalize(r.Pattern.TrimEnd('$')).Replace("*", "")))
            .ThenByDescending(r => r.Allow).ToArray();
        return matches.Length == 0 ? new(true) : new(matches[0].Allow, agent, (matches[0].Allow ? "Allow: " : "Disallow: ") + matches[0].Pattern);
    }
    private static bool Matches(string pattern, string path)
    {
        var end = pattern.EndsWith('$');
        pattern = Normalize(end ? pattern[..^1] : pattern);
        var p = 0; var s = 0; var star = -1; var retry = 0;
        while (s < path.Length)
        {
            if (p == pattern.Length && !end) return true;
            if (p < pattern.Length && pattern[p] == '*') { star = p++; retry = s; }
            else if (p < pattern.Length && pattern[p] == path[s]) { p++; s++; }
            else if (star >= 0) { p = star + 1; s = ++retry; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
    private static string Normalize(string value)
    {
        var result = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length && byte.TryParse(value.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                var c = (char)b;
                if (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~') result.Append(c);
                else result.Append('%').Append(b.ToString("X2"));
                i += 2;
            }
            else if (value[i] > 127)
            {
                var length = char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
                foreach (var octet in Encoding.UTF8.GetBytes(value.Substring(i, length))) result.Append('%').Append(octet.ToString("X2"));
                i += length - 1;
            }
            else result.Append(value[i]);
        }
        return result.ToString();
    }
}
