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
    // SHARPEMU_REUSE_GUEST_IMAGE_MEMORY: pool retired guest-image device-memory
    // blocks and rent them back for later same-size allocations instead of
    // vkFreeMemory + vkAllocateMemory churning a fresh block every draw. A block
    // is parked the instant its image is destroyed -- even while rendering is
    // active -- stamped with the guest/present submission seqs live at that
    // moment; it is rented back only once those seqs have RETIRED (its own
    // last-use fence has signaled), so a rented block can never alias in-flight
    // work. (The old "pool only while the whole GPU is idle" gate was never true
    // during active rendering, so the pool stayed empty all run.) Default unset
    // keeps the byte-identical free-every-time path. See ReleaseGuestImageMemory
    // / TryRentPooledDeviceMemory.
    private static readonly bool _reuseGuestImageMemory =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_REUSE_GUEST_IMAGE_MEMORY"),
            "1",
            StringComparison.Ordinal);
    private sealed partial class Presenter : IDisposable
    {
        // Free-list of retired guest-image device-memory blocks eligible for
        // reuse (SHARPEMU_REUSE_GUEST_IMAGE_MEMORY). Keyed by the exact
        // (allocation size, memory type) so a rented block satisfies the new
        // image's bind requirements without reallocating. A block is parked the
        // instant its image is destroyed -- NOT only when the whole GPU is idle
        // -- and carries the guest/present submission seqs live at park time. It
        // is rented back only once those seqs have RETIRED (its own last-use
        // fence has signaled), so a rent can never alias in-flight GPU work.
        // Render-thread state, no locking (same as _guestImages). Trimmed to
        // MaxPooledDeviceMemoryBytes and fully drained on OOM/dispose. FIFO per
        // key so the oldest (readiest) block is examined first.
        private readonly record struct PooledDeviceMemoryBlock(
            DeviceMemory Memory,
            long GuestGuardSeq,
            long PresentGuardSeq);
        private readonly Dictionary<(ulong Size, uint MemoryTypeIndex), Queue<PooledDeviceMemoryBlock>>
            _freeDeviceMemoryPool = new();
        private int _pooledDeviceMemoryBlockCount;
        private ulong _pooledDeviceMemoryBytes;
        // POOLDBG: how many times ReleaseGuestImageMemory (the sole guest-image
        // device-memory free path, reached only via DestroyGuestImage) has run.
        // Surfaced in every POOLDBG line so a run that shows only rent misses
        // proves whether guest images are being destroyed at all -- park/free_skip
        // can never engage while this stays 0, and it has its own trace budget
        // below so a park is never silenced by rent-attempt trace flooding.
        private long _guestImageReleaseEvents;
        // Monotonic per-block reuse fence tracking (render-thread only). A parked
        // block is safe to rent once every submission that could still reference
        // it has retired: _guestRetireSeq >= its GuestGuardSeq (all guest-queue
        // submissions enqueued up to park time have completed) and
        // _presentRetireSeq >= its PresentGuardSeq (same for presentation, which
        // reads flip snapshots). These count submitted/retired events across BOTH
        // the async ring and legacy present paths and never reset mid-run.
        private long _guestSubmitSeq;
        private long _guestRetireSeq;
        private long _presentSubmitSeq;
        private long _presentRetireSeq;
        // Cap the reuse pool so it cannot itself grow unbounded and hold VRAM
        // hostage. With a ~700MB live guest-image set this headroom is enough to
        // absorb the create/destroy churn while keeping real VRAM ~= live+cap.
        private const ulong MaxPooledDeviceMemoryBytes = 1024UL * 1024 * 1024;
        // Frees a guest image's backing block, returning it to the reuse pool
        // instead of vkFreeMemory when reuse is enabled and it is provably safe.
        // Clears resource.Memory so a double release is a no-op.
        private void ReleaseGuestImageMemory(GuestImageResource resource)
        {
            var memory = resource.Memory;
            resource.Memory = default;
            if (memory.Handle == 0)
            {
                return;
            }

            // Release-only trace budget, disjoint from ShouldTracePool's shared
            // rent/park event counter: guarantee the first 300 releases (plus
            // every 128th after) always emit their park/free_skip line. Earlier
            // VM runs showed ONLY rent misses and zero park/free_skip; because
            // rent attempts fire on every device-local allocation and share
            // ShouldTracePool's 300-event budget, an actual release could be
            // silenced by rent flooding. This dedicated counter makes a real
            // release provably visible, and staying 0 all run proves the
            // opposite: DestroyGuestImage was never reached, so the pool cannot
            // engage no matter what (allocation growth is all NEW distinct
            // blocks, never destroy-and-repark churn).
            var traceRelease = false;
            if (_reuseGuestImageMemory)
            {
                var releaseCount = ++_guestImageReleaseEvents;
                traceRelease = releaseCount <= 300 || (releaseCount & 127) == 0;
            }

            // Pool whenever reuse is on, the block's size/type is known and
            // device-local, and the pool has headroom. Parking is decoupled from
            // global GPU idle: the block is stamped with the guest/present
            // submission seqs live right now (every submission that could still
            // reference it has a seq <= these), and TryRentPooledDeviceMemory
            // refuses to hand it back until those seqs have RETIRED. So a rent can
            // never alias in-flight GPU work even while rendering is active --
            // which is exactly when the old "_pendingGuestSubmissions.Count == 0"
            // idle gate was never true, leaving the pool empty all run.
            var deviceLocal = resource.MemorySize != 0 &&
                IsDeviceLocalMemoryType(resource.MemoryTypeIndex);
            var headroom =
                _pooledDeviceMemoryBytes + resource.MemorySize <= MaxPooledDeviceMemoryBytes;
            if (_reuseGuestImageMemory && deviceLocal && headroom)
            {
                // Reserved, no longer live: drop it from the live ledger but keep
                // the VkDeviceMemory alive for reuse.
                _deviceMemoryLedger.Untrack(memory.Handle);
                var key = (resource.MemorySize, resource.MemoryTypeIndex);
                if (!_freeDeviceMemoryPool.TryGetValue(key, out var queue))
                {
                    queue = new Queue<PooledDeviceMemoryBlock>();
                    _freeDeviceMemoryPool[key] = queue;
                }

                queue.Enqueue(new PooledDeviceMemoryBlock(
                    memory, _guestSubmitSeq, _presentSubmitSeq));
                _pooledDeviceMemoryBlockCount++;
                _pooledDeviceMemoryBytes += resource.MemorySize;
                if (traceRelease)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][POOLDBG] park release#={_guestImageReleaseEvents} " +
                        $"size={resource.MemorySize / (1024 * 1024)}MB " +
                        $"type={resource.MemoryTypeIndex} guestGuard={_guestSubmitSeq} " +
                        $"presentGuard={_presentSubmitSeq} pending={_pendingGuestSubmissions.Count} " +
                        $"presentInFlight={_presentationInFlight} reclaim={_reclaimInProgress} " +
                        $"pool={_pooledDeviceMemoryBytes / (1024 * 1024)}MB/{_pooledDeviceMemoryBlockCount}");
                }

                return;
            }

            if (traceRelease)
            {
                Console.Error.WriteLine(
                    $"[LOADER][POOLDBG] free_skip release#={_guestImageReleaseEvents} " +
                    $"size={resource.MemorySize / (1024 * 1024)}MB " +
                    $"type={resource.MemoryTypeIndex} deviceLocal={deviceLocal} " +
                    $"sizeKnown={resource.MemorySize != 0} headroom={headroom} " +
                    $"pool={_pooledDeviceMemoryBytes / (1024 * 1024)}MB/{_pooledDeviceMemoryBlockCount}");
            }

            FreeDeviceMemory(memory);
        }
        private bool TryRentPooledDeviceMemory(
            ulong size,
            uint memoryTypeIndex,
            out DeviceMemory memory)
        {
            if (_freeDeviceMemoryPool.TryGetValue((size, memoryTypeIndex), out var queue) &&
                queue.Count != 0)
            {
                // FIFO by park order and the guard seqs are monotonic, so the
                // front block is the readiest: if its guards have not retired,
                // nothing behind it has either. Only rent once both the guest and
                // present submissions live at park time have fully retired -- that
                // is the block's own last-use fence signaling.
                var front = queue.Peek();
                if (_guestRetireSeq >= front.GuestGuardSeq &&
                    _presentRetireSeq >= front.PresentGuardSeq)
                {
                    queue.Dequeue();
                    _pooledDeviceMemoryBlockCount--;
                    _pooledDeviceMemoryBytes -= size;
                    memory = front.Memory;
                    if (ShouldTracePool())
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][POOLDBG] rent_hit size={size / (1024 * 1024)}MB " +
                            $"type={memoryTypeIndex} releases={_guestImageReleaseEvents} " +
                            $"guestGuard={front.GuestGuardSeq}/{_guestRetireSeq} " +
                            $"presentGuard={front.PresentGuardSeq}/{_presentRetireSeq} " +
                            $"pool={_pooledDeviceMemoryBytes / (1024 * 1024)}MB/{_pooledDeviceMemoryBlockCount}");
                    }

                    return true;
                }

                if (ShouldTracePool())
                {
                    Console.Error.WriteLine(
                        $"[LOADER][POOLDBG] rent_miss_notready size={size / (1024 * 1024)}MB " +
                        $"type={memoryTypeIndex} releases={_guestImageReleaseEvents} " +
                        $"guestGuard={front.GuestGuardSeq}/{_guestRetireSeq} " +
                        $"presentGuard={front.PresentGuardSeq}/{_presentRetireSeq} " +
                        $"queued={queue.Count}");
                }

                memory = default;
                return false;
            }

            if (ShouldTracePool())
            {
                Console.Error.WriteLine(
                    $"[LOADER][POOLDBG] rent_miss_empty size={size / (1024 * 1024)}MB " +
                    $"type={memoryTypeIndex} releases={_guestImageReleaseEvents} " +
                    $"keys={_freeDeviceMemoryPool.Count}");
            }

            memory = default;
            return false;
        }
        // Really frees every pooled block (vkFreeMemory). Called when the driver
        // is under memory pressure or at teardown, where the reserved VRAM must
        // be handed back to the driver rather than kept for reuse.
        private void DrainFreeDeviceMemoryPool()
        {
            if (_freeDeviceMemoryPool.Count == 0)
            {
                return;
            }

            foreach (var queue in _freeDeviceMemoryPool.Values)
            {
                while (queue.Count != 0)
                {
                    _vk.FreeMemory(_device, queue.Dequeue().Memory, null);
                }
            }

            _freeDeviceMemoryPool.Clear();
            _pooledDeviceMemoryBlockCount = 0;
            _pooledDeviceMemoryBytes = 0;
        }
    }
}
