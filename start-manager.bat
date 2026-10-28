@echo off
rem ============================================================================
rem  Orc Manager launcher  -  replaces the old start-sandbox-server.bat
rem ----------------------------------------------------------------------------
rem  What changed and why
rem    The old script started ONE server (manager spawn + hand-typed admin lines
rem    on stdin). This one starts the RESIDENT MANAGER instead: it lives on its
rem    own, hosts any number of server instances, and every agent reaches it
rem    through MCP. Start this FIRST, then start your host (WorkBuddy).
rem
rem  Order matters
rem    manager (this window)  ->  your host  ->  agents call MCP tools
rem    If the manager is not running, every manager.* tool call fails with a
rem    readable "please start orc-manager serve" message (it does not crash).
rem
rem  Build output slot
rem    Every project builds into artifacts\slot, see Directory.Build.props.
rem    src\Orc.Manager\bin\Debug\net8.0\ is a deprecated leftover: it is never
rem    refreshed and never used as a launch source. If the slot DLL is older
rem    than any .cs/.csproj under src\, this script rebuilds first, then starts
rem    from the slot.
rem
rem  Usage
rem    start-manager.bat                  start resident manager on 51999
rem    start-manager.bat --port 52001     custom port
rem    start-manager.bat --check          self-check only, do not start
rem    start-manager.bat --config-mcp     write the MCP entry, then exit
rem ============================================================================
setlocal EnableExtensions
cd /d "%~dp0"

set "PORT=51999"
set "MODE=serve"
set "MANAGER_DLL=%~dp0artifacts\slot\bin\Orc.Manager\debug\Orc.Manager.dll"
set "SERVER_DLL=%~dp0artifacts\slot\bin\Orc.Server\debug\Orc.Server.dll"
set "MANAGER_PEER=%~dp0artifacts\slot\bin\Orc.Manager\debug\Orc.Server.dll"
set "PACKS=%~dp0samples\card-pack-demo"
set "MCP_JSON=%USERPROFILE%\.workbuddy\mcp.json"

:parse
if "%~1"=="" goto :parsed
if /i "%~1"=="--port" (
  set "PORT=%~2"
  shift
  shift
  goto :parse
)
if /i "%~1"=="--check" (
  set "MODE=check"
  shift
  goto :parse
)
if /i "%~1"=="--config-mcp" (
  set "MODE=config"
  shift
  goto :parse
)
if /i "%~1"=="--help" goto :usage
if /i "%~1"=="-h" goto :usage
echo [FAIL] unknown option: %~1
echo.
goto :usage

:parsed
if not exist "%PACKS%\manifest.json" set "PACKS=%~dp0samples\card-pack-ai"

call :preflight
if errorlevel 1 goto :halt

if /i "%MODE%"=="check"  goto :check
if /i "%MODE%"=="config" goto :config

title Orc Manager :%PORT%
echo ============================================================
echo  Orc Manager  (resident)
echo ------------------------------------------------------------
echo  listen   : 127.0.0.1:%PORT%
echo  manager  : %MANAGER_DLL%
echo  server   : %SERVER_DLL%
echo  pack hint: %PACKS%
echo ------------------------------------------------------------
echo  Keep this window open. Start your host AFTER this line:
echo      Orc.Manager is ready, listening on 127.0.0.1:%PORT%
echo  Ctrl+C stops the manager and reaps every instance it started.
echo ============================================================
echo.
echo  MCP entry (paste into mcpServers if not already configured):
echo    "orc-manager": {
echo      "command": "dotnet",
echo      "args": ["%MANAGER_DLL:\=\\%", "mcp", "--connect", "127.0.0.1:%PORT%"]
echo    }
echo.
echo  Or let the script do it:  start-manager.bat --config-mcp --port %PORT%
echo ============================================================
echo.

dotnet "%MANAGER_DLL%" serve --port %PORT%

set "RC=%ERRORLEVEL%"
echo.
echo ============================================================
echo  manager exited with code %RC%
echo    0 = normal shutdown
echo    1 = startup failure / bad arguments
echo ============================================================
pause
exit /b %RC%

rem ---------------------------------------------------------------- preflight
:preflight
where dotnet >nul 2>nul
if errorlevel 1 goto :pfNoDotnet

rem Never fall back to the deprecated src\Orc.Manager\bin\ path.
if not exist "%MANAGER_DLL%" goto :pfNoManager

rem Slot DLL older than any .cs/.csproj under src\ => rebuild from the slot.
set "NEWEST="
for /f "delims=" %%F in ('dir /b /s /a-d /o:-d "%~dp0src\*.cs" "%~dp0src\*.csproj" 2^>nul') do if not defined NEWEST set "NEWEST=%%F"
set "STALE="
if defined NEWEST for %%A in ("%MANAGER_DLL%") do for %%B in ("%NEWEST%") do if "%%~tB" GTR "%%~tA" set "STALE=1"
if not defined STALE goto :pfCheckServer

