#!/usr/bin/env bash
# Shared definitions for the Tools/ scripts. SOURCED, never executed:
#
#   . "$(dirname "$0")/lib.sh"
#
# Why this file exists: the answer to "where does variant V of platform P live" was written out
# independently in build.sh, serve-web.sh and package.sh, none of which could see BuildScript.cs — which
# is the only thing that actually decides. That is not hypothetical drift. serve-web.sh had
# `Builds/Web/UnityMapRenderer` hardcoded as its default while `build.sh web --dev` wrote
# `Builds/Web-Development/`, so every development web player was served as the last release one, and the
# only symptom was RuntimeDiagnostics reporting devBuild=False.
#
# BuildScript.cs remains the authority — it composes the path it writes and prints it as `BUILD OK <path>`.
# What is here is the shell's MIRROR of that scheme, and build.sh checks the two against each other on
# every build rather than trusting this file to have kept up.

# ── The output scheme, mirroring BuildScript.OutputRoot / ProductBase / RunBuild ─────────────────────
PRODUCT="UnityMapRenderer"      # BuildScript.ProductBase
OUTPUT_ROOT="Builds"            # BuildScript.OutputRoot

# Directory BuildScript writes a platform's RELEASE player into, relative to the repo root.
# $1 = platform (android|macos|linux|web). Returns 1 on an unknown platform rather than echoing a path
# that would later be created by mkdir -p and look real.
platform_dir() {
  case "$1" in
    android) echo Android ;;
    macos)   echo macOS   ;;
    linux)   echo Linux   ;;
    web)     echo Web     ;;
    *) return 1 ;;
  esac
}

# The artifact BuildScript names inside that directory. $1 = platform.
platform_artifact() {
  case "$1" in
    android) echo "$PRODUCT.apk"    ;;
    macos)   echo "$PRODUCT.app"    ;;
    linux)   echo "$PRODUCT.x86_64" ;;
    web)     echo "$PRODUCT"        ;;   # a directory, not a file
    *) return 1 ;;
  esac
}

# build_dir <platform> [dev]  — dev=1 appends -Development, matching RunBuild's platformDir logic.
build_dir() {
  local dir
  dir="$(platform_dir "$1")" || return 1
  if [ "${2:-0}" -eq 1 ]; then echo "$OUTPUT_ROOT/$dir-Development"; else echo "$OUTPUT_ROOT/$dir"; fi
}

# build_artifact <platform> [dev] — the full path BuildScript writes, repo-root-relative.
build_artifact() {
  local dir artifact
  dir="$(build_dir "$1" "${2:-0}")"      || return 1
  artifact="$(platform_artifact "$1")"   || return 1
  echo "$dir/$artifact"
}

# ── The unity CLI ────────────────────────────────────────────────────────────────────────────────────
# Both scripts drive the editor through it — `unity build` and `unity test` — and neither can do
# anything without it. Having it installed and on PATH is a REQUIREMENT of working on this repo, so
# this refuses rather than hunting for it: a script that guesses at install locations is the thing the
# CLI was adopted to delete.
require_unity_cli() { # $1 = what the caller does with it, for the error message
  command -v unity >/dev/null 2>&1 && return 0
  echo "the \`unity\` CLI is not on PATH. This script ${1} through it — install it and re-run." >&2
  return 1
}

# ── Editor state ─────────────────────────────────────────────────────────────────────────────────────
# Batch mode cannot share the project with an open Editor — but only THIS project's Editor matters: a
# Unity editing a *different* clone (e.g. unity-map-renderer-test) is fine and must not block us, so
# the path compare is exact. `unity editors running` enumerates from the process table plus each
# project's Pipeline lockfile, and needs no package in the project it reports on.
#
# This used to read `pgrep -x Unity` and parse -projectPath out of `ps`. That FAILED OPEN wherever
# pgrep does not exist — Git Bash / MSYS, which this repo's scripts supported — reporting "no editor"
# with a live Unity on the project and no error. Measured, not assumed: with pgrep off PATH the old
# body returned "not open" against a running batch Unity.
#
# It therefore fails CLOSED now. If the CLI cannot answer, callers refuse: being told to close an
# Editor that is not open costs a second, and the other direction costs a corrupted Library.
# Requires $ROOT to be set by the caller.
this_project_editor_open() {
  local running
  if ! running="$(unity editors running --format tsv 2>/dev/null)"; then
    echo "could not ask the \`unity\` CLI which Editors are running — refusing rather than guessing." >&2
    return 0
  fi
  # Columns: Project, Version, PID, Path. Header row skipped; command substitution is never a TTY,
  # so tsv stays tsv here (on a terminal it would render as the human table instead).
  printf '%s\n' "$running" | awk -F'\t' -v root="$ROOT" 'NR>1 && $4==root {f=1} END {exit !f}'
}

# The whole preflight both scripts run before launching batch mode: refuse a live Editor, clear a
# stale lockfile. Callers map the refusal to exit 3.
require_project_unlocked() { # $1 = what the caller is about to run, for the error message
  if this_project_editor_open; then
    echo "The Unity Editor for THIS project is open — close it before running batch ${1}." >&2
    echo "(A Unity editing a different clone is fine.)" >&2
    return 1
  fi
  if [ -e "$ROOT/Temp/UnityLockfile" ]; then
    echo "Stale Unity lockfile present but this project's Editor isn't open — removing it and continuing." >&2
    rm -f "$ROOT/Temp/UnityLockfile"
  fi
  return 0
}
