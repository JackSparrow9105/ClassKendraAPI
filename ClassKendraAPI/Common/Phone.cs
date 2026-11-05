using System.Text.RegularExpressions;

namespace ClassKendraAPI.Common;

public static class Phone
{
    /// <summary>"98765 43210", "+91 9876543210", "09876543210" → "9876543210". Null if not a valid Indian mobile.</summary>
    public static string? Normalise(string? raw)
    {
        var digits = Regex.Replace(raw ?? "", @"\D", "");
        if (digits.Length == 12 && digits.StartsWith("91")) digits = digits[2..];
        if (digits.Length == 11 && digits.StartsWith('0')) digits = digits[1..];
        return Regex.IsMatch(digits, "^[6-9][0-9]{9}$") ? digits : null;
    }
}