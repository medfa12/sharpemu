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

// Exact pool-size shape a descriptor pool was created for (sampled-image /
// storage-image / storage-buffer descriptor counts). Two TranslatedDrawResources
// with the same shape can share one vkCreateDescriptorPool + reset-at-rent
// instead of a create/destroy pair each.
internal readonly record struct DescriptorPoolBucketKey(
    uint SampledImages,
    uint StorageImages,
    uint StorageBuffers);

// Identifies a per-draw OwnsStorage texture image eligible for reuse: the
// exact ImageCreateInfo shape (format/extent/usage) that CreateTextureResource
// and CreateCopyOnSampleTextureResource build. AttachmentUsage tracks whether
// the image was created with the color-attachment/storage usage superset
// (!IsBlockCompressedFormat) vs the plain sampled-only usage set, since the two
// are not bind-compatible.
internal readonly record struct PooledTextureImageKey(
    int Format,
    uint Width,
    uint Height,
    bool AttachmentUsage);

// Pure helpers pulled out of the pooling paths so they are unit-testable
// without a live VkDevice. Every rent/park decision in the pools below is
// required to go through these so tests pin the exact behavior.
internal static class DrawObjectPoolMath
{
    // Mirrors the pow2 bucketing CreateHostBuffer already uses (VulkanVideoPresenter.cs,
    // CreateHostBuffer): round up to the next power of two, with a 4-byte floor
    // so a zero/near-zero request still lands in a shareable bucket.
    public static ulong SizeClass(ulong size) =>
        BitOperations.RoundUpToPowerOf2(Math.Max(size, 4));

    // Returns the indices (into entries, in ascending-index order for equal
    // stamps) of the oldest-LastUse entries whose cumulative Bytes first
    // reaches bytesToFree. Empty when bytesToFree == 0. If the request exceeds
    // the total parked bytes, every index is returned.
    public static int[] SelectLruEvictions(
        IReadOnlyList<(long LastUse, ulong Bytes)> entries,
        ulong bytesToFree)
    {
        if (bytesToFree == 0 || entries.Count == 0)
        {
            return [];
        }

        var order = new int[entries.Count];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        // Stable sort by ascending LastUse; ties keep ascending index order
        // (Array.Sort is not guaranteed stable, so break ties explicitly).
        Array.Sort(order, (a, b) =>
        {
            var cmp = entries[a].LastUse.CompareTo(entries[b].LastUse);
            return cmp != 0 ? cmp : a.CompareTo(b);
        });

        var result = new List<int>();
        ulong freed = 0;
        foreach (var index in order)
        {
            if (freed >= bytesToFree)
            {
                break;
            }

            result.Add(index);
            freed += entries[index].Bytes;
        }

        return result.ToArray();
    }
}

