<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Astro Bot (PPSA21564) bring-up notes

Working state of the Astro Bot bring-up effort on this fork. Read this before
touching the boot/render path — the "eliminated" table below exists so nobody
re-runs a dead hypothesis.

## Current state (2026-07-22)

- Boots stable, soft-continues past engine asserts (audio-propagation, font,
  and the still-open `SoundManager.cpp:306 defaultBusses.size()==1` audio-bus
  assert), presents 4K guest frames, loads all menu assets.
- **The black-screen tonemap root cause is now FOUND and firmware-confirmed
  (see below).** With the fix, the display buffer 0x507410000 goes from
  0-nonblack to fully filled — the presented frame is no longer black, but a
  washed grey (the exposure is still a stopgap; see the two remaining items).
- NOT yet reached: the true interactive menu render. Two gates remain, both
  under active work on branches: (a) the SoundManager default-bus audio gate,
  (b) real auto-exposure so the tonemap produces the correct image instead of
  washed grey.

### Tonemap black — ROOT CAUSE (2026-07-22, cross-validated two ways)

Confirmed both empirically (per-draw capture on the T4) and against the
decrypted PS5 firmware (`libSceAgcDriver`/oracle disassembly). The tonemap
pixel shader read its gamut/exposure constant buffers as ZERO because of two
independent bugs plus a missing input:

1. **GFX10 scalar operand 125 is architectural NULL, not a mutable `s125`.**
   Our SMEM decoder treated encoding 125 as a real register, which offset the
   base of *every* constant-buffer read in the shader. Fixed: decode 125 as
   constant zero (ISA-correct; matches Kyty + the firmware disassembly).
2. **The shader scalar evaluator recorded only ONE `s_buffer_load` binding
   (PC 0x38) and zero-filled the destinations of every other reachable SMEM
   load.** So the gamut constants (e.g. s0=0.1915, s1=-0.57 at cbuffer
   byte 48) read 0 → the grade cancelled the scene to black. The proper fix
   is CFG-based resource discovery in `Gen5ShaderScalarEvaluator.cs`;
   `SHARPEMU_RECOVER_UNBOUND_SMEM` / `SHARPEMU_ASTRO_TONEMAP_FIX` recover the
   nearest same-descriptor binding as an interim.
3. **The 1x1 auto-exposure luminance at ~0x532830000 is never written back to
   guest memory** (the game's luminance-reduction compute runs on-GPU but its
   output stayed in device memory). The tonemap sampled zero exposure. Real
   fix is compute→guest-memory writeback (`SHARPEMU_COMPUTE_WRITEBACK`);
   `SHARPEMU_ASTRO_TONEMAP_FIX` substitutes a 0.25 constant as a stopgap
   (hence the washed grey).

## Fixed root causes (keep these in mind, they were hard-won)

| Fix | Commit | Mechanism |
|---|---|---|
| Tonemap outputs black | `a55d1c9`+`8bdc546` (stopgap); root cause 2026-07-22 (branch `gpu/tonemap`) | Originally patched with `SHARPEMU_PS_FORCE_EXPOSURE_SCALAR`. The actual root feed is now known and firmware-confirmed — the s125-NULL decode + zero-filled SMEM cbuffer bindings + un-written-back auto-exposure (see "Tonemap black — ROOT CAUSE" above). |
| Online-init loop | `ec0de11`+`dafb50a` | NP state machine reported SIGNED_OUT via 4 coupled signals; the title retries forever. `SHARPEMU_NP_FAKE_SIGNED_IN=1` + `SHARPEMU_NP_FAKE_USERCTX=1` make it coherently signed-in (incl. firing the registered state callback). |
| Post-load total deadlock | `8ab12f4` | Condvar signal-stealing lost-wakeup in PthreadCondWaitCore: a thread signaling then immediately re-waiting on the same cond consumed the token meant for an older waiter (DrawThread/Draw-Extra-Geometry handshake). Fixed default-on with an epoch guard; covered by KernelPthreadCondvarTests. |
| APR resolve batch-abort | `691b790` | One missing `~~N` variant file aborted registration of a whole resolve batch. Now registers what resolves. |

## Eliminated hypotheses — DO NOT RE-RUN

