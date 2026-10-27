@echo off
rem Builds Switchboard Remote Manager into the output folder next to this file:
rem   output\SwitchboardRM.exe   (self-contained, nothing to install)
rem   output\tools\...           (bundled helpers from tools\, if collected)
cd /d "%~dp0"
dotnet publish SwitchboardRM.csproj -c Release
if errorlevel 1 (
  echo.
  echo Publish FAILED - see the messages above.
) else (
  echo.
  echo Done: %~dp0output
)
pause
