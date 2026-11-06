# Verification Report — Task Manager Lite v2.2.0

| | |
|---|---|
| **Version checked** | 2.2.0 (release tag `v2.2.0`, commit `923522d`) |
| **Date** | 2026-11-06 |
| **Test PC** | Windows 10 Home 22H2 (build 19045), 64-bit, 4 logical processors, 16 GB RAM, .NET Framework 4.8 |
| **Method** | Two independent passes: **Pass 1** checks the files, build and published copies; **Pass 2** downloads the real release from GitHub and runs it from a clean folder. |
| **Result** | ✅ **Passed.** 4 documentation/policy issues were found and fixed (none in the program itself). |

---

## Pass 1 — Files, build and published copies

| # | Check | Result |
|---|---|---|
| 1 | Source compiles with the Windows built-in C# compiler at the highest warning level (`/warn:4`) | ✅ 0 errors, 0 warnings |
| 2 | Source is identical in all four places: working copy, GitHub folder, release package, and GitHub tag `v2.2.0` | ✅ identical (line endings ignored) |
| 3 | Version is the same everywhere: source `2.2.0`, program file `2.2.0.0`, packaged program `2.2.0.0` | ✅ |
| 4 | The packaged program is the same file as the installed one, and newer than the source (no stale build) | ✅ |
| 5 | RAR archive integrity test (WinRAR `t`) | ✅ OK, with a 3% recovery record |
| 6 | ZIP archive extracts completely | ✅ 11 files |
| 7 | All SHA-256 checksums in `SHA256SUMS.txt` match the files | ✅ 3/3 |
| 8 | The files downloaded from the GitHub release are byte-identical to the local ones | ✅ zip, rar and checksums |
| 9 | Git: local `main` = GitHub `main`, nothing uncommitted, tags `v2.1.0`→`db0b667` and `v2.2.0`→`923522d` | ✅ |
| 10 | Nothing private is tracked in the repository (`.exe`, `settings.ini`, `data/`) | ✅ none (15 tracked files) |
| 11 | Repository is public, licence detected by GitHub as **MIT**, `v2.2.0` is the latest release, no drafts | ✅ |
| 12 | Relative links in all Markdown files point to existing files | ✅ |
| 13 | Secret and personal-data scan of all text files (tokens, keys, passwords, email addresses, personal folder paths) | ✅ nothing found |
| 14 | Screenshots reviewed by eye for private information | ✅ only process names and system figures (the Settings screenshot with a user-folder path was left out on purpose) |

## Pass 2 — Real download, clean install

The `TaskManagerLite-v2.2.0-win-x64.zip` asset was **downloaded from GitHub**, extracted into an empty folder with no settings, and started as a new user would.

| # | Check | Result |
|---|---|---|
| 1 | Starts and shows version **v2.2.0** | ✅ |
| 2 | Opens on the **Processes** page with live data | ✅ (screenshot taken after 4 s) |
| 3 | Its own memory use | ✅ 45–50 MB working set |
| 4 | Its own CPU use during the first 14 s (including start-up) | ✅ 2.3 s of CPU time |
| 5 | Creates `settings.ini` on first run, with the default priority app `chrome → Above normal` | ✅ |
| 6 | Does **not** rebuild itself after extraction (no `.new.exe` / `.old.exe` left over) | ✅ |
| 7 | Closes cleanly | ✅ exit code 0 |

## Feature tests (during development and real use on the test PC)

| Feature | How it was checked | Result |
|---|---|---|
| CPU % for every process, including Windows services, without admin | Screenshots show CPU % and priority for `System`, `dwm`, `svchost` and others (version 1 couldn't read these) | ✅ |
| Ask AI (Claude Code) | Two real questions answered in 13–15 s, using live process data | ✅ |
| Ask AI cannot use tools | With the same blocked-tools settings the app uses, Claude was told to run `Get-Date` and couldn't (no shell tool available). The app also blocks agent and messaging tools. | ✅ |
| Self-update | Raised the version in the source; v2.0.0 rebuilt itself into v2.0.1 and restarted on its own | ✅ |
| Priority apps | A rule for Notepad raised it from Normal to Above normal on the next refresh, and it was logged | ✅ |
| Trim background apps | Real use: 26 processes trimmed, about 1.6–1.8 GB moved out of RAM | ✅ |
| Clear cached memory (admin restart) | Real use: 6.7 GB of cached memory cleared | ✅ |
| Junk-file cleanup | Real use: 647 files removed (about 9 GB); 401 in-use or protected files skipped safely | ✅ |
| Light / dark themes, dark title bar | Screenshots of every page | ✅ |

## Issues found and fixed

| # | Issue | Fix |
|---|---|---|
| 1 | `SECURITY.md` asks people to report problems privately, but **private vulnerability reporting was turned off** on the repository | Turned on (GitHub → Security) |
| 2 | One research link in `docs/DESIGN-PROMPT.md` was dead (HTTP 410 Gone) | Removed |
| 3 | The User Guide's update example still used version 2.1.0 | Changed to `2.2.0 → 2.2.1` |
| 4 | `docs/DESIGN-PROMPT.md` didn't yet mention the Free up memory page, `--page 4`, `--purge` or the Priority apps wording | Updated |

These are documentation and settings fixes only. The program and the v2.2.0 downloads are unchanged, so their checksums stay valid. The release package contains the earlier copies of these two docs.

## Known limitations

- **Not code-signed**: Windows SmartScreen may warn on first start. Users can build it themselves with `build.bat`.
- **Tested on Windows 10 only**: Windows 11 isn't tested yet, although it uses the same Windows functions.
- **Ask AI** needs Claude Code installed and signed in. Answers depend on the Claude model chosen.
- **External reference links**: `community.bitsum.com` (HTTP 403) and `deepwiki.com` (HTTP 429) refused automated checks; both load normally in a browser.
- Changing the priority of programs that run as administrator needs Task Manager Lite to be run as administrator too.

## SHA-256 checksums (v2.2.0)

```
2fd235e2b837c05a1f2969e77546a26cda919b8e5cb0fc5a021faeff521778f9  TaskManagerLite.exe
17aa2165918a2ec770dca2dd540ef89ee0b396e4b6b921d8fbdf9ef4218777aa  TaskManagerLite-v2.2.0-win-x64.rar
36905e2f3a6e1b5b685dce36c6a0b281e878323087df9115b4ce227a6cbfa654  TaskManagerLite-v2.2.0-win-x64.zip
```
