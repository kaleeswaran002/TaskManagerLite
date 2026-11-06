@echo off
rem Rebuilds TaskManagerLite.exe with the C# compiler built into Windows (.NET Framework 4.8).
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /win32icon:src\TaskManagerLite.ico /out:TaskManagerLite.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll src\TaskManagerLite.cs
if errorlevel 1 (echo Build failed.) else (echo Built TaskManagerLite.exe)
pause
