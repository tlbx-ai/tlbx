using System.Globalization;

namespace Ai.Tlbx.MidTerm.Services.Boards;

public sealed record BoardCalendarDay(string Date, int Day, bool InMonth, int IsoYear, int Week, int Quarter);
public sealed record BoardCalendarMonth(int Year, int Month, List<BoardCalendarDay> Days);

internal static class BoardCalendar
{
    internal static BoardCalendarMonth Month(string? value)
    {
        var month = value ?? DateTime.Today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (!DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)
            || first.Year is < 2 or > 9998)
            throw new ArgumentException("Month must use yyyy-MM with a year between 2 and 9998.");
        var offset = ((int)first.DayOfWeek + 6) % 7;
        var start = first.AddDays(-offset);
        var days = new List<BoardCalendarDay>(42);
        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            var timestamp = date.ToDateTime(TimeOnly.MinValue);
            days.Add(new(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), date.Day,
                date.Month == first.Month, ISOWeek.GetYear(timestamp), ISOWeek.GetWeekOfYear(timestamp), (date.Month - 1) / 3 + 1));
        }
        return new(first.Year, first.Month, days);
    }
}
