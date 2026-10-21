@echo off
rem Launch Orc.Server headless (play mode) with the ai sample card pack.
rem Keep this window open: the MCP bridge connects to this process on port 51888.
cd /d "%~dp0"
echo [orc] cwd = %CD%
echo [orc] starting Orc.Server.exe --headless --mode play --packs samples\card-pack-ai --seat1 agent --port 51888
src\Orc.Server\bin\Debug\net8.0\Orc.Server.exe --headless --mode play --packs .\samples\card-pack-ai --seat0 ai --seat1 agent --port 51888 --run-dir .\outputs\agent-match\mcp-run --wait-agent 3600 --exit-on-end
echo.
echo [orc] server exited with code %ERRORLEVEL%
pause
