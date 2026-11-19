using System.ComponentModel;
using System.Text;
using PyCompat;
using RepoCommit;
using WinBackground;

namespace TeamsChannelSync;

/// <summary>One locked sync, then, if it wrote anything, [git]'s commit and the after-run hook.
///
/// [git] commits exactly the files written (those git sees as changed), as "Ingest {folder} {name}, ...", and
/// notifies "Filed, not committed" if it can't. The hook is for anything else a repo wants done. It gets a JSON
/// report on stdin, the same shape doc-filer gives its hook, so one script can serve both:
///
///     {"config": "...", "written": ["C:\\...\\2026-10-23.md"], "failed": [], "unmatched": [], "summary": "1 written"}
///
/// It runs from the settings file's folder, with no window.</summary>
public static class Run
{
    public static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(1800);
    public const double LockTimeoutSeconds = 600;

    /// <summary>The locks per settings file, named exactly as the Python version named them, so a .NET sync and a
    /// Python watcher exclude each other.</summary>
    public static string WatchLockName(Config config) => $"teams-channel-sync-watch-{Locks.Key(config.Path)}";

    public static string RunLockName(Config config) => $"teams-channel-sync-run-{Locks.Key(config.Path)}";

    /// <summary>The hook's stdin: json.dumps({...}) on one line.</summary>
    public static string Payload(Config config, Report report)
    {
        var b = new StringBuilder("{\"config\": ");
        PyJson.Quote(b, config.Path);
        b.Append(", \"written\": ").Append(PyJson.StringList(report.Written));
        b.Append(", \"failed\": [], \"unmatched\": [], \"summary\": ");
        PyJson.Quote(b, report.Summary());
        return b.Append('}').ToString();
    }

    public static void AfterRun(Config config, Report report, Action<string> log, Func<IReadOnlyList<string>, string, string, TimeSpan, Completed>? runner = null)
    {
        runner ??= (args, input, cwd, timeout) => args.Count == 0
            ? throw new ArgumentException("no program to run")
            : Shell.Run(args[0], args.Skip(1), timeout, stdin: input, workingDirectory: cwd, utf8: true);
        var payload = Payload(config, report);
        log($"after-run hook: {string.Join(' ', config.AfterRun)}");
        try
        {
            var done = runner(config.AfterRun, payload, Path.GetDirectoryName(config.Path)!, HookTimeout);
            foreach (var line in PyStr.SplitLines(done.StandardOutput + done.StandardError))
            {
                var text = PyStr.Strip(line);
                if (text.Length > 0)
                    log($"  hook: {text}");
            }
            if (done.ExitCode != 0)
                log($"after-run hook exited {done.ExitCode}");
        }
        catch (Win32Exception error)
        {
            log($"after-run hook failed: {Shell.WinErrorText(error)}");
        }
        catch (ShellTimeoutException error)
        {
            log($"after-run hook failed: {error.PythonMessage}");   // subprocess.TimeoutExpired's own wording
        }
        catch (Exception error)
        {
            log($"after-run hook failed: {error.Message}");
        }
    }

    /// <summary>"exco-notes 2026-10-23": a file's folder and its name without the extension, as a commit names it.</summary>
    public static string Named(string repoPath)
    {
        var parts = repoPath.Split('/');
        var name = parts[^1];
        var dot = name.LastIndexOf('.');
        return $"{(parts.Length > 1 ? parts[^2] : "")} {(dot > 0 && dot < name.Length - 1 ? name[..dot] : name)}";
    }

    /// <summary>[git]: commit what the sync wrote, only the files git sees as changed, in git status's order:
    /// "Ingest exco-notes 2026-10-16, exco-notes 2026-10-23", then "Filed automatically by teams-channel-sync
    /// ({settings})." and the trailers. If the commit fails, a notification ("Filed, not committed"); a push that
    /// fails is only logged. Null when there's no [git] (or commit = false). git not set up (not installed, not a
    /// repository, or a folder inside one) is Skipped: one log line a process, no notification.</summary>
    public static CommitResult? Commit(Config config, Report report, Action<string> log, Action<string, IReadOnlyList<string>> notify)
    {
        if (config.Git is not { Commit: true } git)
            return null;
        var committer = git.Open(log);
        if (committer.Skipped is { } why)
            return CommitResult.Skip(why);   // the notes are filed; the library has said why, once
        var names = "";
        void Tell() => notify("Filed, not committed", [PyStr.Head(names, 300), $"See {System.IO.Path.GetFileName(config.LogFile)}."]);
        try
        {
            var paths = committer.Changed(report.Written.Where(File.Exists));
            if (paths.Count == 0)
            {
                log("git: nothing to commit");
                return CommitResult.Nothing;
            }
            names = string.Join(", ", paths.Select(Named));
            var settings = committer.RepoPath(config.Path) ?? System.IO.Path.GetFileName(config.Path);
            var result = committer.Commit(Committer.Message($"Ingest {names}", $"Filed automatically by teams-channel-sync ({settings})."),
                paths, trailers: git.Trailers, push: git.Push);
            if (result.Error is not null)
                Tell();
            return result;
        }
        catch (Exception error) when (error is GitException or InvalidOperationException or Win32Exception or TimeoutException or IOException)
        {
            log($"git: commit failed: {error.Message}");
            if (names.Length == 0)
                names = string.Join(", ", report.Written.Select(System.IO.Path.GetFileNameWithoutExtension));
            Tell();
            return CommitResult.Failed(error.Message);
        }
    }

    /// <summary>run(): the sync under the run lock (TimeoutException if another doesn't finish within ten minutes),
    /// then, if wanted, not a dry run and something was written, [git]'s commit and the hook (if after_run is set).</summary>
    public static Report Once(Config config, IGraph graph, bool dryRun = false, bool adopt = false, bool hook = false,
        string? only = null, Action<string>? log = null, Func<IReadOnlyList<string>, string, string, TimeSpan, Completed>? runner = null,
        Action<string, IReadOnlyList<string>>? notify = null)
    {
        log ??= _ => { };
        Report report;
        using (var held = new NamedLock(RunLockName(config)))
        {
            if (!held.Acquire(LockTimeoutSeconds))
                throw new TimeoutException($"Another sync for {config.Path} didn't finish within {LockTimeoutSeconds / 60:0} minutes.");
            report = Notes.Sync(config, graph, dryRun, adopt, only, log);
        }
        if (hook && !dryRun && report.Written.Count > 0)
        {
            Commit(config, report, log, notify ?? Toast.Notify);
            if (config.AfterRun.Count > 0)
                AfterRun(config, report, log, runner);
        }
        return report;
    }
}
