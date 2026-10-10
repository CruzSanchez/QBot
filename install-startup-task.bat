@echo off
setlocal

rem One-time setup (safe to re-run): registers the "DownloadBot" Windows Scheduled Task that runs
rem run-bot.bat at logon, restarts it automatically if the bot crashes, and removes Task
rem Scheduler's default 3-day run limit. The real work is in install-startup-task.ps1 - this just
rem launches it so you can still double-click / right-click -> Run as administrator.
rem
rem This modifies Windows Task Scheduler and asks for your Windows password (so the task can run
rem whether or not you're logged in - required for the auto-deploy workflow's `schtasks /run`).

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-startup-task.ps1"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Failed to register the scheduled task. Try right-clicking this file and choosing
    echo "Run as administrator", then run it again.
)

pause
