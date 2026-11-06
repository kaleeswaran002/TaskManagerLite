# Task Manager Lite — User Guide

## 1. First start
Run `TaskManagerLite.exe`. The window opens on **Processes**, and numbers fill in within about two seconds. The sidebar has five pages: **Processes**, **Smart Advisor**, **Free up memory**, **Ask AI** and **Settings**. The health score at the bottom of the sidebar opens the Smart Advisor.

The four tiles at the top are always visible:
- **CPU**: total processor use.
- **Memory**: RAM in use / total.
- **Committed memory**: memory that apps have reserved, out of RAM + page file. If this reaches 100%, apps crash, so it's the most important number on a busy PC.
- **System**: processes, threads, handles and uptime.

A value turns red at 85% or more.

## 2. Processes
- **Search** (Ctrl+F) filters by name or PID.
- **Group by app** combines processes with the same name, for example all Chrome processes.
- **Sort** by clicking a column header. Click it again to reverse the order.
- **Columns**: *Memory* is the private working set (the same as Windows Task Manager); *Commit* is that app's share of committed memory.
- **Colours**: yellow shading means heavy CPU or memory, grey names are Windows processes, and a blue priority means it isn't Normal.
- **Details panel**: select a row to see what it is, whether it's safe to end, and where its file is.
- **Buttons / right-click**: Ask AI about it · End task · Lower priority · Set priority · Open file location · Search online · Copy name.
- **Refresh every** 10 s, 30 s, 60 s or Paused. **F5** refreshes immediately.

Ending a task or changing a priority may show "Access is denied" for programs running as administrator. Right-click the shortcut and choose **Run as administrator** to control those.

## 3. Smart Advisor
Shows a **health score** and cards ranked Critical → Warning → Tip. Each card has a fix button and **Ask AI about this**.

What it checks every refresh: committed memory and RAM (80% and 90%), CPU busy for three refreshes in a row, windows that are not responding, an app whose memory grows by more than 700 MB, Chrome / AI-app / Python / VS Code memory, Defender scans, dwm CPU, drive C: free space, page-file size, uptime of more than 7 days, and self-update errors.

**Auto-troubleshoot switches:**
- *Notify me about problems*: a tray pop-up for warnings, at most once every 30 minutes per issue.
- *Auto-lower background CPU hogs*: an app above 30% CPU for three refreshes (not Chrome, not the app you're using) is set to Below normal.
- *Apply my app priority rules*: keeps your **Priority apps** (see Settings) at the priority you chose.

Everything the app does is listed in the **Activity log** (also saved to `data\troubleshoot.log`). Priority changes reset when the app restarts.

## 4. Free up memory
- **Memory bar**: In use · Modified · Cached (standby) · Free.
- **Trim background apps**: moves idle memory of background apps out of RAM. It skips Windows processes, the app in front, this app and Chrome (tick *Include Chrome* to include it). Apps refill memory as you use them.
- **Clear cached memory**: empties Windows' file cache. It needs admin: the app restarts as administrator after a UAC prompt and then clears it. Cached memory isn't wasted, so use this only when something stutters.
- **Auto-trim when RAM is above 85%**: at most every 10 minutes.
- **Junk files**: tick what to remove and click **Clean selected**. Your temp files older than a day, crash dumps and error reports are ticked by default. Windows temp needs admin. The Recycle Bin is unticked and asks before emptying.

## 5. Ask AI
Needs Claude Code installed and signed in (run `claude` once in a terminal to sign in).
- Click a question chip, or type your own and press **Enter** (Shift+Enter for a new line).
- Answers take about 10–30 seconds. **Cancel** stops one.
- **Copy report** copies the snapshot, so you can paste it into claude.ai or ChatGPT instead.
- Choose the model in Settings: Fast (Haiku), Balanced (Sonnet) or Best (Opus).
- Claude can't run commands or change anything; it only answers.

## 6. Settings
Theme · Always on top · Minimize to tray · Start with Windows (minimized) · Claude model · **Priority apps** · Install updates automatically · Check for updates · Rebuild now · Open program folder.

### Priority apps (add / delete)
Keep the apps that matter most running smoothly.
- **Add**: type the process name as shown in the Processes list (for example `chrome`, `Code`, `python`; `.exe` is optional), choose a priority, and click **Add / update** (or press Enter). To change an app's level, select it, pick a new level and click **Add / update** again.
- **Add from the list**: on the Processes page, right-click a process → **Add to priority apps** → choose a level.
- **Delete**: select one or more apps and click **Delete selected** (or press Delete). You can set their running copies back to Normal straight away.
- **Levels**: *High* (use sparingly; it can make Windows less responsive), *Above normal* (recommended for Chrome/Quantman and your editor), *Below normal* or *Low* (background work such as agents or sync tools).
- Apps set to High or Above normal are never auto-lowered or trimmed.
- The rules apply while the switch *Apply my priority apps* is on (in Settings and in Smart Advisor; adding an app turns it on). New processes of a listed app are set within one refresh.
- Core Windows processes can't be added.

## 7. Updating
Edit `src\TaskManagerLite.cs` and raise `Version` (for example `2.1.0` → `2.1.1`). Within 30 minutes, or at the next start, the app rebuilds itself and restarts. If the build fails, the current version keeps running and the error is shown in Settings and `data\update.log`.

## 8. Troubleshooting
| Problem | Fix |
|---|---|
| SmartScreen blocks the program | More info → Run anyway, or build it yourself with `build.bat`. |
| Ask AI: "returned no answer" | Run `claude` in a terminal once and sign in, then try again. |
| "Access is denied" | The process runs as admin; start Task Manager Lite as administrator. |
| Update failed | See Settings → Updates and `data\update.log`. |
| Reset all settings | Close the app and delete `settings.ini`. |
| The window is hidden | Double-click the tray icon, or start the program again (it brings the open window to the front). |

## 9. Tips for a smooth multi-app PC
- Watch **Committed memory**, not just RAM.
- Use ChatGPT in a browser tab instead of the desktop app (saves about 1 GB).
- In Chrome, turn on Memory Saver and add your trading site under *Always keep these sites active*.
- Restart long-running agents (such as Python scripts) now and then to release memory.
- Consider a larger page file (16 GB) if committed memory often goes above 85%.
