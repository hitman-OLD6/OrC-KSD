@echo off
rem ===========================================================================
rem  Orc headless SANDBOX server launcher
rem ---------------------------------------------------------------------------
rem  Why this order matters:
rem    The MCP bridge (Orc.Mcp.Bridge) is spawned by the host at session start
rem    and probes 127.0.0.1:51888 immediately. If nothing is listening, the
rem    bridge exits at once and the orc-seat1 tools never get registered.
rem    So: START THIS SERVER FIRST, then start / restart WorkBuddy.
rem
rem  This window must stay open. Closing it stops the server.
rem
rem  Usage:
rem     start-sandbox-server.bat            foreground (recommended)
rem     start-sandbox-server.bat --check    self-check only, does not start
rem     start-sandbox-server.bat --detach   background via python helper
rem ===========================================================================
setlocal EnableExtensions
cd /d "%~dp0"

set "PORT=51888"
set "RUN_DIR=%~dp0outputs\agent-match\mcp-run"
set "SERVER_DLL=%~dp0src\Orc.Server\bin\Debug\net8.0\Orc.Server.dll"
set "PACKS=%~dp0samples\card-pack-demo"
set "WAIT_AGENT=1800"
set "MCP_JSON=%USERPROFILE%\.workbuddy\mcp.json"

title Orc sandbox server :%PORT%

if /i "%~1"=="--detach" goto :detach
if /i "%~1"=="--check"  goto :check

rem ---------------------------------------------------------------- preflight
call :preflight
if errorlevel 1 goto :halt

echo ============================================================
echo  Orc headless sandbox server
echo ------------------------------------------------------------
echo  mode        : sandbox  (debug writes ENABLED)
echo  port        : %PORT%
echo  run dir     : %RUN_DIR%
echo  card pack   : %PACKS%
echo  wait agent  : %WAIT_AGENT%s
echo ------------------------------------------------------------
echo  Keep this window OPEN. Closing it stops the server.
echo  Wait for the "ready" JSON line, then start WorkBuddy.
echo ============================================================
echo.

if not exist "%RUN_DIR%" mkdir "%RUN_DIR%" >nul 2>nul

dotnet "%SERVER_DLL%" ^
  --headless ^
  --mode sandbox ^
  --allow-debug-writes ^
  --packs "%PACKS%" ^
  --seat0 ai ^
  --seat1 agent ^
  --port %PORT% ^
  --run-dir "%RUN_DIR%" ^
  --deck-rules lenient ^
  --wait-agent %WAIT_AGENT%

set "RC=%ERRORLEVEL%"
echo.
echo ============================================================
echo  server exited with code %RC%
echo    0 = game ended normally
echo    1 = startup failure / bad arguments
echo    2 = runtime error
echo    3 = timed out waiting for the agent seat
echo ------------------------------------------------------------
echo  logs: %RUN_DIR%\events.jsonl   %RUN_DIR%\calls.jsonl
echo ============================================================
echo.
pause
exit /b %RC%

rem ---------------------------------------------------------------- helpers
:preflight
where dotnet >nul 2>nul
if errorlevel 1 (
  echo [FAIL] dotnet not found in PATH.
  echo        Install the .NET 8 SDK / runtime, then retry.
  exit /b 1
)

if not exist "%SERVER_DLL%" (
  echo [FAIL] server build missing:
  echo        %SERVER_DLL%
  echo        Build it first:  dotnet build OrcEngine.slnx
  exit /b 1
)

netstat -ano | findstr /C:":%PORT%" | findstr /C:"LISTENING" >nul 2>nul
if not errorlevel 1 (
  echo [FAIL] port %PORT% is already listening - another instance is alive.
  echo        Inspect : netstat -ano ^| findstr :%PORT%
  echo        Stop it : python sandbox-server-ctl.py stop
  exit /b 1
)
exit /b 0

:check
echo [check] dotnet ............
where dotnet >nul 2>nul && (dotnet --version) || (echo        MISSING & goto :halt)
echo [check] server dll ........
if exist "%SERVER_DLL%" (echo        OK  %SERVER_DLL%) else (echo        MISSING %SERVER_DLL% & goto :halt)
echo [check] card pack .........
if exist "%PACKS%\manifest.json" (echo        OK  %PACKS%) else (echo        MISSING manifest.json in %PACKS% & goto :halt)
echo [check] port %PORT% .......
netstat -ano | findstr /C:":%PORT%" | findstr /C:"LISTENING" >nul 2>nul
if errorlevel 1 (echo        FREE) else (echo        IN USE - stop the other instance first)
echo [check] mcp.json ..........
if not exist "%MCP_JSON%" (
  echo        MISSING %MCP_JSON%
) else (
  findstr /C:"127.0.0.1:%PORT%" "%MCP_JSON%" >nul 2>nul && (echo        port OK) || (echo        port MISMATCH - must point at 127.0.0.1:%PORT%)
  findstr /C:"outputs/agent-match/mcp-run/agent.token" "%MCP_JSON%" >nul 2>nul && (echo        token path OK) || (echo        token path MISMATCH - must be %RUN_DIR%\agent.token)
)
echo.
echo [check] done.
pause
exit /b 0

:detach
rem Background mode: sandbox-server-ctl.py uses DETACHED_PROCESS + CREATE_BREAKAWAY_FROM_JOB.
rem CAVEAT: inside a Job Object (agent sandbox / some editors / CI) breakaway may be
rem refused, and the instance dies with the parent task. Use foreground mode there.
where python >nul 2>nul
if errorlevel 1 (
  echo [FAIL] python not found - use the foreground mode instead.
  pause
  exit /b 1
)
python "%~dp0sandbox-server-ctl.py" start --port %PORT% --run-dir "%RUN_DIR%" --packs "%PACKS%" --wait-agent %WAIT_AGENT%
echo.
echo [sandbox] instance started in background; this window can be closed.
pause
exit /b 0

:halt
echo.
pause
exit /b 1
