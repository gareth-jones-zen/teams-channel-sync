using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PyCompat;
using Tomlyn.Model;
using WinBackground;

namespace TeamsChannelSync;

/// <summary>One channel, and the folder its posts become.</summary>
public sealed record Source(string Name, string Team, string Channel, Regex Title, string Folder, string Filename = "{date}.md",
    string Format = NoteFormats.Markdown, long Recent = 10);

/// <summary>The settings file: who signs in, and which channels become which folders of notes.
///
///     base = "base-config.toml"               # optional: settings shared with other tools (see the README)
///     login_hint = "you@example.com"          # optional
///     tenant = "organizations"                # optional: a tenant id pins sign-in to one organisation
///     msal_cache = "..."                      # optional: share another tool's sign-in
///     state_file = "..."                      # optional
///     log_file = "..."                        # optional: where watch logs
///     after_run = ["python", "commit.py"]     # optional: run after a sync that wrote files (JSON on stdin)
///     [git]                                   # optional: commit what a sync wrote (see the README)
///     repo = "."
///     push = true
///     trailers = ["Co-Authored-By: Name &lt;name@example.com&gt;"]
///
///     [schedule]                              # optional: the shared scheduling settings (background-tools-lib)
///     every_minutes = 15                      # how often watch syncs (default 15)
///     hours = "07:00-19:00"                   # optional: sync only in this window (default all day)
///     days = "Mon-Fri"                        # optional: and on these days (default every day)
///     # watch_every_minutes is the old name for every_minutes: still read, with a line in the log
///
///     [[source]]
///     name = "exco"
///     team = "&lt;team id&gt;"                      # `teams-channel-sync channels` lists them
///     channel = "&lt;channel id&gt;"
///     title = '(?i)ExCo Notes\s*[-–]\s*(?P&lt;date&gt;\d{4}-\d{2}-\d{2})'
///     folder = "exco-notes"                   # relative to this file
///     filename = "{date}.md"                  # named groups from `title`
///     format = "bullets"                      # bullets | markdown
///     recent = 10                             # how many of the newest posts to look at
///
/// Paths may use ~ and %VARIABLES%; relative paths are relative to the settings file. The same file the Python
/// version read, read the same way: paths come out as the same text, so the state, log and locks are shared.</summary>
public sealed class Config
{
    public static readonly string[] Scopes = ["ChannelMessage.Read.All"];

    /// <summary>Every top-level setting (tables included). A base file's settings are used only when they're one of
    /// these: see the README, "Sharing settings with other tools".</summary>
    public static readonly string[] KnownKeys =
        ["login_hint", "tenant", "msal_cache", "state_file", "log_file", "after_run", "git", "watch_every_minutes", "schedule", "source"];
    public const string EnvConfig = "TEAMS_CHANNEL_SYNC_CONFIG";

    public const string Template = """
        # teams-channel-sync settings. See the README.
        # base = "base-config.toml"   # optional: settings shared with other tools
        # login_hint = "you@example.com"

        # One [[source]] per channel. `teams-channel-sync channels` lists your teams and channels with their ids.
        # [[source]]
        # name = "weekly-notes"
        # team = "<team id>"
        # channel = "<channel id>"
        # title = '(?i)Weekly notes\s*[-–]\s*(?P<date>\d{4}-\d{2}-\d{2})'
        # folder = "weekly-notes"
        # filename = "{date}.md"
        # format = "markdown"

        """;

    public required string Path { get; init; }
    public List<Source> Sources { get; } = [];
    public string? LoginHint { get; init; }
    public string Tenant { get; init; } = "organizations";
    public string? MsalCache { get; init; }
    public required string StateFile { get; init; }
    public required string LogFile { get; init; }
    public List<string> AfterRun { get; init; } = [];

    /// <summary>[git], or null: nothing committed. Commits what a sync wrote (with --hook, and in watch).</summary>
    public RepoCommit.GitSettings? Git { get; init; }
    public long WatchEveryMinutes { get; init; } = 15;

    /// <summary>The tool's own defaults for [schedule]: a watcher that syncs every 15 minutes, all day.</summary>
    public static readonly ScheduleSettings ScheduleDefaults = new() { EveryMinutes = 15 };

    /// <summary>[schedule] (a watcher): every_minutes is how often watch syncs; hours and days limit when.</summary>
    public ScheduleSettings Schedule { get; init; } = ScheduleDefaults;

