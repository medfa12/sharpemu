#!/usr/bin/env bash
# vm-astro.sh <r1|r2> <secs> "<ENV k=v;k=v>" -- deploy current git HEAD to the VM, build incrementally, boot Astro Bot, print render signals.
set -euo pipefail
REPO="${1:-r1}"; SECS="${2:-120}"; ENVP="${3:-none}"
KEY="$HOME/astro-vm-ssh-key"; VM="astro@"
export GIT_SSH_COMMAND="ssh -i $KEY -o StrictHostKeyChecking=accept-new"

# astro-run.ps1 expands each pair as `set NAME=VALUE && ...`. In cmd.exe the
# space before `&&` becomes part of VALUE, so flags tested against the exact
# string "1" silently stay disabled. Join the requested assignments with `&`
# and append a throwaway assignment that alone absorbs that trailing space.
# Restrict the accepted syntax before embedding it in the remote cmd line.
REMOTE_ENVP="$ENVP"
if [[ -n "$ENVP" && "$ENVP" != "none" ]]; then
  REMOTE_ENVP=""
  IFS=';' read -r -a ENV_ITEMS <<< "$ENVP"
  for ITEM in "${ENV_ITEMS[@]}"; do
    ITEM="${ITEM#"${ITEM%%[![:space:]]*}"}"
    ITEM="${ITEM%"${ITEM##*[![:space:]]}"}"
    if [[ ! "$ITEM" =~ ^SHARPEMU_[A-Z0-9_]+=[A-Za-z0-9_.,:+-]+$ ]]; then
      echo "[vm-astro] invalid env pair: $ITEM" >&2
      exit 2
    fi
    if [[ -n "$REMOTE_ENVP" ]]; then
      REMOTE_ENVP+="& set "
    fi
    REMOTE_ENVP+="$ITEM"
  done
  REMOTE_ENVP+="& set SHARPEMU_VM_ENV_SENTINEL=1"
fi

echo "[vm-astro] pushing HEAD -> vm-$REPO"
git push -f "$VM:C:/$REPO" HEAD:master 2>&1 | tail -1
echo "[vm-astro] build+boot ($SECS s, env='$ENVP') ..."
ssh -i "$KEY" "$VM" "powershell -ExecutionPolicy Bypass -File C:\\sharpemu\\astro-run.ps1 -Repo $REPO -Secs $SECS -EnvPairs '\"$REMOTE_ENVP\"'"
echo "[vm-astro] === render signals ==="
ssh -i "$KEY" "$VM" "powershell -ExecutionPolicy Bypass -File C:\\sharpemu\\astro-signals.ps1 -Repo $REPO"
