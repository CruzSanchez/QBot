@echo off
setlocal

rem Runs DownloadBot the same way it's been run manually all along - "dotnet run" from inside
rem the project folder - so nothing about secrets/config changes. Existing "dotnet user-secrets"
rem values, appsettings.json, and the port/environment from launchSettings.json all keep working
rem exactly as already set up.
rem
rem Requires the .NET SDK to already be installed on this machine (the same one used to build
rem and test the project). This is a convenience launcher, not a standalone/portable deployment -
rem it does not need anything published or copied anywhere else.
rem
rem RESTARTS ITSELF if the bot dies with a non-zero exit code (a crash, a host stop on an unhandled
rem exception, the process being killed) - this loop is the restart mechanism, so it doesn't depend
rem on Task Scheduler's own "restart on failure" setting. A clean stop (exit code 0 - Ctrl+C, or the
rem deploy workflow's POST /shutdown) is NOT restarted. If a deploy force-kills a stuck bot, the
rem workflow's `schtasks /end` then removes this loop before the restart delay is up.
rem
rem Gives up after 50 restarts in one launch so a bot that can't start at all (bad config) doesn't
rem loop forever. RESTART_DELAY (seconds, default 15) can be overridden for testing.
rem
rem "unattended" (passed by the scheduled task): skips the final pause, which would hang a task
rem nobody's watching, and returns the real exit code.

if not defined RESTART_DELAY set RESTART_DELAY=15
set /a PING_COUNT=%RESTART_DELAY%+1
set RESTARTS=0

cd /d "%~dp0DownloadBot"

echo Starting DownloadBot...
echo (Press Ctrl+C to stop it cleanly - the bot posts a shutdown notice to Discord before exiting.)
echo.

:run
dotnet run -c Release
set EXITCODE=%ERRORLEVEL%

echo.
echo DownloadBot has stopped (exit code %EXITCODE%).

if %EXITCODE% EQU 0 goto done

set /a RESTARTS+=1
if %RESTARTS% GTR 50 (
    echo Giving up after 50 restarts.
    goto done
)

echo Crashed - restarting in %RESTART_DELAY% seconds [restart %RESTARTS% of 50]...
rem ping is the sleep: "timeout" refuses to run when there's no interactive console.
ping -n %PING_COUNT% 127.0.0.1 >nul
goto run

:done
if /i not "%~1"=="unattended" pause
exit /b %EXITCODE%
