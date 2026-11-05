#!/usr/bin/env bash
# Local gate before a commit: format check, warnings-as-errors build, .NET tests, Python tests. Stops at the first failure.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet format Tasktor.Linux.slnf --verify-no-changes
dotnet build Tasktor.Linux.slnf -warnaserror -nologo -v q
dotnet test Tasktor.Linux.slnf --no-build -nologo -v q
python3 -m pytest -q worker
echo "check: all green"
