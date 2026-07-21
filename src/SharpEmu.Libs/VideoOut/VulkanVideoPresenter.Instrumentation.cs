// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using SharpEmu.Libs.Agc;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Windowing;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private static readonly bool _perfPhases =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PERF_PHASES"),
            "1",
            StringComparison.Ordinal);
    // SHARPEMU_DRAW_TIMING: fine-grained, per-sub-step CPU timing of the
    // offscreen-draw drain. Isolates which part of ExecuteOffscreenDraw is slow
    // (guest-image setup, texture/buffer resolve, descriptor build, pipeline
    // key lookup, pipeline creation, command recording, submit) so a
    // per-draw-catch-all 'drainExec' can be broken down. Every accumulator is
    // touched only under this gate, so a default run is byte-identical and the
    // per-draw path stays allocation-free (Stopwatch.GetTimestamp + long adds).
    private static readonly bool _drawTiming =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_DRAW_TIMING"),
            "1",
            StringComparison.Ordinal);
    private sealed partial class Presenter : IDisposable
    {
        // SHARPEMU_PERF_PHASES: measures the FULL frame period, not just the
        // inside of one present. Because presents can be many Render() calls
        // apart (the render thread idles in WaitForRenderWork between them), the
        // idle/drain/fence times are ACCUMULATED across every Render() call and
        // flushed on the next present, so one line is emitted per present with a
        // true present-to-present period and a breakdown of where it went.
        // A heartbeat also fires during long stalls so the log never goes silent.
        private long _perfPhaseFrame;
        private long _perfPeriodStartTs;   // timestamp of last present (period base)
        private long _perfLastEmitTs;      // last time any PHASES line was emitted
        private long _perfIdleAccumTicks;  // WaitForRenderWork time since last present
        private long _perfDrainAccumTicks; // guest offscreen/compute drain time
        private long _perfFenceAccumTicks; // blocking GPU fence/idle waits
        private const double PerfHeartbeatMs = 1000.0;
        // SHARPEMU_DRAW_TIMING accumulators (render thread only, so no locking).
        // Ticks are Stopwatch timestamps summed per sub-step; a DRAWTIME line is
        // emitted and every accumulator is reset every DrawTimingEmitInterval
        // real (submitted) draws, so each line is a per-interval breakdown.
        private long _dtValidateTicks;       // prologue: MRT format/blend validation + LINQ, pre-setup
        private long _dtSetupTicks;          // guest-image get + depth + transient pass (incl capacityWait)
        private long _dtCapacityWaitTicks;   // EnsureGuestSubmissionCapacity blocking + reaping (subset of setup)
        private long _dtCapFenceTicks;       // capacityWait: pure GPU fence-wait (WaitForFences on oldest)
        private long _dtCapReapTicks;        // capacityWait: CPU reap (fb/renderpass/imageview destroys, frees)
        private bool _dtCapacityActive;      // true only inside EnsureGuestSubmissionCapacity, to scope the split
        private long _dtPostTicks;           // post-submit bookkeeping (publish/locks/marks), pre-teardown
        private long _dtTeardownTicks;       // finally: transient framebuffer/renderpass destroy + resource free
        private long _dtResolveTicks;        // texture/global/vertex/index resolve+upload
        private long _dtDescriptorTicks;     // descriptor set/layout build
        private long _dtPipelineLookupTicks; // pipeline key build (incl shader digest) + dict lookup
        private long _dtPipelineCreateTicks; // vkCreateGraphicsPipelines (cache miss only)
        private long _dtRecordTicks;         // command buffer begin..end recording
        private long _dtSubmitTicks;         // QueueSubmit + fence create/enqueue
        private long _dtTotalTicks;          // whole per-draw span (for 'other' derivation)
        private int _dtDraws;                // submitted draws in the current interval
        private long _dtDrawsTotal;          // cumulative submitted draws (context only)
        private int _dtPipelineHits;         // graphics pipeline cache hits
        private int _dtPipelineMisses;       // graphics pipeline cache misses (real vkCreate)
        private int _dtDigestMisses;         // shader-digest cache misses (recomputed SHA256)
        private const int DrawTimingEmitInterval = 50;
        // SHARPEMU_CAPTURE_DRAWS=1 emits one authoritative capture.draw line per
        // offscreen graphics draw whose output render target(s) and sampled input
        // texture(s) carry nonblack_pixels read from the real bound VkImage (via
        // the same readback as vk.guest_image / TraceGuestImageContents), never
        // from guest memory.
        private static readonly bool CaptureDrawsEnabled = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_CAPTURE_DRAWS"),
            "1",
            StringComparison.Ordinal);
        // SHARPEMU_DUMP_SHADER_ADDR=0x<pixelShaderAddr>: dump the vertex+pixel
        // SPIR-V (base64) once for any capture.draw whose pixel shader matches,
        // regardless of whether translation/submit threw. The existing SPIRVDUMP
        // path only fires on a draw that raises, but a shader that translates and
        // runs yet writes black (e.g. the final tonemap/composite) never throws,
        // so it can only be captured by matching its address here. Piggybacks on
        // SHARPEMU_CAPTURE_DRAWS (which must also be set).
        private static readonly ulong DumpShaderAddr = ParseDumpShaderAddr();
        private readonly HashSet<ulong> _dumpedTargetShaders = new();

        private static ulong ParseDumpShaderAddr()
        {
            var raw = Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SHADER_ADDR");
            if (string.IsNullOrEmpty(raw))
            {
                return 0;
            }

            var span = raw.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                span.StartsWith("0X", StringComparison.Ordinal))
            {
                span = span[2..];
            }

            return ulong.TryParse(
                span,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
                ? value
                : 0;
        }
        // Monotonic id assigned to each offscreen draw when SHARPEMU_CAPTURE_DRAWS
        // is set, so a capture.draw line can be pinned to a specific draw in
        // execution order on the single render thread.
        private long _captureDrawSeq;
        // Throttled [POOLDBG] gate: full detail for the first events, then a
        // periodic sample, so a diagnostic run (SHARPEMU_REUSE_GUEST_IMAGE_MEMORY)
        // shows park/rent/skip decisions without flooding the uploaded log.
        private long _poolDebugEvents;
        private bool ShouldTracePool()
        {
            if (!_reuseGuestImageMemory)
            {
                return false;
            }

            var n = ++_poolDebugEvents;
            return n <= 300 || (n & 127) == 0;
        }
        private static double TicksToMs(long ticks) =>
            ticks * 1000.0 / Stopwatch.Frequency;
        // Emits one SHARPEMU_PERF_PHASES line. presented=true is a real present
        // (full period + this frame's present span); presented=false is a stall
        // heartbeat (present span 0, partial=1) so a long idle gap still logs.
        private void EmitPerfPhases(long nowTs, long presentTicks, bool presented)
        {
            var periodMs = _perfPeriodStartTs == 0
                ? 0.0
                : Stopwatch.GetElapsedTime(_perfPeriodStartTs, nowTs).TotalMilliseconds;
            Console.Error.WriteLine(
                $"[LOADER][PHASES] frame={_perfPhaseFrame} period={periodMs:F2} " +
                $"drainExec={TicksToMs(_perfDrainAccumTicks):F2} " +
                $"gpuFenceWait={TicksToMs(_perfFenceAccumTicks):F2} " +
                $"present={TicksToMs(presentTicks):F2} " +
                $"idleWaitForGuest={TicksToMs(_perfIdleAccumTicks):F2}" +
                (presented ? string.Empty : " partial=1"));
        }
        // Emits one SHARPEMU_DRAW_TIMING breakdown line covering the draws since
        // the last emit, then zeroes the interval accumulators. 'other' is the
        // whole-draw span minus every measured sub-step (validation/LINQ, guest
        // registry lock sections, cleanup) so nothing is hidden. pMiss==draws
        // means a real vkCreateGraphicsPipelines every draw (driver shader
        // compile); digMiss==2*draws means the shader-digest cache never hits
        // (a fresh SPIR-V byte[] per draw -> SHA256 recomputed each time).
        private void EmitDrawTiming()
        {
            var validate = TicksToMs(_dtValidateTicks);
            var capacityWait = TicksToMs(_dtCapacityWaitTicks);
            // capacityWait split: capFence = pure GPU fence-wait (submissions
            // retiring on the GPU); capReap = CPU reap/destroy of retired
            // submissions (framebuffer/renderpass/imageview destroys, frees).
            // capFence + capReap ~= capacityWait; if capFence dominates the cost
            // is GPU-execution-bound, if capReap dominates it is CPU-teardown-bound.
            var capFence = TicksToMs(_dtCapFenceTicks);
            var capReap = TicksToMs(_dtCapReapTicks);
            // _dtSetupTicks spans the whole setup phase INCLUDING the capacity
            // wait; report the image-create part alone so setup and capacityWait
            // are disjoint and sum back to the raw setup span.
            var setup = TicksToMs(_dtSetupTicks) - capacityWait;
            var resolve = TicksToMs(_dtResolveTicks);
            var descriptors = TicksToMs(_dtDescriptorTicks);
            var pipeLookup = TicksToMs(_dtPipelineLookupTicks);
            var pipeCreate = TicksToMs(_dtPipelineCreateTicks);
            var record = TicksToMs(_dtRecordTicks);
            var submit = TicksToMs(_dtSubmitTicks);
            var postSubmit = TicksToMs(_dtPostTicks);
            var teardown = TicksToMs(_dtTeardownTicks);
            var total = TicksToMs(_dtTotalTicks);
            var other = total - validate - setup - capacityWait - resolve -
                descriptors - pipeLookup - pipeCreate - record - submit -
                postSubmit - teardown;
            var perDraw = _dtDraws > 0 ? total / _dtDraws : 0.0;
            Console.Error.WriteLine(
                $"[LOADER][DRAWTIME] draws={_dtDraws} total={total:F1} perDraw={perDraw:F2} " +
                $"validate={validate:F1} setup={setup:F1} capacityWait={capacityWait:F1} " +
                $"capFence={capFence:F1} capReap={capReap:F1} " +
                $"resolve={resolve:F1} descriptors={descriptors:F1} " +
                $"pipeLookup={pipeLookup:F1} pipeCreate={pipeCreate:F1} " +
                $"record={record:F1} submit={submit:F1} " +
                $"postSubmit={postSubmit:F1} teardown={teardown:F1} other={other:F1} " +
                $"pHit={_dtPipelineHits} pMiss={_dtPipelineMisses} digMiss={_dtDigestMisses} " +
                $"cumDraws={_dtDrawsTotal}");

            _dtValidateTicks = 0;
            _dtSetupTicks = 0;
            _dtCapacityWaitTicks = 0;
            _dtCapFenceTicks = 0;
            _dtCapReapTicks = 0;
            _dtResolveTicks = 0;
            _dtDescriptorTicks = 0;
            _dtPipelineLookupTicks = 0;
            _dtPipelineCreateTicks = 0;
            _dtRecordTicks = 0;
            _dtSubmitTicks = 0;
            _dtPostTicks = 0;
            _dtTeardownTicks = 0;
            _dtTotalTicks = 0;
            _dtDraws = 0;
            _dtPipelineHits = 0;
            _dtPipelineMisses = 0;
            _dtDigestMisses = 0;
        }
        // SHARPEMU_CAPTURE_DRAWS: emit ONE authoritative line per offscreen draw
        // whose output target(s) and sampled input texture(s) report
        // nonblack_pixels read from the REAL bound VkImage. This is what makes it
        // trustworthy where the old shader_draw probe was not: the numbers come
        // from CmdCopyImageToBuffer against the VkImage (same readback as
        // TraceGuestImageContents), never from guest memory (which is 0 for a
        // GPU-rendered render target).
        //
        // Timing/layout safety: the draw's command buffer was already submitted
        // by the caller. We rebind _commandBuffer to the presentation command
        // buffer and QueueWaitIdle once, draining THIS draw and every earlier
        // queued draw (single graphics queue). After that the output targets hold
        // their final content and every sampled input's producing pass has
        // completed, so each per-image readback (which re-records, submits, and
        // waits on the presentation command buffer) reads a settled VkImage. This
        // mirrors the established vk.guest_write_sample readback pattern, which
        // likewise sets _commandBuffer = _presentationCommandBuffer + QueueWaitIdle
        // before calling TraceGuestImageContents mid-frame.
        private void CaptureDrawContents(
            VulkanOffscreenGuestDraw work,
            GuestImageResource[] targets,
            TranslatedDrawResources resources)
        {
            if (_reclaimInProgress || _memoryPanic)
            {
                return;
            }

            var seq = ++_captureDrawSeq;
            // Drain this draw (and all earlier queued work) so the readbacks below
            // observe settled VkImages. The presentation command buffer is the
            // safe scratch buffer for out-of-band readback; the draw's own command
            // buffer is already submitted and pending free.
            _commandBuffer = _presentationCommandBuffer;
            Check(
                _vk.QueueWaitIdle(_queue),
                "vkQueueWaitIdle(capture draws)");

            if (DumpShaderAddr != 0 &&
                work.Draw.PixelShaderAddress == DumpShaderAddr &&
                _dumpedTargetShaders.Add(DumpShaderAddr))
            {
                Console.Error.WriteLine(
                    $"[LOADER][SPIRVDUMP] addr=0x{DumpShaderAddr:X16} " +
                    $"vs_bytes={work.Draw.VertexSpirv.Length} " +
                    $"ps_bytes={work.Draw.PixelSpirv.Length}");
                Console.Error.WriteLine(
                    $"[LOADER][SPIRVDUMP] addr=0x{DumpShaderAddr:X16} vs_b64={Convert.ToBase64String(work.Draw.VertexSpirv)}");
                Console.Error.WriteLine(
                    $"[LOADER][SPIRVDUMP] addr=0x{DumpShaderAddr:X16} ps_b64={Convert.ToBase64String(work.Draw.PixelSpirv)}");
            }

            var line = new System.Text.StringBuilder();
            line.Append("capture.draw seq=").Append(seq)
                .Append(" ps=0x").Append(work.Draw.PixelShaderAddress.ToString("X16"))
                .Append(" vtx=").Append(work.Draw.VertexCount)
                .Append(" mrt=").Append(targets.Length)
                .Append(" inputs=").Append(resources.Textures.Count(t => !t.IsStorage));

            for (var index = 0; index < targets.Length; index++)
            {
                line.Append(' ');
                AppendCapturedImage(line, "out" + index, targets[index]);
            }

            var inputIndex = 0;
            foreach (var texture in resources.Textures)
            {
                if (texture.IsStorage)
                {
                    continue;
                }

                line.Append(' ');
                var label = "in" + inputIndex;
                inputIndex++;

                // The sampler reads a real VkImage in one of two GPU-content
                // cases: a native-format alias binds the guest-image cache
                // surface directly (GuestImage), or a copy-on-sample bridge copies
                // a producer image into this texture (CopySource is the producer).
                // Both are GuestImageResources the VkImage readback understands.
                // A pure CPU upload (no GuestImage/CopySource) genuinely sources
                // guest memory, so it is reported as src=cpu_upload without a
                // VkImage count -- the very ambiguity this capture exists to avoid
                // conflating with GPU content.
                var source = texture.GuestImage ?? texture.CopySource;
                if (source is { } image)
                {
                    var src = texture.CopySource is not null && texture.GuestImage is null
                        ? "copy"
                        : "alias";
                    AppendCapturedImage(line, label, image, src, texture.Address);
                }
                else
                {
                    line.Append(label)
                        .Append("=[addr=0x").Append(texture.Address.ToString("X16"))
                        .Append(" src=cpu_upload]");
                }
            }

            Console.Error.WriteLine("[LOADER][CAPTURE] " + line);
        }
        private void AppendCapturedImage(
            System.Text.StringBuilder line,
            string label,
            GuestImageResource image,
            string? src = null,
            ulong bindAddress = 0)
        {
            line.Append(label).Append("=[addr=0x").Append(image.Address.ToString("X16"));
            if (src is not null && bindAddress != image.Address)
            {
                line.Append(" bind=0x").Append(bindAddress.ToString("X16"));
            }

            line.Append(" fmt=").Append(image.Format)
                .Append(" size=").Append(image.Width).Append('x').Append(image.Height);
            if (TryReadbackGuestImageNonblack(image, out var nonblack, out var pixels))
            {
                line.Append(" nonblack=").Append(nonblack).Append('/').Append(pixels);
            }
            else
            {
                line.Append(" nonblack=unsupported");
            }

            if (src is not null)
            {
                line.Append(" src=").Append(src);
            }

            line.Append(']');
        }
    }
}
