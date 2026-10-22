@echo off
rem ============================================================================
rem  DEPRECATED - this script no longer starts a server directly.
rem ----------------------------------------------------------------------------
rem  Why: it used --headless / --seat0 / --seat1 / --wait-agent / --exit-on-end,
rem  ALL of which were removed when the server moved to the manager model.
rem  Running it as-is would just print "unknown option" and exit.
rem
rem  Current way to get an AI-vs-agent match:
rem    1. start-manager.bat                    (resident manager, this window)
rem    2. from your host / agent, call the MCP tool:
rem         manager.spawn {
rem           instanceId: "agent-match",
rem           packDirectories: ["D:/OrC-KSD/samples/card-pack-ai"],
rem           seats: [{ kind: "ai" }, { kind: "agent" }],
rem           mode: "play",
rem           deckRules: "none"        // sample pack has 5 card types only
rem         }
rem    3. connect Orc.Mcp.Bridge to the returned port, then drive:
rem         manager.admin { instanceId: "agent-match", op: "conn.admit", args: { connectionId: "..." } }
rem         manager.admin { instanceId: "agent-match", op: "match.start" }
rem ============================================================================
echo [orc] start-agent-player-match.bat is retired.
echo [orc] Use start-manager.bat, then call the MCP tool manager.spawn.
echo.
echo [orc] forwarding to start-manager.bat so this window is still useful...
echo.
call "%~dp0start-manager.bat" %*
exit /b %ERRORLEVEL%
