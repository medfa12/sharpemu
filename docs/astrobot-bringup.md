<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Astro Bot (PPSA21564) bring-up notes

Working state of the Astro Bot bring-up effort on this fork. Read this before
touching the boot/render path — the "eliminated" table below exists so nobody
re-runs a dead hypothesis.

## Current state (2026-07-21)

- Boots stable, soft-continues past engine asserts (audio-propagation, font).
- Loads ALL menu assets (title_screen, worldmap/hub, pause menus) in ~15 min.
- Renders real scene-mesh geometry (vtx=24192 draws, 100%-content 4K HDR
  targets) since the condvar deadlock fix — previously only fullscreen
  post-passes ever ran.
- NOT yet reached: the interactive menu (libScePad never loads, no
  scePadReadState). Presented swapchain frame is still black/gradient even
  when the scene target has content (present-election picks a black buffer).

## Fixed root causes (keep these in mind, they were hard-won)

| Fix | Commit | Mechanism |
|---|---|---|
| Tonemap outputs black | `a55d1c9`+`8bdc546` | ps=0x500640200 multiplies the scene by a cbuffer exposure scalar (S_BUFFER_LOAD, user-SGPR reg 125, byte offset 116) that reads 0. `SHARPEMU_PS_FORCE_EXPOSURE_SCALAR=<float>` pins it. Root feed (why the guest never writes it) still unknown. |
| Online-init loop | `ec0de11`+`dafb50a` | NP state machine reported SIGNED_OUT via 4 coupled signals; the title retries forever. `SHARPEMU_NP_FAKE_SIGNED_IN=1` + `SHARPEMU_NP_FAKE_USERCTX=1` make it coherently signed-in (incl. firing the registered state callback). |
| Post-load total deadlock | `8ab12f4` | Condvar signal-stealing lost-wakeup in PthreadCondWaitCore: a thread signaling then immediately re-waiting on the same cond consumed the token meant for an older waiter (DrawThread/Draw-Extra-Geometry handshake). Fixed default-on with an epoch guard; covered by KernelPthreadCondvarTests. |
| APR resolve batch-abort | `691b790` | One missing `~~N` variant file aborted registration of a whole resolve batch. Now registers what resolves. |

## Eliminated hypotheses — DO NOT RE-RUN

Rendering-black (each disproven by measurement, mostly via
`SHARPEMU_CAPTURE_DRAWS=1` per-draw VkImage readback):
geometry/transform collapse; s107/VRcp NaN; EXEC mask zero at export
(measured TRUE via forced select); exposure 1x1 TEXTURE at 0x532830000
(forcing it non-zero changed nothing — the scalar is a CBUFFER value);
HDR10 A2R10G10B10 output format; unbound-smem zeroing; cbuffer re-read race;
viewport 1x1 clip; post-draw clear; input aliasing; present-selection as the
cause of the black tonemap output.

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

1. Pool/reuse per-draw Vulkan objects (command buffers, fences, image views,
   host buffers) instead of create-per-draw + destroy-per-reap. This is the
   FPS lever; home: `VulkanVideoPresenter.GuestMemoryPool.cs` /
   `PipelineCaches.cs` partials.
2. Present election: scene renders to 0x520440000 with 100% content but the
   presented frame is black — fix the flip/composite election or the tonemap
   input binding for the post-deadlock frame graph.
3. Find why the exposure cbuffer scalar is 0 (proper fix for the tonemap
   force) and why libScePad never loads (what gates the interactive gamemode).
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
