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

cd /d "%~dp0DownloadBot"

echo Starting DownloadBot...
echo (Press Ctrl+C to stop it cleanly - the bot posts a shutdown notice to Discord before exiting.)
echo.

dotnet run -c Release

echo.
echo DownloadBot has stopped (exit code %ERRORLEVEL%).
pause
