# Prompt: Task Manager Lite (final, v2)

**Goal:** A lightweight Task Manager for a 16 GB Windows 10 PC that runs VS Code + Claude Code, the Claude and ChatGPT desktop apps, the Hermes agent (Python) and Quantman (algo trading) in Chrome. It must use very little RAM itself, refresh automatically (10 s / 30 s / 60 s / paused), find what causes lag, and fix or explain it.

**Monitor:** CPU, RAM, committed memory, cache, processes/threads/handles and uptime, with history graphs. A process list (Name, PID, CPU %, Memory, Commit, Priority) with heat shading, sorting, search and Group by app.

**AI features:**
- Smart Advisor (offline): a built-in knowledge base of common processes ("what is it / is it safe to end"), plus rules tuned to the apps above, giving a health score out of 100 and action buttons.
- Ask AI: sends a snapshot (numbers and process names only) to Claude Code with all tools disabled (`claude -p --disallowedTools ... --setting-sources project --strict-mcp-config --no-session-persistence`) and shows the answer in the app. Also: "Ask AI about this" on any process or finding, and Copy report.

**Auto troubleshoot:** detects nearly-full committed memory or RAM, sustained high CPU, apps that are not responding, memory leaks (an app's memory growing for a while), heavy Chrome or AI-app usage, Defender scans, dwm load, low disk space, a small page file, and long uptime. Opt-in automatic fixes: tray notifications, lowering the priority of background CPU hogs (never Chrome or the window you're using), and giving Chrome priority for Quantman. Every action is written to an activity log.

**Free up memory (v2.1):**
- A memory bar showing in use / modified / cached (standby) / free, read with SystemMemoryListInformation, which works without admin rights.
- "Trim background apps": EmptyWorkingSet on idle apps of the current user. It skips Windows, the app in front, this app and Chrome (unless ticked). Optionally runs automatically when RAM is above 85% (at most every 10 minutes).
- "Clear cached memory": purges the standby list (NtSetSystemInformation with MemoryPurgeStandbyList). Needs admin, so the app restarts itself elevated with `--purge`. The UI explains that cached memory isn't wasted.
- Junk files: user temp files (older than 1 day), Windows temp (admin), crash dumps, error reports, and the Recycle Bin (opt-in, with a confirmation). Files in use are skipped.

**Auto update:** watches src\TaskManagerLite.cs. When `Version` is raised (or the source changes), it rebuilds with the built-in C# compiler, swaps the .exe and restarts itself. This happens automatically, or with one click.

**UI:** sidebar navigation (Processes, Smart Advisor, Ask AI, Settings), light, dark or match-Windows theme with a dark title bar, tray icon, single instance, settings remembered, F5 to refresh, Ctrl+F to search.

**Low-overhead method:** one NtQuerySystemInformation call per refresh (as in System Informer); CPU % from the change in CPU time between refreshes; GetPerformanceInfo for memory and commit; no performance counters; the list and cards are only redrawn while visible.

**Command line:** `--page N` (0 Processes, 1 Advisor, 2 Ask AI, 3 Settings), `--ask "question"`, `--minimized`.

## References
- System Informer architecture: https://deepwiki.com/PKRoma/ProcessHacker/1.1-architecture-overview
- Per-process CPU from time deltas: https://www.philosophicalgeek.com/?p=434
- CPU from TotalProcessorTime: https://backiee.wasmer.app/https_www_iditect_com/faq/csharp/calculating-process-cpu-usage-from-processtotalprocessortime-in-c_html
- Process Lasso note on GUI refresh cost: https://community.bitsum.com/forum/index.php?msg=16512