echo [info] slot binaries are older than src sources - rebuilding...
dotnet build "%~dp0OrcEngine.sln" --no-restore
if errorlevel 1 goto :pfBuildFailed
if not exist "%MANAGER_DLL%" goto :pfNoManager

:pfCheckServer
if not exist "%SERVER_DLL%" goto :pfNoServer
rem The manager spawns Orc.Server.dll from ITS OWN output folder.
if not exist "%MANAGER_PEER%" goto :pfNoPeer

netstat -ano | findstr /C:":%PORT%" | findstr /C:"LISTENING" >nul 2>nul
if not errorlevel 1 goto :pfPortBusy
exit /b 0

:pfNoDotnet
echo [FAIL] dotnet not found in PATH.
echo        Install the .NET 8 SDK / runtime, then retry.
exit /b 1

:pfNoManager
echo [FAIL] manager build missing in the artifact slot:
echo        %MANAGER_DLL%
echo        Build it first:  dotnet build OrcEngine.sln
echo        slot is artifacts\slot, src\Orc.Manager\bin\ is deprecated and unused
exit /b 1

:pfBuildFailed
echo [FAIL] build failed - not starting a stale manager.
exit /b 1

:pfNoServer
echo [FAIL] server build missing in the artifact slot:
echo        %SERVER_DLL%
echo        Build it first:  dotnet build OrcEngine.sln
exit /b 1

:pfNoPeer
echo [FAIL] Orc.Server.dll is not next to Orc.Manager.dll.
echo        Rebuild the manager:  dotnet build src\Orc.Manager\Orc.Manager.csproj
exit /b 1

:pfPortBusy
echo [FAIL] port %PORT% is already listening - a manager may already be running.
echo        Inspect : netstat -ano ^| findstr :%PORT%
echo        Use another port: start-manager.bat --port 52000
exit /b 1

rem ---------------------------------------------------------------- self check
:check
echo [check] dotnet ................
where dotnet >nul 2>nul && (dotnet --version) || (echo         MISSING & goto :halt)
echo [check] manager dll ...........
echo         OK  %MANAGER_DLL%
echo [check] server dll ............
echo         OK  %SERVER_DLL%
echo [check] server dll next to mgr.
if exist "%MANAGER_PEER%" (
  echo         OK
) else (
  echo         MISSING - spawn will fail with instance-boot-failed
)
echo [check] slot newer than src ...
if defined STALE (echo         NO - stale, preflight would rebuild) else (echo         OK)
echo [check] card pack .............
if exist "%PACKS%\manifest.json" (echo         OK  %PACKS%) else (echo         MISSING manifest.json)
echo [check] port %PORT% ..............
netstat -ano | findstr /C:":%PORT%" | findstr /C:"LISTENING" >nul 2>nul
if errorlevel 1 (echo         FREE) else (echo         IN USE - stop the running manager first)
echo [check] mcp.json ..............
if not exist "%MCP_JSON%" (
  echo         MISSING %MCP_JSON%
) else (
  findstr /C:"orc-manager" "%MCP_JSON%" >nul 2>nul && (echo         entry present) || (echo         entry MISSING - run: start-manager.bat --config-mcp)
  findstr /C:"127.0.0.1:%PORT%" "%MCP_JSON%" >nul 2>nul && (echo         port OK) || (echo         port MISMATCH - expected 127.0.0.1:%PORT%)
)
echo.
echo [check] done.
pause
exit /b 0

rem ---------------------------------------------------------------- write config
:config
where python >nul 2>nul
if errorlevel 1 (
  echo [FAIL] python not found - paste the MCP entry manually:
  echo    "orc-manager": {
  echo      "command": "dotnet",
  echo      "args": ["%MANAGER_DLL:\=\\%", "mcp", "--connect", "127.0.0.1:%PORT%"]
  echo    }
  pause
  exit /b 1
)

python "%~dp0tools\manager_mcp_config.py" --port %PORT%
set "RC=%ERRORLEVEL%"
if "%RC%"=="2" set "RC=0"
echo.
echo [next] restart your host (WorkBuddy) so it reloads MCP servers.
pause
exit /b %RC%

:usage
echo Usage:
echo   start-manager.bat [--port ^<n^>] [--check] [--config-mcp]
echo.
echo   (no option)    start the resident manager on port 51999
echo   --port ^<n^>     listen on a custom port
echo   --check        self-check only (no start)
echo   --config-mcp   upsert the orc-manager entry into the host MCP config
pause
exit /b 0

:halt
echo.
pause
exit /b 1
