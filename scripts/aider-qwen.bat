@echo off
setlocal EnableDelayedExpansion
title Aider + Qwen Coder

rem ============================================================
rem  Makes sure Aider + Qwen2.5-Coder (via Ollama) works, then starts it.
rem  Usage:  aider-qwen.bat            start Aider in the current folder
rem          aider-qwen.bat check      only check/repair, do not start Aider
rem          aider-qwen.bat "C:\path"  start Aider in that folder
rem ============================================================

set "MODEL=qwen2.5-coder:7b"
set "CTX=16384"
set "OLLAMA_API_BASE=http://127.0.0.1:11434"
set "SETTINGS=%~dp0aider.model.settings.yml"
set "CHECK_ONLY=0"
set "WORKDIR=%CD%"

if /i "%~1"=="check" (
    set "CHECK_ONLY=1"
) else if not "%~1"=="" (
    set "WORKDIR=%~1"
)

echo.
echo === 1/4  Ollama ===
where ollama >nul 2>&1
if errorlevel 1 (
    echo [FAIL] ollama.exe not found on PATH. Install Ollama from https://ollama.com first.
    goto :fail
)
call :ollama_up
if not errorlevel 1 goto :ollama_ok
echo Ollama server is not running - starting it...
start "" /min ollama serve
set /a TRIES=0
:wait_ollama
timeout /t 1 /nobreak >nul
call :ollama_up
if not errorlevel 1 goto :ollama_ok
set /a TRIES+=1
if %TRIES% lss 20 goto :wait_ollama
echo [FAIL] Ollama did not come up on %OLLAMA_API_BASE%.
goto :fail
:ollama_ok
echo [ OK ] Ollama is running.

echo.
echo === 2/4  Model %MODEL% ===
ollama list | findstr /b /i /c:"%MODEL%" >nul
if errorlevel 1 (
    echo %MODEL% is not downloaded yet - pulling it now ^(about 4.7 GB^)...
    ollama pull %MODEL%
    if errorlevel 1 (
        echo [FAIL] Could not pull %MODEL%.
        goto :fail
    )
)
echo [ OK ] %MODEL% is available.

echo.
echo === 3/4  Aider ===
set "AIDER="
where aider >nul 2>&1 && set "AIDER=aider"
if not defined AIDER if exist "%USERPROFILE%\.local\bin\aider.exe" set "AIDER=%USERPROFILE%\.local\bin\aider.exe"
if not defined AIDER (
    echo Aider is not installed - installing it with uv ^(Python 3.12^)...
    where uv >nul 2>&1
    if errorlevel 1 (
        echo [FAIL] uv not found. Install it with:  winget install --id=astral-sh.uv
        goto :fail
    )
    uv tool install --python 3.12 --force aider-chat
    if errorlevel 1 (
        echo [FAIL] Aider install failed.
        goto :fail
    )
    if exist "%USERPROFILE%\.local\bin\aider.exe" set "AIDER=%USERPROFILE%\.local\bin\aider.exe"
    if not defined AIDER where aider >nul 2>&1 && set "AIDER=aider"
)
if not defined AIDER (
    echo [FAIL] Aider installed but aider.exe was not found. Open a new terminal and run again.
    goto :fail
)
"%AIDER%" --version
if errorlevel 1 (
    echo [FAIL] Aider is installed but will not start.
    goto :fail
)
echo [ OK ] Aider works.

echo.
echo === 4/4  Settings ===
rem Ollama defaults to a small context window, which makes Aider forget the files.
rem This file tells Aider to ask Ollama for %CTX% tokens.
if not exist "%SETTINGS%" (
    > "%SETTINGS%" echo - name: ollama_chat/%MODEL%
    >> "%SETTINGS%" echo   extra_params:
    >> "%SETTINGS%" echo     num_ctx: %CTX%
    echo Created %SETTINGS%
)
echo [ OK ] Context window set to %CTX% tokens.

if "%CHECK_ONLY%"=="1" (
    echo.
    echo All checks passed. Run this bat without "check" to start coding.
    goto :done
)

echo.
echo Starting Aider with %MODEL% in "%WORKDIR%" ...
echo.
cd /d "%WORKDIR%" || goto :fail
"%AIDER%" --model ollama_chat/%MODEL% --model-settings-file "%SETTINGS%" --no-show-model-warnings --no-analytics --no-check-update
goto :done

:ollama_up
curl -s -m 2 "%OLLAMA_API_BASE%/api/version" >nul 2>&1
exit /b %errorlevel%

:fail
echo.
echo Something needs fixing - see the [FAIL] line above.
pause
exit /b 1

:done
endlocal
exit /b 0
