using System.Text;
using PyCompat;

namespace TeamsChannelSync.Tests;

/// <summary>[schedule], the shared scheduling settings (background-tools-lib): a watcher, so every_minutes is how
/// often watch syncs, and hours and days limit when. watch_every_minutes is its old name, still read.</summary>
public sealed class ScheduleTests : IDisposable
{
    private readonly Golden.TempFolder temp = new();

    public void Dispose() => temp.Dispose();

    private string? Env(string name) => name == "LOCALAPPDATA" ? Path.Combine(temp.Path, "local") : null;

    private Config Load(string text)
    {
        var path = Path.Combine(temp.Path, "teams-channel-sync.toml");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return Config.Load(path, Env);
    }

    [Fact]
    public void With_no_schedule_it_syncs_every_15_minutes_all_day_as_before()
    {
        var config = Load("");
        Assert.Equal(15, config.WatchEveryMinutes);
        Assert.Equal(15, config.Schedule.EveryMinutes);
        Assert.True(config.Schedule.IsActive(new DateTime(2026, 11, 22, 3, 0, 0)));
        Assert.Empty(config.Deprecations);
    }

    [Fact]
    public void Schedule_is_read()
    {
        var config = Load("[schedule]\nevery_minutes = 5\nhours = \"07:00-19:00\"\ndays = \"Mon-Fri\"\n");
        Assert.Equal(5, config.WatchEveryMinutes);
        Assert.True(config.Schedule.IsActive(new DateTime(2026, 11, 23, 9, 0, 0)));    // a Monday morning
        Assert.False(config.Schedule.IsActive(new DateTime(2026, 11, 23, 20, 0, 0)));  // the evening
        Assert.False(config.Schedule.IsActive(new DateTime(2026, 11, 22, 9, 0, 0)));   // a Sunday
        Assert.Empty(config.Deprecations);
    }

    [Fact]
    public void The_old_name_still_works_with_a_line_for_the_log()
    {
        var config = Load("watch_every_minutes = 10\n");
        Assert.Equal(10, config.WatchEveryMinutes);
        Assert.Equal(10, config.Schedule.EveryMinutes);
        Assert.Contains("watch_every_minutes", Assert.Single(config.Deprecations));
    }

    [Fact]
    public void Both_forms_or_an_unknown_key_is_an_error()
    {
        Assert.Contains("both set", Assert.Throws<PyValueError>(() => Load("watch_every_minutes = 10\n[schedule]\nevery_minutes = 5\n")).Message);
        Assert.Contains("[schedule]", Assert.Throws<PyValueError>(() => Load("[schedule]\nevery_minute = 5\n")).Message);
        Assert.Throws<PyValueError>(() => Load("[schedule]\ndaily_at = \"07:00\"\n"));   // a watcher has no daily run
    }
}
