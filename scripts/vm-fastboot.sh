#!/bin/bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Fast boot-test iteration against the GCP test VM.
#
#   vm-fastboot.sh <tag> <env-tokens> [rebuild|runonly] [secs]
#
# - runonly (default): reuse the VM's already-built binary, only change the
#   env -> skips the ~40s VM-side build.
# - rebuild: re-zip + upload the source tree + build (use when code changed).
# - secs: guest run window in seconds (default 120; menu-load work needs 600+).
# - env-tokens: comma-separated "env:K=V" tokens WITHOUT a trailing comma,
#   e.g. "env:SHARPEMU_NP_FAKE_SIGNED_IN=1,env:SHARPEMU_PERF_PHASES=1".
#
# The VM runs a watcher polling the instance-metadata key "sharpemu-job";
# results upload to $BUCKET/logs/<tag>-err.txt. Decode frame dumps in the log
# with scripts/framedecode.py. See docs/astrobot-bringup.md.
#
# Overridable via environment: SHARPEMU_VM_PROJECT, SHARPEMU_VM_ZONE,
# SHARPEMU_VM_NAME, SHARPEMU_VM_BUCKET, SHARPEMU_SRC (repo/worktree to zip),
# SHARPEMU_OUT (where logs land).
set -u
TAG="${1:?tag required}"
ENVTOK="${2:-}"
MODE="${3:-runonly}"
SECS="${4:-120}"
PROJ="${SHARPEMU_VM_PROJECT:-plated-life-480308-b1}"
ZONE="${SHARPEMU_VM_ZONE:-us-central1-a}"
VM="${SHARPEMU_VM_NAME:-sharpemu-t4}"
BUCKET="${SHARPEMU_VM_BUCKET:-gs://sharpemu-drop-plated-life}"
WT="${SHARPEMU_SRC:-$(cd "$(dirname "$0")/.." && pwd)}"
DST="${SHARPEMU_OUT:-/tmp/sharpemu-boot}"
mkdir -p "$DST"
say(){ echo "$(date -u +%H:%M:%SZ) $*"; }

JOB=""
if [ "$MODE" = "rebuild" ]; then
  cd "$WT" || exit 1
  say "rebuild: zipping + uploading source..."
  rm -f "$DST/sharpemu-src.zip"
  zip -qr "$DST/sharpemu-src.zip" src tests tools assets scripts LICENSES \
    SharpEmu.slnx Directory.Build.props Directory.Packages.props global.json nuget.config \
    LICENSE.txt README.md CONTRIBUTING.md REUSE.toml \
    -x "*/bin/*" "*/obj/*" "*/artifacts/*" "artifacts/*" "*/__pycache__/*"
  gsutil -q cp "$DST/sharpemu-src.zip" "$BUCKET/sharpemu-src.zip" || { say "upload failed"; exit 1; }
  JOB="rebuild,rar:astrobot,secs=$SECS,${ENVTOK:+$ENVTOK,}$TAG"
else
  JOB="rar:astrobot,secs=$SECS,${ENVTOK:+$ENVTOK,}$TAG"
fi

gcloud compute instances add-metadata $VM --project=$PROJ --zone=$ZONE \
  --metadata=^@^sharpemu-job="$JOB" >/dev/null 2>&1
say "queued $TAG ($MODE, ${SECS}s) job=[$JOB]"

# tighter early poll (10s), back off to 20s
for i in $(seq 1 90); do
  if gsutil -q stat "$BUCKET/logs/$TAG-err.txt" 2>/dev/null; then
    gsutil -q cp "$BUCKET/logs/$TAG-err.txt" "$DST/$TAG-err.txt" 2>/dev/null
    say "=== $TAG DONE -> $DST/$TAG-err.txt ($(wc -l < "$DST/$TAG-err.txt") lines) ==="
    exit 0
  fi
  if [ "$i" -le 12 ]; then sleep 10; else sleep 20; fi
done
say "TIMEOUT $TAG"; exit 1
