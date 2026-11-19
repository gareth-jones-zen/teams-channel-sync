# teams-channel-sync

Keeps a folder of markdown files in step with a Teams channel's posts. Written for a weekly
"ExCo Notes - 2026-10-09" post, it works for any channel where the posts you want can be told
apart by their titles: weekly notes, release notes, a team digest.

- **Each post is filed by the date in its title**, never the date it went up. Posts whose titles
  don't match (notices, chatter, replies) get no file.
- **Authors edit posts after publishing.** A post edited since its file was written replaces the
  file, if the text changed.
- **It never overwrites a file it didn't write.** A file already there (copied in by hand, say) is
  taken on only if it matches the post. Otherwise it's reported, and `adopt` takes it on as it stands.

## Install

Windows (x64) only; nothing else is needed.

1. Download `teams-channel-sync-<version>-win-x64.zip` from [Releases](https://github.com/gareth-jones-zen/teams-channel-sync/releases).
2. Unzip it to a folder that will stay put, such as `%LOCALAPPDATA%\Programs\teams-channel-sync`. The scheduled task (below) runs the program from there.
3. Run the commands below from that folder, or add it to your `PATH`.

It holds two programs: `teams-channel-sync.exe`, for a console, and `teams-channel-sync-background.exe`, the same program with no window, which the logon task runs.

**To upgrade while the watcher runs:** rename `teams-channel-sync.exe` and `teams-channel-sync-background.exe` to `.old` (Windows lets you rename a program that's running, but not overwrite it), unzip the new release into the same folder, then run `teams-channel-sync autostart restart`. That stops the watcher, waits for it to exit and starts the new version, keeping the task as it was. Delete the `.old` files afterwards. If the old watcher hasn't exited after 30 seconds, nothing is started and it carries on as it was.

## Getting started

```
teams-channel-sync config       # create the settings file and say where it is
teams-channel-sync sign-in      # once: a browser opens to sign in to Microsoft
teams-channel-sync ids "<link>" # a channel's team and channel ids, from Teams' Copy link
```

**Finding the ids:**
- In Teams, open the channel's **…** menu, choose **Copy link**, and pass it to `ids`. It needs no sign-in.
- `teams-channel-sync channels` lists every team and channel you're in instead. The first time, it asks you to approve reading team and channel names.

Then add a `[[source]]` to the settings file:

```toml
base = "base-config.toml"   # optional: settings shared with other tools
login_hint = "you@example.com"

[[source]]
name = "exco"
team = "<team id>"
channel = "<channel id>"
title = '(?i)ExCo Notes\s*[-–]\s*(?P<date>\d{4}-\d{2}-\d{2})'   # named groups name the file
folder = "C:/Users/you/Documents/ExCo notes"                      # or relative to the settings file
filename = "{date}.md"
format = "bullets"        # bullets | markdown
recent = 10               # how many of the newest posts to look at
```

```
teams-channel-sync sync --dry-run   # what it would write
teams-channel-sync sync             # write new and edited notes
teams-channel-sync check            # compare the recent posts with the files, line by line
teams-channel-sync adopt            # take on hand-copied files as they stand
```

`sync --json` prints what was written.

## Sharing settings with other tools: `base`

Several tools on the shared library can keep common settings in one file, named at the top of each tool's settings
(`base = "base-config.toml"`, relative to the settings file).

- **The base's `[paths]` are variables** for this tool's paths (`msal_cache`, `state_file`, `log_file`, a source's
  `folder`, `[git]`'s `repo`), looked up before the environment: with `EA_LOCAL = "%LOCALAPPDATA%/claude-ea"` there,
  `log_file = "%EA_LOCAL%/teams-channel-sync.log"` works here.
- **Its other settings that this tool knows and doesn't set itself are used**, such as `login_hint`, `tenant` and
  `msal_cache`; settings it doesn't know are left out, so one base serves tools that use different parts of it.
- **A table such as `[git]` applies only if this file has that section too**, even empty (`[git]` on its own line):
  the base's keys fill it in, this file's own keys winning.
- A base that's missing or can't be read is refused like any other settings problem. The loader is
  [background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib)'s `PyCompat.BaseSettings`.

## Keeping it current: `watch` and `autostart`

```
teams-channel-sync autostart on       # watch at every logon, and start it now
teams-channel-sync autostart status
teams-channel-sync autostart off
teams-channel-sync watch              # the same loop, in a console
```

`watch` syncs every source every `watch_every_minutes` (default 15), so notes posted on a Friday
evening are filed by the time anyone looks.

- **It polls.** Graph can only push channel posts to a public web endpoint, so it asks. A sync with nothing new costs one Graph call per source.
- **One watcher per settings file.** A second one exits at once, with code 3. A sync run by hand waits for one already in progress.
- **The settings are re-read when they change**, so a new source needs no restart.
- **It never opens a browser.** A lapsed sign-in is logged and notified once, and it carries on once someone runs `sign-in`.
- **It logs `alive` hourly**, to `%LOCALAPPDATA%\teams-channel-sync\teams-channel-sync.log` by default (set `log_file`). A silent log means a stuck watcher, not a quiet channel.

**`autostart on`** registers a Task Scheduler task, `teams-channel-sync watch`:
- it runs as you, only while you're logged on, with no admin rights;
- it catches up a missed start, restarts the watcher after a crash, and is never stopped for running too long;
- it runs `teams-channel-sync-background.exe` directly, so there's no window, and stopping the task stops the watcher.

## Committing to git: `[git]`

If the notes folder is in a git repository, `[git]` commits what each sync wrote:

```toml
[git]
repo = "."                                   # the repository's top (relative to this file)
push = true                                  # optional (default false): push after each commit
trailers = ["Co-Authored-By: Name <name@example.com>"]   # optional: the message's last lines
```

It commits after any sync that wrote a file, as the hook runs (always in `watch`, and with `--hook` for `sync`), before
the hook. **Only the files written are committed**, and of those only the ones git sees as changed, so nothing else
changed or staged in the repository is swept up. The message names each file by its folder and name, in git status's
order:

```
Ingest exco-notes 2026-10-16, exco-notes 2026-10-23

Filed automatically by teams-channel-sync (tools/teams-channel-sync.toml).

Co-Authored-By: Name <name@example.com>
```

**A commit that fails** (a hook in the repository refusing it, say) is logged and notified ("Filed, not committed"); the
files stay where they were written. **A push that fails** is only logged; the commit stays. `commit = false` keeps the
section but commits nothing; with no section, nothing is committed. It's
[background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib)'s `RepoCommit`: git is run from the
PATH, every path is literal, another git holding the index is waited for briefly, and the log has counts and the commit
id, never contents.

**git not set up is quiet, not a failure**: with `[git]` but no git installed, or a `repo` that isn't a git repository, or a folder *inside* one rather than its top (nothing is committed into a repository the folder only sits in), the notes are written as usual, one line says "git: not committing: ..." (once a process: once a logon for the watcher; after `git init`, `autostart restart` to start committing), and nothing is notified. **No upstream, no push**: with `push = true` and a branch with no upstream (no remote), the commit stays and no push is tried; one log line says so.

## The after-run hook

To do anything else with what's filed, such as forward it, set `after_run`:

```toml
after_run = ["powershell", "-NoProfile", "-File", "commit.ps1"]   # any command, and its arguments
```

It runs after any sync that wrote a file: always in `watch`, and with `--hook` for `sync`. It runs from the settings file's folder, with no window, and gets a JSON report on stdin:

```json
{"config": "C:\\…\\config.toml", "written": ["C:\\…\\exco-notes\\2026-10-23.md"],
 "failed": [], "unmatched": [], "summary": "1 written, 0 adopted, 0 untracked, 7 unchanged"}
```

It's the same shape as [doc-filer](https://github.com/gareth-jones-zen/doc-filer)'s hook, so one script can serve both. Here `written` holds full paths. Its output goes to the log.

## What the posts need to look like

It's written for a series of posts that all follow one template, such as weekly notes. A post is filed only if:

- **it's a new post, not a reply.** Replies have no subject, so they're never filed;
- **it has a subject** (in Teams, the "Add a subject" line when starting a post), **and the subject matches `title`.** The pattern's named groups fill `filename`, so put what names the file, usually a date, in the subject: `ExCo Notes - 2026-10-09` with `title = '(?i)ExCo Notes\s*[-–]\s*(?P<date>\d{4}-\d{2}-\d{2})'` and `filename = "{date}.md"` gives `2026-10-09.md`. A post whose subject doesn't match (a notice, chatter) is skipped;
- **it's among the channel's `recent` newest posts** (10 by default), counting replies and edits as activity. Deleted posts are skipped.

**The body, for `format = "bullets"`** (the default template): **one nested bulleted list**, using Teams' own bullets or numbering. For example, a post with the subject `ExCo Notes - 2026-10-09` and this body:

```text
This week's notes:                  ← dropped (not in the list)
• Consumer
    ◦ Orders up 4% on last week
    ◦ Churn flat
• Business
    ◦ Two large wins, see https://example.com/wins
Have a good weekend!                ← dropped
```

is filed as `2026-10-09.md`:

```markdown
- Consumer
  - Orders up 4% on last week
  - Churn flat
- Business
  - Two large wins, see https://example.com/wins
```

- **Only the list is kept.** Headings, paragraphs and sign-offs outside it are dropped.
- **Numbered lists come out as bullets**, nested by indent, two spaces a level.
- **Bold, italic and other formatting are dropped**, and a link keeps only its text. Line breaks inside a bullet become spaces.
- **Emoji typed as text are kept; emoji added from Teams' emoji picker are lost** (they arrive as images).
- **Put a bullet's own text before its sub-list.** Text after a sub-list, inside the same bullet, is dropped.

**For anything else, use `format = "markdown"`**, which keeps the whole post (see below). The subject rules are the same.

**Edits are picked up:** if the author edits a post after it's filed, the file is rewritten, but only if the converted text changed.

## Formats

- **`bullets`:** only the post's lists, as plain nested bullets with a two-space indent. A header or sign-off outside the list is dropped. Entities are converted, emphasis dropped, emoji and wording kept, and links reduced to their text. This is for posts written as one nested list.
- **`markdown`:** the whole post, with paragraphs, line breaks, nested and numbered lists, bold, italic, and links as `[text](url)`.

## Sign-in and permissions

It signs in as Microsoft's "Graph Command Line Tools" app, so there's no app to register.

- **Reading posts needs `ChannelMessage.Read.All`.** That usually needs an admin's approval: request it on the "Approval required" screen at sign-in. Some organisations have already granted it for this app.
- **`channels` also needs `Team.ReadBasic.All` and `Channel.ReadBasic.All`,** which users can usually approve themselves.

**Settings:**
- `msal_cache` points at another tool's token cache, to share its sign-in.
- `tenant` pins sign-in to one organisation.
- `state_file` sets where it records what it has written (default `%LOCALAPPDATA%\teams-channel-sync\`).

## What it can't do yet

- **Notes posted as a reply have no title**, so they're skipped.

## Building

It's .NET 11, published as Native AOT. It needs [background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib)
checked out beside this repository (or set the `BackgroundToolsLib` property to a checkout elsewhere), then:

```
dotnet test -c Release
dotnet publish src/TeamsChannelSync.Console -c Release -r win-x64 -o out
dotnet publish src/TeamsChannelSync.Background -c Release -r win-x64 -o out
```

A tag `v*` builds and publishes a release.
