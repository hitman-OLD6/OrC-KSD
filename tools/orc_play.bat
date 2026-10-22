@echo off
rem ============================================================================
rem  OrC human CLI launcher (D13.3)
rem    - creates a local venv under tools\orc\.venv (once)
rem    - installs prompt_toolkit (only needed by the full-screen TUI)
rem    - runs the client; the working directory is the repo root
rem  NOTE: this file is intentionally pure ASCII + CRLF. cmd.exe parses .bat
rem        with the OEM code page, so non-ASCII text here breaks the script.
rem ============================================================================
setlocal EnableExtensions
set "HERE=%~dp0"
cd /d "%HERE%.."
chcp 65001 >nul 2>nul
set "VENV=%HERE%orc\.venv"
set "PY=%VENV%\Scripts\python.exe"

set "ARGS=%*"
if "%ARGS%"=="" set "ARGS=play"

if exist "%PY%" goto run

echo [orc] creating isolated venv: %VENV%
where py >nul 2>nul
if not errorlevel 1 (
  py -3.13 -m venv "%VENV%" >nul 2>nul
  if not exist "%PY%" py -3 -m venv "%VENV%" >nul 2>nul
) else (
  python -m venv "%VENV%" >nul 2>nul
)

:run
if not exist "%PY%" goto fallback
"%PY%" -c "import prompt_toolkit" >nul 2>nul
if errorlevel 1 (
  echo [orc] installing prompt_toolkit ...
  "%PY%" -m pip install -q --disable-pip-version-check -r "%HERE%orc\requirements.txt" >nul 2>nul
)
"%PY%" "%HERE%orc_play.py" %ARGS%
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" goto failed
exit /b %RC%

:fallback
echo [orc] venv unavailable; running with the current interpreter (--plain only).
python "%HERE%orc_play.py" --plain %ARGS%
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" goto failed
exit /b %RC%

:failed
echo.
echo [orc] exited with code %RC%
pause
exit /b %RC%
