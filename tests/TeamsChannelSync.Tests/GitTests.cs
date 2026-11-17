using System.Text;
using System.Text.Json.Nodes;
using PyCompat;

namespace TeamsChannelSync.Tests;

/// <summary>[git]: what's committed after a sync is exactly what the EA's after-run hook (doc_filer_hook.py, which
/// committed for this tool until [git]) did for the same report. Golden/hooks.json is that hook run, unchanged, on
/// each case in a temporary repository: its commits (message, name-status), git status after and its notifications.
/// Two lines differ, by design: the message names this tool and its settings rather than the hook ("Filed
/// automatically by teams-channel-sync (tools/teams-channel-sync.toml)." for "... by doc-filer
/// (tools/doc_filer_hook.py)."), and a notification points at this tool's log rather than doc-filer's.</summary>
public sealed class GitTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("tcs-git-").FullName;
    private readonly List<string> log = [];
    private readonly List<(string Title, IReadOnlyList<string> Lines)> notes = [];

    public void Dispose() => GitRepo.Delete(root);

    private static JsonArray Golden() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "hooks.json")))!.AsArray();

    public static TheoryData<string> Cases() => [.. Golden().Select(g => (string)g!["name"]!)];

    private static Config Settings(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return Config.Load(path);
    }

    /// <summary>The EA's layout: the settings in tools/ of the repository.</summary>
    private static Config EaSettings(string repo) => Settings(Path.Combine(repo, "tools", "teams-channel-sync.toml"),
        "state_file = \"state.json\"\nlog_file = \"teams-channel-sync.log\"\n\n[git]\nrepo = \"..\"\n"
        + "trailers = [\"Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>\"]\n");

    [Theory]
    [MemberData(nameof(Cases))]
    public void Commits_are_the_python_hooks(string name)
    {
        var golden = Golden().Single(g => (string)g!["name"]! == name)!.AsObject();
        var repo = new GitRepo(Path.Combine(root, "repo"), golden["committed"] as JsonObject, golden["failing_hook"] is not null);
        repo.Write(golden["written"] as JsonObject);
        var config = EaSettings(repo.Path);
        var report = new Report();
        foreach (var path in golden["report"]!["written"]!.AsArray())
            report.Written.Add(repo.File(((string)path!)["<repo>/".Length..]));

        Run.Commit(config, report, log.Add, (title, lines) => notes.Add((title, lines)));

        var expected = golden["commits"]!.ToJsonString().Replace("by doc-filer (tools/doc_filer_hook.py).",
            "by teams-channel-sync (tools/teams-channel-sync.toml).", StringComparison.Ordinal);
        Assert.Equal(expected, repo.Commits().ToJsonString());
        Assert.Equal(golden["status"]!.ToJsonString(), repo.Status().ToJsonString());
        Assert.Equal(golden["notes"]!.ToJsonString().Replace("See doc-filer.log.", "See teams-channel-sync.log.", StringComparison.Ordinal),
            new JsonArray([.. notes.Select(n => (JsonNode?)new JsonArray(JsonValue.Create(n.Title),
                new JsonArray([.. n.Lines.Select(l => (JsonNode?)JsonValue.Create(l))])))]).ToJsonString());
    }

    [Fact]
    public void A_sync_commits_only_with_hook_not_dry_run_and_something_written()
    {
        var repo = new GitRepo(Path.Combine(root, "repo"));
        var config = Settings(Path.Combine(repo.Path, "settings.toml"),
            RunTests.Settings("[git]\nrepo = \".\"\ntrailers = [\"Co-Authored-By: X <x@example.com>\"]\n"));
        var post = RunTests.Messages(("ExCo Notes - 2026-10-09", "2026-10-12T08:00:00Z", "<ul><li>a</li></ul>"));
        Run.Once(config, new FakeGraph(_ => post), hook: false, log: log.Add, notify: (t, l) => notes.Add((t, l)));   // not asked for
        Run.Once(config, new FakeGraph(_ => RunTests.Messages(("ExCo Notes - 2026-10-16", "2026-10-19T08:00:00Z", "<ul><li>b</li></ul>"))),
            dryRun: true, hook: true, log: log.Add, notify: (t, l) => notes.Add((t, l)));                              // a dry run
        Assert.Empty(repo.Commits());

        Run.Once(config, new FakeGraph(_ => RunTests.Messages(("ExCo Notes - 2026-10-16", "2026-10-19T08:00:00Z", "<ul><li>b</li></ul>"))),
            hook: true, log: log.Add, notify: (t, l) => notes.Add((t, l)));
        var commit = Assert.Single(repo.Commits());
        Assert.Equal("Ingest notes 2026-10-16\n\nFiled automatically by teams-channel-sync (settings.toml).\n\nCo-Authored-By: X <x@example.com>",
            (string)commit!["message"]!);
        Assert.Equal("[\"A\\tnotes/2026-10-16.md\"]", commit["files"]!.ToJsonString());
        Assert.Contains("?? notes/2026-10-09.md", repo.Status().Select(s => (string)s!));   // written without --hook: left alone
        Assert.Contains(log, l => l.StartsWith("git: committed 1 path(s) as ", StringComparison.Ordinal));

        Run.Once(config, new FakeGraph(_ => RunTests.Messages(("ExCo Notes - 2026-10-16", "2026-10-19T08:00:00Z", "<ul><li>b</li></ul>"))),
            hook: true, log: log.Add, notify: (t, l) => notes.Add((t, l)));                                             // nothing new
        Assert.Single(repo.Commits());
        Assert.Empty(notes);
    }

    [Fact]
    public void No_git_section_commits_nothing()
    {
        var repo = new GitRepo(Path.Combine(root, "repo"));
        var config = Settings(Path.Combine(repo.Path, "settings.toml"), RunTests.Settings());
        Assert.Null(config.Git);
        Run.Once(config, new FakeGraph(_ => RunTests.Messages(("ExCo Notes - 2026-10-09", "2026-10-12T08:00:00Z", "<ul><li>a</li></ul>"))),
            hook: true, log: log.Add, notify: (t, l) => notes.Add((t, l)));
        Assert.Empty(repo.Commits());
        Assert.Null(Run.Commit(config, new Report(), log.Add, (t, l) => notes.Add((t, l))));
    }

    [Fact]
    public void Named_is_the_folder_and_the_stem() =>
        Assert.Equal(["exco-notes 2026-10-23", " top", "a b.c", "a .hidden"],
            [Run.Named("reports/exco-notes/2026-10-23.md"), Run.Named("top.md"), Run.Named("x/a/b.c.md"), Run.Named("a/.hidden")]);

    [Theory]
    [InlineData("[git]\nrepo = \".\"\nrepository = \"x\"\n", "repository under [git] in ")]
    [InlineData("[git]\npush = true\n", "needs repo")]
    [InlineData("[git]\nrepo = \"no-such-folder\"\n", "isn't a folder: ")]
    [InlineData("[git]\nrepo = \".\"\npush = \"yes\"\n", "push under [git] in ")]
    public void Bad_settings_are_refused(string toml, string message) =>
        Assert.Contains(message, Assert.Throws<PyValueError>(() => Settings(Path.Combine(root, "bad.toml"), toml)).Message);
}
