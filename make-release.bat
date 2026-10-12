@echo off
rem Double-click to build both release zips into the "release" folder (Windows).
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo Could not find dotnet. Install the .NET 6 SDK: https://dotnet.microsoft.com/download/dotnet/6.0
  pause
  exit /b 1
)

for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Get-Content IsaacValley\manifest.json -Raw | ConvertFrom-Json).Version"`) do set VERSION=%%v
echo Building Isaac Valley %VERSION%...
if not exist release mkdir release

dotnet build IsaacValley -c Release
if errorlevel 1 (
  echo.
  echo Build failed ^(see above^).
  pause
  exit /b 1
)

powershell -NoProfile -Command "$z = Get-ChildItem IsaacValley\bin\Release -Recurse -Filter IsaacValley*.zip | Select-Object -First 1; Copy-Item $z.FullName release\IsaacValley-%VERSION%-Stardew.zip -Force"

del /q "release\IsaacValleyBridge-%VERSION%-Isaac.zip" 2>nul
tar -a -c -f "release\IsaacValleyBridge-%VERSION%-Isaac.zip" --exclude "save*.dat" --exclude "link_*" --exclude "*.tmp" IsaacValleyBridge

echo.
echo Done:
dir /b release
explorer release
pause
