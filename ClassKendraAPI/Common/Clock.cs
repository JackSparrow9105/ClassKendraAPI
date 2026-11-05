namespace ClassKendraAPI.Common;

public static class Clock
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    public static DateTime IndiaNow => DateTime.UtcNow + Ist;
    public static DateOnly Today => DateOnly.FromDateTime(IndiaNow);
}