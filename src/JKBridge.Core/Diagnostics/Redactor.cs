namespace JKBridge.Core.Diagnostics;

public static class Redactor
{
    public static string? Sanitize(string? value, string? userProfile = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var result = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            result = result.Replace(
                userProfile.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                "%USERPROFILE%",
                StringComparison.OrdinalIgnoreCase);
        }

        return result.Length <= 4_096 ? result : result[..4_096] + "…";
    }
}
