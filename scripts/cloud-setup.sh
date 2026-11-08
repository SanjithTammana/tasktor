#!/usr/bin/env bash
# Session-start setup for cloud (Linux) sessions: make sure .NET 10 and the Python worker deps exist.
# Safe to run repeatedly. Does nothing on Windows or outside remote sessions.
set -u
[ "${CLAUDE_CODE_REMOTE:-}" = "true" ] || exit 0

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$HOME/.dotnet/tools:$PATH"

if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  echo "[setup] installing .NET SDK 10"
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_ROOT" >/dev/null \
    || echo "[setup] dotnet-install.sh failed; trying the distro package"
fi
if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.' && command -v apt-get >/dev/null 2>&1; then
  (apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq dotnet-sdk-10.0) >/dev/null 2>&1 \
    || echo "[setup] WARNING: .NET install failed; record it in docs/PROGRESS.md under Blockers"
fi

if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  {
    echo "export DOTNET_ROOT=\"$DOTNET_ROOT\""
    echo "export PATH=\"$DOTNET_ROOT:$HOME/.dotnet/tools:\$PATH\""
    echo "export DOTNET_CLI_TELEMETRY_OPTOUT=1"
    echo "export DOTNET_NOLOGO=1"
  } >> "$CLAUDE_ENV_FILE"
fi

if [ -f worker/requirements.txt ]; then
  python3 -m pip install -q -r worker/requirements.txt || echo "[setup] WARNING: pip install failed"
fi
exit 0
