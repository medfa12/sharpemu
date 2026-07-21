<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SHARPEMU_* environment flags — working reference

The tree defines ~150 `SHARPEMU_` flags (grep for the authoritative list).
This documents the ones that matter for current bring-up work, grouped by
intent, so nobody has to reverse-engineer them from call sites. All are
default-off unless noted.

## Compatibility shims (needed to progress Astro Bot today)

| Flag | Effect |
|---|---|
| `SHARPEMU_NP_FAKE_SIGNED_IN=1` | NP state=SIGNED_IN, reachability=Reachable, fires registered state callbacks. Without it the title loops at online-init. |
| `SHARPEMU_NP_FAKE_USERCTX=1` | sceNpWebApi2CreateUserContext returns a synthetic context instead of NOT_SIGNED_IN. Set together with the above. |
| `SHARPEMU_PS_FORCE_EXPOSURE_SCALAR=<float>` | Pins the tonemap shader's (ps=0x500640200) cbuffer exposure scalar (reads 0 otherwise → black frame). 0.5 is a sane start; 1.0 over-saturates. |

## Diagnostics (each proved its worth; costs noted)

| Flag | Effect / cost |
|---|---|
| `SHARPEMU_LOG_SYNC=1` | Runtime wait/signal trace of all guest sync HLE, tagged by guest thread. THE deadlock-hunting tool. Verbose but boot-speed-safe. |
| `SHARPEMU_DRAW_TIMING=1` | Per-draw sub-step timing (`[LOADER][DRAWTIME]`, incl. capFence/capReap split). Low overhead. |
| `SHARPEMU_PERF_PHASES=1` | Present-to-present frame-period phases incl. idleWaitForGuest (`[LOADER][PHASES]`). Low overhead. |
| `SHARPEMU_DUMP_SWAPCHAIN=1` | Presented-frame RGB dumps (`[LOADER][FRAMEDUMP]`, 24-frame budget). Decode with `scripts/framedecode.py`. |
| `SHARPEMU_TRACE_GUEST_IMAGE_ADDRS=0x..;0x..` | Dump specific guest surfaces (`[LOADER][GIMGDUMP]`). Moderate cost per addr. |
| `SHARPEMU_DUMP_SHADER_ADDR=0x<psAddr>` | Dump a matched draw's SPIR-V (base64) even when it does not throw. Needs `SHARPEMU_CAPTURE_DRAWS=1`. |
| `SHARPEMU_CAPTURE_DRAWS=1` | Authoritative per-draw VkImage readback. **~156 full GPU drains per frame — makes boots ~2 orders slower. Never combine with perf/progress measurements.** |
| `SHARPEMU_TRACE_GUEST_IMAGES=1` | Same cost warning as CAPTURE_DRAWS. |
| `SHARPEMU_LOG_IO=1`, `SHARPEMU_LOG_AMPR=1`/`_READS=1` | File-IO / AMPR command-buffer tracing (found the `~~N` variant batch-abort). |
| `SHARPEMU_LOG_NP=1`, `SHARPEMU_LOG_VIDEOOUT_FPS=1` | NP call trace; submitted/presented FPS lines. |

## Perf experiments (env-gated, effect measured; keep until superseded)

| Flag | Status |
|---|---|
| `SHARPEMU_POOL_DRAW_OBJECTS=1` | Pools per-draw Vulkan objects (fences, command buffers, descriptor pools, per-draw texture image/view/memory) across draws instead of destroy-per-reap; adds LRU + stats to the host-buffer pool. Targets the measured ~570 ms/draw capReap. `[POOLSTATS]` line every 64 draws proves engagement. Not yet boot-verified. |
| `SHARPEMU_REUSE_GUEST_IMAGE_MEMORY=1` | Guest-image device-memory reuse pool. Structurally cannot engage during pure load (no destroys happen); POOLDBG `releases=` counter proves activity either way. Relevant only once eviction/replacement fires. |
| `SHARPEMU_CACHE_RENDERPASS=1` | Caches transient render passes/framebuffers. Correct, but measured cost lives in the reap (capReap), so gain was small. |
| `SHARPEMU_DEEP_PIPELINE=1` | Raises submission/work caps 8/16→32/48. No measured effect (retire speed dominates). |
| `SHARPEMU_POOL_GUEST_ORCHESTRATORS=1` | Pools guest-orchestrator host threads. No measured effect on frame time. |

Removed (do not resurrect without new evidence): `SHARPEMU_ASYNC_PRESENT`
(frames-in-flight present ring; liveness bug, and present measured at 2 ms —
recover from git history at 778838f/e3d1bdf if ever needed),
`SHARPEMU_PS_FORCE_EXEC` (hypothesis refuted before use).