Rendering-black (each disproven by measurement, mostly via
`SHARPEMU_CAPTURE_DRAWS=1` per-draw VkImage readback):
geometry/transform collapse; s107/VRcp NaN; EXEC mask zero at export
(measured TRUE via forced select); exposure 1x1 TEXTURE at 0x532830000
(forcing it non-zero *alone* changed nothing — but see below, it IS one of
three coupled causes); HDR10 A2R10G10B10 output format; cbuffer re-read race;
viewport 1x1 clip; post-draw clear; input aliasing; present-selection as the
cause of the black tonemap output.

⚠️ CORRECTION (2026-07-22): "unbound-smem zeroing" was previously listed here
as eliminated — that was WRONG. Recovering the unbound SMEM cbuffer bindings
(plus the s125-NULL decode) is exactly what fixed the black tonemap. See the
"Tonemap black — ROOT CAUSE" section above. And forcing the 1x1 exposure alone
"changed nothing" only because the missing cbuffer bindings *independently*
produced zero/NaN — with the bindings recovered, the exposure DOES matter.

Performance ~0.5 FPS (each disproven by instrumented boots):
GPU sync/present flush (measured gpuFenceWait=0.0ms, present=2ms);
Windows timer quantum (already 1 ms via HostTimerResolution, PR #130);
guest-orchestrator thread churn (pooling it changed nothing);
VRAM pressure / allocation reuse ("gross" is a cumulative counter, live is
flat ~700 MB; and DestroyGuestImage never fires during pure load, so the
reuse pool structurally cannot engage — see POOLDBG `releases=` counter).

MEASURED remaining perf cost: `capReap` ~570 ms/draw — CPU-side per-draw
Vulkan object teardown in the submission reap (image views, command buffers,
fences, buffers destroyed every draw). `capFence=0`: the GPU itself is fast.

## Next steps, ranked

1. DONE IN CODE, NEEDS BOOT VERIFICATION: per-draw Vulkan object pooling
   landed opt-in as `SHARPEMU_POOL_DRAW_OBJECTS=1` (partial
   `VulkanVideoPresenter.DrawObjectPool.cs`). Pools fences, command buffers,
   descriptor pools (bucketed by descriptor counts), and per-draw OwnsStorage
   texture image/view/memory triples; adds LRU + stats to the existing
   host-buffer pool. Parking happens only at the reap's post-fence-signal
   destroy points. A `[POOLSTATS]` line every 64 draws proves engagement
   (rent-hit/miss/park/trim per class) — check it FIRST on the verification
   boot, then compare capReap. Storage-scratch images and
   ownership-transferring first-uploads are deliberately excluded.
2. Present-election black: DIAGNOSED (code-read, high conf) — the election
   is NOT broken. The game flips 0x507410000/0x5093F0000, written only by the
   tonemap ps=0x500640200, which multiplies by the zero exposure scalar; with
   `SHARPEMU_PS_FORCE_EXPOSURE_SCALAR` unset the elected buffer is
   legitimately black and faithfully blitted (the nodeadlock1/scene1 boots
   did not set the flag). The scene target 0x520440000 is an intermediate
   that never enters the election. Discriminating boot (NOT a perf boot):
   `SHARPEMU_PS_FORCE_EXPOSURE_SCALAR=0.1` + `SHARPEMU_CAPTURE_DRAWS=1` +
   `SHARPEMU_DUMP_SWAPCHAIN=1`; grep `[CAPTURE].*ps=0x0000000500640200` for
   in0/out0 addr+nonblack (in0 stale/black => tonemap input rebinding; out0
   nonblack but swapchain black => election/staleness), then
   `vk.submit_call|vk.flip_redirect|vk.submit_guest_image_unknown`, then
   FRAMEDUMP.
3. ANSWERED (2026-07-22): why the exposure/gamut cbuffer scalars read 0 — the
   s125-NULL decode + zero-filled SMEM bindings (see ROOT CAUSE above). Proper
   fixes in flight: CFG-based SMEM resource discovery in
   `Gen5ShaderScalarEvaluator.cs` and `SHARPEMU_COMPUTE_WRITEBACK` for real
   auto-exposure. Still open: the `SoundManager` default-bus audio gate, and
   why libScePad never loads (what gates the interactive gamemode).
4. Watch run-to-run variance — if threads park again, use
   `SHARPEMU_LOG_SYNC=1` (see methodology) to find the next lost wakeup.

## Methodology (learned the expensive way)

- Measure before fixing. Two multi-boot detours (AMPR zero-fill, GPU-sync)
  came from acting on a plausible theory without confirming the actual code
  path / phase timing first. `SHARPEMU_DRAW_TIMING` and `SHARPEMU_PERF_PHASES`
  exist so one boot attributes cost precisely.
- NEVER run progress/perf boots with `SHARPEMU_CAPTURE_DRAWS` or
  `SHARPEMU_TRACE_GUEST_IMAGES` — they QueueWaitIdle per draw (~156x/frame)
  and invalidate all timing (and slowed a week of boots once).
- Deadlock hunts: boot with `SHARPEMU_LOG_SYNC=1`, grep `[LOADER][SYNC]`,
  find threads whose last action is a park with no later wake, then trace the
  `owner=` of the mutex they block on.
- Frame inspection: `[LOADER][FRAMEDUMP]`/`[GIMGDUMP]` base64 → PNG via
  `scripts/framedecode.py`; target a specific surface with
  `SHARPEMU_TRACE_GUEST_IMAGE_ADDRS=0x<addr>[;0x<addr>]`.

## Boot loop (GCP VM)

VM `sharpemu-t4` (project plated-life-480308-b1, us-central1-a), Windows +
T4-vWS, persistent 500 GB disk holding the game dump at
`C:\games\astrobot-rar\PPSA21564-app`, ffmpeg (Bink2 build) at
`C:\ffmpeg\bin`, VB-Audio Virtual Cable installed. A watcher on the VM polls
instance metadata key `sharpemu-job`; `scripts/vm-fastboot.sh` drives the
zip→upload→job→poll loop (see script header for usage). The VM is normally
STOPPED (start it first); Spot preemption is common — retry.

## Boot loop — fast VM iteration (2026-07-22, supersedes the metadata-watcher path above)

The current, much faster loop uses a real git checkout on the VM + incremental
build instead of tarball upload + full publish:

- VM `astro-vm` (project `pfe-ey`, us-central1-a, Windows 11 + T4-vWS). NOTE the
  external IP is EPHEMERAL and changes on every (re)start (spot preemption is
  common) — update `scripts/vm-astro.sh` and the git remotes after each start.
- ONE build on the VM: `C:\r1` is a git repo (push target,
  `receive.denyCurrentBranch=updateInstead`) built incrementally to
  `C:\r1\artifacts\bin\Release\net10.0\win-x64\SharpEmu.exe`. `C:\dotnet` = SDK,
  `C:\mingit` = git, `C:\glfw3.dll` = the loose glfw the build copies in.
- `scripts/vm-astro.sh <r1> <secs> "<ENV k=v;k=v>"` from a worktree pushes HEAD
  (delta, ~1s), builds incrementally on the VM (~10–25s vs ~90s publish), boots
  Astro Bot in the interactive session for `<secs>`, and prints render signals.
  The GLFW window needs an interactive session (headless CopyFromScreen returns
  blank) — use `SHARPEMU_DUMP_SWAPCHAIN=1` + `scripts/framedecode.py` for a
  reliable frame instead of a desktop screenshot.

## Firmware "oracle" — replace stubs with correct implementations (2026-07-22)

`games/ps5-403-oracle/filesystems/merged/` is a decrypted PS5 4.03 firmware
(569 native modules = the REAL Sony implementations). Use it as ground truth:

- `python3 scripts/oracle_index.py` → `scripts/oracle_nids.tsv`
  (NID⟶name⟶module⟶vaddr⟶size for 279k exported symbols).
- `python3 scripts/oracle_disasm.py <NID>…` → x86-64 disassembly of the real
  function. Classify (pure-logic / syscall-wrapper / ioctl), extract the
  OBSERVABLE contract (struct field offsets, return/error code per branch,
  side effects), name it via the SDK 4.00 headers (`games/PS5_SDK_4_00`), and
  reimplement faithfully. Match what the game observes; verify two ways (disasm
  ground truth + boot behaviour). Some "stubs" are stubs in the firmware too
  (e.g. `sceAgcDriverRegisterResource` = 6 bytes returning `0x8A6C9018`).

> Keeping this doc current: when a boot-blocking hypothesis is confirmed or
> refuted, or a `SHARPEMU_*` flag is added, update the sections above AND
> `docs/env-flags.md` in the same change. The living detail lives in the
> maintainer's memory map; this file is the shareable summary.
