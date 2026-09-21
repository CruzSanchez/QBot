@echo off
setlocal

rem One-time setup: registers a Windows Scheduled Task that runs run-bot.bat automatically every
rem time you log in, so the bot starts on its own after a PC restart/login - no need to remember
rem to double-click anything.
rem
rem This modifies Windows Task Scheduler. Run it once, ideally as Administrator (right-click ->
rem "Run as administrator") - creating a task with /rl HIGHEST can fail without elevation.

set TASK_NAME=DownloadBot
set SCRIPT_PATH=%~dp0run-bot.bat

echo Installing scheduled task "%TASK_NAME%"...
echo   Trigger: at logon (current user)
echo   Runs:    %SCRIPT_PATH%
echo.

schtasks /create /tn "%TASK_NAME%" /tr "\"%SCRIPT_PATH%\"" /sc onlogon /rl HIGHEST /f

if %ERRORLEVEL% EQU 0 (
    echo.
    echo Done. DownloadBot will now start automatically the next time you log in.
    echo To test it immediately without rebooting: schtasks /run /tn "%TASK_NAME%"
    echo To remove it later, run uninstall-startup-task.bat.
) else (
    echo.
    echo Failed to create the scheduled task. Try right-clicking this file and choosing
    echo "Run as administrator", then run it again.
)

pause
