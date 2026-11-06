#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")"
pwsh -NoProfile -File ./prepare-release.ps1
