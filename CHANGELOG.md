# Changelog

## 2.2.0 — 2026-11-06
- New **Priority apps** in Settings: keep chosen apps at High, Above normal, Below normal or Low, with **Add / update** and **Delete selected**.
- Right-click a process → **Add to priority apps**.
- Priority apps above Normal are never auto-lowered or trimmed.
- Deleting an app can set its running copies back to Normal.
- "Give Chrome (Quantman) priority" became "Apply my app priority rules"; Chrome → Above normal is the default rule.

## 2.1.0 — 2026-11-06
- New **Free up memory** page: memory composition bar, Trim background apps, Clear cached memory (admin), and junk-file cleanup.
- Optional auto-trim when RAM is above 85%.
- Smart Advisor RAM warnings now link to Free up memory.

## 2.0.1 — 2026-11-06
- Release used to verify that self-update works.

## 2.0.0 — 2026-11-06
- New interface: sidebar, light/dark/match-Windows theme, tray icon.
- Smart Advisor with health score and built-in process knowledge.
- Ask AI through Claude Code (read-only).
- Auto-troubleshoot: alerts, auto-lower CPU hogs, Chrome priority, activity log.
- Self-update from the source folder.
- Process data read with a single NtQuerySystemInformation call, so Windows processes show CPU without admin rights.

## 1.0.0 — 2026-11-06
- First version: CPU, RAM and committed-memory tiles, process list, 10/30/60-second refresh, end task, priority.