internal static unsafe partial class VulkanVideoPresenter
{
    // SHARPEMU_POOL_DRAW_OBJECTS: pool every per-draw Vulkan object that today
    // is created and destroyed once per guest draw -- fences, command buffers,
    // descriptor pools, and per-draw OwnsStorage texture image/view/memory
    // triples -- instead of vkDestroy*/vkFree* churning them every reap. This
    // is the ~570ms/draw capReap teardown cost (see EnsureGuestSubmissionCapacity
    // -> CollectCompletedGuestSubmissions -> DestroyTranslatedDrawResources).
    // Parking happens only at points already provably post-fence-signal (or
    // pre-submit unwind), so no new synchronization is added. Default unset
    // keeps every call site byte-identical to today.
    private static readonly bool _poolDrawObjects =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_POOL_DRAW_OBJECTS"),
            "1",
            StringComparison.Ordinal);

    private sealed partial class Presenter : IDisposable
    {
        // Backing entry for a pooled per-draw OwnsStorage texture image. Views
        // are keyed by DstSelect (component swizzle) since two draws sampling
        // the same underlying image content can request different swizzles.
        // Owned and destroyed together with the image (LRU trim / OOM drain /
        // dispose) -- there is no global VkImage-handle-keyed cache, so driver
        // handle reuse can never alias a stale view here.
        private sealed class PooledTextureImage
        {
            public Image Image;
            public DeviceMemory Memory;
            public ulong MemorySize;
            public PooledTextureImageKey Key;
            public Dictionary<uint, ImageView> Views = new();
            public long LastUseStamp;
        }

        private const int MaxPooledGuestFences = 64;
        private const int MaxPooledGuestCommandBuffers = 64;
        private const int MaxPooledDescriptorPools = 256;
        private const int MaxPooledDescriptorPoolsPerBucket = 32;
        private const ulong MaxPooledTextureImageBytes = 512UL * 1024 * 1024;

        // Render-thread-only pool state (same convention as _freeDeviceMemoryPool /
        // _guestImages): every submit/dispatch/draw/present runs on one thread, so
        // none of this needs locking.
        private readonly Stack<Fence> _freeGuestFences = new();
        private readonly Stack<CommandBuffer> _freeGuestCommandBuffers = new();
        private readonly Dictionary<DescriptorPoolBucketKey, Stack<DescriptorPool>>
            _freeDescriptorPools = new();
        private int _freeDescriptorPoolCount;
        private readonly Dictionary<PooledTextureImageKey, Queue<PooledTextureImage>>
            _freeTextureImages = new();
        private ulong _pooledTextureImageBytes;
        private int _pooledTextureImageCount;
        private long _texturePoolUseStamp;

        // SHARPEMU_POOL_DRAW_OBJECTS host-buffer-pool LRU trim: park stamp per
        // pooled buffer (by VkBuffer handle), guarded by the existing
        // _hostBufferPoolGate (declared alongside _hostBufferPool). Only read/
        // written when the flag is set; off the flag RecycleHostBuffer never
        // touches these and the pool keeps its original drop-newest behavior.
        private readonly Dictionary<ulong, long> _hostBufferLastParked = new();
        private long _hostBufferParkStamp;

        // [POOLSTATS] interval counters. Only ever mutated when _poolDrawObjects
        // is set, so a default run touches none of this. hit/miss/park/trim per
        // object class, reset every EmitPoolStats call; a cumulative draw count
        // gives context across intervals.
        private long _cbRentHit, _cbRentMiss, _cbPark, _cbTrim;
        private long _fenceRentHit, _fenceRentMiss, _fencePark, _fenceTrim;
        private long _dpoolRentHit, _dpoolRentMiss, _dpoolPark, _dpoolTrim;
        private long _texRentHit, _texRentMiss, _texPark, _texTrim;
        private long _hostbufRentHit, _hostbufRentMiss, _hostbufPark, _hostbufTrim;
        private int _poolStatsDraws;
        private long _poolStatsDrawsTotal;
        private const int PoolStatsEmitInterval = 64;

        // Rents an unsignaled VkFence for a guest submission. Off the flag this
        // is just vkCreateFence, unchanged from before pooling existed.
        private Fence RentGuestFence()
        {
            if (_poolDrawObjects && _freeGuestFences.TryPop(out var pooled))
            {
                _fenceRentHit++;
                return pooled;
            }

            if (_poolDrawObjects)
            {
                _fenceRentMiss++;
            }

            var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            Check(_vk.CreateFence(_device, &fenceInfo, null, out var fence), "vkCreateFence(guest)");
            return fence;
        }

        // Retires a guest submission's fence. needsReset must be true on every
        // path where the fence may have signaled (the normal reap/wait paths);
        // false only on the QueueSubmit-failed unwind, where the fence is
        // already unsignaled and was never touched by the driver. INVARIANT:
        // every fence sitting in _freeGuestFences is unsignaled, so a rent can
        // never observe a spuriously "already complete" submission.
        private void ParkGuestFence(Fence fence, bool needsReset)
        {
            if (!_poolDrawObjects)
            {
                _vk.DestroyFence(_device, fence, null);
                return;
            }

            if (needsReset)
            {
                var local = fence;
                Check(_vk.ResetFences(_device, 1, &local), "vkResetFences(pool)");
            }

            if (_freeGuestFences.Count >= MaxPooledGuestFences)
            {
                _fenceTrim++;
                _vk.DestroyFence(_device, fence, null);
                return;
            }

            _fencePark++;
            _freeGuestFences.Push(fence);
        }

        // Rents a command buffer from the guest command pool (created with
        // ResetCommandBufferBit, so per-buffer vkResetCommandBuffer is legal).
        private CommandBuffer RentGuestCommandBuffer()
        {
            if (_poolDrawObjects && _freeGuestCommandBuffers.TryPop(out var pooled))
            {
                Check(_vk.ResetCommandBuffer(pooled, 0), "vkResetCommandBuffer(pool)");
                _cbRentHit++;
                return pooled;
            }

            if (_poolDrawObjects)
            {
                _cbRentMiss++;
            }

            var allocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            CommandBuffer commandBuffer;
            Check(
                _vk.AllocateCommandBuffers(_device, &allocateInfo, out commandBuffer),
                "vkAllocateCommandBuffers(guest)");
            return commandBuffer;
        }

        // Retires a guest command buffer. Never call with _presentationCommandBuffer
        // or any alias of it -- only the local per-draw handle.
        private void ParkGuestCommandBuffer(CommandBuffer commandBuffer)
        {
            if (!_poolDrawObjects)
            {
                var local = commandBuffer;
                _vk.FreeCommandBuffers(_device, _commandPool, 1, &local);
                return;
            }

            if (_freeGuestCommandBuffers.Count >= MaxPooledGuestCommandBuffers)
            {
                _cbTrim++;
                var local = commandBuffer;
                _vk.FreeCommandBuffers(_device, _commandPool, 1, &local);
                return;
            }

            _cbPark++;
            _freeGuestCommandBuffers.Push(commandBuffer);
        }

        // Rents a reset, ready-to-allocate-from descriptor pool for the given
        // bucket shape, or returns false on a pool miss (caller creates fresh).
        private bool TryRentDescriptorPool(DescriptorPoolBucketKey key, out DescriptorPool pool)
        {
            if (_poolDrawObjects &&
                _freeDescriptorPools.TryGetValue(key, out var stack) &&
                stack.TryPop(out pool))
            {
                _freeDescriptorPoolCount--;
                Check(_vk.ResetDescriptorPool(_device, pool, 0), "vkResetDescriptorPool(pool)");
                _dpoolRentHit++;
                return true;
            }

            if (_poolDrawObjects)
            {
                _dpoolRentMiss++;
            }

            pool = default;
            return false;
        }

        // Retires a descriptor pool that was allocated under a known bucket key
        // (resources.DescriptorPoolKey != default). Reset happens at rent, not
        // here, so the retired DescriptorSet handle is simply dead the instant
        // the next rent resets the pool; nothing may touch it after park.
        private void ParkDescriptorPool(DescriptorPool pool, DescriptorPoolBucketKey key)
        {
            if (!_poolDrawObjects)
            {
                _vk.DestroyDescriptorPool(_device, pool, null);
                return;
            }

            if (!_freeDescriptorPools.TryGetValue(key, out var stack))
            {
                stack = new Stack<DescriptorPool>();
                _freeDescriptorPools[key] = stack;
            }

            if (stack.Count >= MaxPooledDescriptorPoolsPerBucket ||
                _freeDescriptorPoolCount >= MaxPooledDescriptorPools)
            {
                _dpoolTrim++;
                _vk.DestroyDescriptorPool(_device, pool, null);
                return;
            }

            _dpoolPark++;
            stack.Push(pool);
            _freeDescriptorPoolCount++;
        }

        // Tries to rent a pooled per-draw texture image/memory pair for the
        // given key. On a hit, re-tracks it in the device-memory ledger (it was
        // untracked at park) and returns the entry with an existing or freshly
        // created view for dstSelect. Caller must NOT rent unless the texture is
        // provably going to stay OwnsStorage==true with GuestImage==null --
        // see the eligibility check in CreateTextureResource.
        private bool TryRentPooledTextureImage(
            PooledTextureImageKey key,
            uint dstSelect,
            out PooledTextureImage entry,
            out ImageView view)
        {
            if (_poolDrawObjects &&
                _freeTextureImages.TryGetValue(key, out var queue) &&
                queue.TryDequeue(out var pooled))
            {
                _texRentHit++;
                _pooledTextureImageBytes -= pooled.MemorySize;
                _pooledTextureImageCount--;
                _deviceMemoryLedger.Track(pooled.Memory.Handle, pooled.MemorySize, deviceLocal: true);
                if (!pooled.Views.TryGetValue(dstSelect, out view))
                {
                    var viewInfo = new ImageViewCreateInfo
                    {
                        SType = StructureType.ImageViewCreateInfo,
                        Image = pooled.Image,
                        ViewType = ImageViewType.Type2D,
                        Format = (Format)key.Format,
                        Components = ToVkComponentMapping(dstSelect),
                        SubresourceRange = ColorSubresourceRange(),
                    };
                    Check(
                        _vk.CreateImageView(_device, &viewInfo, null, out view),
                        "vkCreateImageView(texture pool)");
                    pooled.Views[dstSelect] = view;
                }

                entry = pooled;
                return true;
            }

            if (_poolDrawObjects)
            {
                _texRentMiss++;
            }

            entry = default!;
            view = default;
            return false;
        }

        // Parks a retired per-draw texture image (its whole View set stays
        // alive) and LRU-trims the pool back under MaxPooledTextureImageBytes if
        // needed. Called only from DestroyTranslatedDrawResources, i.e. only
        // after the owning submission's fence has signaled (or on the never-
        // submitted unwind), so this is never in-flight VRAM.
        private void ParkPooledTextureImage(PooledTextureImage entry)
        {
            entry.LastUseStamp = ++_texturePoolUseStamp;
            _deviceMemoryLedger.Untrack(entry.Memory.Handle);
            if (!_freeTextureImages.TryGetValue(entry.Key, out var queue))
            {
                queue = new Queue<PooledTextureImage>();
                _freeTextureImages[entry.Key] = queue;
            }

            queue.Enqueue(entry);
            _pooledTextureImageBytes += entry.MemorySize;
            _pooledTextureImageCount++;
            _texPark++;

            if (_pooledTextureImageBytes <= MaxPooledTextureImageBytes)
            {
                return;
            }

            TrimPooledTextureImages(_pooledTextureImageBytes - MaxPooledTextureImageBytes);
        }

        // Drops entries (all keys pooled) to free at least bytesToFree, oldest
        // (by LastUseStamp) first, via DrawObjectPoolMath.SelectLruEvictions.
        private void TrimPooledTextureImages(ulong bytesToFree)
        {
            if (bytesToFree == 0 || _pooledTextureImageCount == 0)
            {
                return;
            }

            var flat = new List<(PooledTextureImageKey Key, PooledTextureImage Entry)>(
                _pooledTextureImageCount);
            foreach (var (key, queue) in _freeTextureImages)
            {
                foreach (var entry in queue)
                {
                    flat.Add((key, entry));
                }
            }

            var scratch = new List<(long LastUse, ulong Bytes)>(flat.Count);
            foreach (var item in flat)
            {
                scratch.Add((item.Entry.LastUseStamp, item.Entry.MemorySize));
            }

            var victims = DrawObjectPoolMath.SelectLruEvictions(scratch, bytesToFree);
            if (victims.Length == 0)
            {
                return;
            }

            var victimSet = new HashSet<int>(victims);
            var rebuilt = new Dictionary<PooledTextureImageKey, Queue<PooledTextureImage>>();
            for (var i = 0; i < flat.Count; i++)
            {
                var (key, entry) = flat[i];
                if (victimSet.Contains(i))
                {
                    DestroyPooledTextureImage(entry);
                    _pooledTextureImageBytes -= entry.MemorySize;
                    _pooledTextureImageCount--;
                    _texTrim++;
                    continue;
                }

                if (!rebuilt.TryGetValue(key, out var queue))
                {
                    queue = new Queue<PooledTextureImage>();
                    rebuilt[key] = queue;
                }

                queue.Enqueue(entry);
            }

            _freeTextureImages.Clear();
            foreach (var (key, queue) in rebuilt)
            {
                _freeTextureImages[key] = queue;
            }
        }

        private void DestroyPooledTextureImage(PooledTextureImage entry)
        {
            foreach (var view in entry.Views.Values)
            {
                _vk.DestroyImageView(_device, view, null);
            }

            _vk.DestroyImage(_device, entry.Image, null);
            _vk.FreeMemory(_device, entry.Memory, null);
        }

        // Drains every parked draw-object pool. deviceIdle=true (DisposeVulkan,
        // after vkDeviceWaitIdle) clears the command-buffer stack without
        // vkFreeCommandBuffers -- destroying the command pool frees them anyway.
        // deviceIdle=false (mid-run OOM recovery, command pool still live) frees
        // them explicitly.
        private void DrainDrawObjectPools(bool deviceIdle)
        {
            foreach (var queue in _freeTextureImages.Values)
            {
                while (queue.TryDequeue(out var entry))
                {
                    DestroyPooledTextureImage(entry);
                }
            }

            _freeTextureImages.Clear();
            _pooledTextureImageBytes = 0;
            _pooledTextureImageCount = 0;

            foreach (var stack in _freeDescriptorPools.Values)
            {
                while (stack.TryPop(out var pool))
                {
                    _vk.DestroyDescriptorPool(_device, pool, null);
                }
            }

            _freeDescriptorPools.Clear();
            _freeDescriptorPoolCount = 0;

            while (_freeGuestFences.TryPop(out var fence))
            {
                _vk.DestroyFence(_device, fence, null);
            }

            if (deviceIdle)
            {
                _freeGuestCommandBuffers.Clear();
            }
            else
            {
                while (_freeGuestCommandBuffers.TryPop(out var commandBuffer))
                {
                    var local = commandBuffer;
                    _vk.FreeCommandBuffers(_device, _commandPool, 1, &local);
                }
            }
        }

        // [POOLSTATS] engagement trace, mirrors the DRAWTIME interval idiom:
        // called from ExecuteOffscreenDraw's finally every PoolStatsEmitInterval
        // submitted draws. Hit ratios near 100% after warmup prove reuse; park
        // staying 0 proves the reap never reaches the pool (the guest-image-pool
        // failure mode); trim exploding proves the caps/keys are wrong.
        private void EmitPoolStats()
        {
            Console.Error.WriteLine(
                $"[LOADER][POOLSTATS] draws={_poolStatsDraws} cum={_poolStatsDrawsTotal} " +
                $"cb={_cbRentHit}/{_cbRentMiss}/{_cbPark}/{_cbTrim} " +
                $"fence={_fenceRentHit}/{_fenceRentMiss}/{_fencePark}/{_fenceTrim} " +
                $"dpool={_dpoolRentHit}/{_dpoolRentMiss}/{_dpoolPark}/{_dpoolTrim} " +
                $"tex={_texRentHit}/{_texRentMiss}/{_texPark}/{_texTrim} " +
                $"hostbuf={_hostbufRentHit}/{_hostbufRentMiss}/{_hostbufPark}/{_hostbufTrim} " +
                $"parkedTexMB={_pooledTextureImageBytes / (1024 * 1024)}/{_pooledTextureImageCount} " +
                $"parkedHostMB={_pooledHostBufferBytes / (1024 * 1024)} " +
                $"parkedCb={_freeGuestCommandBuffers.Count} " +
                $"parkedFence={_freeGuestFences.Count} " +
                $"parkedDpool={_freeDescriptorPoolCount}");

            _cbRentHit = 0; _cbRentMiss = 0; _cbPark = 0; _cbTrim = 0;
            _fenceRentHit = 0; _fenceRentMiss = 0; _fencePark = 0; _fenceTrim = 0;
            _dpoolRentHit = 0; _dpoolRentMiss = 0; _dpoolPark = 0; _dpoolTrim = 0;
            _texRentHit = 0; _texRentMiss = 0; _texPark = 0; _texTrim = 0;
            _hostbufRentHit = 0; _hostbufRentMiss = 0; _hostbufPark = 0; _hostbufTrim = 0;
            _poolStatsDraws = 0;
        }
    }
}
