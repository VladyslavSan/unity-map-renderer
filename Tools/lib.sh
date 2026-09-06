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

# ── Editor state ─────────────────────────────────────────────────────────────────────────────────────
# Batch mode cannot share the project with an open Editor — but only THIS project's Editor matters: a
# Unity editing a *different* clone (e.g. unity-map-renderer-test) is fine and must not block us.
# Exact-equality is deliberate: a substring match would wrongly fire on a sibling like "${ROOT}-test".
# The lowercase compare handles the Editor's `-projectpath` vs batch mode's `-projectPath`. A lockfile
# with no such process is STALE (a prior batch run was killed and left it behind) — callers clear it
# rather than refuse forever. Requires $ROOT to be set by the caller.
this_project_editor_open() {
  local pid pp
  for pid in $(pgrep -x Unity 2>/dev/null); do
    pp="$(ps -ww -o command= -p "$pid" 2>/dev/null \
          | awk '{for(i=1;i<NF;i++) if(tolower($i)=="-projectpath"){print $(i+1);exit}}')"
    [ "$pp" = "$ROOT" ] && return 0
  done
  return 1
}
