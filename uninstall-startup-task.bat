@echo off
setlocal

rem Removes the scheduled task created by install-startup-task.bat.

set TASK_NAME=DownloadBot

schtasks /delete /tn "%TASK_NAME%" /f

if %ERRORLEVEL% EQU 0 (
    echo Scheduled task "%TASK_NAME%" removed. DownloadBot will no longer start automatically at login.
) else (
    echo Could not remove the scheduled task - it may not exist, or try running as Administrator.
)

pause
