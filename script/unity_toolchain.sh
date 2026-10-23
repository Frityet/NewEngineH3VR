#!/usr/bin/env bash
# Sourced by the local build entry points. The optional native editor is kept
# inside this workspace so installing it never replaces the user's Unity app.
: "${ROOT_DIR:?Set ROOT_DIR to the NativeH3VR workspace before selecting the toolchain}"
NATIVE_UNITY_APP="$ROOT_DIR/.tools/unity2021-arm64-expanded/Unity.pkg.tmp/Payload/Unity/Unity.app"
if [[ -z "${UNITY_EDITOR:-}" ]]; then
  if [[ -x "$NATIVE_UNITY_APP/Contents/MacOS/Unity" ]]; then
    UNITY_EDITOR="$NATIVE_UNITY_APP/Contents/MacOS/Unity"
  else
    UNITY_EDITOR="/Applications/Unity/Unity.app/Contents/MacOS/Unity"
  fi
fi
# This operates on already prepared projects with a resolved package cache.
# Unity 2021's package server is Intel-only, although its compiler/player are ARM64.
UNITY_LOCAL_ARGS=()
if [[ "${NATIVEH3VR_USE_PACKAGE_MANAGER:-0}" != "1" ]]; then
  UNITY_LOCAL_ARGS+=(-noUpm)
fi