    /// <summary>Old setting names still read: one log line each when watch starts.</summary>
    public IReadOnlyList<string> Deprecations { get; init; } = [];

    public static string? Environment(string name) =>
        string.IsNullOrEmpty(name) || name.Contains('\0') ? null : System.Environment.GetEnvironmentVariable(name);

    /// <summary>%APPDATA%\teams-channel-sync\config.toml (or under the home folder if APPDATA isn't set).</summary>
    public static string DefaultPath(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var appData = env("APPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(appData) ? PyPath.Home(env) : appData, "teams-channel-sync", "config.toml"));
    }

    /// <summary>%LOCALAPPDATA%\teams-channel-sync: the default state and log, and Python's lock folder.</summary>
    public static string LocalFolder(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var local = env("LOCALAPPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(local) ? PyPath.Home(env) : local, "teams-channel-sync"));
    }

    private static string PathSetting(string value, string folder, Func<string, string?> env)
    {
        var expanded = PyPath.Pure(PyPath.ExpandVars(PyPath.ExpandUser(value, env), env));
        return PyPath.IsAbsolute(expanded) ? expanded : PyPath.Resolve(System.IO.Path.Combine(folder, expanded));
    }

    /// <summary>12 hex characters from the settings path, ignoring case: the default state file's name.</summary>
    internal static string StateKey(string path) =>
        System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..12];

    /// <summary>load(path): the given path, else TEAMS_CHANNEL_SYNC_CONFIG, else the default. Throws
    /// FileNotFoundException if there's no file there, and PyValueError for settings that can't be used.</summary>
    public static Config Load(string? path = null, Func<string, string?>? env = null)
    {
        env ??= Environment;
        var given = !string.IsNullOrEmpty(path) ? path : env(EnvConfig) is { Length: > 0 } fromEnv ? fromEnv : DefaultPath(env);
        var full = PyPath.Resolve(given);
        if (!File.Exists(full))
            throw new FileNotFoundException($"No settings file at {full}. Run `teams-channel-sync config` to create one.");
        TomlTable raw;
        var local = LocalFolder(env);
        try
        {
            var text = PyText.ReadText(full);
            if (text.StartsWith('\ufeff'))
                throw new PyValueError("Invalid statement (at line 1, column 1)");   // tomllib refuses a byte order mark
            // With the base file it names (base = "base-config.toml"), if any: from here on, paths expand with the
            // base's [paths] as well as the environment.
            var read = BaseSettings.Read(text, full, env, KnownKeys);
            raw = read.Raw;
            env = read.Env;
        }
        catch (FormatException error)
        {
            throw new PyValueError(error.Message);   // the base file: its message names it
        }
        catch (Exception error) when (error is not (IOException or UnauthorizedAccessException or PyValueError))
        {
            throw new PyValueError($"{full}: {error.Message}");
        }
        var folder = System.IO.Path.GetDirectoryName(full)!;
        string? Text(string key) => raw.TryGetValue(key, out var value) ? Str(value, key) : null;
        string? OptionalPath(string key) => Text(key) is { Length: > 0 } value ? PathSetting(value, folder, env) : null;

        RepoCommit.GitSettings? git = null;
        if (raw.TryGetValue("git", out var gitSection))
        {
            try
            {
                git = RepoCommit.GitSettings.Read(gitSection, full, value => PathSetting(value, folder, env));
            }
            catch (FormatException error)
            {
                throw new PyValueError(error.Message);
            }
            if (!Directory.Exists(git.Repo))
                throw new PyValueError($"repo under [git] in {full} isn't a folder: {git.Repo}");
        }

        // [schedule], the shared scheduling settings; watch_every_minutes is its old name, still read (not both).
        var deprecations = new List<string>();
        var oldEvery = raw.TryGetValue("watch_every_minutes", out var every) ? (long?)Int(every, "watch_every_minutes") : null;
        ScheduleSettings schedule;
        if (raw.TryGetValue("schedule", out var section))
        {
            if (oldEvery is not null)
                throw new PyValueError($"{full}: watch_every_minutes and [schedule] are both set: keep every_minutes under [schedule].");
            if (section is not TomlTable table)
                throw new PyValueError($"{full}: schedule should be a [schedule] table.");
            try
            {
                schedule = ScheduleSettings.Parse(table, ScheduleDefaults, TaskKind.Watcher, $"{full}: [schedule]");
            }
            catch (FormatException error)
            {
                throw new PyValueError(error.Message);
            }
        }
        else if (oldEvery is { } minutes)
        {
            schedule = ScheduleDefaults with { EveryMinutes = (int)Math.Clamp(minutes, 1, 1440) };
            deprecations.Add("watch_every_minutes is the old name: move it to [schedule] every_minutes");
        }
        else
            schedule = ScheduleDefaults;

        var config = new Config
        {
            Path = full,
            Schedule = schedule,
            Deprecations = deprecations,
            Git = git,
            LoginHint = Text("login_hint"),
            Tenant = Text("tenant") ?? "organizations",
            MsalCache = OptionalPath("msal_cache"),
            StateFile = OptionalPath("state_file") ?? PyPath.Pure(System.IO.Path.Combine(local, $"state-{StateKey(full)}.json")),
            LogFile = OptionalPath("log_file") ?? PyPath.Pure(System.IO.Path.Combine(local, "teams-channel-sync.log")),
            AfterRun = raw.TryGetValue("after_run", out var hook) ? Strings(hook, "after_run") : [],
            WatchEveryMinutes = Math.Max(1, schedule.EveryMinutes ?? 15),
        };

        if (raw.TryGetValue("source", out var sources))
        {
            if (sources is not TomlTableArray tables)
                throw new PyValueError($"{full}: source must be a list of [[source]] tables.");
            var i = 0;
            foreach (var item in tables)
            {
                i++;
                var label = item.TryGetValue("name", out var named) ? Str(named, "name") : i.ToString(CultureInfo.InvariantCulture);
                var missing = new[] { "team", "channel", "title", "folder" }.Where(key => !Truthy(item, key)).ToList();
                if (missing.Count > 0)
                    throw new PyValueError($"[[source]] {label} in {full} has no {string.Join(", ", missing)}.");
                var format = item.TryGetValue("format", out var f) ? Str(f, "format") : NoteFormats.Markdown;
                if (!NoteFormats.Names.Contains(format))
                    throw new PyValueError($"[[source]] {label}: format must be one of {string.Join(", ", NoteFormats.Names)}.");
                var title = PyRegex.Compile(Str(item["title"], "title"));
                var filename = item.TryGetValue("filename", out var fn) ? Str(fn, "filename") : "{date}.md";
                var groups = PyRegex.GroupNames(title);
                foreach (var field in PyFormat.Fields(filename))
                    if (!groups.Contains(field))
                        throw new PyValueError($"[[source]] {label}: filename uses {{{field}}}, but title has no group named {field}.");
                config.Sources.Add(new Source(
                    Name: item.TryGetValue("name", out var n) ? Str(n, "name") : $"source{i}",
                    Team: Str(item["team"], "team"),
                    Channel: Str(item["channel"], "channel"),
                    Title: title,
                    Folder: PathSetting(Str(item["folder"], "folder"), folder, env),
                    Filename: filename,
                    Format: format,
                    Recent: item.TryGetValue("recent", out var recent) ? Int(recent, "recent") : 10));
            }
        }
        return config;
    }

    /// <summary>create_default(): the default settings file, from the template if it isn't there yet.</summary>
    public static string CreateDefault(Func<string, string?>? env = null)
    {
        var path = DefaultPath(env);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            PyStr.WriteText(path, Template.ReplaceLineEndings("\n"));
        }
        return path;
    }

    private static bool Truthy(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value switch
        {
            null => false,
            string s => s.Length > 0,
            bool b => b,
            long l => l != 0,
            double d => d != 0,
            TomlArray a => a.Count > 0,
            TomlTable t => t.Count > 0,
            _ => true,
        };

    /// <summary>Python's str() of a setting, for the settings that are text.</summary>
    private static string Str(object? value, string key) => value switch
    {
        string s => s,
        long l => l.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "True" : "False",
        _ => throw new PyValueError($"{key} must be text."),
    };

    /// <summary>Python's int() of a setting: whole numbers, decimals truncated, or digits in text.</summary>
    private static long Int(object? value, string key) => value switch
    {
        long l => l,
        double d when double.IsFinite(d) => (long)Math.Truncate(d),
        bool b => b ? 1 : 0,
        string s when long.TryParse(PyStr.Strip(s), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => throw new PyValueError($"{key} must be a whole number."),
    };

    private static List<string> Strings(object? value, string key) => value switch
    {
        TomlArray array => array.Select(item => Str(item, key)).ToList(),
        _ => throw new PyValueError($"{key} must be a list, as [\"python\", \"commit.py\"]."),
    };
}
