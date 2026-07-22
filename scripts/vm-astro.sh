#!/usr/bin/env bash
# vm-astro.sh <r1|r2> <secs> "<ENV k=v;k=v>" -- deploy current git HEAD to the VM, build incrementally, boot Astro Bot, print render signals.
set -euo pipefail
REPO="${1:-r1}"; SECS="${2:-120}"; ENVP="${3:-none}"
KEY="$HOME/astro-vm-ssh-key"; VM="astro@34.45.90.170"
export GIT_SSH_COMMAND="ssh -i $KEY -o StrictHostKeyChecking=accept-new"
echo "[vm-astro] pushing HEAD -> vm-$REPO"
git push -f "$VM:C:/$REPO" HEAD:master 2>&1 | tail -1
echo "[vm-astro] build+boot ($SECS s, env='$ENVP') ..."
ssh -i "$KEY" "$VM" "powershell -ExecutionPolicy Bypass -File C:\\sharpemu\\astro-run.ps1 -Repo $REPO -Secs $SECS -EnvPairs '\"$ENVP\"'"
echo "[vm-astro] === render signals ==="
ssh -i "$KEY" "$VM" "powershell -ExecutionPolicy Bypass -File C:\\sharpemu\\astro-signals.ps1 -Repo $REPO"
