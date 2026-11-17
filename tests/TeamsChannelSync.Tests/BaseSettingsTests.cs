using System.Text;
using PyCompat;

namespace TeamsChannelSync.Tests;

/// <summary>A settings file can name a base file of settings shared with other tools: its [paths] are variables for
/// this tool's paths, its settings this tool knows fill in what the file doesn't set, and [git] applies only where the
/// file has the section too.</summary>
public sealed class BaseSettingsTests : IDisposable
{
    private readonly Golden.TempFolder temp = new();

    public BaseSettingsTests()
    {
        Directory.CreateDirectory(Path.Combine(temp.Path, "tools"));
        File.WriteAllText(Path.Combine(temp.Path, "tools", "base-config.toml"), """
            login_hint = "me@example.com"
            tenant = "contoso"
            msal_cache = "%SHARED%/msal-cache.json"
            time_zone = "GMT Standard Time"

            [paths]
            SHARED = "%LOCALAPPDATA%/shared"
            LOGS = "%SHARED%/logs"

            [git]
            repo = ".."
            push = true
            trailers = ["Co-Authored-By: Someone <someone@example.com>"]

            [review]
            file = "%SHARED%/pending.json"
            """, new UTF8Encoding(false));
    }

    public void Dispose() => temp.Dispose();

    private string? Env(string name) => name == "LOCALAPPDATA" ? Path.Combine(temp.Path, "local") : null;

    private Config Load(string text)
    {
        var path = Path.Combine(temp.Path, "tools", "teams-channel-sync.toml");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return Config.Load(path, Env);
    }

    private string Local(params string[] parts) => PyPath.Pure(Path.Combine([temp.Path, "local", .. parts]));

    [Fact]
    public void Shared_settings_and_path_variables_come_from_the_base()
    {
        var config = Load("base = \"base-config.toml\"\nlog_file = \"%LOGS%/teams-channel-sync.log\"\n");
        Assert.Equal("me@example.com", config.LoginHint);
        Assert.Equal("contoso", config.Tenant);
        Assert.Equal(Local("shared", "msal-cache.json"), config.MsalCache);
        Assert.Equal(Local("shared", "logs", "teams-channel-sync.log"), config.LogFile);
        Assert.Null(config.Git);   // no [git] here, so the base's doesn't apply
    }

    [Fact]
    public void The_files_own_settings_win()
    {
        var config = Load("base = \"base-config.toml\"\ntenant = \"own\"\nmsal_cache = \"cache.json\"\n");
        Assert.Equal("own", config.Tenant);
        Assert.Equal(PyPath.Resolve(Path.Combine(temp.Path, "tools", "cache.json")), config.MsalCache);
        Assert.Equal("me@example.com", config.LoginHint);
    }

    [Fact]
    public void Git_applies_only_where_the_file_has_the_section()
    {
        var config = Load("base = \"base-config.toml\"\n[git]\npush = false\n");
        Assert.NotNull(config.Git);
        Assert.Equal(PyPath.Resolve(temp.Path), config.Git!.Repo);
        Assert.False(config.Git.Push);
        Assert.Equal(["Co-Authored-By: Someone <someone@example.com>"], config.Git.Trailers);
    }

    [Fact]
    public void Without_a_base_nothing_changes()
    {
        var config = Load("login_hint = \"x@example.com\"\n");
        Assert.Equal(("x@example.com", "organizations", null), (config.LoginHint, config.Tenant, config.MsalCache));
        Assert.Null(config.Git);
    }

    [Fact]
    public void A_missing_base_is_a_settings_error()
    {
        var error = Assert.Throws<PyValueError>(() => Load("base = \"nope.toml\"\n"));
        Assert.Contains("doesn't exist", error.Message);
        Assert.Contains("nope.toml", error.Message);
    }
}
