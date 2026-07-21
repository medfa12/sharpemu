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
    // SHARPEMU_CACHE_RENDERPASS: cache the transient VkRenderPass + VkFramebuffer
    // that ExecuteOffscreenDraw builds per depth/MRT draw, instead of
    // vkCreateRenderPass + vkCreateFramebuffer + vkDestroy* every single draw.
    //   * Render passes key on (color formats[], depth format, clear-depth) --
    //     only a handful of distinct combos -- and reference no image, so they
    //     are never destroyed per-draw (only at DisposeVulkan). Always safe.
    //   * Framebuffers key on (attachment image-view handles[], extent). They
    //     DO reference specific image views, so a cached framebuffer is purged
    //     the instant any of its attachment images is destroyed
    //     (PurgeCachedFramebuffersForImage, hooked into DestroyGuestImage). That
    //     destroy path is always preceded by WaitForAllGuestSubmissions (evict /
    //     realloc) or DeviceWaitIdle (dispose), the same drain-before-destroy
    //     invariant the per-image cached framebuffer already depends on, so no
    //     in-flight submission can reference a purged framebuffer.
    // Default (unset) keeps the byte-identical create-and-destroy-every-draw path.
    private static readonly bool _cacheRenderPass =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_CACHE_RENDERPASS"),
            "1",
            StringComparison.Ordinal);
    private sealed partial class Presenter : IDisposable
    {
        // SHARPEMU_CACHE_RENDERPASS caches (all render-thread state, no locking).
        // Render passes: keyed by attachment formats + clear-depth, never image-
        // coupled, destroyed only at DisposeVulkan. Framebuffers: keyed by exact
        // attachment view handles + extent; _framebufferViewIndex maps each view
        // handle to the framebuffer keys that reference it so DestroyGuestImage
        // can purge dependents. _framebufferPurgeHandles is scratch for that purge.
        private readonly Dictionary<RenderPassCacheKey, RenderPass> _renderPassCache = new();
        private readonly Dictionary<FramebufferCacheKey, CachedFramebuffer> _framebufferCache = new();
        private readonly Dictionary<ulong, List<FramebufferCacheKey>> _framebufferViewIndex = new();
        private readonly HashSet<ulong> _framebufferPurgeHandles = [];
        // Structural key for the transient render-pass cache: the ordered color
        // attachment formats, the depth format (-1 = none), and whether depth is
        // cleared (its load op differs). Equality compares the format array by value.
        private sealed class RenderPassCacheKey : IEquatable<RenderPassCacheKey>
        {
            private readonly int[] _formats;
            private readonly int _depthFormat;
            private readonly bool _clearDepth;
            private readonly int _hash;

            public RenderPassCacheKey(int[] formats, int depthFormat, bool clearDepth)
            {
                _formats = formats;
                _depthFormat = depthFormat;
                _clearDepth = clearDepth;
                var hash = new HashCode();
                foreach (var format in formats)
                {
                    hash.Add(format);
                }

                hash.Add(depthFormat);
                hash.Add(clearDepth);
                _hash = hash.ToHashCode();
            }

            public bool Equals(RenderPassCacheKey? other)
            {
                if (other is null ||
                    _depthFormat != other._depthFormat ||
                    _clearDepth != other._clearDepth ||
                    _formats.Length != other._formats.Length)
                {
                    return false;
                }

                for (var index = 0; index < _formats.Length; index++)
                {
                    if (_formats[index] != other._formats[index])
                    {
                        return false;
                    }
                }

                return true;
            }

            public override bool Equals(object? obj) => Equals(obj as RenderPassCacheKey);

            public override int GetHashCode() => _hash;
        }

        // Structural key for the transient framebuffer cache: the ordered
        // attachment image-view handles (color then depth) plus the render extent.
        // Distinct guest images always own distinct view handles, so identical
        // keys mean genuinely reusable framebuffers.
        private sealed class FramebufferCacheKey : IEquatable<FramebufferCacheKey>
        {
            private readonly ulong[] _handles;
            private readonly uint _width;
            private readonly uint _height;
            private readonly int _hash;

            public FramebufferCacheKey(ulong[] handles, uint width, uint height)
            {
                _handles = handles;
                _width = width;
                _height = height;
                var hash = new HashCode();
                foreach (var handle in handles)
                {
                    hash.Add(handle);
                }

                hash.Add(width);
                hash.Add(height);
                _hash = hash.ToHashCode();
            }

            public bool Equals(FramebufferCacheKey? other)
            {
                if (other is null ||
                    _width != other._width ||
                    _height != other._height ||
                    _handles.Length != other._handles.Length)
                {
                    return false;
                }

                for (var index = 0; index < _handles.Length; index++)
                {
                    if (_handles[index] != other._handles[index])
                    {
                        return false;
                    }
                }

                return true;
            }

            public override bool Equals(object? obj) => Equals(obj as FramebufferCacheKey);

            public override int GetHashCode() => _hash;
        }

        // A cached framebuffer plus the view handles it was built from, kept so
        // the purge path can unregister it from every referencing view handle.
        private sealed class CachedFramebuffer(Framebuffer framebuffer, ulong[] viewHandles)
        {
            public Framebuffer Framebuffer { get; } = framebuffer;
            public ulong[] ViewHandles { get; } = viewHandles;
        }
        // SHARPEMU_CACHE_RENDERPASS: returns a cached (VkRenderPass, VkFramebuffer)
        // pair for a transient depth/MRT offscreen draw, creating either half only
        // on a cache miss. The render pass keys on the attachment formats +
        // clear-depth (never destroyed per-draw); the framebuffer keys on the
        // exact attachment image-view handles + extent, and every cached
        // framebuffer registers its view handles so DestroyGuestImage can purge it
        // when an attachment image goes away (see PurgeCachedFramebuffersForImage).
        private (RenderPass RenderPass, Framebuffer Framebuffer)
            GetOrCreateCachedRenderPassAndFramebuffer(
                IReadOnlyList<Format> formats,
                IReadOnlyList<ImageView> attachmentViews,
                uint width,
                uint height,
                Format? depthFormat,
                ImageView depthView,
                bool clearDepth)
        {
            var hasDepth = depthFormat.HasValue;
            var renderPassKey = new RenderPassCacheKey(
                FormatKey(formats),
                hasDepth ? (int)depthFormat!.Value : -1,
                clearDepth);
            if (!_renderPassCache.TryGetValue(renderPassKey, out var renderPass))
            {
                renderPass = CreateRenderPassOnly(formats, depthFormat, clearDepth);
                _renderPassCache[renderPassKey] = renderPass;
            }

            // Framebuffer compatibility ignores load/store ops, so the key omits
            // clearDepth: one framebuffer serves both the clear and load render
            // passes over the same views.
            var handleCount = attachmentViews.Count + (hasDepth ? 1 : 0);
            var handles = new ulong[handleCount];
            for (var index = 0; index < attachmentViews.Count; index++)
            {
                handles[index] = attachmentViews[index].Handle;
            }

            if (hasDepth)
            {
                handles[attachmentViews.Count] = depthView.Handle;
            }

            var framebufferKey = new FramebufferCacheKey(handles, width, height);
            if (!_framebufferCache.TryGetValue(framebufferKey, out var cached))
            {
                var framebuffer = CreateFramebufferOnly(
                    renderPass, attachmentViews, hasDepth, depthView, width, height);
                cached = new CachedFramebuffer(framebuffer, handles);
                _framebufferCache[framebufferKey] = cached;
                foreach (var handle in handles)
                {
                    if (handle == 0)
                    {
                        continue;
                    }

                    if (!_framebufferViewIndex.TryGetValue(handle, out var keys))
                    {
                        keys = [];
                        _framebufferViewIndex[handle] = keys;
                    }

                    keys.Add(framebufferKey);
                }
            }

            return (renderPass, cached.Framebuffer);
        }
        // Purges every cached framebuffer that references one of this image's
        // attachment image views, destroying the Vulkan object. Called from
        // DestroyGuestImage, which is always reached after the render thread has
        // drained all in-flight guest submissions (WaitForAllGuestSubmissions on
        // the evict / realloc paths, DeviceWaitIdle on dispose), so no submission
        // can still reference a purged framebuffer. Handle reuse by the driver is
        // safe because the stale entry is removed before the handle can be reissued.
        private void PurgeCachedFramebuffersForImage(GuestImageResource resource)
        {
            if (!_cacheRenderPass || _framebufferCache.Count == 0)
            {
                return;
            }

            _framebufferPurgeHandles.Clear();
            AddPurgeHandle(resource.View.Handle);
            foreach (var mipView in resource.MipViews)
            {
                AddPurgeHandle(mipView.Handle);
            }

            foreach (var view in resource.FormatViews.Values)
            {
                AddPurgeHandle(view.Handle);
            }

            foreach (var handle in _framebufferPurgeHandles)
            {
                if (!_framebufferViewIndex.TryGetValue(handle, out var keys))
                {
                    continue;
                }

                foreach (var key in keys)
                {
                    if (!_framebufferCache.Remove(key, out var cached))
                    {
                        continue;
                    }

                    _vk.DestroyFramebuffer(_device, cached.Framebuffer, null);
                    // Unregister this framebuffer from every OTHER view handle it
                    // referenced so those index lists never point at a freed object.
                    foreach (var otherHandle in cached.ViewHandles)
                    {
                        if (otherHandle == handle || otherHandle == 0)
                        {
                            continue;
                        }

                        if (_framebufferViewIndex.TryGetValue(otherHandle, out var otherKeys))
                        {
                            otherKeys.Remove(key);
                        }
                    }
                }

                _framebufferViewIndex.Remove(handle);
            }

            _framebufferPurgeHandles.Clear();
        }

        private void AddPurgeHandle(ulong handle)
        {
            if (handle != 0)
            {
                _framebufferPurgeHandles.Add(handle);
            }
        }
        // Destroys every cached render pass and framebuffer. Called only from
        // DisposeVulkan after DeviceWaitIdle, so the GPU is idle and nothing
        // references these objects.
        private void DestroyRenderPassFramebufferCaches()
        {
            foreach (var cached in _framebufferCache.Values)
            {
                _vk.DestroyFramebuffer(_device, cached.Framebuffer, null);
            }

            _framebufferCache.Clear();
            _framebufferViewIndex.Clear();
            _framebufferPurgeHandles.Clear();

            foreach (var renderPass in _renderPassCache.Values)
            {
                _vk.DestroyRenderPass(_device, renderPass, null);
            }

            _renderPassCache.Clear();
        }
        private static int[] FormatKey(IReadOnlyList<Format> formats)
        {
            var key = new int[formats.Count];
            for (var index = 0; index < formats.Count; index++)
            {
                key[index] = (int)formats[index];
            }

            return key;
        }
    }
}
