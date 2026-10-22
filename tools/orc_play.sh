#!/usr/bin/env bash
# OrC human CLI launcher (D13.3). Creates tools/orc/.venv once, installs
# prompt_toolkit (TUI only), then runs the client from the repo root.
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$HERE/.." || exit 2
VENV="$HERE/orc/.venv"
PY="$VENV/bin/python"
ARGS=("$@")
[ ${#ARGS[@]} -eq 0 ] && ARGS=(play)

if [ ! -x "$PY" ]; then
  echo "[orc] creating isolated venv: $VENV"
  python3 -m venv "$VENV" >/dev/null 2>&1 || python -m venv "$VENV" >/dev/null 2>&1
fi

if [ -x "$PY" ]; then
  "$PY" -c "import prompt_toolkit" >/dev/null 2>&1 \
    || "$PY" -m pip install -q --disable-pip-version-check -r "$HERE/orc/requirements.txt" >/dev/null 2>&1
  exec "$PY" "$HERE/orc_play.py" "${ARGS[@]}"
fi

echo "[orc] venv unavailable; running with the current interpreter (--plain only)."
exec python3 "$HERE/orc_play.py" --plain "${ARGS[@]}" 2>/dev/null \
  || exec python "$HERE/orc_play.py" --plain "${ARGS[@]}"
