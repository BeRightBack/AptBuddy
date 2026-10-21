# AptBuddy

A friendly GUI for `apt` on Kali/Debian, built with **Avalonia UI on .NET 10**.

It wraps the everyday package loop: find a package, see what it needs and who needs
it, install / remove / hold it, upgrade everything, manage repositories, and clean
up leftovers — without memorizing `apt` incantations.

## Why this exists

Synaptic is fine, but this machine (Kali 2026.3 · apt 3.3.3 · sqv signature checking)
has a *bespoke* third-party repo story documented in [`../REPOS.md`](../REPOS.md):
keys must be fetched fresh (SHA1-signed bindings are rejected), fingerprints must be
verified manually, and the `signed-by=` entry always accompanies the source.

AptBuddy bakes that flow into the UI and **reuses the verified
[`../add-repo.sh`](../add-repo.sh)** for the actually-dangerous parts.

## Features

| Tab | What it does |
|---|---|
| **Packages** | Three views — **Installed / Available / All** (defaults to **Available**) — with a live (as-you-type) search and an **Apps only** toggle that hides libraries and dev packages (`lib*`, `gir1.2-*`, `python3-*`, `node-*`, `php-*`, kernel headers/modules, R/Haskell bindings, `-dev`, `-dbg`, `-doc`, `-common`, …; "All" always shows everything, and a **Show all N** button lifts the 1,000-row display cap). Master lists are loaded once into memory, so searching and switching views never re-spawns `apt`; all tabs load in parallel at startup. Rows show name, version (installed rows), summary and an installed ✓. Select → details from `apt-cache policy`/`show` (loaded concurrently + stale-guarded), dependency list (`depends`), installed dependents (`rdepends --installed`), then **Install / Reinstall / Remove / Hold** (elevated). **Install .deb…** installs a locally-downloaded package file via the file dialog (`apt-get install path.deb`, dependencies resolved from repos) — it also accepts **`.tar.gz` bundles of `.deb`s** (e.g. the upstream LibreOffice tarball): it extracts the archive and installs every `.deb` inside. Such locally-installed packages (e.g. `google-chrome-stable`) appear in the lists even though they're not in any repo. Every install/remove/autoremove action rebuilds the master lists, so views and counts are always live. |
| **Updates** | `apt list --upgradable` view, one-click **Upgrade all**, refresh lists (`apt update`). |
| **Repositories** | Read-only view of every source in `/etc/apt/sources.list(.d)` (both one-line `.list` and deb822 `.sources`). **Add repository…** wizard: fetch key → show fingerprint → you confirm → runs `add-repo.sh` elevated. |
| **Cleanup** | `autoremove` forecast + execution, and currently-held packages (`apt-mark showhold`). |

Every privileged command's output lands in the bottom **command log** pane.
Destructive actions (Remove / Upgrade all / autoremove) are red; primary actions accent-colored;
the package list is zebra-striped and shows a live result count (with a **Show all N** lift cap).

## Security model

- **Read-only** queries run as your user.
- **Anything that changes the system** runs through `pkexec` → the desktop's native
  PolicyKit password prompt. The app never sees or stores your password.
- **Adding a repository** fetches the vendor key, shows its fingerprint, and requires
  your explicit confirmation before anything is written — the same guarantee as the
  `MATCH` step in `add-repo.sh`, enforced in the dialog.

## Build & run

```bash
cd AptBuddy
dotnet build AptBuddy.slnx      # builds Core + tests + app (0 warnings expected)
dotnet test AptBuddy.slnx       # parser unit tests
dotnet run --project src/AptBuddy   # opens the window
dotnet run --project src/AptBuddy -- nmap   # opens straight into a search
```

**Zero-dependency desktop launcher** (no .NET runtime needed on the target box):

```bash
cd AptBuddy
dotnet publish src/AptBuddy -c Release -r linux-x64 --self-contained true -o publish
~/Documents/'Default Project'/aptbuddy.sh   # or launch from the app menu: "AptBuddy"
aptbuddy nmap                               # CLI search term works here too
```

The publish bundles the .NET 10 runtime (~100 MB). `aptbuddy.sh` (also wired up as
`~/.local/share/applications/aptbuddy.desktop`) refuses to open a second instance.
Re-run the publish step to pick up code changes.

**Packaging** (installable `.deb`, e.g. to ship to another Kali box):

```bash
~/Documents/'Default Project'/make-deb.sh
sudo dpkg -i ~/Documents/'Default Project'/aptbuddy_1.0-1_amd64.deb
```

Installs to `/opt/aptbuddy` with a system-wide `aptbuddy` command and menu entry.
Note: with the deb installed, *remove* the user-level `~/.local/share/applications/aptbuddy.desktop`
to avoid a duplicate "AptBuddy" entry in the menu.

- Requires the .NET 10 SDK (this machine: 10.0.401 from the Microsoft repo).
- Needs a desktop session with a **PolicyKit agent** (Xfce/KDE defaults) so `pkexec`
  can show a password prompt.

## Layout

```
src/AptBuddy.Core/      no-UI layer: process runner, apt parsers, AptService, RepoService
src/AptBuddy/           Avalonia app: MainWindow, AddRepoWindow, MVVM view models
tests/AptBuddy.Core.Tests/  xUnit tests for every parser
```

- `Models.cs` — records returned to the UI.
- `Parsers.cs` — pure functions turning `apt` output into those records (unit-tested).
- `AptService.cs` — command wiring; `RunPrivilegedAsync` is the single `pkexec` choke point.
- `RepoService.cs` — reads sources, and `FetchKeyFingerprintAsync` powers the dialog's
  verify step.

## Notes / limitations

- The **Add repository** dialog needs the path to `add-repo.sh`
  (defaults to `~/Documents/Default Project/add-repo.sh`, editable in the dialog).
- Headless (no PolicyKit agent / no tty): privileged actions fail with the captured
  `pkexec` error in the log — read-only browsing still works.
- Targets `net10.0` only — deliberate: .NET 8 LTS ends 2026-11, .NET 9 is already EOL,
  so this machine standardizes on .NET 10.