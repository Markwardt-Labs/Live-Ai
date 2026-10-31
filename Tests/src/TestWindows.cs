namespace Markwardt.LiveAi.Tests;

public static class TestWindows
{
    public static IReadOnlyList<PromptWindow> Claude(double? session = null, DateTimeOffset? sessionResets = null, double? week = null, DateTimeOffset? weekResets = null)
    {
        DateTimeOffset readAt = DateTimeOffset.UtcNow;
        List<PromptWindow> windows = [];

        if (session is { } now)
        {
            windows.Add(new PromptWindow { Name = "Session", Used = now, Length = TimeSpan.FromHours(5), ReadAt = readAt, ResetsAt = sessionResets });
        }

        if (week is { } all)
        {
            windows.Add(new PromptWindow { Name = "Week", Used = all, Length = TimeSpan.FromDays(7), ReadAt = readAt, ResetsAt = weekResets });
        }

        return windows;
    }

    public static bool Used(this IReadOnlyList<PromptWindow> windows, string name, double above)
        => windows.Any(window => window.Name == name && window.Used > above);
}
