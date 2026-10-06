@echo off
echo ============================================
echo  EvoCUA-8B Q4_K_S + Qwen3-VL mmproj (Vulkan)
echo  All layers on GPU, ctx 8192, kv q8_0
echo  Port: 5002   http://127.0.0.1:5002
echo ============================================
echo.
echo Use this only to run the model by hand.
echo The agent shell starts koboldcpp on its own.

cd /d "%USERPROFILE%\llm\koboldcpp"

.\koboldcpp-nocuda.exe ^
  --model "%USERPROFILE%\llm\models\evocua-8b-20260105-q4_k_s.gguf" ^
  --mmproj "%USERPROFILE%\llm\models\mmproj-Evocua-F16.gguf" ^
  --usevulkan ^
  --gpulayers 999 ^
  --contextsize 8192 ^
  --quantkv q8_0 ^
  --threads 6 ^
  --blasthreads 6 ^
  --port 5002 ^
  --host 127.0.0.1 ^
  --jinja

pause
