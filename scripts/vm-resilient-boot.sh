#!/bin/bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Boot-test wrapper that survives Spot-VM preemption: starts the VM if it is
# stopped, waits for the metadata watcher, queues the job, and retries the
# whole cycle (up to 5x) if the VM is preempted mid-run.
#
#   vm-resilient-boot.sh <tag> <env-tokens> [secs]
#
# Same env-token format and overridable environment as vm-fastboot.sh.
# Always rebuild-mode is NOT assumed: pass-through of an existing uploaded
# build; run vm-fastboot.sh with "rebuild" once first if code changed.
set -u
TAG="${1:?tag required}"
ENVTOK="${2:-}"
SECS="${3:-120}"
PROJ="${SHARPEMU_VM_PROJECT:-plated-life-480308-b1}"
ZONE="${SHARPEMU_VM_ZONE:-us-central1-a}"
VM="${SHARPEMU_VM_NAME:-sharpemu-t4}"
BUCKET="${SHARPEMU_VM_BUCKET:-gs://sharpemu-drop-plated-life}"
DST="${SHARPEMU_OUT:-/tmp/sharpemu-boot}"
mkdir -p "$DST"
JOB="rar:astrobot,secs=$SECS,${ENVTOK:+$ENVTOK,}$TAG"
say(){ echo "$(date -u +%H:%M:%SZ) $*"; }
status(){ gcloud compute instances describe $VM --project=$PROJ --zone=$ZONE --format="value(status)" 2>/dev/null; }

for attempt in 1 2 3 4 5; do
  say "attempt $attempt"
  st=$(status)
  if [ "$st" != "RUNNING" ]; then
    say "starting VM (was $st)..."
    gcloud compute instances start $VM --project=$PROJ --zone=$ZONE >/dev/null 2>&1 || { say "start failed (capacity); wait 60"; sleep 60; continue; }
    sleep 170
  fi
  # watcher warmup
  for i in $(seq 1 10); do
    [ "$(status)" != "RUNNING" ] && break
    if gcloud compute instances get-serial-port-output $VM --project=$PROJ --zone=$ZONE --port=3 2>/dev/null | tail -30 | grep -qE "watcher v[0-9]+ online|WATCH|job "; then say "watcher up"; break; fi
    sleep 20
  done
  [ "$(status)" != "RUNNING" ] && { say "VM died during warmup; retry"; continue; }
  gcloud compute instances add-metadata $VM --project=$PROJ --zone=$ZONE --metadata=^@^sharpemu-job="$JOB" >/dev/null 2>&1
  say "queued $TAG (attempt $attempt)"
  landed=0
  for i in $(seq 1 48); do
    if gsutil -q stat "$BUCKET/logs/$TAG-err.txt" 2>/dev/null; then landed=1; break; fi
    if [ "$(status)" != "RUNNING" ]; then say "VM terminated mid-run; retry"; break; fi
    sleep 15
  done
  if [ "$landed" = "1" ]; then
    gsutil -q cp "$BUCKET/logs/$TAG-err.txt" "$DST/$TAG-err.txt" 2>/dev/null
    say "=== $TAG DONE (attempt $attempt) -> $DST/$TAG-err.txt ==="
    exit 0
  fi
done
say "GAVE UP after retries (persistent spot preemption in $ZONE)"; exit 1
