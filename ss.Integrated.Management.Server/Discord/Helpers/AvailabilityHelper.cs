namespace ss.Internal.Management.Server.Discord.Helpers;

/// <summary>
/// This is how we translate availability into days of the week. Availability is stored like this:
/// 00000000000000000000000000000000|00000000000000000000000000000000|00000000000000000000000000000000|00000000000000000000000000000000
/// Each string of 0's represents a day of the week, starting with Friday. Each bit is an hour in 24-hour format
/// LSB is 0:00, MSB is 23:00
/// </summary>
public static class AvailabilityHelper
{
    private static readonly string[] DayLabels = { "Viernes", "Sábado", "Domingo", "Lunes" };

    public static string GetDayBits(string fullAvailability, int dayIndex)
    {
        if (string.IsNullOrEmpty(fullAvailability)) return new string('0', 24);

        var parts = fullAvailability.Split('|');
        return parts.Length > dayIndex ? parts[dayIndex] : new string('0', 24);
    }

    public static string DayToName(int i) => i >= 0 && i < DayLabels.Length ? DayLabels[i] : "Día no preferente.";

    public static bool IsAvailable(string availability, int dayIndex, int hour)
    {
        if (string.IsNullOrWhiteSpace(availability)) return false;

        var days = availability.Split('|');
        if (dayIndex >= days.Length) return false;

        string dayStr = days[dayIndex];

        if (dayStr.Length < 24) return false;

        int index = dayStr.Length - 1 - hour;

        if (index < 0 || index >= dayStr.Length) return false;

        return dayStr[index] == '1';
    }

    public static string GetDayName(int dayIndex) => dayIndex switch
    {
        0 => "Viernes",
        1 => "Sábado",
        2 => "Domingo",
        3 => "Lunes",
        _ => "Otro día"
    };
}