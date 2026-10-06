@echo off
setlocal

rem Downloads the local GUI-agent model used by the agent shell.
rem EvoCUA-8B-20260105 is a Qwen3-VL-8B fine-tune and needs its matching mmproj for vision.

set "MODEL_DIR=%USERPROFILE%\llm\models"
set "REPO=https://huggingface.co/AhmedMostafa-notabot/EvoCUA-8B-20260105-Q4_K_S-GGUF/resolve/main"

if not exist "%MODEL_DIR%" mkdir "%MODEL_DIR%"

echo ============================================
echo  EvoCUA-8B Q4_K_S  (about 4.8 GB)
echo  mmproj-Evocua-F16 (about 1.2 GB)
echo  Target: %MODEL_DIR%
echo ============================================
echo.

curl.exe -L --fail --progress-bar -o "%MODEL_DIR%\evocua-8b-20260105-q4_k_s.gguf" "%REPO%/evocua-8b-20260105-q4_k_s.gguf"
if errorlevel 1 goto failed

curl.exe -L --fail --progress-bar -o "%MODEL_DIR%\mmproj-Evocua-F16.gguf" "%REPO%/mmproj-Evocua-F16.gguf"
if errorlevel 1 goto failed

echo.
echo Done. The agent shell finds these files automatically.
pause
exit /b 0

:failed
echo.
echo Download failed. Check your connection and free disk space.
pause
exit /b 1
