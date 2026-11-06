# Privacy Policy

Task Manager Lite runs entirely on your PC. It has no telemetry, analytics, accounts or ads.

## What leaves your PC, and only when you click it

| Feature | What is sent | To whom |
|---|---|---|
| **Ask AI** | Your question plus a snapshot: CPU/RAM/commit figures, disk free space, uptime, the names and resource use of your top processes, Smart Advisor findings, and the app's recent actions. | Anthropic, through **your own** Claude Code installation and sign-in. Anthropic's terms and privacy policy apply. |
| **Ask AI about this / a process** | As above, plus that process's name and file path. | Same as above |
| **Search online** | The process name, as a Google search in your browser | Google |
| **Open claude.ai** / **Chrome settings** | Nothing; it only opens the page | – |

No files, documents, passwords, browsing history or window contents are ever read or sent. Use **Copy report** to see exactly what Ask AI would send.

## What is stored on your PC

All of this is in the program folder:

- `settings.ini`: your settings
- `data\troubleshoot.log`: the activity log (actions the app took)
- `data\update.log`: the last self-update build output
- `data\ai\last-answer.md`: the last AI answer

If you tick **Start with Windows**, the app adds one value to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Unticking it removes the value.

## Deleting your data

Close the app and delete its folder.
