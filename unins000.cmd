@echo off
setlocal

set "APP_DIR=%~dp0"

echo Stopping Desktop AI Agent...
taskkill /IM AgentShell.exe /F >nul 2>nul

echo Removing app files from:
echo %APP_DIR%

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$path = [System.IO.Path]::GetFullPath('%APP_DIR%');" ^
  "Start-Sleep -Seconds 1;" ^
  "Remove-Item -LiteralPath $path -Recurse -Force"

echo Done.
endlocal
