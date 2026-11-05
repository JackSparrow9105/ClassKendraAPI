namespace ClassKendraAPI.Common;

public static class Money
{
    /// <summary>Indian grouping, same as the app's rupees(): 184500 → "₹1,84,500".</summary>
    public static string Inr(int amount)
    {
        var negative = amount < 0;
        var digits = Math.Abs((long)amount).ToString();
        if (digits.Length > 3)
        {
            var last3 = digits[^3..];
            var rest = digits[..^3];
            var groups = new List<string>();
            while (rest.Length > 2) { groups.Insert(0, rest[^2..]); rest = rest[..^2]; }
            if (rest.Length > 0) groups.Insert(0, rest);
            digits = string.Join(",", groups) + "," + last3;
        }
        return (negative ? "-₹" : "₹") + digits;
    }
}
