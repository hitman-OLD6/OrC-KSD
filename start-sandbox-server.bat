@echo off
rem ============================================================================
rem  DEPRECATED - superseded by start-manager.bat
rem ----------------------------------------------------------------------------
rem  The old launcher started ONE server and kept it alive by piping admin lines
rem  on stdin. The model is now: a RESIDENT manager + MCP clients.
rem
rem  This file is kept only so existing shortcuts / deep links keep working.
rem  It forwards to start-manager.bat.
rem
rem  To pull up a match, do not use this window: call the MCP tool
rem      manager.spawn { ... }
rem  then drive the returned instance port with Orc.Mcp.Bridge.
rem ============================================================================
echo [orc] start-sandbox-server.bat is superseded by start-manager.bat
echo [orc] forwarding to the resident manager launcher...
echo.
call "%~dp0start-manager.bat" %*
exit /b %ERRORLEVEL%
