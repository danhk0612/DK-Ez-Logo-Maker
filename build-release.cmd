@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "PUBLISH_DIR=%CD%\publish\win-x64"
set "APP_EXE=%PUBLISH_DIR%\DK Ez Logo Maker.exe"

rem A running published executable cannot be replaced by dotnet publish.
if exist "%APP_EXE%" (
  powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$target=[IO.Path]::GetFullPath($env:APP_EXE); $locked=$false; Get-Process -ErrorAction SilentlyContinue | ForEach-Object { try { if ($_.Path -and [IO.Path]::GetFullPath($_.Path) -eq $target) { $locked=$true } } catch {} }; if ($locked) { exit 2 } else { exit 0 }"
  if errorlevel 2 (
    echo.
    echo Build stopped: DK Ez Logo Maker is currently running from the publish folder.
    echo Close the application and run build-release.cmd again.
    echo Path: "%APP_EXE%"
    exit /b 2
  )
)

rem Clean the previous publish output first so stale files cannot remain.
if exist "%PUBLISH_DIR%" (
  rmdir /s /q "%PUBLISH_DIR%" 2>nul
  if exist "%PUBLISH_DIR%" (
    echo.
    echo Build stopped: the previous publish folder could not be removed.
    echo Close any program using files in this folder, then try again.
    echo Path: "%PUBLISH_DIR%"
    exit /b 3
  )
)

dotnet publish DK.EzLogoMaker.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained false ^
  -p:PublishSingleFile=true ^
  -o "%PUBLISH_DIR%"

if errorlevel 1 (
  echo.
  echo Build failed.
  exit /b 1
)

echo.
echo Build complete: "%PUBLISH_DIR%"
endlocal
