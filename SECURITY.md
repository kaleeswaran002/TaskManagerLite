# Security Policy

## Supported versions

| Version | Supported |
|---|---|
| 2.2.x | ✅ |
| < 2.2 | ❌ |

## Reporting a vulnerability

Please use **GitHub → Security → Report a vulnerability** (private reporting) instead of a public issue. Include the steps to reproduce it. You should get a reply within 7 days.

## Safety design

- **Ask AI** runs Claude Code in print mode with every command, file, web and agent tool disallowed (`--disallowedTools`), with user settings and MCP servers not loaded (`--setting-sources project --strict-mcp-config`), and without saving the session. Claude can only answer in text.
- **Protected processes**: core Windows processes (System, csrss, lsass, svchost, dwm, Defender, and others) can't be ended or reprioritized from the app.
- **Confirmations**: ending tasks, setting High priority and emptying the Recycle Bin always ask first.
- **Admin rights** are requested only when you click **Clear cached memory**, through the normal Windows UAC prompt.
- **Junk cleanup** deletes only temp files older than one day, crash dumps and error reports. Files in use are skipped, and junction links are never followed.
- **Self-update** compiles `src\TaskManagerLite.cs` from the program folder. Anyone who can write to that folder can change the program, so keep it inside your own user profile, not in a shared or public folder.
- The program isn't code-signed. Build it yourself with `build.bat` if you prefer.
