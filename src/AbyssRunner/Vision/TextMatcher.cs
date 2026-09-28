namespace AbyssRunner.Vision;

public static class TextMatcher
{
    public static string Normalize(string s)
        => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).Trim();

    public static bool IsMatch(string actual, string target, string mode)
    {
        var a = Normalize(actual);
        var t = Normalize(target);
        if (a.Length == 0 || t.Length == 0) return false;

        if (string.Equals(mode, "contains", StringComparison.OrdinalIgnoreCase))
            return a.Contains(t, StringComparison.OrdinalIgnoreCase);

        if (string.Equals(mode, "exact", StringComparison.OrdinalIgnoreCase))
            return string.Equals(a, t, StringComparison.OrdinalIgnoreCase);

        if (a.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Length >= 3 && Math.Abs(a.Length - t.Length) <= 1 && Levenshtein(a, t) <= 1) return true;
        return false;
    }

    public static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
