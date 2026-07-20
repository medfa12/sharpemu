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

internal enum GuestDrawKind
{
    None,
    FullscreenBarycentric,

    /// <summary>
    /// RDNA NGG primitive-shader draw (merged ES/GS launched through the
    /// geometry pipeline). Detected today; the compute-amplification capture
    /// that turns it into real geometry is not yet wired.
    /// </summary>
    NggPrimitive,
}

internal sealed record VulkanGuestDrawTexture(
    ulong Address,
    uint Width,
    uint Height,
    uint Format,
    uint NumberType,
    byte[] RgbaPixels,
    bool IsFallback,
    bool IsStorage,
    uint MipLevels = 1,
    uint MipLevel = 0,
    uint Pitch = 0,
    uint TileMode = 0,
    uint DstSelect = 0xFAC,
    VulkanGuestSampler Sampler = default);

internal readonly record struct VulkanGuestSampler(
    uint Word0,
    uint Word1,
    uint Word2,
    uint Word3);

internal sealed record VulkanGuestMemoryBuffer(
    ulong BaseAddress,
    byte[] Data);

internal sealed record VulkanGuestVertexBuffer(
    uint Location,
    uint ComponentCount,
    uint DataFormat,
    uint NumberFormat,
    ulong BaseAddress,
    uint Stride,
    uint OffsetBytes,
    byte[] Data);

internal sealed record VulkanGuestIndexBuffer(
    byte[] Data,
    bool Is32Bit);

// GPU-driven indexed indirect draw arguments. Data holds the raw
// VkDrawIndexedIndirectCommand block(s) (5 dwords each: indexCount,
// instanceCount, firstIndex, vertexOffset, firstInstance); Offset selects the
// command within Data and Stride is its byte size (20). When present on a
// translated draw the presenter records vkCmdDrawIndexedIndirect against a
// dedicated indirect VkBuffer instead of vkCmdDrawIndexed with a CPU count.
internal sealed record VulkanGuestIndirectArgs(
    byte[] Data,
    uint Offset,
    uint Stride);

internal readonly record struct VulkanGuestRect(
    int X,
    int Y,
    uint Width,
    uint Height);

internal readonly record struct VulkanGuestViewport(
    float X,
    float Y,
    float Width,
    float Height,
    float MinDepth,
    float MaxDepth);

internal readonly record struct VulkanGuestBlendState(
    bool Enable,
    uint ColorSrcFactor,
    uint ColorDstFactor,
    uint ColorFunc,
    uint AlphaSrcFactor,
    uint AlphaDstFactor,
    uint AlphaFunc,
    bool SeparateAlphaBlend,
    uint WriteMask)
{
    public static VulkanGuestBlendState Default { get; } = new(
        Enable: false,
        ColorSrcFactor: 1,
        ColorDstFactor: 0,
        ColorFunc: 0,
        AlphaSrcFactor: 1,
        AlphaDstFactor: 0,
        AlphaFunc: 0,
        SeparateAlphaBlend: false,
        WriteMask: 0xFu);
}

internal sealed record VulkanGuestRenderState(
    IReadOnlyList<VulkanGuestBlendState> Blends,
    VulkanGuestRect? Scissor,
    VulkanGuestViewport? Viewport)
{
    public static VulkanGuestRenderState Default { get; } = new(
        [VulkanGuestBlendState.Default],
        Scissor: null,
        Viewport: null);

    public VulkanGuestBlendState Blend =>
        Blends.Count == 0 ? VulkanGuestBlendState.Default : Blends[0];
}

internal sealed record VulkanGuestRenderTarget(
    ulong Address,
    uint Width,
    uint Height,
    uint Format,
    uint NumberType,
    uint MipLevels = 1,
    uint ComponentSwap = 0);

internal sealed record VulkanGuestDepthTarget(
    ulong Address,
    uint Width,
    uint Height,
    uint Format,
    uint TileMode,
    bool TestEnable,
    bool WriteEnable,
    uint CompareOp,
    bool ClearEnable = false,
    float ClearDepth = 1f,
    bool ReadOnly = false);

internal readonly record struct VulkanRenderTargetFormat(
    Format Format,
    Gen5PixelOutputKind OutputKind)
{
    public bool IsInteger => OutputKind is Gen5PixelOutputKind.Uint or Gen5PixelOutputKind.Sint;
}

internal sealed record VulkanTranslatedGuestDraw(
    byte[] VertexSpirv,
    byte[] PixelSpirv,
    IReadOnlyList<VulkanGuestDrawTexture> Textures,
    IReadOnlyList<VulkanGuestMemoryBuffer> GlobalMemoryBuffers,
    IReadOnlyList<VulkanGuestVertexBuffer> VertexBuffers,
    uint AttributeCount,
    uint VertexCount,
    uint InstanceCount,
    uint PrimitiveType,
    VulkanGuestIndexBuffer? IndexBuffer,
    VulkanGuestRenderState RenderState,
    VulkanGuestIndirectArgs? IndirectArgs = null,
    // Position-capture prepass for a pass-through NGG draw. When
    // ComputeCaptureSpirv is non-null the presenter runs the export shader as a
    // compute kernel that writes one clip-space vec4 per vertex into a
    // device-local buffer, then draws that buffer as a location-0 vertex stream
    // with a passthrough vertex shader. Null on every ordinary draw.
    byte[]? ComputeCaptureSpirv = null,
    NggComputeCapture? ComputeCapture = null,
    uint ComputeInvocationCount = 0,
    IReadOnlyList<VulkanGuestMemoryBuffer>? ComputeCaptureInputs = null,
    // Guest address of the pixel shader that produced PixelSpirv. Diagnostic
    // only (SHARPEMU_CAPTURE_DRAWS): lets a per-draw capture line be correlated
    // with the agc.shader_draw ps=... trace. 0 when the submitter did not
    // supply it.
    ulong PixelShaderAddress = 0);

internal sealed record VulkanOffscreenGuestDraw(
    VulkanTranslatedGuestDraw Draw,
    IReadOnlyList<VulkanGuestRenderTarget> Targets,
    bool PublishTarget,
    VulkanGuestDepthTarget? Depth = null);

internal sealed record VulkanComputeGuestDispatch(
    ulong ShaderAddress,
    byte[] ComputeSpirv,
    IReadOnlyList<VulkanGuestDrawTexture> Textures,
    IReadOnlyList<VulkanGuestMemoryBuffer> GlobalMemoryBuffers,
    uint GroupCountX,
    uint GroupCountY,
    uint GroupCountZ);

// Ordered-flip capture work item (SHARPEMU_ORDERED_FLIP). Enqueued into the
// guest-work FIFO on a display-buffer flip so that the resolved source render
// target is frozen into an immutable snapshot IN GUEST-QUEUE ORDER, before the
// next frame overwrites the double-buffered contents. Version is a monotonic id
// that the resulting Presentation carries so the present path binds the exact
// captured snapshot.
internal sealed record VulkanOrderedGuestFlip(
    long Version,
    ulong Address,
    uint Width,
    uint Height);

internal static unsafe class VulkanVideoPresenter
{
    private const uint DefaultWindowWidth = 1280;
    private const uint DefaultWindowHeight = 720;
    private const int MaxPendingGuestWork = 16;
    private const int MaxGuestWorkPerRender = 16;
    private const uint GuestPrimitiveRectList = 0x11;
    private const uint GuestFormatR32Uint = 0x10004;
    private const uint GuestFormatR32Sint = 0x20004;
    private const uint GuestFormatR32Sfloat = 0x30004;
    private const uint GuestFormatR16G16Uint = 0x10005;
    private const uint GuestFormatR16G16Sint = 0x20005;
    private const uint GuestFormatR16G16Sfloat = 0x30005;
    private const uint GuestFormatR8G8B8A8Uint = 0x1000A;
    private const uint GuestFormatR8G8B8A8Sint = 0x2000A;
    private const uint GuestFormatR16G16B16A16Uint = 0x1000C;
    private const uint GuestFormatR16G16B16A16Sint = 0x2000C;
    private const uint MinPresentableGuestImageWidth = 960;
    private const uint MinPresentableGuestImageHeight = 540;
    // Geometry/clear passes sample no textures; a frame's final composite pass
    // samples many, so a published target only qualifies as the composite
    // candidate once it reads at least this many inputs.
    private const int MinCompositeInputTextureCount = 2;
    // Registered display buffers are 4K surfaces; DMA/copy destinations within
    // this window past a registered base count as writes to that buffer.
    private const ulong DisplayBufferCopyWindowBytes = 64UL << 20;

    private static readonly object _gate = new();
    private static readonly Queue<object> _pendingGuestWork = new();
    private static readonly Dictionary<ulong, uint> _availableGuestImages = new();
    private static readonly Dictionary<ulong, uint> _gpuGuestImages = new();
    // Addresses that are (or are enqueued to become) GPU draw targets, keyed to
    // their canonical guest format. Unlike _availableGuestImages this never
    // contains CPU-uploaded asset or registered display-buffer addresses, so
    // the texture-capture gate can defer resolution for these addresses without
    // starving the CPU upload/refresh path. Draws execute in submission order
    // on the render thread, so an address registered here at enqueue time holds
    // a rendered VkImage by the time any later-enqueued draw samples it.
    private static readonly Dictionary<ulong, uint> _renderTargetGuestImages = new();
    private static readonly HashSet<(ulong Address, uint Width, uint Height)>
        _tracedGuestImageSubmissions = [];
    private static readonly HashSet<(ulong Address, uint Width, uint Height)>
        _tracedDepthExtentRepairs = [];
    // One line per unique irregular MRT layout (mismatched extents or
    // duplicate-address slots) so a per-frame draw does not spam the log.
    private static readonly HashSet<string> _tracedIrregularMrtLayouts = [];
    private static readonly HashSet<ulong> _dumpedFailedDrawShaders = [];
    // Most recently GPU-published render target large enough to be the game's
    // final composite frame. Flips of registered display buffers that never
    // received a GPU render present this image instead of dropping the frame.
    private static ulong _latestPresentableGuestImageAddress;
    // Best composite candidate of the current render window: among targets
    // published since the previous flip, the pass sampling the most input
    // textures wins (ties broken by area, then recency). Each flip latches the
    // winner and re-opens the election so the redirect tracks the newest frame.
    private static ulong _frameCompositeCandidateAddress;
    private static long _frameCompositeCandidateScore;
    private static ulong _lastFrameCompositeAddress;
    // Per rendered-target address, the last RGB content signal observed by a
    // guest-image readback: absent = never sampled (unknown), true = holds
    // color, false = observed all-black (opaque black or zeroed). Populated
    // only when a readback actually runs (diagnostic guest-image traces / an
    // NGG capture), so it stays empty on an ordinary run and the present path
    // then behaves exactly as before.
    private static readonly Dictionary<ulong, bool> _renderedContentNonblack = new();
    // Most recent presentable-sized target a readback observed to be nonblack.
    // When the elected composite is positively known to be all-black this is
    // presented instead, so a black many-input composite no longer wins over a
    // color-bearing target. Gated by SHARPEMU_PRESENT_CONTENT_PREFER.
    private static ulong _latestNonblackPresentableAddress;
    // Display buffers registered through sceVideoOutRegisterBuffers and, per
    // buffer, the source of the last observed DMA/copy into it; a copy source
    // is the authoritative image to present when that buffer is flipped.
    private static readonly HashSet<ulong> _registeredDisplayBuffers = [];
    private static readonly Dictionary<ulong, ulong> _displayBufferCopySources = new();
    private static readonly HashSet<(ulong Display, ulong Source)>
        _tracedDisplayBufferCopies = [];
    private static volatile bool _hasRegisteredDisplayBuffers;
    private static long _grossVkDeviceAlloc;
    private static long _vkAllocCount;
    private static Thread? _thread;
    private static Presentation? _latestPresentation;

    // Timestamp of the most recent AvPlayer video-frame present. While this is
    // fresh, the game's own display-buffer flips are suppressed so an actively
    // playing video (intro) owns the swapchain instead of the game's concurrent
    // loading frames. Gated by SHARPEMU_AVPLAYER_PRESENT via TryPresentVideoFrame.
    private static long _lastVideoPresentTicks;

    private static bool VideoPresentIsActive() =>
        _lastVideoPresentTicks != 0 &&
        System.Diagnostics.Stopwatch.GetTimestamp() - _lastVideoPresentTicks
            < System.Diagnostics.Stopwatch.Frequency / 2;
    private static byte[]? _copyFragmentSpirv;
    private static uint _windowWidth;
    private static uint _windowHeight;
    private static bool _closed;
    private const string DebugUtilsExtensionName = "VK_EXT_debug_utils";
    private const uint NvidiaVendorId = 0x10DE;
    private static bool _splashHidden;
    private static long _enqueuedGuestWorkSequence;
    private static long _completedGuestWorkSequence;
    private static long _presentedFrameTotal;
    private static string? _selectedDeviceName;
    private static volatile bool _closeRequested;

    internal static long PresentedFrameTotal => Interlocked.Read(ref _presentedFrameTotal);

    internal static string? SelectedDeviceName => Volatile.Read(ref _selectedDeviceName);

    internal static bool HasStopped
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    internal static void RequestClose() => _closeRequested = true;

    private static bool ShouldTracePresentedGuestImageContentsForDiagnostics()
    {
        var mode = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES");
        return string.Equals(mode, "1", StringComparison.Ordinal) ||
               string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase);
    }

    // Ordered-flip capture is OFF by default; "1" enables it. When off, the
    // present path is byte-for-byte the existing composite-heuristic behaviour.
    internal static bool ShouldUseOrderedGuestFlip() =>
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_ORDERED_FLIP"),
            "1",
            StringComparison.Ordinal);

    // Focused, default-off gate for the title-composite black-output fix. When
    // set, the sampled-input binding gate becomes format-strict (mirroring the
    // acelogic fork's IsGuestImageAvailable) so a composite input whose sampled
    // guest format does not match any GPU producer registered at its address
    // reads real guest memory instead of deferring to an alias that resolves
    // black, and the native-format alias may still bind a genuine GPU producer
    // even after guest memory was read. Default runs are byte-identical.
    internal static bool ShouldUseFlipCompositeFix() =>
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_FLIP_COMPOSITE_FIX"),
            "1",
            StringComparison.Ordinal);

    // Monotonic id for ordered-flip snapshots. Assigned under _gate at enqueue.
    private static long _orderedGuestFlipVersionSequence;

    // Caller must hold _gate. Enqueues an ordered-flip capture of sourceAddress
    // into the guest-work FIFO and returns its version. The Presentation is
    // built later by ExecuteOrderedGuestFlip on the render thread, once the
    // no-wait snapshot copy has been recorded and submitted in queue order.
    private static long TryEnqueueOrderedGuestFlipLocked(
        ulong sourceAddress,
        uint width,
        uint height)
    {
        var version = ++_orderedGuestFlipVersionSequence;
        EnqueueGuestWorkLocked(
            new VulkanOrderedGuestFlip(version, sourceAddress, width, height));
        return version;
    }

    // Caller must hold _gate. On a display-buffer flip, either enqueues an
    // ordered-flip capture (SHARPEMU_ORDERED_FLIP on -- the capture publishes its
    // own version-tagged Presentation on the render thread once the no-wait copy
    // is recorded), or, when off, builds the composite-heuristic Presentation
    // immediately with the current byte-for-byte shape (GuestImageVersion == 0).
    // Returns the enqueued ordered-flip version, or 0 when off.
    private static long BuildDisplayFlipPresentationLocked(
        ulong presentAddress,
        uint width,
        uint height)
    {
        if (ShouldUseOrderedGuestFlip())
        {
            return TryEnqueueOrderedGuestFlipLocked(presentAddress, width, height);
        }

        // An actively playing AvPlayer video owns the swapchain; skip the game's
        // concurrent display-buffer flip so the intro clip is not overwritten by
        // its loading frames. Mirrors the same VideoPresentIsActive guard in the
        // ordered-flip publish path (ExecuteOrderedGuestFlip). The video's own
        // frames arrive via TryPresentVideoFrame and are never suppressed.
        if (VideoPresentIsActive())
        {
            return 0;
        }

        var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
        _latestPresentation = new Presentation(
            null,
            width,
            height,
            sequence,
            GuestDrawKind.None,
            TranslatedDraw: null,
            // The blit must run after the frame's draws; without this the flip
            // samples the source image mid-render.
            RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
            IsSplash: false,
            GuestImageAddress: presentAddress);
        return 0;
    }

    public static void EnsureStarted(uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed || _thread is not null)
            {
                return;
            }
        }

        var hasSplash = PngSplashLoader.TryLoad(
            out var splashPixels,
            out var splashWidth,
            out var splashHeight);
        lock (_gate)
        {
            if (_closed || _thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            _latestPresentation ??= _splashHidden
                ? new Presentation(
                    CreateBlackFrame(width, height),
                    width,
                    height,
                    1,
                    GuestDrawKind.None,
                    TranslatedDraw: null,
                    RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                    IsSplash: false)
                : hasSplash
                ? new Presentation(
                    splashPixels,
                    splashWidth,
                    splashHeight,
                    1,
                    GuestDrawKind.None,
                    TranslatedDraw: null,
                    RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                    IsSplash: true)
                : new Presentation(
                    null,
                    width,
                    height,
                    0,
                    GuestDrawKind.None,
                    TranslatedDraw: null,
                    RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                    IsSplash: false);
            _closeRequested = false;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "SharpEmu Vulkan VideoOut",
            };
            _thread.Start();
        }
    }

    public static void HideSplashScreen()
    {
        lock (_gate)
        {
            _splashHidden = true;
            if (_closed || _latestPresentation is not { IsSplash: true } latest)
            {
                return;
            }

            var sequence = latest.Sequence + 1;
            _latestPresentation = new Presentation(
                CreateBlackFrame(latest.Width, latest.Height),
                latest.Width,
                latest.Height,
                sequence,
                GuestDrawKind.None,
                TranslatedDraw: null,
                RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                IsSplash: false);
            System.Threading.Monitor.PulseAll(_gate);
            Console.Error.WriteLine("[LOADER][INFO] Vulkan VideoOut hid splash");
        }
    }

    public static void Submit(byte[] bgraFrame, uint width, uint height)
    {
        if (bgraFrame.Length != checked((int)(width * height * 4)))
        {
            return;
        }

        if (ShouldTracePresentedGuestImageContentsForDiagnostics())
        {
            Console.Error.WriteLine($"[LOADER][TRACE] vk.submit_call kind=Submit {width}x{height}");
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            _latestPresentation = new Presentation(
                bgraFrame,
                width,
                height,
                sequence,
                GuestDrawKind.None,
                TranslatedDraw: null,
                RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                IsSplash: false);
            System.Threading.Monitor.PulseAll(_gate);
            if (_thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            _closeRequested = false;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "SharpEmu Vulkan VideoOut",
            };
            _thread.Start();
        }
    }

    // Direct-present a CPU-decoded video frame (BGRA8, matching the swapchain's
    // B8G8R8A8 order) straight onto the swapchain, bypassing the guest's own
    // texture-allocator blit. Gated by the caller (sceAvPlayer, under
    // SHARPEMU_AVPLAYER_PRESENT) so default runs never reach here. Reuses the
    // same deadlock-free CPU-upload Presentation plumbing as Submit and the
    // splash screen: the frame is latched as _latestPresentation.Pixels and the
    // render thread maps it into the staging buffer and copies/scales it to the
    // acquired swapchain image on its own loop. No QueueWaitIdle or other
    // render-thread wait is taken on this (guest AvPlayer worker) thread, so it
    // can never wedge the presenter. Oversized frames (e.g. 3840x2160) are
    // downscaled to the window extent by the existing ScaleBgra path before the
    // staging-size check, so they present rather than being dropped. Returns
    // false (logged + skipped by the caller) on a malformed buffer or a closed
    // presenter. Because it lands in the normal swapchain present path, the
    // frame is captured by SHARPEMU_DUMP_SWAPCHAIN like any other.
    public static bool TryPresentVideoFrame(byte[] bgra, int width, int height)
    {
        if (width <= 0 ||
            height <= 0 ||
            bgra.Length != checked(width * height * 4))
        {
            return false;
        }

        if (ShouldTracePresentedGuestImageContentsForDiagnostics())
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] vk.submit_call kind=TryPresentVideoFrame {width}x{height}");
        }

        lock (_gate)
        {
            if (_closed)
            {
                return false;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            _latestPresentation = new Presentation(
                bgra,
                (uint)width,
                (uint)height,
                sequence,
                GuestDrawKind.None,
                TranslatedDraw: null,
                RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                IsSplash: false);
            // Mark the video active so the game's own flips do not overwrite the
            // playing clip on the swapchain (they otherwise race and win). The
            // window lapses shortly after the last delivered frame (end of clip).
            _lastVideoPresentTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            System.Threading.Monitor.PulseAll(_gate);
            if (_thread is not null)
            {
                return true;
            }

            _windowWidth = (uint)width;
            _windowHeight = (uint)height;
            _closeRequested = false;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "SharpEmu Vulkan VideoOut",
            };
            _thread.Start();
        }

        return true;
    }

    private static readonly HashSet<ulong> _tracedNggAmplify = new();

    /// <summary>
    /// Marks an NGG primitive-shader draw whose compute-amplification capture
    /// (running the merged ES/GS as a compute dispatch that writes POS/PARAM/
    /// PRIM exports into vertex/index SSBOs, then feeding them to the existing
    /// indexed draw path) is not yet implemented. Emitting the trace here lets
    /// a boot confirm detection reached the presenter seam where the dispatch
    /// will eventually be issued.
    /// </summary>
    public static void NoteNggAmplifyPending(
        ulong exportShaderAddress,
        uint instanceCount,
        uint indexCount)
    {
        lock (_gate)
        {
            if (!_tracedNggAmplify.Add(exportShaderAddress))
            {
                return;
            }
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] vk.ngg_amplify es=0x{exportShaderAddress:X16} " +
            $"subgroups={instanceCount} indexCount={indexCount} dispatched=0 " +
            "(compute-capture not yet implemented)");
    }

    public static void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height)
    {
        if (drawKind == GuestDrawKind.None || width == 0 || height == 0)
        {
            return;
        }

        if (ShouldTracePresentedGuestImageContentsForDiagnostics())
        {
            Console.Error.WriteLine($"[LOADER][TRACE] vk.submit_call kind=SubmitGuestDraw({drawKind}) {width}x{height}");
        }

        lock (_gate)
        {
            if (_closed ||
                _latestPresentation is { Pixels: null } latest &&
                latest.DrawKind == drawKind &&
                latest.Width == width &&
                latest.Height == height)
            {
                return;
            }

            // An actively playing AvPlayer video owns the swapchain; the game's
            // concurrent loading draws must not overwrite the intro clip.
            if (VideoPresentIsActive())
            {
                return;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            _latestPresentation = new Presentation(
                null,
                width,
                height,
                sequence,
                drawKind,
                TranslatedDraw: null,
                RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                IsSplash: false);
            System.Threading.Monitor.PulseAll(_gate);
            if (_thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "SharpEmu Vulkan VideoOut",
            };
            _thread.Start();
        }
    }

    public static void SubmitTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<VulkanGuestDrawTexture> textures,
        IReadOnlyList<VulkanGuestMemoryBuffer> globalMemoryBuffers,
        uint width,
        uint height,
        uint attributeCount,
        byte[]? vertexSpirv = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        VulkanGuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<VulkanGuestVertexBuffer>? vertexBuffers = null,
        VulkanGuestRenderState? renderState = null)
    {
        if (pixelSpirv.Length == 0 || width == 0 || height == 0)
        {
            return;
        }

        if (ShouldTracePresentedGuestImageContentsForDiagnostics())
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] vk.submit_call kind=SubmitTranslatedDraw {width}x{height} textures={textures.Count}");
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            // An actively playing AvPlayer video owns the swapchain; the game's
            // concurrent translated composite must not overwrite the intro clip.
            if (VideoPresentIsActive())
            {
                return;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            _latestPresentation = new Presentation(
                null,
                width,
                height,
                sequence,
                GuestDrawKind.None,
                new VulkanTranslatedGuestDraw(
                    vertexSpirv ?? [],
                    pixelSpirv,
                    textures.ToArray(),
                    globalMemoryBuffers.ToArray(),
                    vertexBuffers?.ToArray() ?? [],
                    attributeCount,
                    vertexCount,
                    instanceCount,
                    primitiveType,
                    indexBuffer,
                    renderState ?? VulkanGuestRenderState.Default),
                RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                IsSplash: false);
            System.Threading.Monitor.PulseAll(_gate);
            if (_thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "SharpEmu Vulkan VideoOut",
            };
            _thread.Start();
        }
    }

    public static void SubmitOffscreenTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<VulkanGuestDrawTexture> textures,
        IReadOnlyList<VulkanGuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        VulkanGuestRenderTarget target,
        byte[]? vertexSpirv = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        VulkanGuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<VulkanGuestVertexBuffer>? vertexBuffers = null,
        VulkanGuestRenderState? renderState = null,
        VulkanGuestDepthTarget? depthTarget = null,
        VulkanGuestIndirectArgs? indirectArgs = null,
        ulong pixelShaderAddress = 0)
    {
        SubmitOffscreenTranslatedDraw(
            pixelSpirv,
            textures,
            globalMemoryBuffers,
            attributeCount,
            [target],
            vertexSpirv,
            vertexCount,
            instanceCount,
            primitiveType,
            indexBuffer,
            vertexBuffers,
            renderState,
            depthTarget,
            indirectArgs,
            pixelShaderAddress: pixelShaderAddress);
    }

    public static void SubmitOffscreenTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<VulkanGuestDrawTexture> textures,
        IReadOnlyList<VulkanGuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        IReadOnlyList<VulkanGuestRenderTarget> targets,
        byte[]? vertexSpirv = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        VulkanGuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<VulkanGuestVertexBuffer>? vertexBuffers = null,
        VulkanGuestRenderState? renderState = null,
        VulkanGuestDepthTarget? depthTarget = null,
        VulkanGuestIndirectArgs? indirectArgs = null,
        byte[]? computeCaptureSpirv = null,
        NggComputeCapture? computeCapture = null,
        uint computeInvocationCount = 0,
        IReadOnlyList<VulkanGuestMemoryBuffer>? computeCaptureInputs = null,
        ulong pixelShaderAddress = 0)
    {
        if (pixelSpirv.Length == 0 ||
            targets.Count == 0 ||
            targets.Count > 8 ||
            targets.Any(target =>
                target.Address == 0 || target.Width == 0 || target.Height == 0))
        {
            return;
        }

        // Mismatched target extents and duplicate-address slots are handled at
        // execution time (min-extent render area, scratch redirect); neither is
        // a reason to drop the draw here.
        var firstTarget = targets[0];
        if (ShouldTracePresentedGuestImageContentsForDiagnostics())
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] vk.submit_call kind=SubmitOffscreenTranslatedDraw " +
                $"targets={targets.Count} first=0x{firstTarget.Address:X16} " +
                $"{firstTarget.Width}x{firstTarget.Height} textures={textures.Count}");

            // For the final display composite (the fullscreen pass that writes a
            // registered flip buffer), list each sampled input so we can tell
            // whether it binds a GPU-produced image (RgbaPixels empty -> resolved
            // to the rendered VkImage) or falls back to a CPU upload of
            // never-written guest memory (RgbaPixels populated -> reads black).
            bool isDisplayComposite;
            lock (_gate)
            {
                isDisplayComposite = _registeredDisplayBuffers.Contains(firstTarget.Address);
            }
            if (isDisplayComposite)
            {
                for (var i = 0; i < textures.Count; i++)
                {
                    var t = textures[i];
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.composite_input target=0x{firstTarget.Address:X16} " +
                        $"idx={i} addr=0x{t.Address:X16} fmt={t.Format} num={t.NumberType} " +
                        $"deferred={(t.RgbaPixels.Length == 0)} fallback={t.IsFallback} " +
                        $"storage={t.IsStorage}");
                }
            }
        }

        var effectiveRenderState = renderState ?? VulkanGuestRenderState.Default;
        if (effectiveRenderState.Blends.Count == 1 && targets.Count > 1)
        {
            effectiveRenderState = effectiveRenderState with
            {
                Blends = Enumerable.Repeat(effectiveRenderState.Blends[0], targets.Count).ToArray(),
            };
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            foreach (var target in targets)
            {
                var guestTextureFormat = GetGuestTextureFormat(
                    target.Format,
                    target.NumberType);
                // Pre-register the target as a GPU producer even when its guest
                // format has no canonical mapping (stored as 0): a consumer
                // translated between this enqueue and the draw's execution must
                // defer to the rendered image instead of uploading never-written
                // guest memory. _availableGuestImages keeps format semantics for
                // the flip election, so only known formats enter it.
                _renderTargetGuestImages[target.Address] = guestTextureFormat;
                if (guestTextureFormat != 0)
                {
                    _availableGuestImages[target.Address] = guestTextureFormat;
                }
            }

            EnqueueGuestWorkLocked(
                new VulkanOffscreenGuestDraw(
                    new VulkanTranslatedGuestDraw(
                        vertexSpirv ?? [],
                        pixelSpirv,
                        textures.ToArray(),
                        globalMemoryBuffers.ToArray(),
                        vertexBuffers?.ToArray() ?? [],
                        attributeCount,
                        vertexCount,
                        instanceCount,
                        primitiveType,
                        indexBuffer,
                        effectiveRenderState,
                        indexBuffer is not null ? indirectArgs : null,
                        computeCaptureSpirv,
                        computeCapture,
                        computeInvocationCount,
                        computeCaptureInputs,
                        pixelShaderAddress),
                    targets.ToArray(),
                    PublishTarget: true,
                    Depth: depthTarget));
        }
    }

    // Depth-only guest pass: a draw with no color exports (depth prepass or a
    // DB clear draw) still writes/clears its depth surface. The work item binds
    // a synthetic address-0 color target that execution redirects to a cached
    // scratch surface (color writes are masked off by the caller's render
    // state), so the shared offscreen path renders it and publishes the depth
    // address as a GPU producer for later deferred-lighting reads.
    public static void SubmitDepthOnlyTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<VulkanGuestDrawTexture> textures,
        IReadOnlyList<VulkanGuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        VulkanGuestDepthTarget depthTarget,
        byte[]? vertexSpirv = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        VulkanGuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<VulkanGuestVertexBuffer>? vertexBuffers = null,
        VulkanGuestRenderState? renderState = null,
        VulkanGuestIndirectArgs? indirectArgs = null)
    {
        if (pixelSpirv.Length == 0 ||
            depthTarget.Address == 0 ||
            depthTarget.Width == 0 ||
            depthTarget.Height == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            EnqueueGuestWorkLocked(
                new VulkanOffscreenGuestDraw(
                    new VulkanTranslatedGuestDraw(
                        vertexSpirv ?? [],
                        pixelSpirv,
                        textures.ToArray(),
                        globalMemoryBuffers.ToArray(),
                        vertexBuffers?.ToArray() ?? [],
                        attributeCount,
                        vertexCount,
                        instanceCount,
                        primitiveType,
                        indexBuffer,
                        renderState ?? VulkanGuestRenderState.Default,
                        indexBuffer is not null ? indirectArgs : null),
                    [new VulkanGuestRenderTarget(
                        Address: 0,
                        depthTarget.Width,
                        depthTarget.Height,
                        Format: 10,
                        NumberType: 0)],
                    PublishTarget: false,
                    Depth: depthTarget));
        }
    }

    // Stage 2 ordered-flip composite redirect. Renders the frame's retained
    // targetless composite draw directly into the flipped display buffer, then
    // pre-registers that address as a GPU-produced image so the ordered-flip
    // capture that follows (TrySubmitGuestImage -> BuildDisplayFlipPresentation)
    // snapshots the freshly rendered scanout surface instead of resolving to an
    // internal composite target. The offscreen draw is enqueued ahead of the
    // ordered capture on the same guest-work FIFO, so it executes first and the
    // capture reads its output. Only reachable from the SHARPEMU_ORDERED_FLIP
    // gated RFlip path, so default runs never touch this. Returns false if the
    // target is unusable (empty shader, zero extent, or an unmapped format).
    public static bool TrySubmitDisplayCompositeDraw(
        byte[] pixelSpirv,
        IReadOnlyList<VulkanGuestDrawTexture> textures,
        IReadOnlyList<VulkanGuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        VulkanGuestRenderTarget target,
        byte[]? vertexSpirv = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        VulkanGuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<VulkanGuestVertexBuffer>? vertexBuffers = null,
        VulkanGuestRenderState? renderState = null)
    {
        if (pixelSpirv.Length == 0 ||
            target.Address == 0 ||
            target.Width == 0 ||
            target.Height == 0)
        {
            return false;
        }

        var guestTextureFormat = GetGuestTextureFormat(target.Format, target.NumberType);
        if (guestTextureFormat == 0)
        {
            return false;
        }

        SubmitOffscreenTranslatedDraw(
            pixelSpirv,
            textures,
            globalMemoryBuffers,
            attributeCount,
            target,
            vertexSpirv,
            vertexCount,
            instanceCount,
            primitiveType,
            indexBuffer,
            vertexBuffers,
            renderState);

        lock (_gate)
        {
            if (_closed)
            {
                return false;
            }

            // Pre-register the scanout address as a completed GPU image so the
            // flip resolves known=true and captures this address directly. The
            // offscreen draw enqueued above republishes the same address once it
            // executes, so the ordered capture reads a rendered VkImage.
            _gpuGuestImages[target.Address] = guestTextureFormat;
        }

        return true;
    }

    public static void SubmitStorageTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<VulkanGuestDrawTexture> textures,
        IReadOnlyList<VulkanGuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        uint width,
        uint height)
    {
        if (pixelSpirv.Length == 0 ||
            width == 0 ||
            height == 0 ||
            textures.All(texture => !texture.IsStorage))
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            EnqueueGuestWorkLocked(
                new VulkanOffscreenGuestDraw(
                    new VulkanTranslatedGuestDraw(
                        [],
                        pixelSpirv,
                        textures.ToArray(),
                        globalMemoryBuffers.ToArray(),
                        [],
                        attributeCount,
                        3,
                        1,
                        4,
                        null,
                        VulkanGuestRenderState.Default),
                    [new VulkanGuestRenderTarget(
                        Address: 0,
                        width,
                        height,
                        Format: 12,
                        NumberType: 7)],
                    PublishTarget: false));
        }
    }

    public static void SubmitComputeDispatch(
        ulong shaderAddress,
        byte[] computeSpirv,
        IReadOnlyList<VulkanGuestDrawTexture> textures,
        IReadOnlyList<VulkanGuestMemoryBuffer> globalMemoryBuffers,
        uint groupCountX,
        uint groupCountY,
        uint groupCountZ)
    {
        if (computeSpirv.Length == 0 ||
            groupCountX == 0 ||
            groupCountY == 0 ||
            groupCountZ == 0 ||
            textures.All(texture => !texture.IsStorage))
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            // Register the dispatch's storage outputs as render targets at
            // enqueue time, exactly like graphics targets. A later graphics draw
            // in the same submit that samples one of these addresses is enqueued
            // before this dispatch executes; without the pre-registration its
            // availability gate misses and it snapshots never-written guest
            // memory (zeros) instead of deferring to the compute-written image.
            // The address is registered even when its guest render-target format
            // is unmapped, because the execution path creates the VkImage via the
            // sampled-format decoder regardless; the stored value is presence-only.
            foreach (var texture in textures)
            {
                if (!texture.IsStorage || texture.Address == 0)
                {
                    continue;
                }

                var guestTextureFormat = GetGuestTextureFormat(
                    texture.Format,
                    texture.NumberType);
                _availableGuestImages[texture.Address] = guestTextureFormat;
                _renderTargetGuestImages[texture.Address] = guestTextureFormat;
            }

            EnqueueGuestWorkLocked(
                new VulkanComputeGuestDispatch(
                    shaderAddress,
                    computeSpirv,
                    textures.ToArray(),
                    globalMemoryBuffers.ToArray(),
                    groupCountX,
                    groupCountY,
                    groupCountZ));
        }
    }

    public static bool TrySubmitGuestImage(
        ulong address,
        uint width,
        uint height,
        uint pitchInPixel)
    {
        var traceSubmission = false;
        lock (_gate)
        {
            // VideoOut registration does not imply a rendered Vulkan image.
            var presentAddress = address;
            var presentReason = "direct";
            var known = _gpuGuestImages.ContainsKey(address);
            if (!known &&
                TryResolveDisplayBufferSourceLocked(
                    address,
                    out var sourceAddress,
                    out presentReason))
            {
                if (_tracedGuestImageSubmissions.Add((address, width, height)))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.flip_redirect display=0x{address:X16} " +
                        $"source=0x{sourceAddress:X16} {width}x{height} " +
                        $"reason={presentReason}");
                }

                presentAddress = sourceAddress;
                known = true;
            }

            // The flip closes the frame's render window; latch its composite so
            // the next window elects a fresh candidate.
            LatchFrameCompositeLocked();

            if (ShouldTracePresentedGuestImageContentsForDiagnostics())
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.submit_call kind=TrySubmitGuestImage addr=0x{address:X16} " +
                    $"{width}x{height} known={known} source=0x{presentAddress:X16} " +
                    $"reason={presentReason} composite=0x{_lastFrameCompositeAddress:X16}");
            }

            if (_closed)
            {
                return false;
            }

            // The caller (VideoOutExports.SubmitFlip) reports the flip as successful either
            // way, so an unregistered address means the frame is dropped silently; warn once
            // per address so that shows up in the log.
            if (!known)
            {
                if (_tracedGuestImageSubmissions.Add((address, width, height)))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] vk.submit_guest_image_unknown addr=0x{address:X16} " +
                        $"{width}x{height} - flip target has no completed GPU render output " +
                        $"(available={_availableGuestImages.ContainsKey(address)} " +
                        $"gpu_images={_gpuGuestImages.Count} avail_images={_availableGuestImages.Count})");
                }

                return false;
            }

            traceSubmission =
                _tracedGuestImageSubmissions.Add((address, width, height));
            _ = BuildDisplayFlipPresentationLocked(presentAddress, width, height);
            System.Threading.Monitor.PulseAll(_gate);
            if (_thread is not null)
            {
                return true;
            }

            _windowWidth = width;
            _windowHeight = height;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "SharpEmu Vulkan VideoOut",
            };
            _thread.Start();
        }

        if (traceSubmission)
        {
            var effectivePitch = pitchInPixel == 0 ? width : pitchInPixel;
            Console.Error.WriteLine(
                $"[LOADER][TRACE] vk.submit_guest_image addr=0x{address:X16} " +
                $"size={width}x{height} pitch={effectivePitch}");
        }

        return true;
    }

    // Display buffers registered through sceVideoOutRegisterBuffers are valid flip targets
    // even when no AGC render-target write to them was ever observed.
    internal static void RegisterKnownDisplayBuffer(ulong address, uint guestFormat)
    {
        if (address == 0 || guestFormat == 0)
        {
            return;
        }

        lock (_gate)
        {
            _availableGuestImages[address] = guestFormat;
            _registeredDisplayBuffers.Add(address);
            _hasRegisteredDisplayBuffers = true;
        }
    }

    // Called for GPU DMA/copy packets. A copy whose destination is a registered
    // display buffer reveals the true present source for that buffer's flips.
    internal static void NoteGuestMemoryCopy(
        ulong destinationAddress,
        ulong sourceAddress,
        uint byteCount)
    {
        if (!_hasRegisteredDisplayBuffers || destinationAddress == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var displayBuffer in _registeredDisplayBuffers)
            {
                if (destinationAddress < displayBuffer ||
                    destinationAddress >= displayBuffer + DisplayBufferCopyWindowBytes)
                {
                    continue;
                }

                if (_tracedDisplayBufferCopies.Add((displayBuffer, sourceAddress)))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.dma_to_display display=0x{displayBuffer:X16} " +
                        $"dst=0x{destinationAddress:X16} src=0x{sourceAddress:X16} " +
                        $"bytes={byteCount}");
                }

                if (sourceAddress != 0)
                {
                    _displayBufferCopySources[displayBuffer] = sourceAddress;
                }
            }
        }
    }

    // Games composite the frame into internal render targets and then flip a
    // display buffer registered through sceVideoOutRegisterBuffers; on hardware
    // a final GPU pass writes that buffer, but the HLE GPU does not execute it
    // yet, so no rendered image ever exists at the flipped address. Bridge the
    // flip to the best rendered source instead of dropping the frame, in
    // priority order: an observed copy into the buffer, the frame's composite
    // pass, then the last published full-screen target.
    // Caller must hold _gate.
    private static bool TryResolveDisplayBufferSourceLocked(
        ulong flipAddress,
        out ulong sourceAddress,
        out string reason)
    {
        sourceAddress = 0;
        reason = "none";
        if (!_availableGuestImages.ContainsKey(flipAddress))
        {
            return false;
        }

        // A GPU/DMA copy into this display buffer identified its true source.
        if (_displayBufferCopySources.TryGetValue(flipAddress, out var copySource) &&
            _gpuGuestImages.ContainsKey(copySource))
        {
            sourceAddress = copySource;
            reason = "copy_source";
            return true;
        }

        // The frame's composite pass (most sampled input textures wins).
        var composite = _frameCompositeCandidateAddress != 0
            ? _frameCompositeCandidateAddress
            : _lastFrameCompositeAddress;
        if (composite != 0 && _gpuGuestImages.ContainsKey(composite))
        {
            // The most-inputs heuristic can elect an all-black composite (Astro
            // Bot's title composite reads many inputs yet resolves to opaque
            // black) over a color-bearing target. When a readback has positively
            // observed the elected composite as all-black AND a different
            // presentable target as nonblack, present the color-bearing target
            // instead. Only fires on positive black/nonblack evidence, so an
            // ordinary run with no readback keeps the plain composite choice.
            if (ShouldPreferNonblackPresentContent() &&
                _renderedContentNonblack.TryGetValue(composite, out var compositeNonblack) &&
                !compositeNonblack &&
                _latestNonblackPresentableAddress != 0 &&
                _latestNonblackPresentableAddress != composite &&
                _gpuGuestImages.ContainsKey(_latestNonblackPresentableAddress))
            {
                sourceAddress = _latestNonblackPresentableAddress;
                reason = "composite_black_prefer_content";
                return true;
            }

            sourceAddress = composite;
            reason = "composite";
            return true;
        }

        sourceAddress = _latestPresentableGuestImageAddress;
        reason = "latest_target";
        return sourceAddress != 0 && _gpuGuestImages.ContainsKey(sourceAddress);
    }

    // Caller must hold _gate.
    private static void NoteRenderedGuestImageLocked(
        ulong address,
        uint width,
        uint height,
        int inputTextureCount = 0)
    {
        if (width < MinPresentableGuestImageWidth ||
            height < MinPresentableGuestImageHeight)
        {
            return;
        }

        _latestPresentableGuestImageAddress = address;

        // The final composite pass samples many inputs (Astro Bot's menu
        // composite reads 15 textures); geometry/clear passes read none.
        if (inputTextureCount < MinCompositeInputTextureCount)
        {
            return;
        }

        var score = ((long)inputTextureCount << 32) | ((long)width * height);
        if (score >= _frameCompositeCandidateScore)
        {
            _frameCompositeCandidateScore = score;
            _frameCompositeCandidateAddress = address;
        }
    }

    // Caller must hold _gate.
    private static void LatchFrameCompositeLocked()
    {
        if (_frameCompositeCandidateAddress != 0)
        {
            _lastFrameCompositeAddress = _frameCompositeCandidateAddress;
        }

        _frameCompositeCandidateAddress = 0;
        _frameCompositeCandidateScore = 0;
    }

    // SHARPEMU_PRESENT_CONTENT_PREFER gates the "present a nonblack target over
    // an all-black composite" refinement. Default on; set to "0" to fall back
    // to the pure most-inputs composite heuristic during a live bisect.
    private static bool ShouldPreferNonblackPresentContent() =>
        !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PRESENT_CONTENT_PREFER"),
            "0",
            StringComparison.Ordinal);

    // Records what a guest-image readback observed for a rendered target so the
    // flip resolver can prefer color-bearing targets over an all-black
    // composite. Runs on the render thread; takes _gate itself (Monitor is
    // reentrant, so calling it from a site that already holds _gate is safe).
    internal static void NoteGuestImageContentObserved(
        ulong address,
        uint width,
        uint height,
        bool hasNonblackContent)
    {
        if (address == 0)
        {
            return;
        }

        lock (_gate)
        {
            _renderedContentNonblack[address] = hasNonblackContent;
            if (hasNonblackContent)
            {
                if (width >= MinPresentableGuestImageWidth &&
                    height >= MinPresentableGuestImageHeight)
                {
                    _latestNonblackPresentableAddress = address;
                }
            }
            else if (_latestNonblackPresentableAddress == address)
            {
                _latestNonblackPresentableAddress = 0;
            }
        }
    }

    // Caller must hold _gate.
    private static void ForgetRenderedGuestImageLocked(ulong address)
    {
        if (_latestPresentableGuestImageAddress == address)
        {
            _latestPresentableGuestImageAddress = 0;
        }

        if (_frameCompositeCandidateAddress == address)
        {
            _frameCompositeCandidateAddress = 0;
            _frameCompositeCandidateScore = 0;
        }

        if (_lastFrameCompositeAddress == address)
        {
            _lastFrameCompositeAddress = 0;
        }

        _renderedContentNonblack.Remove(address);
        if (_latestNonblackPresentableAddress == address)
        {
            _latestNonblackPresentableAddress = 0;
        }
    }

    internal static void ResetGuestImageTrackingForTests()
    {
        lock (_gate)
        {
            _availableGuestImages.Clear();
            _gpuGuestImages.Clear();
            _renderTargetGuestImages.Clear();
            _tracedGuestImageSubmissions.Clear();
            _tracedDepthExtentRepairs.Clear();
            _tracedIrregularMrtLayouts.Clear();
            _latestPresentableGuestImageAddress = 0;
            _frameCompositeCandidateAddress = 0;
            _frameCompositeCandidateScore = 0;
            _lastFrameCompositeAddress = 0;
            _renderedContentNonblack.Clear();
            _latestNonblackPresentableAddress = 0;
            _registeredDisplayBuffers.Clear();
            _displayBufferCopySources.Clear();
            _tracedDisplayBufferCopies.Clear();
            _hasRegisteredDisplayBuffers = false;
        }
    }

    internal static void PublishRenderedGuestImageForTests(
        ulong address,
        uint guestFormat,
        uint width,
        uint height,
        int inputTextureCount = 0)
    {
        lock (_gate)
        {
            _availableGuestImages[address] = guestFormat;
            _gpuGuestImages[address] = guestFormat;
            _renderTargetGuestImages[address] = guestFormat;
            NoteRenderedGuestImageLocked(address, width, height, inputTextureCount);
        }
    }

    internal static void RegisterPendingRenderTargetForTests(
        ulong address,
        uint format,
        uint numberType)
    {
        lock (_gate)
        {
            var guestTextureFormat = GetGuestTextureFormat(format, numberType);
            if (guestTextureFormat != 0)
            {
                _availableGuestImages[address] = guestTextureFormat;
                _renderTargetGuestImages[address] = guestTextureFormat;
            }
        }
    }

    internal static void RegisterCpuGuestImageForTests(ulong address, uint guestFormat)
    {
        lock (_gate)
        {
            _availableGuestImages[address] = guestFormat;
            _gpuGuestImages.Remove(address);
            _renderTargetGuestImages.Remove(address);
            ForgetRenderedGuestImageLocked(address);
        }
    }

    internal static void LatchFrameCompositeForTests()
    {
        lock (_gate)
        {
            LatchFrameCompositeLocked();
        }
    }

    internal static void RemoveRenderedGuestImageForTests(ulong address)
    {
        lock (_gate)
        {
            _availableGuestImages.Remove(address);
            _gpuGuestImages.Remove(address);
            _renderTargetGuestImages.Remove(address);
            ForgetRenderedGuestImageLocked(address);
        }
    }

    internal static bool IsGpuGuestImageForTests(ulong address)
    {
        lock (_gate)
        {
            return _gpuGuestImages.ContainsKey(address);
        }
    }

    internal static int PendingGuestWorkCountForTests()
    {
        lock (_gate)
        {
            return _pendingGuestWork.Count;
        }
    }

    // Sampled-texture (T#) format decode, exposed so a regression test can pin
    // that it agrees with the render-target (CB) decode of the same dfmt/num
    // pair. A divergence would make the present sampler search a VkFormat the
    // scene render target was never registered under -- e.g. Astro Bot's title
    // present sampling the R16G16B16A16Sfloat scene as dfmt 12 / num 7.
    internal static Format GetSampledTextureFormatForTests(uint format, uint numberType) =>
        Presenter.GetTextureFormat(format, numberType);

    internal static bool TryResolveDisplayBufferSourceForTests(
        ulong flipAddress,
        out ulong sourceAddress)
    {
        lock (_gate)
        {
            return TryResolveDisplayBufferSourceLocked(
                flipAddress,
                out sourceAddress,
                out _);
        }
    }

    // Ordered-flip capture test hooks. These drive the real enqueue/present-shape
    // code paths without a Vulkan device (no render thread is started).
    internal static void ResetOrderedFlipStateForTests()
    {
        lock (_gate)
        {
            _pendingGuestWork.Clear();
            _enqueuedGuestWorkSequence = 0;
            _completedGuestWorkSequence = 0;
            _orderedGuestFlipVersionSequence = 0;
            _latestPresentation = null;
            _lastVideoPresentTicks = 0;
        }
    }

    // Present-arbitration test hooks. Drive the VideoPresentIsActive gate that
    // suppresses the game's concurrent flips/draws while an AvPlayer intro clip
    // is presenting, without a Vulkan device or a real TryPresentVideoFrame call.
    internal static void MarkVideoPresentActiveForTests()
    {
        lock (_gate)
        {
            _lastVideoPresentTicks =
                System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    internal static void ClearVideoPresentActiveForTests()
    {
        lock (_gate)
        {
            _lastVideoPresentTicks = 0;
        }
    }

    internal static bool VideoPresentIsActiveForTests()
    {
        lock (_gate)
        {
            return VideoPresentIsActive();
        }
    }

    // Runs the exact flip-presentation branch used by TrySubmitGuestImage.
    // Returns the enqueued ordered-flip version (0 when the gate is off).
    internal static long BuildDisplayFlipPresentationForTests(
        ulong presentAddress,
        uint width,
        uint height)
    {
        lock (_gate)
        {
            return BuildDisplayFlipPresentationLocked(presentAddress, width, height);
        }
    }

    internal static bool TryDequeueOrderedGuestFlipForTests(
        out long version,
        out ulong address,
        out uint width,
        out uint height)
    {
        lock (_gate)
        {
            if (_pendingGuestWork.TryDequeue(out var work) &&
                work is VulkanOrderedGuestFlip flip)
            {
                version = flip.Version;
                address = flip.Address;
                width = flip.Width;
                height = flip.Height;
                return true;
            }

            version = 0;
            address = 0;
            width = 0;
            height = 0;
            return false;
        }
    }

    // Reports the current latched presentation's flip identity, or (0, 0) with a
    // false return when no presentation has been built (e.g. the ordered-flip
    // path defers Presentation creation to the render thread).
    internal static bool TryGetLatestFlipPresentationForTests(
        out ulong guestImageAddress,
        out long guestImageVersion)
    {
        lock (_gate)
        {
            if (_latestPresentation is { } latest)
            {
                guestImageAddress = latest.GuestImageAddress;
                guestImageVersion = latest.GuestImageVersion;
                return true;
            }

            guestImageAddress = 0;
            guestImageVersion = 0;
            return false;
        }
    }

    // First-use initialization value for a depth surface the guest never
    // explicitly cleared: the compare-neutral value for the draw's ZFUNC.
    // Reverse-Z Greater/GreaterOrEqual tests must start at the 0.0 far plane
    // (a 1.0 fill rejects every fragment); Less/LessOrEqual start at 1.0.
    // Every other compare (Never/Equal/NotEqual/Always) has no neutral value,
    // so the guest's DB_DEPTH_CLEAR register value is the best initializer.
    internal static float SelectFirstUseDepthClearValue(
        uint compareOp,
        float guestClearDepth) =>
        compareOp switch
        {
            1 or 3 => 1.0f, // Less / LessOrEqual
            4 or 6 => 0.0f, // Greater / GreaterOrEqual (reverse-Z)
            _ => guestClearDepth,
        };

    internal static bool IsGpuGuestImageAvailable(
        ulong address,
        uint format,
        uint numberType)
    {
        if (address == 0)
        {
            return false;
        }

        lock (_gate)
        {
            // Whether a GPU producer exists at an address is independent of the
            // format the consumer samples it in, so deferral is decided purely by
            // address presence in the GPU-produced sets. Any address the GPU has
            // rendered (this frame or a prior one) holds real content in its
            // VkImage; guest memory for it was never written and reads back black.
            // Prefer the rendered image even when the sampled format differs from
            // the rendered format -- resolution binds a native-format view rather
            // than uploading never-written memory. Genuine CPU-asset textures live
            // only in _availableGuestImages and are deliberately not deferred here.
            return _gpuGuestImages.ContainsKey(address) ||
                _renderTargetGuestImages.ContainsKey(address);
        }
    }

    // Binding-time decision for a sampled (non-storage) guest input: should it
    // be deferred to its GPU-produced VkImage (bound with empty pixels, resolved
    // at execution against the rendered surface) instead of uploading guest
    // memory? Default (loose) mirrors IsGpuGuestImageAvailable: any address the
    // GPU has ever produced defers, regardless of the sampled format. Under
    // SHARPEMU_FLIP_COMPOSITE_FIX the fork's strict rule applies: defer only when
    // a producer registered at the address matches the sampled guest format, so a
    // format-mismatched or never-produced input reads real guest memory rather
    // than deferring empty and cascading to black.
    internal static bool ShouldDeferSampledGuestTexture(
        ulong address,
        uint format,
        uint numberType)
    {
        if (address == 0)
        {
            return false;
        }

        var flipCompositeFix = ShouldUseFlipCompositeFix();
        var sampledGuestFormat = GetGuestTextureFormat(format, numberType);
        lock (_gate)
        {
            var gpuKnown = _gpuGuestImages.TryGetValue(address, out var gpuFormat);
            var renderTargetKnown =
                _renderTargetGuestImages.TryGetValue(address, out var renderTargetFormat);
            return DeferSampledGuestTextureDecision(
                flipCompositeFix,
                gpuKnown,
                gpuFormat,
                renderTargetKnown,
                renderTargetFormat,
                sampledGuestFormat);
        }
    }

    // Pure decision extracted for unit testing. See ShouldDeferSampledGuestTexture.
    internal static bool DeferSampledGuestTextureDecision(
        bool flipCompositeFix,
        bool gpuFormatKnown,
        uint gpuFormat,
        bool renderTargetFormatKnown,
        uint renderTargetFormat,
        uint sampledGuestFormat)
    {
        if (!gpuFormatKnown && !renderTargetFormatKnown)
        {
            return false;
        }

        if (!flipCompositeFix)
        {
            return true;
        }

        // A producer registered with format 0 rendered through a target whose
        // guest format has no canonical mapping; a format mismatch cannot be
        // proven, and the rendered image is still authoritative over guest
        // memory the GPU never wrote. Defer on presence.
        if ((gpuFormatKnown && gpuFormat == 0) ||
            (renderTargetFormatKnown && renderTargetFormat == 0))
        {
            return true;
        }

        return sampledGuestFormat != 0 &&
            ((gpuFormatKnown && gpuFormat == sampledGuestFormat) ||
             (renderTargetFormatKnown && renderTargetFormat == sampledGuestFormat));
    }

    private static readonly HashSet<ulong> _tracedGateRejects = new();

    // A rendered target sampled in a different guest format still aliases its
    // VkImage when both formats share a Vulkan compatibility class; a mutable
    // image view reinterprets the texels. Cross-class pairs (different texel
    // size) cannot form a legal view and must keep the guest-memory path.
    private static bool AreAliasableGuestFormats(uint imageGuestFormat, uint viewGuestFormat)
    {
        if (imageGuestFormat == viewGuestFormat)
        {
            return true;
        }

        // Canonical guest codes round-trip through TryDecodeRenderTargetFormat
        // with numberType 0.
        return TryDecodeRenderTargetFormat(imageGuestFormat, 0, out var image) &&
            TryDecodeRenderTargetFormat(viewGuestFormat, 0, out var view) &&
            IsCompatibleViewFormat(image.Format, view.Format);
    }

    // A skipped draw never renders its pre-registered targets; drop them from
    // the render-target registry (unless a previous draw did render them) so
    // later consumers fall back to guest memory instead of deferring to an
    // alias that will never exist.
    private static void AbandonPendingRenderTargets(VulkanOffscreenGuestDraw work)
    {
        lock (_gate)
        {
            foreach (var target in work.Targets)
            {
                if (!_gpuGuestImages.ContainsKey(target.Address))
                {
                    _renderTargetGuestImages.Remove(target.Address);
                }
            }
        }
    }

    // MRT attachments may disagree on extent (a half-res aux target bound next
    // to the full-res scene target). Vulkan requires every framebuffer
    // attachment to be at least framebuffer-sized, so the pass renders at the
    // shared minimum extent; the primary target still receives the draw.
    internal static (uint Width, uint Height) ComputeMrtRenderExtent(
        IReadOnlyList<VulkanGuestRenderTarget> targets)
    {
        var width = targets[0].Width;
        var height = targets[0].Height;
        for (var index = 1; index < targets.Count; index++)
        {
            width = Math.Min(width, targets[index].Width);
            height = Math.Min(height, targets[index].Height);
        }

        return (width, height);
    }

    // Slots whose address duplicates an earlier color slot alias the same
    // guest surface (games leave don't-care MRT slots bound to an already-used
    // target). Returns null when every address is distinct; otherwise flags
    // each duplicate slot so its writes can be redirected to a scratch surface
    // instead of double-binding one image as two color attachments, which
    // Vulkan forbids.
    internal static bool[]? FindAliasedColorSlots(
        IReadOnlyList<VulkanGuestRenderTarget> targets)
    {
        bool[]? aliased = null;
        for (var index = 1; index < targets.Count; index++)
        {
            for (var prior = 0; prior < index; prior++)
            {
                if (targets[index].Address == targets[prior].Address)
                {
                    aliased ??= new bool[targets.Count];
                    aliased[index] = true;
                    break;
                }
            }
        }

        return aliased;
    }

    // Synthetic guest address for a duplicate MRT slot's scratch surface. The
    // top bits sit far above any canonical guest VA, and folding the slot and
    // extent into the address lets differently-shaped draws keep their scratch
    // surfaces cached side by side instead of thrashing one cache entry.
    internal static ulong AliasedSlotScratchAddress(int slot, uint width, uint height) =>
        0xFFFF_0000_0000_0000UL |
        ((ulong)(uint)slot << 40) |
        ((ulong)(width & 0xFFFFF) << 20) |
        (height & 0xFFFFF);

    public static bool TrySubmitGuestImageBlit(
        ulong sourceAddress,
        uint sourceWidth,
        uint sourceHeight,
        uint sourceFormat,
        uint sourceNumberType,
        ulong destinationAddress,
        uint destinationWidth,
        uint destinationHeight,
        uint destinationFormat,
        uint destinationNumberType)
    {
        if (sourceAddress == 0 ||
            destinationAddress == 0 ||
            sourceWidth == 0 ||
            sourceHeight == 0 ||
            destinationWidth == 0 ||
            destinationHeight == 0 ||
            !TryGetCopyFragmentShader(out var fragmentSpirv))
        {
            return false;
        }

        lock (_gate)
        {
            if (_closed ||
                !_availableGuestImages.ContainsKey(sourceAddress) ||
                GetGuestTextureFormat(destinationFormat, destinationNumberType) == 0)
            {
                return false;
            }
        }

        SubmitOffscreenTranslatedDraw(
            fragmentSpirv,
            [
                new VulkanGuestDrawTexture(
                    sourceAddress,
                    sourceWidth,
                    sourceHeight,
                    sourceFormat,
                    sourceNumberType,
                    [],
                    IsFallback: false,
                    IsStorage: false),
            ],
            [],
            attributeCount: 1,
            new VulkanGuestRenderTarget(
                destinationAddress,
                destinationWidth,
                destinationHeight,
                destinationFormat,
                destinationNumberType));
        return true;
    }

    private static bool TryGetCopyFragmentShader(out byte[] spirv)
    {
        lock (_gate)
        {
            if (_copyFragmentSpirv is not null)
            {
                spirv = _copyFragmentSpirv;
                return true;
            }
        }

        spirv = SpirvFixedShaders.CreateCopyFragment();

        lock (_gate)
        {
            _copyFragmentSpirv ??= spirv;
            spirv = _copyFragmentSpirv;
        }

        return true;
    }

    private static uint GetGuestTextureFormat(uint format, uint numberType)
    {
        if (!TryDecodeRenderTargetFormat(format, numberType, out var decoded))
        {
            return 0;
        }

        return decoded.Format switch
        {
            Format.R8Unorm => 36,
            Format.R8Uint => 49,
            Format.R8G8Unorm => 3,
            Format.A2R10G10B10UnormPack32 or
                Format.A2B10G10R10UnormPack32 => 9,
            Format.B10G11R11UfloatPack32 => 7,
            Format.R32Uint => GuestFormatR32Uint,
            Format.R32Sint => GuestFormatR32Sint,
            Format.R32Sfloat => GuestFormatR32Sfloat,
            Format.R16G16Unorm => 5,
            Format.R16G16Uint => GuestFormatR16G16Uint,
            Format.R16G16Sint => GuestFormatR16G16Sint,
            Format.R16G16Sfloat => GuestFormatR16G16Sfloat,
            Format.R32G32Sfloat => 75,
            // sRGB shares its canonical code with UNORM: the same VkImage is
            // aliased through a mutable sRGB view at sampling time.
            Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb => 56,
            Format.R8G8B8A8Uint => GuestFormatR8G8B8A8Uint,
            Format.R8G8B8A8Sint => GuestFormatR8G8B8A8Sint,
            Format.R16G16B16A16Unorm => 12,
            Format.R16G16B16A16Uint => GuestFormatR16G16B16A16Uint,
            Format.R16G16B16A16Sint => GuestFormatR16G16B16A16Sint,
            Format.R16G16B16A16Sfloat => 71,
            Format.R32G32B32A32Sfloat => 14,
            _ => 0,
        };
    }

    internal static bool TryDecodeRenderTargetFormat(
        uint dataFormat,
        uint numberType,
        out VulkanRenderTargetFormat result) =>
        TryDecodeRenderTargetFormat(dataFormat, numberType, componentSwap: 0, out result);

    // SHARPEMU_FORCE_SDR=1: every A2R10G10B10 (HDR10, COLOR_2_10_10_10) color
    // target renders all-black in our pipeline while 8-bit RGBA renders fine.
    // Decode dataFormat=9 as R8G8B8A8Unorm so the final tonemap output lands in a
    // format that renders and presents (the "disable HDR" fallback; colors may be
    // off since the game's HDR encode is unconverted, but the menu becomes visible).
    private static readonly bool ForceSdrOutput = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_FORCE_SDR"), "1", StringComparison.Ordinal);

    internal static bool TryDecodeRenderTargetFormat(
        uint dataFormat,
        uint numberType,
        uint componentSwap,
        out VulkanRenderTargetFormat result)
    {
        if (ForceSdrOutput && dataFormat == 9)
        {
            dataFormat = 10;
            numberType = 0;
        }

        var format = (dataFormat, numberType) switch
        {
            (4, 4) => Format.R32Uint,
            (4, 5) => Format.R32Sint,
            (4, 7) => Format.R32Sfloat,
            (5, 4) => Format.R16G16Uint,
            (5, 5) => Format.R16G16Sint,
            (5, 7) => Format.R16G16Sfloat,
            // CB_COLOR_INFO.COMP_SWAP is independent from FORMAT. For
            // COLOR_2_10_10_10, STD exposes the low 10-bit component as R
            // (A2B10G10R10 in Vulkan); ALT reverses R and B.
            (9, _) when componentSwap == 0 => Format.A2B10G10R10UnormPack32,
            (9, _) when componentSwap == 1 => Format.A2R10G10B10UnormPack32,
            (9, _) => Format.Undefined,
            (10, 4) => Format.R8G8B8A8Uint,
            (10, 5) => Format.R8G8B8A8Sint,
            (10, 9) => Format.R8G8B8A8Srgb,
            (10, _) => Format.R8G8B8A8Unorm,
            (11, 7) => Format.R32G32Sfloat,
            (12, 4) => Format.R16G16B16A16Uint,
            (12, 5) => Format.R16G16B16A16Sint,
            (12, 7) => Format.R16G16B16A16Sfloat,
            (GuestFormatR32Uint, _) or (20, 0) => Format.R32Uint,
            (GuestFormatR32Sint, _) => Format.R32Sint,
            (GuestFormatR32Sfloat, _) or (29, 0) or (4, 0) => Format.R32Sfloat,
            (GuestFormatR16G16Uint, _) => Format.R16G16Uint,
            (GuestFormatR16G16Sint, _) => Format.R16G16Sint,
            (GuestFormatR16G16Sfloat, _) => Format.R16G16Sfloat,
            (GuestFormatR8G8B8A8Uint, _) => Format.R8G8B8A8Uint,
            (GuestFormatR8G8B8A8Sint, _) => Format.R8G8B8A8Sint,
            (GuestFormatR16G16B16A16Uint, _) => Format.R16G16B16A16Uint,
            (GuestFormatR16G16B16A16Sint, _) => Format.R16G16B16A16Sint,
            (1, 0) or (36, 0) => Format.R8Unorm,
            (49, 0) => Format.R8Uint,
            (3, 0) => Format.R8G8Unorm,
            (5, 0) => Format.R16G16Unorm,
            (6, 7) or (7, 0) or (7, 7) => Format.B10G11R11UfloatPack32,
            (12, 0) => Format.R16G16B16A16Unorm,
            (13, 0) or (13, 7) or (14, 0) or (14, 7) => Format.R32G32B32A32Sfloat,
            (22, 0) or (71, 0) => Format.R16G16B16A16Sfloat,
            (56, 0) or (62, 0) or (64, 0) => Format.R8G8B8A8Unorm,
            (75, 0) => Format.R32G32Sfloat,
            _ => Format.Undefined,
        };

        if (format == Format.Undefined)
        {
            result = default;
            return false;
        }

        var outputKind = format switch
        {
            Format.R8Uint or Format.R32Uint or Format.R16G16Uint or
                Format.R8G8B8A8Uint or Format.R16G16B16A16Uint => Gen5PixelOutputKind.Uint,
            Format.R32Sint or Format.R16G16Sint or Format.R8G8B8A8Sint or
                Format.R16G16B16A16Sint => Gen5PixelOutputKind.Sint,
            _ => Gen5PixelOutputKind.Float,
        };
        result = new VulkanRenderTargetFormat(format, outputKind);
        return true;
    }

    private static bool IsCompatibleViewFormat(Format imageFormat, Format viewFormat)
    {
        if (imageFormat == viewFormat)
        {
            return true;
        }

        var imageClass = GetFormatCompatibilityClass(imageFormat);
        return imageClass != 0 && imageClass == GetFormatCompatibilityClass(viewFormat);
    }

    private static uint GetFormatCompatibilityClass(Format format) =>
        format switch
        {
            Format.R8Unorm or
            Format.R8Uint or
            Format.R8Sint => 8,
            Format.R16Sfloat or
            Format.R16Unorm or
            Format.R16Uint or
            Format.R16Sint or
            Format.R8G8Unorm or
            Format.R8G8Uint or
            Format.R8G8Sint => 16,
            Format.R32Uint or
            Format.R32Sint or
            Format.R32Sfloat or
            Format.R16G16Unorm or
            Format.R16G16Uint or
            Format.R16G16Sint or
            Format.R16G16Sfloat or
            Format.R8G8B8A8Unorm or
            Format.R8G8B8A8Srgb or
            Format.R8G8B8A8Uint or
            Format.R8G8B8A8Sint or
            Format.A2R10G10B10UnormPack32 or
            Format.A2B10G10R10UnormPack32 or
            Format.B10G11R11UfloatPack32 => 32,
            Format.R32G32Uint or
            Format.R32G32Sint or
            Format.R32G32Sfloat or
            Format.R16G16B16A16Unorm or
            Format.R16G16B16A16Uint or
            Format.R16G16B16A16Sint or
            Format.R16G16B16A16Sfloat => 64,
            Format.R32G32B32Sfloat => 96,
            Format.R32G32B32A32Uint or
            Format.R32G32B32A32Sint or
            Format.R32G32B32A32Sfloat => 128,
            _ => 0,
        };

    private static bool IsKnownGuestTextureFormat(uint format) =>
        format is 4 or 5 or 7 or 9 or 13 or 14 or 22 or 29 or 36 or 56 or 62 or 64 or 71;

    private static byte[] CreateBlackFrame(uint width, uint height)
    {
        if (width == 0 || height == 0 || width > 8192 || height > 8192)
        {
            width = 1;
            height = 1;
        }

        var pixels = GC.AllocateUninitializedArray<byte>(checked((int)(width * height * 4)));
        pixels.AsSpan().Clear();
        for (var offset = 3; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 0xFF;
        }

        return pixels;
    }

    private static void Run()
    {
        uint width;
        uint height;
        lock (_gate)
        {
            width = _windowWidth == 0 ? _latestPresentation?.Width ?? 1280 : _windowWidth;
            height = _windowHeight == 0 ? _latestPresentation?.Height ?? 720 : _windowHeight;
        }

        try
        {
            using var presenter = new Presenter(width, height);
            presenter.Run();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][ERROR] Vulkan VideoOut presenter failed: {exception}");
        }
        finally
        {
            lock (_gate)
            {
                _closed = true;
                _thread = null;
                System.Threading.Monitor.PulseAll(_gate);
            }
        }
    }

    private static bool TryTakePresentation(long presentedSequence, out Presentation presentation)
    {
        lock (_gate)
        {
            if (_latestPresentation is not { } latest ||
                latest.Sequence == presentedSequence ||
                latest.RequiredGuestWorkSequence > _completedGuestWorkSequence)
            {
                presentation = default;
                return false;
            }

            presentation = latest;
            return true;
        }
    }

    private static void EnqueueGuestWorkLocked(object work)
    {
        while (!_closed &&
               _thread is not null &&
               _pendingGuestWork.Count >= MaxPendingGuestWork)
        {
            System.Threading.Monitor.Wait(_gate);
        }

        if (_closed)
        {
            return;
        }

        _pendingGuestWork.Enqueue(work);
        _enqueuedGuestWorkSequence++;
        System.Threading.Monitor.PulseAll(_gate);
    }

    private static bool TryTakeGuestWork(out object work)
    {
        lock (_gate)
        {
            return _pendingGuestWork.TryDequeue(out work!);
        }
    }

    private static void CompleteGuestWork()
    {
        lock (_gate)
        {
            _completedGuestWorkSequence++;
            System.Threading.Monitor.PulseAll(_gate);
        }
    }

    private readonly record struct Presentation(
        byte[]? Pixels,
        uint Width,
        uint Height,
        long Sequence,
        GuestDrawKind DrawKind,
        VulkanTranslatedGuestDraw? TranslatedDraw,
        long RequiredGuestWorkSequence,
        bool IsSplash,
        ulong GuestImageAddress = 0,
        // Non-zero only for an ordered-flip capture: identifies the frozen
        // snapshot in _guestImageVersions to present instead of lazily sampling
        // the (now-overwritten) live render target at GuestImageAddress.
        long GuestImageVersion = 0);

    internal sealed class GuestImageResource
    {
        public ulong Address;
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public Format Format;
        public Image Image;
        public DeviceMemory Memory;
        public ImageView View;
        public ImageView[] MipViews = [];

        // The single-mip view an attachment must bind. Render-target surfaces
        // expose one view per mip; CPU-backed upload surfaces have no per-mip
        // views and alias their single-mip texture view in View. Indexing
        // MipViews[0] directly threw on every MRT draw whose target address
        // was first seen as a CPU texture upload.
        public ImageView AttachmentView => MipViews.Length > 0 ? MipViews[0] : View;
        public Dictionary<(Format Format, uint MipLevel, uint LevelCount, uint DstSelect), ImageView> FormatViews { get; } = new();
        public RenderPass RenderPass;
        public Framebuffer Framebuffer;
        public bool Initialized;
        public bool InitialUploadPending;
        public bool IsCpuBacked;
        // Depth surfaces carry a real depth format (D32_SFLOAT) and must use the
        // depth aspect for every barrier / view / subresource. A later pass that
        // samples this address binds the depth-aspect view and reads real depth
        // in .r instead of an empty (black) upload.
        public bool IsDepth;
        public ulong CpuContentFingerprint;
        public ulong MemorySize;
        public ulong LastUseStamp;

        // Monotonic stamp of the last COMPLETED content write (render-target
        // resolve, storage write, or upload) into this image. Distinct from
        // LastUseStamp, which advances merely on binding. Alias selection ranks
        // by this first so a freshly re-bound but stale variant cannot hide the
        // sibling that actually holds the guest allocation's current contents.
        public long ContentGeneration;

        // The layout of every mip of Image as of the most recently RECORDED
        // command, plus the stage/access scope of the last barrier (or the
        // readers absorbed into it since). Guest work is recorded on the one
        // render thread in submission order, so record-time tracking is exact.
        // Only TryPlanGuestImageTransition and InvalidateGuestImageLayout may
        // mutate these.
        public ImageLayout CurrentLayout = ImageLayout.Undefined;
        public PipelineStageFlags LastStageMask = PipelineStageFlags.TopOfPipeBit;
        public AccessFlags LastAccessMask;
    }

    internal const AccessFlags GuestImageWriteAccessMask =
        AccessFlags.ShaderWriteBit |
        AccessFlags.ColorAttachmentWriteBit |
        AccessFlags.TransferWriteBit |
        AccessFlags.MemoryWriteBit;

    /// <summary>
    /// Plans the barrier that moves a guest image from its tracked layout to
    /// the layout a consumer declares, and advances the tracked state. Returns
    /// false (no barrier) only when the image is already in the requested
    /// layout, nothing wrote to it since the last barrier, the request itself
    /// is read-only, and the last barrier's destination scope already covers
    /// the requesting stages. A same-layout request after a write, or on a
    /// stage the producing barrier did not reach, still emits: eliding those
    /// is exactly what left compute-written images sampling as zero.
    /// </summary>
    internal static bool TryPlanGuestImageTransition(
        GuestImageResource image,
        ImageLayout newLayout,
        PipelineStageFlags dstStageMask,
        AccessFlags dstAccessMask,
        out ImageMemoryBarrier barrier,
        out PipelineStageFlags srcStageMask)
    {
        if (image.CurrentLayout == newLayout &&
            (image.LastAccessMask & GuestImageWriteAccessMask) == 0 &&
            (dstAccessMask & GuestImageWriteAccessMask) == 0 &&
            (image.LastStageMask & dstStageMask) == dstStageMask)
        {
            image.LastAccessMask |= dstAccessMask;
            barrier = default;
            srcStageMask = default;
            return false;
        }

        srcStageMask = image.LastStageMask;
        barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = image.LastAccessMask,
            DstAccessMask = dstAccessMask,
            OldLayout = image.CurrentLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image.Image,
            // Always the full mip chain: one tracked layout must describe the
            // whole image, or transitioned and untouched mips would diverge.
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = image.IsDepth
                    ? ImageAspectFlags.DepthBit
                    : ImageAspectFlags.ColorBit,
                LevelCount = Math.Max(image.MipLevels, 1),
                LayerCount = 1,
            },
        };
        image.CurrentLayout = newLayout;
        image.LastStageMask = dstStageMask;
        image.LastAccessMask = dstAccessMask;
        return true;
    }

    /// <summary>
    /// Resets tracking for an image whose recorded transitions were abandoned
    /// before submission. The next barrier then discards contents (Undefined)
    /// and synchronizes against everything, so a half-recorded producer can
    /// never leave the tracked layout ahead of the actual one.
    /// </summary>
    internal static void InvalidateGuestImageLayout(GuestImageResource image)
    {
        image.CurrentLayout = ImageLayout.Undefined;
        image.LastStageMask = PipelineStageFlags.AllCommandsBit;
        image.LastAccessMask = AccessFlags.MemoryWriteBit;
    }

    internal readonly record struct GuestSurfaceKey(ulong Address, Format Format);

    /// <summary>
    /// The guest render-image cache. Games reuse a single guest address for
    /// surfaces of different formats within one frame, so every live surface
    /// is keyed by (address, format): rendering a new format at an address
    /// must not destroy the earlier surface a later pass still samples. A
    /// per-address primary index tracks the most-recently-rendered surface,
    /// which is what address-only flips and presents resolve to. The cache
    /// never owns Vulkan objects; callers destroy a surface before removing
    /// it. Render-thread state, no locking.
    /// </summary>
    internal sealed class GuestSurfaceCache
    {
        private readonly Dictionary<GuestSurfaceKey, GuestImageResource> _surfaces = new();
        private readonly Dictionary<ulong, GuestImageResource> _primaries = new();

        public int Count => _surfaces.Count;

        public IEnumerable<GuestImageResource> Surfaces => _surfaces.Values;

        public bool ContainsAddress(ulong address) => _primaries.ContainsKey(address);

        public bool TryGetExact(ulong address, Format format, out GuestImageResource surface) =>
            _surfaces.TryGetValue(new GuestSurfaceKey(address, format), out surface!);

        public void Add(GuestImageResource surface)
        {
            _surfaces.Add(new GuestSurfaceKey(surface.Address, surface.Format), surface);
            _primaries[surface.Address] = surface;
        }

        public void Promote(GuestImageResource surface)
        {
            _primaries[surface.Address] = surface;
        }

        /// <summary>
        /// Removes the surface (already destroyed by the caller); returns
        /// true when no surface remains at its address, i.e. address-level
        /// provenance should be forgotten too.
        /// </summary>
        public bool Remove(GuestImageResource surface)
        {
            _surfaces.Remove(new GuestSurfaceKey(surface.Address, surface.Format));
            if (_primaries.TryGetValue(surface.Address, out var primary) &&
                ReferenceEquals(primary, surface))
            {
                if (FindMostRecentlyUsed(surface.Address) is { } replacement)
                {
                    _primaries[surface.Address] = replacement;
                }
                else
                {
                    _primaries.Remove(surface.Address);
                }
            }

            return !_primaries.ContainsKey(surface.Address);
        }

        public void Clear()
        {
            _surfaces.Clear();
            _primaries.Clear();
        }

        public bool HasInitializedSurface(ulong address)
        {
            foreach (var surface in _surfaces.Values)
            {
                if (surface.Address == address && surface.Initialized)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Sample-path selection: the exact-format surface wins, so a pass
        /// sampling format F at an address binds the surface actually
        /// rendered as F there, never a different-format sibling. Otherwise
        /// the most recently used view-compatible sibling (same Vulkan texel
        /// class, so a mutable-format view reinterprets it) is chosen.
        /// </summary>
        public bool TryFindSampleAlias(
            VulkanGuestDrawTexture texture,
            Format viewFormat,
            out GuestImageResource surface)
        {
            if (TryGetExact(texture.Address, viewFormat, out surface!) &&
                IsCompatibleGuestImageAlias(texture, surface))
            {
                return true;
            }

            GuestImageResource? best = null;
            foreach (var candidate in _surfaces.Values)
            {
                if (candidate.Address != texture.Address ||
                    !IsCompatibleViewFormat(candidate.Format, viewFormat) ||
                    !IsCompatibleGuestImageAlias(texture, candidate))
                {
                    continue;
                }

                // Rank by last-completed content write first: a target switch
                // re-binds a same-address variant before its draw resolves, so a
                // freshly bound but stale image must not outrank the sibling that
                // actually holds the current contents. Recency breaks ties.
                if (best is null ||
                    candidate.ContentGeneration > best.ContentGeneration ||
                    (candidate.ContentGeneration == best.ContentGeneration &&
                     candidate.LastUseStamp > best.LastUseStamp))
                {
                    best = candidate;
                }
            }

            surface = best!;
            return best is not null;
        }

        /// <summary>
        /// Depth surfaces are keyed the same as color, but must never be
        /// aliased through a color view. A pass sampling a guest address that
        /// holds a rendered depth surface resolves here to bind the surface's
        /// depth-aspect view directly and read real depth in .r.
        /// </summary>
        public bool TryFindDepthAlias(ulong address, out GuestImageResource surface)
        {
            GuestImageResource? best = null;
            foreach (var candidate in _surfaces.Values)
            {
                if (candidate.Address != address || !candidate.IsDepth)
                {
                    continue;
                }

                if (best is null || candidate.LastUseStamp > best.LastUseStamp)
                {
                    best = candidate;
                }
            }

            surface = best!;
            return best is not null;
        }

        /// <summary>
        /// Native-format fallback: no strictly view-compatible surface was
        /// bound, but a rendered sibling still holds real content while the
        /// guest memory behind the address was never GPU-written and reads back
        /// black. Ranking, highest priority first: GPU-rendered over CPU upload;
        /// then how well the surface serves the sampler's requested format
        /// (exact, then same texel class, then unrelated); then last-completed
        /// content write; then recency. Honoring the requested format here keeps
        /// a stale empty variant -- whose content-write generation was bumped
        /// merely by being sampled -- from outranking the producer that actually
        /// holds the requested format's contents (Astro Bot's present sampling a
        /// R16G16B16A16Sfloat scene at an address it also touched as R8G8Unorm).
        /// </summary>
        public bool TryFindNativeAlias(
            VulkanGuestDrawTexture texture,
            Format viewFormat,
            out GuestImageResource surface)
        {
            GuestImageResource? best = null;
            foreach (var candidate in _surfaces.Values)
            {
                if (candidate.Address != texture.Address ||
                    !IsCompatibleGuestImageAlias(texture, candidate))
                {
                    continue;
                }

                if (best is null || IsBetterNativeAlias(candidate, best, viewFormat))
                {
                    best = candidate;
                }
            }

            surface = best!;
            return best is not null;
        }

        private static bool IsBetterNativeAlias(
            GuestImageResource candidate,
            GuestImageResource best,
            Format viewFormat)
        {
            // A GPU producer outranks a CPU upload unconditionally: the guest
            // bytes behind a repurposed render-target address read back black.
            if (best.IsCpuBacked != candidate.IsCpuBacked)
            {
                return best.IsCpuBacked;
            }

            // The requested format is the primary key among peers: an exact or
            // class-compatible surface wins over an unrelated sibling regardless
            // of content-generation/recency.
            var candidateFit = NativeAliasFormatFit(candidate.Format, viewFormat);
            var bestFit = NativeAliasFormatFit(best.Format, viewFormat);
            if (candidateFit != bestFit)
            {
                return candidateFit > bestFit;
            }

            // Same format fitness: the freshest completed content write wins,
            // recency breaks remaining ties (preserves d77baeb's ranking).
            if (candidate.ContentGeneration != best.ContentGeneration)
            {
                return candidate.ContentGeneration > best.ContentGeneration;
            }

            return candidate.LastUseStamp > best.LastUseStamp;
        }

        // 2 = exact requested format, 1 = same texel class (a mutable-format
        // view reinterprets it), 0 = unrelated / no request expressed.
        private static int NativeAliasFormatFit(Format surfaceFormat, Format viewFormat)
        {
            if (viewFormat == Format.Undefined)
            {
                return 0;
            }

            if (surfaceFormat == viewFormat)
            {
                return 2;
            }

            return IsCompatibleViewFormat(surfaceFormat, viewFormat) ? 1 : 0;
        }

        /// <summary>
        /// Present-path selection for an address-only flip: the primary
        /// (most-recently-rendered) surface, else the largest initialized
        /// sibling.
        /// </summary>
        public bool TryFindPresentable(ulong address, out GuestImageResource surface)
        {
            if (_primaries.TryGetValue(address, out surface!) && surface.Initialized)
            {
                return true;
            }

            GuestImageResource? best = null;
            foreach (var candidate in _surfaces.Values)
            {
                if (candidate.Address != address || !candidate.Initialized)
                {
                    continue;
                }

                if (best is null ||
                    Area(candidate) > Area(best) ||
                    (Area(candidate) == Area(best) &&
                     candidate.LastUseStamp > best.LastUseStamp))
                {
                    best = candidate;
                }
            }

            surface = best!;
            return best is not null;
        }

        private GuestImageResource? FindMostRecentlyUsed(ulong address)
        {
            GuestImageResource? best = null;
            foreach (var candidate in _surfaces.Values)
            {
                if (candidate.Address == address &&
                    (best is null || candidate.LastUseStamp > best.LastUseStamp))
                {
                    best = candidate;
                }
            }

            return best;
        }

        private static ulong Area(GuestImageResource surface) =>
            (ulong)surface.Width * surface.Height;

        private static bool IsCompatibleGuestImageAlias(
            VulkanGuestDrawTexture texture,
            GuestImageResource guestImage)
        {
            if (guestImage.Width == texture.Width &&
                guestImage.Height == texture.Height)
            {
                return true;
            }

            if (texture.TileMode == 0 ||
                texture.Width == 0 ||
                texture.Height == 0)
            {
                return false;
            }

            // Tiled render targets are rebound through descriptors whose logical
            // extent differs from the current dynamic-resolution surface. Vulkan
            // samples normalized coordinates from whichever variant is bound, so
            // either direction is usable; content-generation ranking (not the
            // extent) decides which cached variant holds the live contents.
            return true;
        }
    }

    private sealed class Presenter : IDisposable
    {
        private const string FullscreenBarycentricVertexSpirv =
            "AwIjBwAAAQALAAgAMgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ACAAAAAAABAAAAG1haW4AAAAADQAAABoAAAApAAAAAwADAAIAAADCAQAABQAEAAQAAABtYWluAAAAAAUABgALAAAAZ2xfUGVyVmVydGV4AAAAAAYABgALAAAAAAAAAGdsX1Bvc2l0aW9uAAYABwALAAAAAQAAAGdsX1BvaW50U2l6ZQAAAAAGAAcACwAAAAIAAABnbF9DbGlwRGlzdGFuY2UABgAHAAsAAAADAAAAZ2xfQ3VsbERpc3RhbmNlAAUAAwANAAAAAAAAAAUABgAaAAAAZ2xfVmVydGV4SW5kZXgAAAUABQAdAAAAaW5kZXhhYmxlAAAABQAFACkAAABiYXJ5Y2VudHJpYwAFAAUALwAAAGluZGV4YWJsZQAAAEcAAwALAAAAAgAAAEgABQALAAAAAAAAAAsAAAAAAAAASAAFAAsAAAABAAAACwAAAAEAAABIAAUACwAAAAIAAAALAAAAAwAAAEgABQALAAAAAwAAAAsAAAAEAAAARwAEABoAAAALAAAAKgAAAEcABAApAAAAHgAAAAAAAAATAAIAAgAAACEAAwADAAAAAgAAABYAAwAGAAAAIAAAABcABAAHAAAABgAAAAQAAAAVAAQACAAAACAAAAAAAAAAKwAEAAgAAAAJAAAAAQAAABwABAAKAAAABgAAAAkAAAAeAAYACwAAAAcAAAAGAAAACgAAAAoAAAAgAAQADAAAAAMAAAALAAAAOwAEAAwAAAANAAAAAwAAABUABAAOAAAAIAAAAAEAAAArAAQADgAAAA8AAAAAAAAAFwAEABAAAAAGAAAAAgAAACsABAAIAAAAEQAAAAMAAAAcAAQAEgAAABAAAAARAAAAKwAEAAYAAAATAAAAAACAvywABQAQAAAAFAAAABMAAAATAAAAKwAEAAYAAAAVAAAAAABAQCwABQAQAAAAFgAAABUAAAATAAAALAAFABAAAAAXAAAAEwAAABUAAAAsAAYAEgAAABgAAAAUAAAAFgAAABcAAAAgAAQAGQAAAAEAAAAOAAAAOwAEABkAAAAaAAAAAQAAACAABAAcAAAABwAAABIAAAAgAAQAHgAAAAcAAAAQAAAAKwAEAAYAAAAhAAAAAAAAACsABAAGAAAAIgAAAAAAgD8gAAQAJgAAAAMAAAAHAAAAIAAEACgAAAADAAAAEAAAADsABAAoAAAAKQAAAAMAAAAsAAUAEAAAACoAAAAiAAAAIQAAACwABQAQAAAAKwAAACEAAAAiAAAALAAFABAAAAAsAAAAIQAAACEAAAAsAAYAEgAAAC0AAAAqAAAAKwAAACwAAAA2AAUAAgAAAAQAAAAAAAAAAwAAAPgAAgAFAAAAOwAEABwAAAAdAAAABwAAADsABAAcAAAALwAAAAcAAAA9AAQADgAAABsAAAAaAAAAPgADAB0AAAAYAAAAQQAFAB4AAAAfAAAAHQAAABsAAAA9AAQAEAAAACAAAAAfAAAAUQAFAAYAAAAjAAAAIAAAAAAAAABRAAUABgAAACQAAAAgAAAAAQAAAFAABwAHAAAAJQAAACMAAAAkAAAAIQAAACIAAABBAAUAJgAAACcAAAANAAAADwAAAD4AAwAnAAAAJQAAAD0ABAAOAAAALgAAABoAAAA+AAMALwAAAC0AAABBAAUAHgAAADAAAAAvAAAALgAAAD0ABAAQAAAAMQAAADAAAAA+AAMAKQAAADEAAAD9AAEAOAABAA==";

        private const string FullscreenBarycentricFragmentSpirv =
            "AwIjBwAAAQALAAgAEgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ABwAEAAAABAAAAG1haW4AAAAACQAAAAwAAAAQAAMABAAAAAcAAAADAAMAAgAAAMIBAAAFAAQABAAAAG1haW4AAAAABQAFAAkAAABvdXRDb2xvcgAAAAAFAAUADAAAAGJhcnljZW50cmljAEcABAAJAAAAHgAAAAAAAABHAAQADAAAAB4AAAAAAAAAEwACAAIAAAAhAAMAAwAAAAIAAAAWAAMABgAAACAAAAAXAAQABwAAAAYAAAAEAAAAIAAEAAgAAAADAAAABwAAADsABAAIAAAACQAAAAMAAAAXAAQACgAAAAYAAAACAAAAIAAEAAsAAAABAAAACgAAADsABAALAAAADAAAAAEAAAArAAQABgAAAA4AAAAAAAAANgAFAAIAAAAEAAAAAAAAAAMAAAD4AAIABQAAAD0ABAAKAAAADQAAAAwAAABRAAUABgAAAA8AAAANAAAAAAAAAFEABQAGAAAAEAAAAA0AAAABAAAAUAAHAAcAAAARAAAADwAAABAAAAAOAAAADgAAAD4AAwAJAAAAEQAAAP0AAQA4AAEA";

        private readonly IWindow _window;
        private const int MaxInFlightGuestSubmissions = 8;
        private const double PerformanceHudSampleSeconds = 0.5;
        private const uint ThreadQueryLimitedInformation = 0x0800;
        private static readonly bool _performanceHudEnabled =
            !string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_PERF_HUD"),
                "0",
                StringComparison.Ordinal);
        private Vk _vk = null!;
        private KhrSurface _surfaceApi = null!;
        private KhrSwapchain _swapchainApi = null!;
        private delegate* unmanaged<Device, DebugUtilsObjectNameInfoEXT*, Result> _setDebugUtilsObjectName;
        private delegate* unmanaged<CommandBuffer, DebugUtilsLabelEXT*, void> _cmdBeginDebugUtilsLabel;
        private delegate* unmanaged<CommandBuffer, void> _cmdEndDebugUtilsLabel;
        private Instance _instance;
        private SurfaceKHR _surface;
        private DebugUtilsMessengerEXT _debugMessenger;
        private ExtDebugUtils? _debugUtils;
        private PhysicalDevice _physicalDevice;
        private bool _supportsIndependentBlend;
        private uint _maxColorAttachments;
        private Device _device;
        private PipelineCache _pipelineCache;
        private string? _pipelineCachePath;
        private Queue _queue;
        private uint _queueFamilyIndex;
        private SwapchainKHR _swapchain;
        private Image[] _swapchainImages = [];
        private ImageView[] _swapchainImageViews = [];
        private Framebuffer[] _framebuffers = [];
        private bool[] _imageInitialized = [];
        private Format _swapchainFormat;
        private Extent2D _extent;
        private RenderPass _renderPass;
        private PipelineLayout _pipelineLayout;
        private Pipeline _barycentricPipeline;
        private CommandPool _commandPool;
        private CommandBuffer _commandBuffer;
        private CommandBuffer _presentationCommandBuffer;
        private VkSemaphore _imageAvailable;
        private VkSemaphore _renderFinished;
        private Fence _presentationFence;
        private bool _presentationInFlight;
        private TranslatedDrawResources? _pendingPresentationResources;
        private VkBuffer _stagingBuffer;
        private DeviceMemory _stagingMemory;
        private ulong _stagingSize;
        private long _presentedSequence;
        private long _performanceHudLastTimestamp;
        private TimeSpan _performanceHudLastProcessCpu;
        private long _performanceHudPresentedFrames;
        private long _performanceHudLastPresentedFrames;
        private long _performanceHudLastReadCount;
        private long _performanceHudLastReadBytes;
        private long _performanceHudLastReadHits;
        private long _performanceHudLastReadPvmBytes;
        private long _performanceHudLastReadLibcBytes;
        private readonly Dictionary<int, TimeSpan> _performanceHudThreadCpu = [];
        private readonly Dictionary<int, string> _performanceHudThreadNames = [];
        private bool _vulkanReady;
        private bool _firstFramePresented;
        private bool _firstGuestDrawPresented;
        private bool _splashPresented;
        private bool _swapchainRecreateDeferred;
        private bool _tracedPresentedSwapchain;
        private bool _tracedNativeIndirectDraw;
        private int _swapchainCaptureCount;
        private long _totalPresentCount;
        // Fire a RenderDoc capture at the first present after this many seconds.
        // Time-based (not present-count based) because the guest flips rarely, so
        // a fixed frame index would never be reached; a delay reliably lands on a
        // present after the menu has had time to render.
        private static readonly double RenderDocCaptureDelaySeconds =
            double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_RENDERDOC_DELAY"), out var d) && d > 0
                ? d
                : 60;
        private static readonly System.Diagnostics.Stopwatch RenderDocClock = new();
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

        // SHARPEMU_FORCE_EXPOSURE=<value>: the game's auto-exposure luminance
        // (a 1x1 float surface) is never written by any pass we execute, so it
        // reads zero and the tonemap multiplies the (fully rendered) HDR scene
        // to black. When set, bind a constant into any tiny (<=2x2) sampled
        // float texture so the tonemap gets a sane exposure and the scene shows.
        // Value "1" (or non-numeric) defaults to 0.25; otherwise the parsed float.
        private static readonly float? ForcedExposureValue = ParseForcedExposure();
        private GuestImageResource? _forcedExposureImage;

        private static float? ParseForcedExposure()
        {
            var raw = Environment.GetEnvironmentVariable("SHARPEMU_FORCE_EXPOSURE");
            if (string.IsNullOrEmpty(raw))
            {
                return null;
            }

            return float.TryParse(
                raw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) && value != 0f
                ? value
                : 0.25f;
        }

        private int _frameDumpBudget = 24;
        private bool _swapchainReadbackPending;
        private bool _deviceLost;
        private bool _deviceLostLogged;
        private int _directPresentationCount;
        private readonly GuestSurfaceCache _guestImages = new();
        private readonly VulkanDeviceMemoryLedger _deviceMemoryLedger = new();
        private PhysicalDeviceMemoryProperties _memoryProperties;
        private int _deviceLocalHeapIndex = -1;
        private int _hostVisibleHeapIndex = -1;
        private bool _supportsMemoryBudget;
        private ulong _useStamp;
        private long _guestImageContentGeneration;
        // Monotonic id assigned to each offscreen draw when SHARPEMU_CAPTURE_DRAWS
        // is set, so a capture.draw line can be pinned to a specific draw in
        // execution order on the single render thread.
        private long _captureDrawSeq;
        private bool _reclaimInProgress;
        private bool _memoryPanic;
        private readonly HashSet<(ulong Address, uint Width, uint Height, Format Format)> _tracedTextureCacheHits = new();
        private static readonly bool _dumpTextures = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_DUMP_TEXTURES"),
            "1",
            StringComparison.Ordinal);
        private readonly HashSet<(ulong Address, uint Width, uint Height, Format Format)> _tracedTextureUploads = new();
        private readonly HashSet<ulong> _tracedUploadFallthrough = new();
        private readonly HashSet<(ulong Address, Format Format)> _tracedCopyOnSample = new();
        private readonly HashSet<(ulong Address, uint Width, uint Height, uint Format)> _dumpedTextures = new();
        private readonly HashSet<(ulong Address, int Size)> _tracedGlobalBuffers = new();
        private readonly HashSet<(ulong Address, Format Format)> _tracedGuestImageContents = new();
        private readonly Dictionary<ulong, int> _tracedGuestWriteCounts = new();
        private int _tracedVertexBufferCount;
        private readonly Dictionary<byte[], Pipeline> _computePipelines =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<GraphicsPipelineKey, Pipeline> _graphicsPipelines = new();
        private readonly Dictionary<VulkanGuestSampler, Sampler> _samplers = new();
        private readonly Dictionary<byte[], string> _shaderDigests =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<DescriptorLayoutKey, DescriptorLayoutBundle>
            _descriptorLayouts = new();
        private readonly Dictionary<HostBufferPoolKey, Stack<HostBufferAllocation>>
            _hostBufferPool = new();
        private readonly Dictionary<ulong, HostBufferAllocation> _hostBufferAllocations = new();
        private readonly object _hostBufferPoolGate = new();
        private ulong _pooledHostBufferBytes;

        // Idle (recycled, not in flight) host-visible bytes to retain. Beyond
        // this the buffer is destroyed instead of pooled. In-flight buffers are
        // additionally bounded by MaxInFlightGuestSubmissions.
        private const ulong MaxPooledHostBufferBytes = 768UL * 1024 * 1024;
        private readonly Queue<PendingGuestSubmission> _pendingGuestSubmissions = new();

        // Ordered-flip capture (SHARPEMU_ORDERED_FLIP). Render-thread-owned:
        // ExecuteOrderedGuestFlip and the present path both run on the render
        // thread, so these need no lock. A snapshot is owned by exactly ONE of
        // _guestImageVersions (captured, awaiting present) or
        // _retiringGuestFlipSnapshots (presented or abandoned, awaiting a queue
        // drain before it is freed) -- never both, so nothing double-frees.
        // Snapshots are ~33MB at 4K, so MaxPendingGuestFlipVersions bounds the
        // dict and CollectAbandonedGuestImageVersions sheds the surplus.
        private const int MaxPendingGuestFlipVersions = 3;
        private readonly Dictionary<long, GuestImageResource> _guestImageVersions = new();
        private readonly HashSet<long> _capturedGuestFlipVersions = new();
        private readonly List<GuestImageResource> _retiringGuestFlipSnapshots = new();
        private long _guestFlipCaptureTraceCount;

        private readonly record struct GraphicsPipelineKey(
            string VertexShader,
            string FragmentShader,
            string RenderTargetLayout,
            PrimitiveTopology Topology,
            string BlendLayout,
            string ResourceLayout,
            string VertexLayout);

        private readonly record struct HostBufferPoolKey(
            BufferUsageFlags Usage,
            ulong Capacity);

        private readonly record struct DescriptorLayoutKey(
            ShaderStageFlags Stages,
            string Resources);

        private sealed record DescriptorLayoutBundle(
            DescriptorSetLayout DescriptorSetLayout,
            PipelineLayout PipelineLayout);

        private sealed record HostBufferAllocation(
            VkBuffer Buffer,
            DeviceMemory Memory,
            HostBufferPoolKey Key);

        private sealed class TranslatedDrawResources
        {
            public string DebugName = "SharpEmu translated";
            public PipelineLayout PipelineLayout;
            public Pipeline Pipeline;
            public bool PipelineCached;
            public bool DescriptorLayoutCached;
            public DescriptorSetLayout DescriptorSetLayout;
            public DescriptorPool DescriptorPool;
            public DescriptorSet DescriptorSet;
            public TextureResource[] Textures = [];
            public GlobalBufferResource[] GlobalMemoryBuffers = [];
            public VertexBufferResource[] VertexBuffers = [];
            public VkBuffer IndexBuffer;
            public DeviceMemory IndexMemory;
            public bool Index32Bit;
            public VkBuffer IndirectArgsBuffer;
            public DeviceMemory IndirectArgsMemory;
            public ulong IndirectArgsOffset;
            public uint IndirectArgsStride;
            public bool HasIndirectArgs;
            public uint VertexCount = 3;
            public uint InstanceCount = 1;
            public PrimitiveTopology Topology = PrimitiveTopology.TriangleList;
            public VulkanGuestBlendState[] Blends = [VulkanGuestBlendState.Default];
            public VulkanGuestRect? Scissor;
            public VulkanGuestViewport? Viewport;
            public RenderPass TransientRenderPass;
            public Framebuffer TransientFramebuffer;
            // NGG position-capture prepass. When CaptureInvocationCount > 0 the
            // recorder dispatches CaptureCompute (the export-as-compute kernel)
            // to fill CaptureOutputBuffer with one clip-space vec4 per vertex,
            // barriers it compute-write -> vertex-read, then draws it as this
            // draw's location-0 vertex stream. CaptureCompute owns the compute
            // pipeline/descriptor pool and every capture buffer (inputs + the
            // device-local output), so it is destroyed after the fence with the
            // graphics resources; CaptureOutputBuffer here is a non-owning handle
            // used only to build the pipeline barrier.
            public TranslatedDrawResources? CaptureCompute;
            public uint CaptureInvocationCount;
            public VkBuffer CaptureOutputBuffer;
            // Diagnostic (SHARPEMU_NGG_READBACK=1): host-visible copy of the
            // capture output, logged at destroy time (after the fence) to see
            // what positions the compute actually wrote.
            public VkBuffer CaptureReadbackBuffer;
            public DeviceMemory CaptureReadbackMemory;
            public ulong CaptureReadbackSize;
            // Diagnostic (NGG debug): host-visible copy of the render TARGET taken
            // in the same command buffer immediately after the capture draw, so a
            // later pass overwriting the target can't mask whether the geometry
            // actually rasterized this frame.
            public VkBuffer CaptureTargetReadbackBuffer;
            public DeviceMemory CaptureTargetReadbackMemory;
            public ulong CaptureTargetReadbackSize;
            public uint CaptureTargetBpp;
            public ulong CaptureTargetAddress;
            public uint CaptureTargetWidth;
            public uint CaptureTargetHeight;
        }

        private sealed class TextureResource
        {
            public ulong Address;
            public VkBuffer StagingBuffer;
            public DeviceMemory StagingMemory;
            public Image Image;
            public DeviceMemory ImageMemory;
            public ImageView View;
            public uint Width;
            public uint Height;
            public uint RowLength;
            public uint DstSelect;
            public bool NeedsUpload;
            public bool OwnsStorage;
            public bool IsStorage;
            public VulkanGuestSampler SamplerState;
            public Sampler Sampler;
            public GuestImageResource? GuestImage;
            public ulong CpuContentFingerprint;
            public bool UpdatesCpuContent;
            // When set, this texture's content is produced on the GPU by an
            // earlier pass (a compute ImageStore / render target) at the same
            // guest address that could not be view-aliased into the sampled
            // format. Instead of uploading empty guest memory, the recorder
            // blits/copies from this producer image into our own image, so the
            // sampler reads real GPU content (Kyty-style copy-on-sample bridge).
            public GuestImageResource? CopySource;
            // The Vulkan format of Image; only populated for copy-on-sample
            // resources, where the recorder chooses vkCmdCopyImage vs
            // vkCmdBlitImage by comparing it against the producer's format.
            public Format Format;
        }

        private sealed class GlobalBufferResource
        {
            public VkBuffer Buffer;
            public DeviceMemory Memory;
            public ulong Size;
        }

        private sealed class VertexBufferResource
        {
            public VkBuffer Buffer;
            public DeviceMemory Memory;
            public ulong Size;
            public uint Location;
            public uint ComponentCount;
            public uint DataFormat;
            public uint NumberFormat;
            public uint Stride;
            public uint OffsetBytes;
            // Set when Buffer is owned by another resource (the NGG capture
            // output buffer, also held by CaptureCompute). The destroy path must
            // not recycle/free it a second time through the vertex-buffer loop.
            public bool External;
        }

        private sealed record PendingGuestSubmission(
            Fence Fence,
            CommandBuffer CommandBuffer,
            TranslatedDrawResources Resources,
            IReadOnlyList<GuestImageResource> TraceImages,
            string DebugName);

        public Presenter(uint width, uint height)
        {
            var options = WindowOptions.DefaultVulkan;
            options.Size = new Vector2D<int>((int)DefaultWindowWidth, (int)DefaultWindowHeight);
            options.Title = VideoOutExports.GetWindowTitle();
            options.WindowBorder = WindowBorder.Fixed;
            // FIFO already provides the presentation clock. Throttling Silk's render loop
            // as well can miss alternating vblanks and collapse delivery to 30 FPS or less.
            options.VSync = false;
            options.FramesPerSecond = 0;
            options.UpdatesPerSecond = 0;
            _window = Window.Create(options);
            _window.Load += Initialize;
            _window.Render += Render;
            _window.Closing += OnWindowClosing;
        }

        private void OnWindowClosing()
        {
            VideoOutExports.NotifyPresentationWindowClosed();
            DisposeVulkan();
        }

        public void Run() => _window.Run();

        public void Dispose()
        {
            DisposeVulkan();
            try
            {
                _window.Dispose();
            }
            catch (InvalidOperationException exception)
                when (exception.Message.Contains("render loop", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan VideoOut window dispose skipped during render loop: {exception.Message}");
            }
        }

        private void Initialize()
        {
            if (PngSplashLoader.TryLoadIcon(out var iconPixels, out var iconWidth, out var iconHeight))
            {
                var icon = new RawImage((int)iconWidth, (int)iconHeight, iconPixels);
                _window.SetWindowIcon(ref icon);
            }

            WaitForRenderDocAttachIfRequested();
            _vk = Vk.GetApi();
            CreateInstance();
            TraceInitStep("instance");
            CreateSurface();
            TraceInitStep("surface");
            SelectPhysicalDevice();
            CreateDevice();
            TraceInitStep("device");
            CreatePipelineCache();
            CreateSwapchain();
            TraceInitStep("swapchain");
            CreateCommandResources();
            TraceInitStep("command-resources");
            CreateGuestDrawResources();
            TraceInitStep("guest-draw-resources");
            _vulkanReady = true;
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan VideoOut ready: {_extent.Width}x{_extent.Height}, format={_swapchainFormat}");
        }

        private static void TraceInitStep(string step) =>
            Console.Error.WriteLine($"[LOADER][INFO] Vulkan VideoOut init: {step} ok");

        private static void WaitForRenderDocAttachIfRequested()
        {
            var value = Environment.GetEnvironmentVariable("SHARPEMU_RENDERDOC_WAIT");
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (string.Equals(value, "enter", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Waiting for RenderDoc attach before Vulkan init. pid={Environment.ProcessId}. Press Enter to continue.");
                _ = Console.ReadLine();
                return;
            }

            var seconds = 15;
            if (int.TryParse(value, out var parsedSeconds))
            {
                seconds = Math.Clamp(parsedSeconds, 1, 300);
            }

            Console.Error.WriteLine(
                $"[LOADER][INFO] Waiting {seconds}s for RenderDoc attach before Vulkan init. pid={Environment.ProcessId}");
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
        }

        private bool IsInstanceExtensionAvailable(string extensionName)
        {
            uint extensionCount = 0;
            if (_vk.EnumerateInstanceExtensionProperties((byte*)null, &extensionCount, null) != Result.Success ||
                extensionCount == 0)
            {
                return false;
            }

            var properties = new ExtensionProperties[extensionCount];
            fixed (ExtensionProperties* propertyPointer = properties)
            {
                if (_vk.EnumerateInstanceExtensionProperties(
                        (byte*)null,
                        &extensionCount,
                        propertyPointer) != Result.Success)
                {
                    return false;
                }

                var expected = Encoding.UTF8.GetBytes(extensionName);
                for (var index = 0; index < extensionCount; index++)
                {
                    if (Utf8NullTerminatedEquals(propertyPointer[index].ExtensionName, expected))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool Utf8NullTerminatedEquals(byte* actual, ReadOnlySpan<byte> expected)
        {
            for (var index = 0; index < expected.Length; index++)
            {
                if (actual[index] != expected[index])
                {
                    return false;
                }
            }

            return actual[expected.Length] == 0;
        }

        private void LoadDebugUtilsCommands()
        {
            var setObjectName = _vk.GetDeviceProcAddr(_device, "vkSetDebugUtilsObjectNameEXT");
            var beginLabel = _vk.GetDeviceProcAddr(_device, "vkCmdBeginDebugUtilsLabelEXT");
            var endLabel = _vk.GetDeviceProcAddr(_device, "vkCmdEndDebugUtilsLabelEXT");
            _setDebugUtilsObjectName =
                (delegate* unmanaged<Device, DebugUtilsObjectNameInfoEXT*, Result>)
                setObjectName.Handle;
            _cmdBeginDebugUtilsLabel =
                (delegate* unmanaged<CommandBuffer, DebugUtilsLabelEXT*, void>)
                beginLabel.Handle;
            _cmdEndDebugUtilsLabel =
                (delegate* unmanaged<CommandBuffer, void>)
                endLabel.Handle;

            if (_setDebugUtilsObjectName is not null)
            {
                Console.Error.WriteLine("[LOADER][INFO] Vulkan debug labels enabled.");
            }
        }

        private void SetDebugName(ObjectType objectType, ulong objectHandle, string name)
        {
            if (_setDebugUtilsObjectName is null ||
                _device.Handle == 0 ||
                objectHandle == 0)
            {
                return;
            }

            var bytes = NullTerminatedUtf8(name);
            fixed (byte* namePointer = bytes)
            {
                var info = new DebugUtilsObjectNameInfoEXT
                {
                    SType = StructureType.DebugUtilsObjectNameInfoExt,
                    ObjectType = objectType,
                    ObjectHandle = objectHandle,
                    PObjectName = namePointer,
                };
                _ = _setDebugUtilsObjectName(_device, &info);
            }
        }

        private void BeginDebugLabel(CommandBuffer commandBuffer, string name)
        {
            if (_cmdBeginDebugUtilsLabel is null ||
                commandBuffer.Handle == 0)
            {
                return;
            }

            var bytes = NullTerminatedUtf8(name);
            fixed (byte* namePointer = bytes)
            {
                var label = new DebugUtilsLabelEXT
                {
                    SType = StructureType.DebugUtilsLabelExt,
                    PLabelName = namePointer,
                };
                label.Color[0] = 0.20f;
                label.Color[1] = 0.60f;
                label.Color[2] = 1.00f;
                label.Color[3] = 1.00f;
                _cmdBeginDebugUtilsLabel(commandBuffer, &label);
            }
        }

        private void EndDebugLabel(CommandBuffer commandBuffer)
        {
            if (_cmdEndDebugUtilsLabel is not null &&
                commandBuffer.Handle != 0)
            {
                _cmdEndDebugUtilsLabel(commandBuffer);
            }
        }

        private static byte[] NullTerminatedUtf8(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Array.Resize(ref bytes, bytes.Length + 1);
            return bytes;
        }

        private static string BuildComputeDebugName(VulkanComputeGuestDispatch dispatch)
        {
            var storage = dispatch.Textures.FirstOrDefault(texture => texture.IsStorage && texture.Address != 0);
            return storage is null
                ? $"SharpEmu compute cs=0x{dispatch.ShaderAddress:X16} " +
                  $"{dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ}"
                : $"SharpEmu compute cs=0x{dispatch.ShaderAddress:X16} " +
                  $"storage=0x{storage.Address:X16} " +
                  $"{storage.Width}x{storage.Height} fmt{storage.Format} " +
                  $"{dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ}";
        }

        private static string GuestImageDebugName(VulkanGuestRenderTarget target, Format format) =>
            $"SharpEmu guest 0x{target.Address:X16} {target.Width}x{target.Height} " +
            $"fmt{target.Format}/{format}";

        private static string TextureDebugName(VulkanGuestDrawTexture texture, Format format) =>
            $"SharpEmu texture 0x{texture.Address:X16} {texture.Width}x{texture.Height} " +
            $"fmt{texture.Format}/{format}";

        private void CreateInstance()
        {
            var applicationName = (byte*)SilkMarshal.StringToPtr("SharpEmu");
            var enableValidation = Environment.GetEnvironmentVariable("SHARPEMU_VK_VALIDATION") == "1";
            byte* validationLayerName = null;

            try
            {
                var applicationInfo = new ApplicationInfo
                {
                    SType = StructureType.ApplicationInfo,
                    PApplicationName = applicationName,
                    ApplicationVersion = Vk.MakeVersion(0, 0, 1),
                    PEngineName = applicationName,
                    EngineVersion = Vk.MakeVersion(0, 0, 1),
                    ApiVersion = Vk.Version12,
                };

                var extensions = _window.VkSurface!.GetRequiredExtensions(out var extensionCount);
                byte* debugUtilsExtension = null;
                var enabledExtensionCount = (int)extensionCount;
                var enabledExtensions = stackalloc byte*[(int)extensionCount + 1];
                for (var index = 0; index < (int)extensionCount; index++)
                {
                    enabledExtensions[index] = extensions[index];
                }

                if (IsInstanceExtensionAvailable(DebugUtilsExtensionName))
                {
                    debugUtilsExtension = (byte*)SilkMarshal.StringToPtr(DebugUtilsExtensionName);
                    enabledExtensions[enabledExtensionCount++] = debugUtilsExtension;
                }

                if (enableValidation && IsInstanceLayerAvailable("VK_LAYER_KHRONOS_validation"))
                {
                    validationLayerName = (byte*)SilkMarshal.StringToPtr("VK_LAYER_KHRONOS_validation");
                }
                else if (enableValidation)
                {
                    Console.Error.WriteLine("[LOADER][WARN] SHARPEMU_VK_VALIDATION=1 but VK_LAYER_KHRONOS_validation not found (Vulkan SDK installed?).");
                }

                var layers = stackalloc byte*[1];
                if (validationLayerName is not null)
                {
                    layers[0] = validationLayerName;
                }

                var createInfo = new InstanceCreateInfo
                {
                    SType = StructureType.InstanceCreateInfo,
                    PApplicationInfo = &applicationInfo,
                    EnabledExtensionCount = (uint)enabledExtensionCount,
                    PpEnabledExtensionNames = enabledExtensions,
                    EnabledLayerCount = validationLayerName is not null ? 1u : 0u,
                    PpEnabledLayerNames = validationLayerName is not null ? layers : null,
                };

                try
                {
                    Check(_vk.CreateInstance(&createInfo, null, out _instance), "vkCreateInstance");
                    if (!_vk.TryGetInstanceExtension(_instance, out _surfaceApi))
                    {
                        throw new InvalidOperationException("VK_KHR_surface is unavailable.");
                    }

                    if (validationLayerName is not null && _vk.TryGetInstanceExtension(_instance, out ExtDebugUtils debugUtils))
                    {
                        _debugUtils = debugUtils;
                        RegisterDebugMessenger(debugUtils);
                        Console.Error.WriteLine("[LOADER][INFO] Vulkan Validation Layers active (SHARPEMU_VK_VALIDATION=1).");
                    }
                }
                finally
                {
                    if (debugUtilsExtension is not null)
                    {
                        SilkMarshal.Free((nint)debugUtilsExtension);
                    }
                }
            }
            finally
            {
                SilkMarshal.Free((nint)applicationName);
                if (validationLayerName is not null)
                {
                    SilkMarshal.Free((nint)validationLayerName);
                }
            }
        }

        private bool IsInstanceLayerAvailable(string layerName)
        {
            uint layerCount = 0;
            if (_vk.EnumerateInstanceLayerProperties(&layerCount, null) != Result.Success || layerCount == 0)
            {
                return false;
            }

            var properties = new LayerProperties[layerCount];
            fixed (LayerProperties* propertyPointer = properties)
            {
                if (_vk.EnumerateInstanceLayerProperties(&layerCount, propertyPointer) != Result.Success)
                {
                    return false;
                }

                var expected = Encoding.UTF8.GetBytes(layerName);
                for (var index = 0; index < layerCount; index++)
                {
                    if (Utf8NullTerminatedEquals(propertyPointer[index].LayerName, expected))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        private void RegisterDebugMessenger(ExtDebugUtils debugUtils)
        {
            var messengerInfo = new DebugUtilsMessengerCreateInfoEXT
            {
                SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt
                                  | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt,
                MessageType = DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                              | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt
                              | DebugUtilsMessageTypeFlagsEXT.GeneralBitExt,
                PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(DebugCallback),
            };

            Check(debugUtils.CreateDebugUtilsMessenger(_instance, &messengerInfo, null, out _debugMessenger),
                "vkCreateDebugUtilsMessengerEXT");
        }

        private static unsafe uint DebugCallback(
            DebugUtilsMessageSeverityFlagsEXT severity,
            DebugUtilsMessageTypeFlagsEXT type,
            DebugUtilsMessengerCallbackDataEXT* callbackData,
            void* userData)
        {
            var message = SilkMarshal.PtrToString((nint)callbackData->PMessage);
            var prefix = severity switch
            {
                DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt => "[VULKAN][ERROR]",
                DebugUtilsMessageSeverityFlagsEXT.WarningBitExt => "[VULKAN][WARN]",
                _ => "[VULKAN][INFO]",
            };
            Console.Error.WriteLine($"{prefix} {message}");
            return Vk.False;
        }
        private void CreateSurface()
        {
            var instanceHandle = new VkHandle(_instance.Handle);
            var surfaceHandle = _window.VkSurface!.Create<AllocationCallbacks>(instanceHandle, null);
            _surface = new SurfaceKHR(surfaceHandle.Handle);
        }

        private void SelectPhysicalDevice()
        {
            uint deviceCount = 0;
            Check(_vk.EnumeratePhysicalDevices(_instance, &deviceCount, null), "vkEnumeratePhysicalDevices");
            if (deviceCount == 0)
            {
                throw new InvalidOperationException("No Vulkan physical device was found.");
            }

            var devices = new PhysicalDevice[deviceCount];
            fixed (PhysicalDevice* devicePointer = devices)
            {
                Check(_vk.EnumeratePhysicalDevices(_instance, &deviceCount, devicePointer), "vkEnumeratePhysicalDevices");
            }

            // Hybrid laptops enumerate the iGPU first, and AMD's integrated driver
            // segfaults compiling some translated shaders, so rank rather than take
            // the first hit. SHARPEMU_VK_DEVICE=<substring> pins an adapter by name.
            var deviceOverride = Environment.GetEnvironmentVariable("SHARPEMU_VK_DEVICE");
            var bestScore = int.MinValue;
            var found = false;

            foreach (var device in devices)
            {
                uint queueCount = 0;
                _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueCount, null);
                var queues = new QueueFamilyProperties[queueCount];
                fixed (QueueFamilyProperties* queuePointer = queues)
                {
                    _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueCount, queuePointer);
                }

                for (uint index = 0; index < queueCount; index++)
                {
                    var supportsGraphics = (queues[index].QueueFlags & QueueFlags.GraphicsBit) != 0;
                    _surfaceApi.GetPhysicalDeviceSurfaceSupport(device, index, _surface, out var supportsPresent);
                    if (!supportsGraphics || !supportsPresent)
                    {
                        continue;
                    }

                    _vk.GetPhysicalDeviceProperties(device, out var properties);
                    var name = SilkMarshal.PtrToString((nint)properties.DeviceName) ?? string.Empty;
                    var score = ScorePhysicalDevice(properties, name, deviceOverride);
                    Console.Error.WriteLine(
                        $"[LOADER][INFO] Vulkan candidate: {name} ({properties.DeviceType}) score={score}");

                    if (score > bestScore)
                    {
                        bestScore = score;
                        _physicalDevice = device;
                        _queueFamilyIndex = index;
                        found = true;
                    }

                    break;
                }
            }

            if (!found)
            {
                throw new InvalidOperationException("No Vulkan graphics/present queue was found.");
            }

            _vk.GetPhysicalDeviceProperties(_physicalDevice, out var selected);
            _maxColorAttachments = selected.Limits.MaxColorAttachments;
            var selectedName = SilkMarshal.PtrToString((nint)selected.DeviceName) ?? "<unknown>";
            Volatile.Write(ref _selectedDeviceName, selectedName);
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan device: {selectedName} ({selected.DeviceType})");
            VideoOutExports.SetSelectedGpuName(selectedName);
            _window.Title = VideoOutExports.GetWindowTitle();
        }

        private static int ScorePhysicalDevice(
            PhysicalDeviceProperties properties,
            string name,
            string? deviceOverride)
        {
            if (!string.IsNullOrWhiteSpace(deviceOverride))
            {
                return name.Contains(deviceOverride, StringComparison.OrdinalIgnoreCase) ? 1000 : -1000;
            }

            var score = properties.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 300,
                PhysicalDeviceType.VirtualGpu => 100,
                PhysicalDeviceType.Cpu => 50,
                // Last resort: only picked when nothing else can present.
                PhysicalDeviceType.IntegratedGpu => -100,
                _ => 10,
            };

            if (properties.VendorID == NvidiaVendorId)
            {
                score += 500;
            }

            return score;
        }

        private void CreateDevice()
        {
            var priority = 1.0f;
            var queueInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = _queueFamilyIndex,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
            _vk.GetPhysicalDeviceFeatures(_physicalDevice, out var supportedFeatures);
            _supportsIndependentBlend = supportedFeatures.IndependentBlend;
            var enabledFeatures = new PhysicalDeviceFeatures
            {
                IndependentBlend = supportedFeatures.IndependentBlend,
                VertexPipelineStoresAndAtomics = supportedFeatures.VertexPipelineStoresAndAtomics,
                FragmentStoresAndAtomics = supportedFeatures.FragmentStoresAndAtomics,
                ShaderInt64 = supportedFeatures.ShaderInt64,
                ShaderImageGatherExtended = supportedFeatures.ShaderImageGatherExtended,
                ShaderStorageImageExtendedFormats = supportedFeatures.ShaderStorageImageExtendedFormats,
                ShaderStorageImageReadWithoutFormat = supportedFeatures.ShaderStorageImageReadWithoutFormat,
                ShaderStorageImageWriteWithoutFormat = supportedFeatures.ShaderStorageImageWriteWithoutFormat,
                RobustBufferAccess = supportedFeatures.RobustBufferAccess,
            };

            if (!supportedFeatures.RobustBufferAccess)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support robustBufferAccess " +
                    "translated shaders performing out-of-bounds buffer access may cause device loss.");
            }

            if (!supportedFeatures.ShaderInt64)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support shaderInt64 " +
                    "translated shaders using 64-bit integers will fail.");
            }

            if (!supportedFeatures.VertexPipelineStoresAndAtomics || !supportedFeatures.FragmentStoresAndAtomics)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support vertexPipelineStoresAndAtomics/fragmentStoresAndAtomics " +
                    "translated shaders using storage buffers in vertex/fragment stages may fail.");
            }

            if (!supportedFeatures.ShaderImageGatherExtended)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support shaderImageGatherExtended " +
                    "translated shaders using image gather with offsets/LOD/bias will fail.");
            }

            if (!supportedFeatures.ShaderStorageImageReadWithoutFormat ||
                !supportedFeatures.ShaderStorageImageWriteWithoutFormat)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support shaderStorageImage(Read|Write)WithoutFormat " +
                    "translated shaders using unformatted storage image load/store will fail.");
            }

            var barycentricFeatures = new PhysicalDeviceFragmentShaderBarycentricFeaturesKHR
            {
                SType = StructureType.PhysicalDeviceFragmentShaderBarycentricFeaturesKhr,
            };
            var maintenance8Features = new PhysicalDeviceMaintenance8FeaturesKHR
            {
                SType = StructureType.PhysicalDeviceMaintenance8FeaturesKhr,
                PNext = &barycentricFeatures,
            };
            var robustness2Features = new PhysicalDeviceRobustness2FeaturesEXT
            {
                SType = StructureType.PhysicalDeviceRobustness2FeaturesExt,
                PNext = &maintenance8Features,
            };
            var featuresQuery = new PhysicalDeviceFeatures2
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &robustness2Features,
            };
            _vk.GetPhysicalDeviceFeatures2(_physicalDevice, &featuresQuery);
            var supportsMaintenance8 = maintenance8Features.Maintenance8;
            var supportsBarycentric = barycentricFeatures.FragmentShaderBarycentric;
            var supportsRobustImageAccess2 = robustness2Features.RobustImageAccess2;
            var supportsNullDescriptor = robustness2Features.NullDescriptor;
            var supportsRobustness2 = supportsRobustImageAccess2 || supportsNullDescriptor;
            if (!supportsBarycentric)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support VK_KHR_fragment_shader_barycentric " +
                    "translated shaders reconstructing V_INTERP_MOV_F32 coefficients will fail.");
            }
            if (!supportsMaintenance8)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support VK_KHR_maintenance8 " +
                    "translated shaders using a dynamic texel offset on non-gather image samples will fail.");
            }

            if (!supportsRobustImageAccess2)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support VK_EXT_robustness2 robustImageAccess2 " +
                    "translated shaders performing out-of-bounds image access may cause device loss.");
            }

            var swapchainExtension = (byte*)SilkMarshal.StringToPtr("VK_KHR_swapchain");
            var maintenance8Extension = (byte*)SilkMarshal.StringToPtr("VK_KHR_maintenance8");
            var robustness2Extension = (byte*)SilkMarshal.StringToPtr("VK_EXT_robustness2");
            var barycentricExtension =
                (byte*)SilkMarshal.StringToPtr("VK_KHR_fragment_shader_barycentric");
            try
            {
                var extensions = stackalloc byte*[4];
                var extensionCount = 0u;
                extensions[extensionCount++] = swapchainExtension;
                if (supportsMaintenance8)
                {
                    extensions[extensionCount++] = maintenance8Extension;
                }

                if (supportsRobustness2)
                {
                    extensions[extensionCount++] = robustness2Extension;
                }

                if (supportsBarycentric)
                {
                    extensions[extensionCount++] = barycentricExtension;
                }

                barycentricFeatures.FragmentShaderBarycentric = supportsBarycentric;
                barycentricFeatures.PNext = null;
                maintenance8Features.Maintenance8 = supportsMaintenance8;
                maintenance8Features.PNext = supportsBarycentric ? &barycentricFeatures : null;
                robustness2Features.RobustBufferAccess2 =
                    supportsRobustImageAccess2 && supportedFeatures.RobustBufferAccess;
                robustness2Features.RobustImageAccess2 = supportsRobustImageAccess2;
                robustness2Features.NullDescriptor = supportsNullDescriptor;
                robustness2Features.PNext = supportsMaintenance8
                    ? &maintenance8Features
                    : (supportsBarycentric ? &barycentricFeatures : null);
                var features2 = new PhysicalDeviceFeatures2
                {
                    SType = StructureType.PhysicalDeviceFeatures2,
                    PNext = supportsRobustness2
                        ? &robustness2Features
                        : (supportsMaintenance8
                            ? &maintenance8Features
                            : (supportsBarycentric ? &barycentricFeatures : null)),
                    Features = enabledFeatures,
                };
                var createInfo = new DeviceCreateInfo
                {
                    SType = StructureType.DeviceCreateInfo,
                    PNext = &features2,
                    QueueCreateInfoCount = 1,
                    PQueueCreateInfos = &queueInfo,
                    EnabledExtensionCount = extensionCount,
                    PpEnabledExtensionNames = extensions,
                };

                Check(_vk.CreateDevice(_physicalDevice, &createInfo, null, out _device), "vkCreateDevice");
            }
            finally
            {
                SilkMarshal.Free((nint)swapchainExtension);
                SilkMarshal.Free((nint)maintenance8Extension);
                SilkMarshal.Free((nint)robustness2Extension);
                SilkMarshal.Free((nint)barycentricExtension);
            }

            _vk.GetDeviceQueue(_device, _queueFamilyIndex, 0, out _queue);
            LoadDebugUtilsCommands();
            if (!_vk.TryGetDeviceExtension(_instance, _device, out _swapchainApi))
            {
                throw new InvalidOperationException("VK_KHR_swapchain is unavailable.");
            }

            QueryDeviceMemoryBudget();
        }

        private void QueryDeviceMemoryBudget()
        {
            _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var properties);
            _memoryProperties = properties;
            var heaps = &properties.MemoryHeaps.Element0;
            ulong heapBytes = 0;
            ulong hostHeapBytes = 0;
            for (var index = 0; index < (int)properties.MemoryHeapCount; index++)
            {
                var deviceLocalHeap =
                    (heaps[index].Flags & MemoryHeapFlags.DeviceLocalBit) != 0;
                if (deviceLocalHeap && heaps[index].Size > heapBytes)
                {
                    _deviceLocalHeapIndex = index;
                    heapBytes = heaps[index].Size;
                }
                else if (!deviceLocalHeap && heaps[index].Size > hostHeapBytes)
                {
                    _hostVisibleHeapIndex = index;
                    hostHeapBytes = heaps[index].Size;
                }
            }

            _supportsMemoryBudget = _vk.IsDeviceExtensionPresent(
                _physicalDevice,
                "VK_EXT_memory_budget");
            var budgetProperties = new PhysicalDeviceMemoryBudgetPropertiesEXT
            {
                SType = StructureType.PhysicalDeviceMemoryBudgetPropertiesExt,
            };
            if (_supportsMemoryBudget)
            {
                var properties2 = new PhysicalDeviceMemoryProperties2
                {
                    SType = StructureType.PhysicalDeviceMemoryProperties2,
                    PNext = &budgetProperties,
                };
                _vk.GetPhysicalDeviceMemoryProperties2(_physicalDevice, &properties2);
            }

            var budget = heapBytes;
            ulong driverUsage = 0;
            if (_supportsMemoryBudget && _deviceLocalHeapIndex >= 0)
            {
                var driverBudget = budgetProperties.HeapBudget[_deviceLocalHeapIndex];
                if (driverBudget != 0)
                {
                    budget = Math.Min(budget, driverBudget);
                }

                driverUsage = budgetProperties.HeapUsage[_deviceLocalHeapIndex];
            }

            var hostBudget = hostHeapBytes;
            if (_supportsMemoryBudget && _hostVisibleHeapIndex >= 0)
            {
                var driverBudget = budgetProperties.HeapBudget[_hostVisibleHeapIndex];
                if (driverBudget != 0)
                {
                    hostBudget = Math.Min(hostBudget, driverBudget);
                }
            }

            // Keep live usage below 80% of each heap's effective budget so
            // driver-internal and swapchain allocations still fit. On UMA
            // devices every heap is device-local, so hostBudget stays zero and
            // the host-visible check is disabled; the heap-based classifier
            // then routes every allocation into the device-local budget.
            _deviceMemoryLedger.BudgetBytes = budget * 8 / 10;
            _deviceMemoryLedger.HostVisibleBudgetBytes = hostBudget * 8 / 10;
            Console.Error.WriteLine(
                $"[LOADER][VKMEM] device-local heap={heapBytes / (1024 * 1024)}MB " +
                $"driver_budget={budget / (1024 * 1024)}MB " +
                $"driver_usage={driverUsage / (1024 * 1024)}MB " +
                $"cap={_deviceMemoryLedger.BudgetBytes / (1024 * 1024)}MB " +
                $"host-visible heap={hostHeapBytes / (1024 * 1024)}MB " +
                $"cap={_deviceMemoryLedger.HostVisibleBudgetBytes / (1024 * 1024)}MB " +
                $"budget_ext={_supportsMemoryBudget}");

            // Full heap/type breakdown so heap exhaustion is diagnosable (the
            // summary above only covers the largest heap of each kind).
            for (var index = 0; index < (int)properties.MemoryHeapCount; index++)
            {
                var line =
                    $"[LOADER][VKMEM] heap[{index}] " +
                    $"size={heaps[index].Size / (1024 * 1024)}MB " +
                    $"flags={heaps[index].Flags}";
                if (_supportsMemoryBudget)
                {
                    line +=
                        $" budget={budgetProperties.HeapBudget[index] / (1024 * 1024)}MB" +
                        $" usage={budgetProperties.HeapUsage[index] / (1024 * 1024)}MB";
                }

                Console.Error.WriteLine(line);
            }

            var memoryTypes = &properties.MemoryTypes.Element0;
            for (var index = 0; index < (int)properties.MemoryTypeCount; index++)
            {
                Console.Error.WriteLine(
                    $"[LOADER][VKMEM] type[{index}] heap={memoryTypes[index].HeapIndex} " +
                    $"flags={memoryTypes[index].PropertyFlags}");
            }
        }

        private bool IsDeviceLocalMemoryType(uint memoryTypeIndex)
        {
            // Classify by the heap the type allocates from, not the type's own
            // property flags: a DeviceLocal|HostVisible (BAR) type consumes the
            // device-local heap while HostVisible|HostCoherent staging types
            // consume the system-RAM heap, and it is heaps that run out. The
            // heap decides which budget the allocation counts against.
            var properties = _memoryProperties;
            if (memoryTypeIndex >= properties.MemoryTypeCount)
            {
                return false;
            }

            var memoryTypes = &properties.MemoryTypes.Element0;
            var heaps = &properties.MemoryHeaps.Element0;
            var heapIndex = (int)memoryTypes[memoryTypeIndex].HeapIndex;
            return (heaps[heapIndex].Flags & MemoryHeapFlags.DeviceLocalBit) != 0;
        }

        private void CreatePipelineCache()
        {
            _vk.GetPhysicalDeviceProperties(_physicalDevice, out var properties);
            var directory = Path.Combine(AppContext.BaseDirectory, "pipeline-cache");
            _pipelineCachePath = Path.Combine(
                directory,
                $"vulkan-{properties.VendorID:X4}-{properties.DeviceID:X4}-{properties.DriverVersion:X8}.bin");

            byte[] initialData = [];
            try
            {
                if (File.Exists(_pipelineCachePath))
                {
                    initialData = File.ReadAllBytes(_pipelineCachePath);
                }
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan pipeline cache could not be read: {exception.Message}");
            }

            fixed (byte* initialDataPointer = initialData)
            {
                var createInfo = new PipelineCacheCreateInfo
                {
                    SType = StructureType.PipelineCacheCreateInfo,
                    InitialDataSize = (nuint)initialData.Length,
                    PInitialData = initialData.Length == 0 ? null : initialDataPointer,
                };
                var result = _vk.CreatePipelineCache(
                    _device,
                    &createInfo,
                    null,
                    out _pipelineCache);
                if (result != Result.Success && initialData.Length != 0)
                {
                    createInfo.InitialDataSize = 0;
                    createInfo.PInitialData = null;
                    Check(
                        _vk.CreatePipelineCache(
                            _device,
                            &createInfo,
                            null,
                            out _pipelineCache),
                        "vkCreatePipelineCache(empty)");
                    initialData = [];
                }
                else
                {
                    Check(result, "vkCreatePipelineCache");
                }
            }

            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan pipeline cache: " +
                $"{(initialData.Length == 0 ? "new" : $"loaded {initialData.Length} bytes")}");
        }

        private void SavePipelineCache()
        {
            if (_pipelineCache.Handle == 0 || string.IsNullOrWhiteSpace(_pipelineCachePath))
            {
                return;
            }

            try
            {
                nuint size = 0;
                Check(
                    _vk.GetPipelineCacheData(_device, _pipelineCache, &size, null),
                    "vkGetPipelineCacheData(size)");
                if (size == 0 || size > int.MaxValue)
                {
                    return;
                }

                var data = new byte[(int)size];
                fixed (byte* dataPointer = data)
                {
                    Check(
                        _vk.GetPipelineCacheData(
                            _device,
                            _pipelineCache,
                            &size,
                            dataPointer),
                        "vkGetPipelineCacheData");
                }

                var directory = Path.GetDirectoryName(_pipelineCachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllBytes(_pipelineCachePath, data.AsSpan(0, (int)size).ToArray());
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan pipeline cache could not be saved: {exception.Message}");
            }
        }

        private void CreateSwapchain()
        {
            TraceInitStep("swapchain: querying surface capabilities");
            Check(
                _surfaceApi.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out var capabilities),
                "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
            TraceInitStep("swapchain: querying surface formats");

            uint formatCount = 0;
            Check(
                _surfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, null),
                "vkGetPhysicalDeviceSurfaceFormatsKHR");
            var formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* formatPointer = formats)
            {
                Check(
                    _surfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, formatPointer),
                    "vkGetPhysicalDeviceSurfaceFormatsKHR");
            }

            var surfaceFormat = ChooseSurfaceFormat(formats);
            _swapchainFormat = surfaceFormat.Format;
            _extent = ChooseExtent(capabilities);
            uint presentModeCount = 0;
            Check(
                _surfaceApi.GetPhysicalDeviceSurfacePresentModes(
                    _physicalDevice,
                    _surface,
                    &presentModeCount,
                    null),
                "vkGetPhysicalDeviceSurfacePresentModesKHR");
            var presentModes = new PresentModeKHR[presentModeCount];
            fixed (PresentModeKHR* presentModePointer = presentModes)
            {
                Check(
                    _surfaceApi.GetPhysicalDeviceSurfacePresentModes(
                        _physicalDevice,
                        _surface,
                        &presentModeCount,
                        presentModePointer),
                    "vkGetPhysicalDeviceSurfacePresentModesKHR");
            }

            var presentMode = presentModes.Contains(PresentModeKHR.MailboxKhr)
                ? PresentModeKHR.MailboxKhr
                : PresentModeKHR.FifoKhr;
            Console.Error.WriteLine($"[LOADER][INFO] Vulkan present mode: {presentMode}");
            var imageCount = capabilities.MinImageCount + 1;
            if (capabilities.MaxImageCount != 0)
            {
                imageCount = Math.Min(imageCount, capabilities.MaxImageCount);
            }

            var compositeAlpha = ChooseCompositeAlpha(capabilities.SupportedCompositeAlpha);
            var createInfo = new SwapchainCreateInfoKHR
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = _surface,
                MinImageCount = imageCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = _extent,
                ImageArrayLayers = 1,
                ImageUsage =
                    ImageUsageFlags.TransferDstBit |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.ColorAttachmentBit,
                ImageSharingMode = SharingMode.Exclusive,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = compositeAlpha,
                PresentMode = presentMode,
                Clipped = true,
            };

            TraceInitStep($"swapchain: creating {_extent.Width}x{_extent.Height} format={surfaceFormat.Format} images={imageCount}");
            Check(_swapchainApi.CreateSwapchain(_device, &createInfo, null, out _swapchain), "vkCreateSwapchainKHR");
            TraceInitStep("swapchain: created, fetching images");

            uint swapchainImageCount = 0;
            Check(
                _swapchainApi.GetSwapchainImages(_device, _swapchain, &swapchainImageCount, null),
                "vkGetSwapchainImagesKHR");
            _swapchainImages = new Image[swapchainImageCount];
            fixed (Image* imagePointer = _swapchainImages)
            {
                Check(
                    _swapchainApi.GetSwapchainImages(_device, _swapchain, &swapchainImageCount, imagePointer),
                    "vkGetSwapchainImagesKHR");
            }

            _imageInitialized = new bool[swapchainImageCount];
        }

        private void CreateCommandResources()
        {
            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
                QueueFamilyIndex = _queueFamilyIndex,
            };
            Check(_vk.CreateCommandPool(_device, &poolInfo, null, out _commandPool), "vkCreateCommandPool");

            var allocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            Check(_vk.AllocateCommandBuffers(_device, &allocateInfo, out _commandBuffer), "vkAllocateCommandBuffers");
            _presentationCommandBuffer = _commandBuffer;

            var semaphoreInfo = new SemaphoreCreateInfo
            {
                SType = StructureType.SemaphoreCreateInfo,
            };
            Check(_vk.CreateSemaphore(_device, &semaphoreInfo, null, out _imageAvailable), "vkCreateSemaphore");
            Check(_vk.CreateSemaphore(_device, &semaphoreInfo, null, out _renderFinished), "vkCreateSemaphore");

            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
            };
            Check(
                _vk.CreateFence(_device, &fenceInfo, null, out _presentationFence),
                "vkCreateFence(presentation)");

            CreateStagingBuffer((ulong)_extent.Width * _extent.Height * 4);
        }

        private void CompletePendingPresentation(bool wait)
        {
            if (!_presentationInFlight)
            {
                return;
            }

            var fence = _presentationFence;
            var result = wait
                ? _vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue)
                : _vk.GetFenceStatus(_device, fence);
            if (!wait && result == Result.NotReady)
            {
                return;
            }

            Check(result, "vkWaitForFences(presentation)");
            _presentationInFlight = false;
            if (_pendingPresentationResources is not null)
            {
                MarkSampledImagesInitialized(_pendingPresentationResources);
                MarkStorageImagesInitialized(_pendingPresentationResources);
                DestroyTranslatedDrawResources(_pendingPresentationResources);
                _pendingPresentationResources = null;
            }
        }

        private CommandBuffer AllocateGuestCommandBuffer()
        {
            var allocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            CommandBuffer commandBuffer;
            Check(
                _vk.AllocateCommandBuffers(
                    _device,
                    &allocateInfo,
                    out commandBuffer),
                "vkAllocateCommandBuffers(guest)");
            return commandBuffer;
        }

        private void SubmitGuestCommandBuffer(
            CommandBuffer commandBuffer,
            TranslatedDrawResources resources,
            IReadOnlyList<GuestImageResource> traceImages)
        {
            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
            };
            Fence fence;
            Check(
                _vk.CreateFence(_device, &fenceInfo, null, out fence),
                "vkCreateFence(guest)");
            try
            {
                var submitInfo = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                };
                Check(
                    _vk.QueueSubmit(_queue, 1, &submitInfo, fence),
                    "vkQueueSubmit(guest)");
            }
            catch
            {
                _vk.DestroyFence(_device, fence, null);
                throw;
            }

            _pendingGuestSubmissions.Enqueue(
                new PendingGuestSubmission(
                    fence,
                    commandBuffer,
                    resources,
                    traceImages,
                    resources.DebugName));
        }

        private void SubmitGuestCommandBufferAndWait(CommandBuffer commandBuffer)
        {
            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
            };
            Fence fence;
            Check(
                _vk.CreateFence(_device, &fenceInfo, null, out fence),
                "vkCreateFence(guest chunk)");
            try
            {
                var submitInfo = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                };
                Check(
                    _vk.QueueSubmit(_queue, 1, &submitInfo, fence),
                    "vkQueueSubmit(guest chunk)");
                Check(
                    _vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue),
                    "vkWaitForFences(guest chunk)");
            }
            finally
            {
                _vk.DestroyFence(_device, fence, null);
            }

            _vk.FreeCommandBuffers(_device, _commandPool, 1, &commandBuffer);
        }

        private void EnsureGuestSubmissionCapacity()
        {
            CollectCompletedGuestSubmissions(waitForOldest: false);
            if (_pendingGuestSubmissions.Count >= MaxInFlightGuestSubmissions)
            {
                CollectCompletedGuestSubmissions(waitForOldest: true);
            }
        }

        private void WaitForAllGuestSubmissions()
        {
            while (_pendingGuestSubmissions.Count != 0)
            {
                CollectCompletedGuestSubmissions(waitForOldest: true);
            }
        }

        private void CollectCompletedGuestSubmissions(bool waitForOldest)
        {
            if (waitForOldest && _pendingGuestSubmissions.TryPeek(out var oldest))
            {
                var fence = oldest.Fence;
                var result = _vk.WaitForFences(
                    _device,
                    1,
                    &fence,
                    true,
                    ulong.MaxValue);
                Check(result, $"vkWaitForFences(guest: {oldest.DebugName})");
            }

            while (_pendingGuestSubmissions.TryPeek(out var submission))
            {
                var status = _vk.GetFenceStatus(_device, submission.Fence);
                if (status == Result.NotReady)
                {
                    break;
                }

                Check(status, $"vkGetFenceStatus(guest: {submission.DebugName})");
                _pendingGuestSubmissions.Dequeue();

                try
                {
                    foreach (var image in submission.TraceImages)
                    {
                        TraceGuestImageContents(image);
                    }
                }
                catch (Exception exception)
                {
                    // Diagnostics only: a failed readback (e.g. OOM on its
                    // host buffer) must not leak the submission's resources,
                    // command buffer, or fence below.
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] guest readback trace failed: {exception.Message}");
                }

                DestroyTranslatedDrawResources(submission.Resources);
                var commandBuffer = submission.CommandBuffer;
                _vk.FreeCommandBuffers(
                    _device,
                    _commandPool,
                    1,
                    &commandBuffer);
                _vk.DestroyFence(_device, submission.Fence, null);
            }
        }

        private IReadOnlyList<GuestImageResource> GetTraceImages(
            TranslatedDrawResources resources,
            IReadOnlyList<GuestImageResource>? renderTargets = null)
        {
            var images = new HashSet<GuestImageResource>();
            foreach (var renderTarget in renderTargets ?? [])
            {
                if (ShouldTraceGuestImageContents(renderTarget))
                {
                    images.Add(renderTarget);
                }
            }

            foreach (var texture in resources.Textures)
            {
                if (texture.IsStorage &&
                    texture.GuestImage is { } image &&
                    ShouldTraceGuestImageContents(image))
                {
                    images.Add(image);
                }
            }

            return images.ToArray();
        }

        private void CreateGuestDrawResources()
        {
            var colorAttachment = new AttachmentDescription
            {
                Format = _swapchainFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.PresentSrcKhr,
            };
            var colorReference = new AttachmentReference
            {
                Attachment = 0,
                Layout = ImageLayout.ColorAttachmentOptimal,
            };
            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorReference,
            };
            var dependency = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
            };
            var renderPassInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 1,
                PAttachments = &colorAttachment,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency,
            };
            Check(_vk.CreateRenderPass(_device, &renderPassInfo, null, out _renderPass), "vkCreateRenderPass");

            _swapchainImageViews = new ImageView[_swapchainImages.Length];
            _framebuffers = new Framebuffer[_swapchainImages.Length];
            for (var index = 0; index < _swapchainImages.Length; index++)
            {
                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = _swapchainImages[index],
                    ViewType = ImageViewType.Type2D,
                    Format = _swapchainFormat,
                    Components = new ComponentMapping(
                        ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity),
                    SubresourceRange = ColorSubresourceRange(),
                };
                Check(
                    _vk.CreateImageView(_device, &viewInfo, null, out _swapchainImageViews[index]),
                    "vkCreateImageView");

                var imageView = _swapchainImageViews[index];
                var framebufferInfo = new FramebufferCreateInfo
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = _renderPass,
                    AttachmentCount = 1,
                    PAttachments = &imageView,
                    Width = _extent.Width,
                    Height = _extent.Height,
                    Layers = 1,
                };
                Check(
                    _vk.CreateFramebuffer(_device, &framebufferInfo, null, out _framebuffers[index]),
                    "vkCreateFramebuffer");
            }

            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
            };
            Check(
                _vk.CreatePipelineLayout(_device, &layoutInfo, null, out _pipelineLayout),
                "vkCreatePipelineLayout");
            CreateBarycentricPipeline();
        }

        private void CreateBarycentricPipeline()
        {
            var vertexBytes = Convert.FromBase64String(FullscreenBarycentricVertexSpirv);
            var fragmentBytes = Convert.FromBase64String(FullscreenBarycentricFragmentSpirv);
            var vertexModule = CreateShaderModule(vertexBytes);
            var fragmentModule = CreateShaderModule(fragmentBytes);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var shaderStages = stackalloc PipelineShaderStageCreateInfo[2];
                shaderStages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = entryPoint,
                };
                shaderStages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragmentModule,
                    PName = entryPoint,
                };

                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList,
                };
                var viewport = new Viewport(0, 0, _extent.Width, _extent.Height, 0, 1);
                var scissor = new Rect2D(new Offset2D(0, 0), _extent);
                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    PViewports = &viewport,
                    ScissorCount = 1,
                    PScissors = &scissor,
                };
                var rasterization = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.CounterClockwise,
                    LineWidth = 1,
                };
                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                };
                var colorBlendAttachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask =
                        ColorComponentFlags.RBit |
                        ColorComponentFlags.GBit |
                        ColorComponentFlags.BBit |
                        ColorComponentFlags.ABit,
                };
                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &colorBlendAttachment,
                };
                var pipelineInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = shaderStages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterization,
                    PMultisampleState = &multisample,
                    PColorBlendState = &colorBlend,
                    Layout = _pipelineLayout,
                    RenderPass = _renderPass,
                    Subpass = 0,
                };
                Check(
                    _vk.CreateGraphicsPipelines(
                        _device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out _barycentricPipeline),
                    "vkCreateGraphicsPipelines");
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, fragmentModule, null);
                _vk.DestroyShaderModule(_device, vertexModule, null);
            }
        }

        private ShaderModule CreateShaderModule(byte[] code)
        {
            fixed (byte* codePointer = code)
            {
                var createInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)code.Length,
                    PCode = (uint*)codePointer,
                };
                Check(
                    _vk.CreateShaderModule(_device, &createInfo, null, out var module),
                    "vkCreateShaderModule");
                return module;
            }
        }

        private TranslatedDrawResources CreateTranslatedDrawResources(
            VulkanTranslatedGuestDraw draw,
            RenderPass renderPass,
            IReadOnlyList<Format> renderTargetFormats,
            Extent2D extent,
            VulkanGuestDepthTarget? depth = null)
        {
            var vertexSpirv = draw.VertexSpirv;
            if (draw.RenderState.Blends.Count != renderTargetFormats.Count)
            {
                throw new InvalidOperationException(
                    "color attachment formats and blend states must have matching counts");
            }

            // A pass-through NGG draw runs its export shader as a compute prepass
            // (ComputeCaptureSpirv) that writes one clip-space vec4 per vertex.
            // We only take that path when every capture field is present and the
            // input-buffer count matches the SPIR-V's expectation
            // (guestBuffers[0..K-1] inputs, [K] the appended output); otherwise we
            // fall back to the ordinary draw so a bad capture never breaks it.
            byte[]? captureSpirv = null;
            NggComputeCapture captureLayout = default;
            IReadOnlyList<VulkanGuestMemoryBuffer>? captureInputs = null;
            var captureInvocations = 0u;
            if (draw.ComputeCaptureSpirv is { Length: > 0 } candidateSpirv &&
                draw.ComputeCapture is { } candidateLayout &&
                draw.ComputeInvocationCount > 0 &&
                draw.ComputeCaptureInputs is { } candidateInputs &&
                candidateInputs.Count == candidateLayout.PositionBufferBindingIndex)
            {
                captureSpirv = candidateSpirv;
                captureLayout = candidateLayout;
                captureInputs = candidateInputs;
                captureInvocations = draw.ComputeInvocationCount;
            }

            var useCapture = captureSpirv is not null;
            if (useCapture)
            {
                // The pass-through export already applies the full transform in
                // the compute prepass, so the raster VS just forwards the captured
                // clip-space position bound at vertex-input location 0.
                // Diagnostic: a hardcoded fullscreen-triangle VS (ignores the
                // vertex input) isolates the render pass/target/depth from the
                // captured-vertex binding.
                vertexSpirv = string.Equals(
                    Environment.GetEnvironmentVariable("SHARPEMU_NGG_DEBUG"),
                    "1",
                    StringComparison.Ordinal)
                    ? SpirvFixedShaders.CreateFullscreenVertex(draw.AttributeCount)
                    : SpirvFixedShaders.CreatePositionPassthroughVertex(
                        draw.AttributeCount,
                        captureLayout.ParamCount);
            }
            else if (vertexSpirv.Length == 0 &&
                !TryCompileFullscreenVertexShader(
                    draw.AttributeCount,
                    out vertexSpirv,
                    out var vertexError))
            {
                throw new InvalidOperationException($"translated vertex shader failed: {vertexError}");
            }

            var resources = new TranslatedDrawResources
            {
                DebugName = "SharpEmu draw",
                Textures = new TextureResource[draw.Textures.Count],
                GlobalMemoryBuffers =
                    new GlobalBufferResource[draw.GlobalMemoryBuffers.Count],
                VertexBuffers = useCapture
                    ? []
                    : new VertexBufferResource[draw.VertexBuffers.Count],
                VertexCount = useCapture
                    ? captureInvocations
                    : GetDrawVertexCount(draw.PrimitiveType, draw.VertexCount, draw.IndexBuffer),
                InstanceCount = useCapture ? 1 : Math.Max(draw.InstanceCount, 1),
                Topology = GetPrimitiveTopology(draw.PrimitiveType),
                Blends = draw.RenderState.Blends.ToArray(),
                Scissor = draw.RenderState.Scissor,
                Viewport = draw.RenderState.Viewport,
            };

            try
            {
                foreach (var texture in draw.Textures)
                {
                    // Skip address-0 storage bindings here: the real resolution
                    // path (ResolveStorageImageResource) uses a scratch image for
                    // those, but ResolveStorageGuestImage throws on address 0,
                    // which dropped the whole producer draw of the present or
                    // composite target every frame.
                    if (texture.IsStorage && texture.Address != 0)
                    {
                        _ = ResolveStorageGuestImage(texture);
                    }
                }

                for (var index = 0; index < draw.Textures.Count; index++)
                {
                    resources.Textures[index] = ResolveTextureResource(draw.Textures[index]);
                }

                for (var index = 0; index < draw.GlobalMemoryBuffers.Count; index++)
                {
                    resources.GlobalMemoryBuffers[index] =
                        CreateGlobalBufferResource(draw.GlobalMemoryBuffers[index]);
                }

                if (useCapture)
                {
                    BuildNggCaptureDrawResources(
                        resources,
                        captureSpirv!,
                        captureLayout,
                        captureInputs!,
                        captureInvocations);
                    CreateTranslatedDescriptorResources(
                        resources,
                        ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit);
                    // Diagnostic: replace the guest pixel shader with a solid-color
                    // one to prove the captured geometry rasterizes into the target
                    // independent of the guest PS's (currently unfed) varyings.
                    var capturePixelSpirv = string.Equals(
                        Environment.GetEnvironmentVariable("SHARPEMU_NGG_DEBUG"),
                        "1",
                        StringComparison.Ordinal)
                        ? SpirvFixedShaders.CreateSolidColorFragment(
                            (uint)renderTargetFormats.Count)
                        : draw.PixelSpirv;
                    CreateTranslatedPipeline(
                        resources,
                        vertexSpirv,
                        capturePixelSpirv,
                        renderPass,
                        renderTargetFormats,
                        extent,
                        depth);
                    return resources;
                }

                for (var index = 0; index < draw.VertexBuffers.Count; index++)
                {
                    resources.VertexBuffers[index] =
                        CreateVertexBufferResource(draw.VertexBuffers[index]);
                }

                if (draw.IndexBuffer is { Data.Length: > 0 } indexBuffer)
                {
                    resources.IndexBuffer = CreateHostBuffer(
                        indexBuffer.Data,
                        BufferUsageFlags.IndexBufferBit,
                        out resources.IndexMemory);
                    resources.Index32Bit = indexBuffer.Is32Bit;

                    // GPU-driven indexed indirect: back the draw with a real
                    // vkCmdDrawIndexedIndirect reading a dedicated indirect
                    // VkBuffer. Only taken for indexed draws that carried
                    // indirect args; every other draw path is untouched.
                    if (draw.IndirectArgs is { } args &&
                        args.Data.Length >= (int)(args.Offset + args.Stride) &&
                        args.Stride >= 20)
                    {
                        resources.IndirectArgsBuffer = CreateHostBuffer(
                            args.Data,
                            BufferUsageFlags.IndirectBufferBit,
                            out resources.IndirectArgsMemory);
                        resources.IndirectArgsOffset = args.Offset;
                        resources.IndirectArgsStride = args.Stride;
                        resources.HasIndirectArgs = true;
                    }
                }

                CreateTranslatedDescriptorResources(
                    resources,
                    ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit);
                CreateTranslatedPipeline(
                    resources,
                    vertexSpirv,
                    draw.PixelSpirv,
                    renderPass,
                    renderTargetFormats,
                    extent,
                    depth);
                return resources;
            }
            catch
            {
                DestroyTranslatedDrawResources(resources);
                throw;
            }
        }

        // Builds the resources for the NGG position-capture path onto an existing
        // graphics TranslatedDrawResources: a device-local output buffer, a child
        // compute TranslatedDrawResources that binds the K export-as-compute input
        // buffers plus that output buffer as guestBuffers[0..K], and the graphics
        // vertex/index streams that draw the captured positions. The output buffer
        // is created with both storage (compute write) and vertex (raster read)
        // usage and is owned solely by the child compute resources, so the
        // graphics vertex-buffer entry that aliases it is flagged External.
        private void BuildNggCaptureDrawResources(
            TranslatedDrawResources resources,
            byte[] captureSpirv,
            NggComputeCapture captureLayout,
            IReadOnlyList<VulkanGuestMemoryBuffer> captureInputs,
            uint invocationCount)
        {
            var inputCount = captureInputs.Count;
            var vertexStride = Math.Max(captureLayout.PositionDwordStride, 1) * (uint)sizeof(uint);
            var outputSize = (ulong)invocationCount * vertexStride;

            VkBuffer outputBuffer = default;
            DeviceMemory outputMemory = default;
            TranslatedDrawResources? captureResources = null;
            try
            {
                var readback = string.Equals(
                    Environment.GetEnvironmentVariable("SHARPEMU_NGG_READBACK"),
                    "1",
                    StringComparison.Ordinal);
                outputBuffer = CreateBuffer(
                    outputSize,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit |
                        (readback ? BufferUsageFlags.TransferSrcBit : 0),
                    MemoryPropertyFlags.DeviceLocalBit,
                    out outputMemory);

                captureResources = new TranslatedDrawResources
                {
                    DebugName = "SharpEmu ngg-capture-compute",
                    GlobalMemoryBuffers = new GlobalBufferResource[inputCount + 1],
                };
                // Own the output buffer through the child first so any later
                // failure frees it via DestroyTranslatedDrawResources.
                captureResources.GlobalMemoryBuffers[inputCount] = new GlobalBufferResource
                {
                    Buffer = outputBuffer,
                    Memory = outputMemory,
                    Size = outputSize,
                };
                for (var index = 0; index < inputCount; index++)
                {
                    captureResources.GlobalMemoryBuffers[index] =
                        CreateGlobalBufferResource(captureInputs[index]);
                }

                CreateTranslatedDescriptorResources(captureResources, ShaderStageFlags.ComputeBit);
                CreateComputePipeline(captureResources, captureSpirv);
            }
            catch
            {
                if (captureResources is not null)
                {
                    DestroyTranslatedDrawResources(captureResources);
                }
                else if (outputBuffer.Handle != 0)
                {
                    _vk.DestroyBuffer(_device, outputBuffer, null);
                    FreeDeviceMemory(outputMemory);
                }

                throw;
            }

            resources.CaptureCompute = captureResources;
            resources.CaptureInvocationCount = invocationCount;
            resources.CaptureOutputBuffer = outputBuffer;
            if (string.Equals(
                    Environment.GetEnvironmentVariable("SHARPEMU_NGG_READBACK"),
                    "1",
                    StringComparison.Ordinal))
            {
                resources.CaptureReadbackBuffer = CreateBuffer(
                    outputSize,
                    BufferUsageFlags.TransferDstBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    out resources.CaptureReadbackMemory);
                resources.CaptureReadbackSize = outputSize;
            }

            // Draw the captured clip-space positions as a location-0 vec4 stream
            // (14/7 -> R32G32B32A32_SFLOAT), followed by one location-(1+i) vec4
            // stream per captured parameter export laid out after the position in
            // the same interleaved buffer. External: owned by the child.
            const uint captureVec4Bytes = 4u * sizeof(float);
            var vertexBuffers = new VertexBufferResource[1 + captureLayout.ParamCount];
            for (var slot = 0u; slot < vertexBuffers.Length; slot++)
            {
                vertexBuffers[slot] = new VertexBufferResource
                {
                    Buffer = outputBuffer,
                    Memory = default,
                    Size = outputSize,
                    Location = slot,
                    ComponentCount = 4,
                    DataFormat = 14,
                    NumberFormat = 7,
                    Stride = vertexStride,
                    OffsetBytes = slot * captureVec4Bytes,
                    External = true,
                };
            }

            resources.VertexBuffers = vertexBuffers;

            // One 32-bit index per captured vertex, in dispatch order, so the
            // ordinary CmdBindIndexBuffer + CmdDrawIndexed branch is taken.
            var indexData = new byte[checked((int)((long)invocationCount * sizeof(uint)))];
            var indexWords = MemoryMarshal.Cast<byte, uint>(indexData.AsSpan());
            for (var index = 0; index < indexWords.Length; index++)
            {
                indexWords[index] = (uint)index;
            }

            resources.IndexBuffer = CreateHostBuffer(
                indexData,
                BufferUsageFlags.IndexBufferBit,
                out resources.IndexMemory);
            resources.Index32Bit = true;
            resources.VertexCount = invocationCount;
            resources.InstanceCount = 1;
            resources.HasIndirectArgs = false;
            // The NGG pass-through geometry is a triangle list; the original
            // indirect draw's VGT primitive type is the NGG *input* topology
            // (points), which would rasterize the mesh as 3 points and produce
            // no coverage. Force triangle-list for the reconstructed draw.
            resources.Topology = PrimitiveTopology.TriangleList;
        }

        // Records the NGG position-capture compute prepass into the offscreen
        // command buffer, ahead of the graphics render pass, then a
        // compute-write -> vertex-read buffer barrier on the output buffer. The
        // command buffer already had its texture/target transitions recorded; a
        // buffer barrier is legal here because we are still outside the render
        // pass instance.
        private void RecordNggCaptureDispatch(TranslatedDrawResources resources)
        {
            if (resources.CaptureCompute is not { } capture ||
                resources.CaptureInvocationCount == 0)
            {
                return;
            }

            _vk.CmdBindPipeline(
                _commandBuffer,
                PipelineBindPoint.Compute,
                capture.Pipeline);
            if (capture.DescriptorSet.Handle != 0)
            {
                var descriptorSet = capture.DescriptorSet;
                _vk.CmdBindDescriptorSets(
                    _commandBuffer,
                    PipelineBindPoint.Compute,
                    capture.PipelineLayout,
                    0,
                    1,
                    &descriptorSet,
                    0,
                    null);
            }

            // Must equal AgcExports.NggCaptureLocalSizeX: the capture SPIR-V bakes
            // local_size_x = 64, so we launch ceil(N / 64) workgroups.
            const uint localSizeX = 64;
            var groupCount = (resources.CaptureInvocationCount + localSizeX - 1) / localSizeX;
            _vk.CmdDispatch(_commandBuffer, groupCount, 1, 1);

            var barrier = new BufferMemoryBarrier
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.VertexAttributeReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = resources.CaptureOutputBuffer,
                Offset = 0,
                Size = Vk.WholeSize,
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.VertexInputBit,
                0,
                0,
                null,
                1,
                &barrier,
                0,
                null);

            if (resources.CaptureReadbackBuffer.Handle != 0)
            {
                var copy = new BufferCopy { Size = resources.CaptureReadbackSize };
                _vk.CmdCopyBuffer(
                    _commandBuffer,
                    resources.CaptureOutputBuffer,
                    resources.CaptureReadbackBuffer,
                    1,
                    &copy);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private TranslatedDrawResources CreateComputeDispatchResources(
            VulkanComputeGuestDispatch dispatch)
        {
            var traceResources = dispatch.Textures.Count >= 8;
            if (traceResources)
            {
                TraceVulkanShader(
                    $"vk.compute_resources begin groups={dispatch.GroupCountX}x" +
                    $"{dispatch.GroupCountY}x{dispatch.GroupCountZ} textures={dispatch.Textures.Count}");
            }

            var resources = new TranslatedDrawResources
            {
                DebugName = BuildComputeDebugName(dispatch),
                Textures = new TextureResource[dispatch.Textures.Count],
                GlobalMemoryBuffers =
                    new GlobalBufferResource[dispatch.GlobalMemoryBuffers.Count],
            };

            try
            {
                for (var index = 0; index < dispatch.Textures.Count; index++)
                {
                    var texture = dispatch.Textures[index];
                    if (texture.IsStorage && texture.Address != 0)
                    {
                        if (traceResources)
                        {
                            TraceVulkanShader(
                                $"vk.compute_resources storage[{index}] begin " +
                                $"addr=0x{texture.Address:X16} fmt={texture.Format} " +
                                $"size={texture.Width}x{texture.Height} " +
                                $"mips={texture.MipLevels} level={texture.MipLevel}");
                        }

                        // Warm only the guest-image cache here (like the graphics
                        // path does). A full ResolveStorageImageResource would
                        // allocate a scratch image or staging buffer whose result
                        // is discarded, leaking device memory on every dispatch;
                        // the resolve loop below creates the tracked resource.
                        _ = ResolveStorageGuestImage(texture);
                        if (traceResources)
                        {
                            TraceVulkanShader($"vk.compute_resources storage[{index}] ready");
                        }
                    }
                }

                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources resolve begin");
                }

                for (var index = 0; index < dispatch.Textures.Count; index++)
                {
                    resources.Textures[index] =
                        ResolveTextureResource(dispatch.Textures[index]);
                }

                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources resolve ready");
                }

                for (var index = 0; index < dispatch.GlobalMemoryBuffers.Count; index++)
                {
                    resources.GlobalMemoryBuffers[index] =
                        CreateGlobalBufferResource(dispatch.GlobalMemoryBuffers[index]);
                }

                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources descriptors begin");
                }

                CreateTranslatedDescriptorResources(resources, ShaderStageFlags.ComputeBit);
                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources descriptors ready");
                }

                if (traceResources)
                {
                    TraceVulkanShader(
                        $"vk.compute_resources pipeline begin " +
                        $"cs=0x{dispatch.ShaderAddress:X16} " +
                        $"spirv={dispatch.ComputeSpirv.Length} " +
                        $"textures={resources.Textures.Length} " +
                        $"globals={resources.GlobalMemoryBuffers.Length}");
                }

                CreateComputePipeline(resources, dispatch.ComputeSpirv);
                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources pipeline ready");
                }

                return resources;
            }
            catch
            {
                DestroyTranslatedDrawResources(resources);
                throw;
            }
        }

        private static bool TryCompileFullscreenVertexShader(
            uint attributeCount,
            out byte[] spirv,
            out string error)
        {
            spirv = [];
            error = string.Empty;
            if (attributeCount > 32)
            {
                error = $"too many interpolated attributes: {attributeCount}";
                return false;
            }

            spirv = SpirvFixedShaders.CreateFullscreenVertex(attributeCount);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CreateTranslatedDescriptorResources(
            TranslatedDrawResources resources,
            ShaderStageFlags stageFlags)
        {
            var textureCount = resources.Textures.Length;
            var sampledImageCount = resources.Textures.Count(texture => !texture.IsStorage);
            var storageImageCount = textureCount - sampledImageCount;
            var globalBufferCount = resources.GlobalMemoryBuffers.Length;
            var bindingCount = textureCount + (globalBufferCount == 0 ? 0 : 1);
            var layout = GetOrCreateDescriptorLayout(resources, stageFlags, bindingCount);
            resources.DescriptorSetLayout = layout.DescriptorSetLayout;
            resources.PipelineLayout = layout.PipelineLayout;
            resources.DescriptorLayoutCached = true;
            if (bindingCount == 0)
            {
                return;
            }

            var setLayout = layout.DescriptorSetLayout;

            var poolSizes = new DescriptorPoolSize[
                (sampledImageCount == 0 ? 0 : 1) +
                (storageImageCount == 0 ? 0 : 1) +
                (globalBufferCount == 0 ? 0 : 1)];
            var poolSizeIndex = 0;
            if (sampledImageCount != 0)
            {
                poolSizes[poolSizeIndex++] = new DescriptorPoolSize
                {
                    Type = DescriptorType.CombinedImageSampler,
                    DescriptorCount = (uint)sampledImageCount,
                };
            }

            if (storageImageCount != 0)
            {
                poolSizes[poolSizeIndex++] = new DescriptorPoolSize
                {
                    Type = DescriptorType.StorageImage,
                    DescriptorCount = (uint)storageImageCount,
                };
            }

            if (globalBufferCount != 0)
            {
                poolSizes[poolSizeIndex] = new DescriptorPoolSize
                {
                    Type = DescriptorType.StorageBuffer,
                    DescriptorCount = (uint)globalBufferCount,
                };
            }

            fixed (DescriptorPoolSize* poolSizePointer = poolSizes)
            {
                var poolInfo = new DescriptorPoolCreateInfo
                {
                    SType = StructureType.DescriptorPoolCreateInfo,
                    MaxSets = 1,
                    PoolSizeCount = (uint)poolSizes.Length,
                    PPoolSizes = poolSizePointer,
                };
                DescriptorPool descriptorPool;
                Check(
                    _vk.CreateDescriptorPool(
                        _device,
                        &poolInfo,
                        null,
                        out descriptorPool),
                    "vkCreateDescriptorPool");
                resources.DescriptorPool = descriptorPool;
            }

            var allocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = resources.DescriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &setLayout,
            };
            DescriptorSet descriptorSet;
            Check(
                _vk.AllocateDescriptorSets(_device, &allocateInfo, out descriptorSet),
                "vkAllocateDescriptorSets");
            resources.DescriptorSet = descriptorSet;

            var imageInfos = new DescriptorImageInfo[textureCount];
            var bufferInfos = new DescriptorBufferInfo[globalBufferCount];
            var writes = new WriteDescriptorSet[bindingCount];
            fixed (DescriptorImageInfo* imageInfoPointer = imageInfos)
            fixed (DescriptorBufferInfo* bufferInfoPointer = bufferInfos)
            fixed (WriteDescriptorSet* writePointer = writes)
            {
                var writeIndex = 0;
                if (globalBufferCount != 0)
                {
                    for (var index = 0; index < globalBufferCount; index++)
                    {
                        bufferInfoPointer[index] = new DescriptorBufferInfo
                        {
                            Buffer = resources.GlobalMemoryBuffers[index].Buffer,
                            Offset = 0,
                            Range = resources.GlobalMemoryBuffers[index].Size,
                        };
                    }

                    writePointer[writeIndex++] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = resources.DescriptorSet,
                        DstBinding = 0,
                        DescriptorCount = (uint)globalBufferCount,
                        DescriptorType = DescriptorType.StorageBuffer,
                        PBufferInfo = bufferInfoPointer,
                    };
                }

                for (var index = 0; index < textureCount; index++)
                {
                    var isStorage = resources.Textures[index].IsStorage;
                    if (!isStorage &&
                        resources.Textures[index].Sampler.Handle == 0)
                    {
                        resources.Textures[index].Sampler =
                            CreateSampler(resources.Textures[index].SamplerState);
                    }

                    imageInfoPointer[index] = new DescriptorImageInfo
                    {
                        Sampler = isStorage ? default : resources.Textures[index].Sampler,
                        ImageView = resources.Textures[index].View,
                        // Must stay the exact predicate RecordSampledImageTransitions
                        // skips on, or a descriptor would declare a layout no
                        // barrier establishes.
                        ImageLayout = isStorage ||
                            resources.Textures[index].GuestImage is { } guestImage &&
                            BindsGuestImageAsStorage(resources, guestImage)
                                ? ImageLayout.General
                                : ImageLayout.ShaderReadOnlyOptimal,
                    };
                    writePointer[writeIndex++] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = resources.DescriptorSet,
                        DstBinding = (uint)(index + 1),
                        DescriptorCount = 1,
                        DescriptorType = isStorage
                            ? DescriptorType.StorageImage
                            : DescriptorType.CombinedImageSampler,
                        PImageInfo = &imageInfoPointer[index],
                    };
                }

                _vk.UpdateDescriptorSets(
                    _device,
                    (uint)bindingCount,
                    writePointer,
                    0,
                    null);
            }
        }

        private void CreateTranslatedPipeline(
            TranslatedDrawResources resources,
            byte[] vertexSpirv,
            byte[] fragmentSpirv,
            RenderPass renderPass,
            IReadOnlyList<Format> renderTargetFormats,
            Extent2D extent,
            VulkanGuestDepthTarget? depth = null)
        {
            var depthKey = depth is { } depthState
                ? $"{depthState.Format}:{(depthState.TestEnable ? 1 : 0)}:{(depthState.WriteEnable ? 1 : 0)}:{depthState.CompareOp}"
                : "none";
            var pipelineKey = new GraphicsPipelineKey(
                GetShaderDigest(vertexSpirv),
                GetShaderDigest(fragmentSpirv),
                string.Join(',', renderTargetFormats.Select(format => (uint)format)) + "|d=" + depthKey,
                resources.Topology,
                string.Join(';', resources.Blends.Select(blend =>
                    $"{(blend.Enable ? 1 : 0)}:{blend.ColorSrcFactor}:{blend.ColorDstFactor}:" +
                    $"{blend.ColorFunc}:{blend.AlphaSrcFactor}:{blend.AlphaDstFactor}:" +
                    $"{blend.AlphaFunc}:{(blend.SeparateAlphaBlend ? 1 : 0)}:{blend.WriteMask}")),
                GetResourceLayoutKey(resources),
                GetVertexLayoutKey(resources));
            if (_graphicsPipelines.TryGetValue(pipelineKey, out var cachedPipeline))
            {
                resources.Pipeline = cachedPipeline;
                resources.PipelineCached = true;
                return;
            }

            var vertexModule = CreateShaderModule(vertexSpirv);
            var fragmentModule = CreateShaderModule(fragmentSpirv);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var shaderStages = stackalloc PipelineShaderStageCreateInfo[2];
                shaderStages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = entryPoint,
                };
                shaderStages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragmentModule,
                    PName = entryPoint,
                };

                var vertexBindingDescriptions =
                    new VertexInputBindingDescription[resources.VertexBuffers.Length];
                var vertexAttributeDescriptions =
                    new VertexInputAttributeDescription[resources.VertexBuffers.Length];
                for (var index = 0; index < resources.VertexBuffers.Length; index++)
                {
                    var vertexBuffer = resources.VertexBuffers[index];
                    vertexBindingDescriptions[index] = new VertexInputBindingDescription
                    {
                        Binding = (uint)index,
                        Stride = vertexBuffer.Stride == 0
                            ? Math.Max(vertexBuffer.ComponentCount, 1) * sizeof(float)
                            : vertexBuffer.Stride,
                        InputRate = VertexInputRate.Vertex,
                    };
                    vertexAttributeDescriptions[index] = new VertexInputAttributeDescription
                    {
                        Location = vertexBuffer.Location,
                        Binding = (uint)index,
                        Format = ToVkVertexFormat(
                            vertexBuffer.DataFormat,
                            vertexBuffer.NumberFormat,
                            vertexBuffer.ComponentCount),
                        Offset = 0,
                    };
                }

                fixed (VertexInputBindingDescription* vertexBindingPointerBase = vertexBindingDescriptions)
                fixed (VertexInputAttributeDescription* vertexAttributePointerBase = vertexAttributeDescriptions)
                {
                    var vertexInput = new PipelineVertexInputStateCreateInfo
                    {
                        SType = StructureType.PipelineVertexInputStateCreateInfo,
                        VertexBindingDescriptionCount = (uint)vertexBindingDescriptions.Length,
                        PVertexBindingDescriptions = vertexBindingDescriptions.Length == 0
                            ? null
                            : vertexBindingPointerBase,
                        VertexAttributeDescriptionCount = (uint)vertexAttributeDescriptions.Length,
                        PVertexAttributeDescriptions = vertexAttributeDescriptions.Length == 0
                            ? null
                            : vertexAttributePointerBase,
                    };
                    var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                    {
                        SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                        Topology = resources.Topology,
                    };
                    var viewport = new Viewport(0, 0, extent.Width, extent.Height, 0, 1);
                    var scissor = new Rect2D(new Offset2D(0, 0), extent);
                    var viewportState = new PipelineViewportStateCreateInfo
                    {
                        SType = StructureType.PipelineViewportStateCreateInfo,
                        ViewportCount = 1,
                        PViewports = &viewport,
                        ScissorCount = 1,
                        PScissors = &scissor,
                    };
                    var rasterization = new PipelineRasterizationStateCreateInfo
                    {
                        SType = StructureType.PipelineRasterizationStateCreateInfo,
                        PolygonMode = PolygonMode.Fill,
                        CullMode = CullModeFlags.None,
                        FrontFace = FrontFace.CounterClockwise,
                        LineWidth = 1,
                    };
                    var multisample = new PipelineMultisampleStateCreateInfo
                    {
                        SType = StructureType.PipelineMultisampleStateCreateInfo,
                        RasterizationSamples = SampleCountFlags.Count1Bit,
                    };
                    var colorBlendAttachments = stackalloc PipelineColorBlendAttachmentState[resources.Blends.Length];
                    for (var index = 0; index < resources.Blends.Length; index++)
                    {
                        var blend = resources.Blends[index];
                        colorBlendAttachments[index] = new PipelineColorBlendAttachmentState
                        {
                            BlendEnable = blend.Enable,
                            SrcColorBlendFactor = ToVkBlendFactor(blend.ColorSrcFactor),
                            DstColorBlendFactor = ToVkBlendFactor(blend.ColorDstFactor),
                            ColorBlendOp = ToVkBlendOp(blend.ColorFunc),
                            SrcAlphaBlendFactor = blend.SeparateAlphaBlend
                                ? ToVkBlendFactor(blend.AlphaSrcFactor)
                                : ToVkBlendFactor(blend.ColorSrcFactor),
                            DstAlphaBlendFactor = blend.SeparateAlphaBlend
                                ? ToVkBlendFactor(blend.AlphaDstFactor)
                                : ToVkBlendFactor(blend.ColorDstFactor),
                            AlphaBlendOp = blend.SeparateAlphaBlend
                                ? ToVkBlendOp(blend.AlphaFunc)
                                : ToVkBlendOp(blend.ColorFunc),
                            ColorWriteMask = ToVkColorWriteMask(blend.WriteMask),
                        };
                    }
                    var colorBlend = new PipelineColorBlendStateCreateInfo
                    {
                        SType = StructureType.PipelineColorBlendStateCreateInfo,
                        AttachmentCount = (uint)resources.Blends.Length,
                        PAttachments = colorBlendAttachments,
                    };
                    var dynamicStateValues = stackalloc DynamicState[2];
                    dynamicStateValues[0] = DynamicState.Viewport;
                    dynamicStateValues[1] = DynamicState.Scissor;
                    var dynamicState = new PipelineDynamicStateCreateInfo
                    {
                        SType = StructureType.PipelineDynamicStateCreateInfo,
                        DynamicStateCount = 2,
                        PDynamicStates = dynamicStateValues,
                    };
                    // Depth test/write come from DB_DEPTH_CONTROL. A null
                    // PDepthStencilState (the no-depth-target case) preserves the
                    // prior depth-off behavior exactly; the render pass built for
                    // that draw has no depth attachment either.
                    var depthStencil = new PipelineDepthStencilStateCreateInfo
                    {
                        SType = StructureType.PipelineDepthStencilStateCreateInfo,
                        DepthTestEnable = depth is { TestEnable: true },
                        DepthWriteEnable = depth is { WriteEnable: true },
                        DepthCompareOp = depth is { } depthCompare
                            ? ToVkCompareOp(depthCompare.CompareOp)
                            : CompareOp.Always,
                        DepthBoundsTestEnable = false,
                        StencilTestEnable = false,
                    };
                    var pipelineInfo = new GraphicsPipelineCreateInfo
                    {
                        SType = StructureType.GraphicsPipelineCreateInfo,
                        StageCount = 2,
                        PStages = shaderStages,
                        PVertexInputState = &vertexInput,
                        PInputAssemblyState = &inputAssembly,
                        PViewportState = &viewportState,
                        PRasterizationState = &rasterization,
                        PMultisampleState = &multisample,
                        PColorBlendState = &colorBlend,
                        PDepthStencilState = depth is not null ? &depthStencil : null,
                        PDynamicState = &dynamicState,
                        Layout = resources.PipelineLayout,
                        RenderPass = renderPass,
                        Subpass = 0,
                    };
                    Pipeline pipeline;
                    Check(
                        _vk.CreateGraphicsPipelines(
                            _device,
                            _pipelineCache,
                            1,
                            &pipelineInfo,
                            null,
                            out pipeline),
                        "vkCreateGraphicsPipelines(translated)");
                    resources.Pipeline = pipeline;
                    resources.PipelineCached = true;
                    _graphicsPipelines.Add(pipelineKey, pipeline);
                    SetDebugName(
                        ObjectType.Pipeline,
                        pipeline.Handle,
                        $"SharpEmu graphics ps={fragmentSpirv.Length}b attrs={resources.Textures.Length}");
                }
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, fragmentModule, null);
                _vk.DestroyShaderModule(_device, vertexModule, null);
            }
        }

        private DescriptorLayoutBundle GetOrCreateDescriptorLayout(
            TranslatedDrawResources resources,
            ShaderStageFlags stageFlags,
            int bindingCount)
        {
            var key = new DescriptorLayoutKey(stageFlags, GetResourceLayoutKey(resources));
            if (_descriptorLayouts.TryGetValue(key, out var cached))
            {
                return cached;
            }

            DescriptorSetLayout descriptorSetLayout = default;
            if (bindingCount != 0)
            {
                var bindings = new DescriptorSetLayoutBinding[bindingCount];
                var bindingOffset = 0;
                if (resources.GlobalMemoryBuffers.Length != 0)
                {
                    bindings[bindingOffset++] = new DescriptorSetLayoutBinding
                    {
                        Binding = 0,
                        DescriptorType = DescriptorType.StorageBuffer,
                        DescriptorCount = (uint)resources.GlobalMemoryBuffers.Length,
                        StageFlags = stageFlags,
                    };
                }

                for (var index = 0; index < resources.Textures.Length; index++)
                {
                    bindings[bindingOffset + index] = new DescriptorSetLayoutBinding
                    {
                        Binding = (uint)(index + 1),
                        DescriptorType = resources.Textures[index].IsStorage
                            ? DescriptorType.StorageImage
                            : DescriptorType.CombinedImageSampler,
                        DescriptorCount = 1,
                        StageFlags = stageFlags,
                    };
                }

                fixed (DescriptorSetLayoutBinding* bindingPointer = bindings)
                {
                    var descriptorInfo = new DescriptorSetLayoutCreateInfo
                    {
                        SType = StructureType.DescriptorSetLayoutCreateInfo,
                        BindingCount = (uint)bindings.Length,
                        PBindings = bindingPointer,
                    };
                    Check(
                        _vk.CreateDescriptorSetLayout(
                            _device,
                            &descriptorInfo,
                            null,
                            out descriptorSetLayout),
                        "vkCreateDescriptorSetLayout");
                }
            }

            var pipelineInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
            };
            if (descriptorSetLayout.Handle != 0)
            {
                pipelineInfo.SetLayoutCount = 1;
                pipelineInfo.PSetLayouts = &descriptorSetLayout;
            }

            PipelineLayout pipelineLayout;
            Check(
                _vk.CreatePipelineLayout(
                    _device,
                    &pipelineInfo,
                    null,
                    out pipelineLayout),
                "vkCreatePipelineLayout");
            var created = new DescriptorLayoutBundle(descriptorSetLayout, pipelineLayout);
            _descriptorLayouts.Add(key, created);
            return created;
        }

        private string GetShaderDigest(byte[] spirv)
        {
            if (_shaderDigests.TryGetValue(spirv, out var digest))
            {
                return digest;
            }

            digest = Convert.ToHexString(SHA256.HashData(spirv));
            _shaderDigests.Add(spirv, digest);
            return digest;
        }

        private static string GetResourceLayoutKey(TranslatedDrawResources resources)
        {
            var key = new StringBuilder();
            key.Append(resources.GlobalMemoryBuffers.Length).Append(':');
            foreach (var texture in resources.Textures)
            {
                key.Append(texture.IsStorage ? 'S' : 'T');
            }

            return key.ToString();
        }

        private static string GetVertexLayoutKey(TranslatedDrawResources resources)
        {
            var key = new StringBuilder();
            foreach (var buffer in resources.VertexBuffers)
            {
                key.Append(buffer.Location).Append(',')
                    .Append(buffer.ComponentCount).Append(',')
                    .Append(buffer.DataFormat).Append(',')
                    .Append(buffer.NumberFormat).Append(',')
                    .Append(buffer.Stride == 0
                        ? Math.Max(buffer.ComponentCount, 1) * sizeof(float)
                        : buffer.Stride)
                    .Append(';');
            }

            return key.ToString();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CreateComputePipeline(
            TranslatedDrawResources resources,
            byte[] computeSpirv)
        {
            if (_computePipelines.TryGetValue(computeSpirv, out var cachedPipeline))
            {
                resources.Pipeline = cachedPipeline;
                resources.PipelineCached = true;
                return;
            }

            var computeModule = CreateShaderModule(computeSpirv);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = computeModule,
                    PName = entryPoint,
                };
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Flags = PipelineCreateFlags.CreateDispatchBaseBit,
                    Stage = stage,
                    Layout = resources.PipelineLayout,
                };
                Pipeline pipeline;
                Check(
                    _vk.CreateComputePipelines(
                        _device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out pipeline),
                    "vkCreateComputePipelines(translated)");
                resources.Pipeline = pipeline;
                resources.PipelineCached = true;
                SetDebugName(
                    ObjectType.Pipeline,
                    pipeline.Handle,
                    $"SharpEmu compute cs={computeSpirv.Length}b");
                _computePipelines.Add(computeSpirv, pipeline);
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, computeModule, null);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private TextureResource ResolveTextureResource(VulkanGuestDrawTexture texture)
        {
            if (texture.IsStorage)
            {
                return ResolveStorageImageResource(texture);
            }

            var vkFormat = GetTextureFormat(texture.Format, texture.NumberType);

            // SHARPEMU_FORCE_EXPOSURE: a tiny (<=2x2) sampled surface is the
            // game's auto-exposure average-luminance value, which nothing writes
            // (so it reads zero and blacks out the tonemap). Bind a constant.
            if (ForcedExposureValue is { } exposure &&
                texture.Address != 0 &&
                texture.Width <= 2 &&
                texture.Height <= 2 &&
                !texture.IsStorage)
            {
                var lumImage = EnsureForcedExposureImage(exposure);
                lumImage.LastUseStamp = _useStamp;
                return new TextureResource
                {
                    Address = texture.Address,
                    Image = lumImage.Image,
                    View = lumImage.View,
                    Width = lumImage.Width,
                    Height = lumImage.Height,
                    RowLength = lumImage.Width,
                    DstSelect = texture.DstSelect,
                    SamplerState = texture.Sampler,
                    GuestImage = lumImage,
                };
            }

            // A rendered depth surface at this address holds real depth in a
            // depth format that no color view can alias. Bind its depth-aspect
            // view directly so the sampler returns real depth in .r -- this is
            // what turns the black deferred-lighting output into sampled depth.
            if (texture.Address != 0 &&
                _guestImages.TryFindDepthAlias(texture.Address, out var depthImage))
            {
                depthImage.LastUseStamp = _useStamp;
                return new TextureResource
                {
                    Address = texture.Address,
                    Image = depthImage.Image,
                    View = depthImage.View,
                    Width = depthImage.Width,
                    Height = depthImage.Height,
                    RowLength = depthImage.Width,
                    DstSelect = texture.DstSelect,
                    SamplerState = texture.Sampler,
                    GuestImage = depthImage,
                };
            }

            if (texture.Address != 0 &&
                _guestImages.TryFindSampleAlias(texture, vkFormat, out var guestImage) &&
                TryGetOrCreateGuestImageView(
                    guestImage,
                    vkFormat,
                    mipLevel: 0,
                    levelCount: guestImage.MipLevels,
                    dstSelect: texture.DstSelect,
                    out var view))
            {
                guestImage.LastUseStamp = _useStamp;
                if (ShouldTraceVulkanResources() &&
                    _tracedTextureCacheHits.Add(
                        (texture.Address, texture.Width, texture.Height, vkFormat)))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.texture_cache_hit addr=0x{texture.Address:X16} " +
                        $"size={texture.Width}x{texture.Height} " +
                        $"image_format={guestImage.Format} view_format={vkFormat}");
                }

                if (guestImage.Width != texture.Width ||
                    guestImage.Height != texture.Height)
                {
                    TraceVulkanShader(
                        $"vk.texture_cache_alias addr=0x{texture.Address:X16} " +
                        $"texture={texture.Width}x{texture.Height} " +
                        $"image={guestImage.Width}x{guestImage.Height} " +
                        $"tile={texture.TileMode} format={vkFormat}");
                }

                if (TryCreateCpuTextureRefreshResource(texture, guestImage, view, out var refresh))
                {
                    return refresh;
                }

                return new TextureResource
                {
                    Address = texture.Address,
                    Image = guestImage.Image,
                    View = view,
                    Width = guestImage.Width,
                    Height = guestImage.Height,
                    RowLength = guestImage.Width,
                    DstSelect = texture.DstSelect,
                    SamplerState = texture.Sampler,
                    GuestImage = guestImage,
                };
            }

            // The strict alias above needs a view in the sampled format. When a
            // rendered target is sampled in an incompatible format (the game
            // reuses a guest address for surfaces of different formats), that
            // view cannot be created -- but the rendered VkImage still holds real
            // content, whereas the guest memory behind the address was never
            // written by the GPU and reads back black. Bind a view in the image's
            // OWN format so the sampler reads real pixels (possibly reinterpreted)
            // instead of an empty upload. Only for non-fallback address textures
            // that carry no CPU pixels (i.e. deferred render-target references).
            // The strict-gate fix may read guest memory for a cross-format input
            // (populated RgbaPixels), but a genuine GPU producer surface still
            // holds the real content -- prefer its native-format view over the
            // guest-memory upload rather than regressing to a black readback.
            if (texture.Address != 0 &&
                !texture.IsFallback &&
                (texture.RgbaPixels.Length == 0 || ShouldUseFlipCompositeFix()) &&
                _guestImages.TryFindNativeAlias(texture, vkFormat, out var renderedImage) &&
                TryGetOrCreateGuestImageView(
                    renderedImage,
                    renderedImage.Format,
                    mipLevel: 0,
                    levelCount: renderedImage.MipLevels,
                    dstSelect: texture.DstSelect,
                    out var nativeView))
            {
                renderedImage.LastUseStamp = _useStamp;
                if (ShouldTracePresentedGuestImageContentsForDiagnostics() &&
                    _tracedGateRejects.Add(texture.Address))
                {
                    Console.Error.WriteLine(
                        "[LOADER][TRACE] vk.native_alias addr=0x" + texture.Address.ToString("X16") +
                        " img_fmt=" + renderedImage.Format + " sampled_view=" + vkFormat +
                        " img=" + renderedImage.Width + "x" + renderedImage.Height);
                }

                return new TextureResource
                {
                    Address = texture.Address,
                    Image = renderedImage.Image,
                    View = nativeView,
                    Width = renderedImage.Width,
                    Height = renderedImage.Height,
                    RowLength = renderedImage.Width,
                    DstSelect = texture.DstSelect,
                    SamplerState = texture.Sampler,
                    GuestImage = renderedImage,
                };
            }

            // The producer at this address rendered/compute-wrote real content
            // into a VkImage, but no compatible sampled view could be aliased
            // and the guest memory carries no CPU pixels. Uploading empty guest
            // memory would read back black and cascade the whole pass to black.
            // Copy the producer's GPU content into a fresh sampled image so the
            // sampler reads real pixels (Kyty-style copy-on-sample bridge).
            // A GPU producer (render target / compute ImageStore) at this address
            // is authoritative even when the enqueue gate already read (empty)
            // guest memory into RgbaPixels -- for these addresses the guest bytes
            // are stale/zero and would upload black. Prefer the producer whenever
            // one is cached, regardless of RgbaPixels (a genuine CPU texture has
            // no initialized GPU surface at its address, so this does not steal
            // real uploads).
            GuestImageResource? producer = null;
            if (texture.Address != 0)
            {
                _guestImages.TryFindPresentable(texture.Address, out producer);
            }

            if (producer is { } gpuProducer &&
                gpuProducer.Image.Handle != 0 &&
                !texture.IsFallback &&
                !IsBlockCompressedFormat(vkFormat))
            {
                return CreateCopyOnSampleTextureResource(texture, gpuProducer);
            }

            if (texture.Address != 0 &&
                !texture.IsFallback &&
                _tracedUploadFallthrough.Add(texture.Address))
            {
                Console.Error.WriteLine(
                    "[LOADER][TRACE] vk.upload_fallthrough addr=0x" + texture.Address.ToString("X16") +
                    " has_producer=" + (producer is not null) +
                    " producer_init=" + (producer?.Initialized) +
                    " producer_fmt=" + (producer?.Format) +
                    " has_surface=" + _guestImages.ContainsAddress(texture.Address) +
                    " rgba=" + texture.RgbaPixels.Length +
                    " vk=" + vkFormat);
            }

            return CreateTextureResource(texture);
        }

        private TextureResource CreateCopyOnSampleTextureResource(
            VulkanGuestDrawTexture texture,
            GuestImageResource producer)
        {
            var width = Math.Max(texture.Width, 1);
            var height = Math.Max(texture.Height, 1);
            var vkFormat = GetTextureFormat(texture.Format, texture.NumberType);

            if (_tracedCopyOnSample.Add((texture.Address, vkFormat)))
            {
                Console.Error.WriteLine(
                    "[LOADER][TRACE] vk.copy_on_sample addr=0x" + texture.Address.ToString("X16") +
                    " producer_fmt=" + producer.Format +
                    " producer=" + producer.Width + "x" + producer.Height +
                    " sampled_fmt=" + vkFormat +
                    " dst=" + width + "x" + height);
            }

            var supportsAttachmentUsage = !IsBlockCompressedFormat(vkFormat);
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags = supportsAttachmentUsage
                    ? ImageCreateFlags.CreateMutableFormatBit | ImageCreateFlags.CreateExtendedUsageBit
                    : 0,
                ImageType = ImageType.Type2D,
                Format = vkFormat,
                Extent = new Extent3D(width, height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = supportsAttachmentUsage
                    ? ImageUsageFlags.TransferDstBit |
                      ImageUsageFlags.SampledBit |
                      ImageUsageFlags.ColorAttachmentBit |
                      ImageUsageFlags.StorageBit |
                      ImageUsageFlags.TransferSrcBit
                    : ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out var image), "vkCreateImage(copy_on_sample)");
            _vk.GetImageMemoryRequirements(_device, image, out var imageRequirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = imageRequirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    imageRequirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            DeviceMemory imageMemory = default;
            ImageView view;
            try
            {
                imageMemory = AllocateDeviceMemory(memoryInfo, "copy_on_sample");
                Check(_vk.BindImageMemory(_device, image, imageMemory, 0), "vkBindImageMemory(copy_on_sample)");

                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = image,
                    ViewType = ImageViewType.Type2D,
                    Format = vkFormat,
                    Components = ToVkComponentMapping(texture.DstSelect),
                    SubresourceRange = ColorSubresourceRange(),
                };
                Check(_vk.CreateImageView(_device, &viewInfo, null, out view), "vkCreateImageView(copy_on_sample)");
            }
            catch
            {
                _vk.DestroyImage(_device, image, null);
                FreeDeviceMemory(imageMemory);
                throw;
            }

            var debugName = TextureDebugName(texture, vkFormat);
            SetDebugName(ObjectType.Image, image.Handle, $"{debugName} copy_on_sample image");
            SetDebugName(ObjectType.ImageView, view.Handle, $"{debugName} copy_on_sample view");

            return new TextureResource
            {
                Address = texture.Address,
                Image = image,
                ImageMemory = imageMemory,
                View = view,
                Width = width,
                Height = height,
                RowLength = width,
                DstSelect = texture.DstSelect,
                SamplerState = texture.Sampler,
                NeedsUpload = true,
                OwnsStorage = true,
                CopySource = producer,
                Format = vkFormat,
            };
        }

        private bool TryCreateCpuTextureRefreshResource(
            VulkanGuestDrawTexture texture,
            GuestImageResource guestImage,
            ImageView view,
            out TextureResource resource)
        {
            resource = default!;
            if (guestImage.Width != texture.Width ||
                guestImage.Height != texture.Height ||
                guestImage.MipLevels != 1 ||
                texture.RgbaPixels.Length == 0)
            {
                return false;
            }

            // A promoted (GPU-rendered) image also accepts dirty CPU refreshes:
            // the game overwrites the surface's guest memory with real texture
            // data after the GPU last touched it, and sampling the stale render
            // would show the old frame. Never-written (all-zero) guest bytes do
            // not count as dirt -- the rendered content stays authoritative.
            if (!guestImage.IsCpuBacked &&
                texture.RgbaPixels.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            {
                return false;
            }

            var rowLength = texture.TileMode == 0
                ? Math.Max(texture.Pitch, texture.Width)
                : texture.Width;
            var expectedSize = GetTextureByteCount(texture.Format, rowLength, texture.Height);
            if (expectedSize == 0 || expectedSize > int.MaxValue)
            {
                return false;
            }

            var pixels = texture.RgbaPixels.Length == (int)expectedSize
                ? texture.RgbaPixels
                : CreateFallbackTexturePixels(texture.Format, rowLength, texture.Height, expectedSize);
            var fingerprint = ComputeTextureContentFingerprint(pixels);
            if ((guestImage.Initialized || guestImage.InitialUploadPending) &&
                guestImage.CpuContentFingerprint == fingerprint)
            {
                return false;
            }

            var uploadPixels = texture.Format == 13
                ? ExpandRgb32Pixels(pixels)
                : pixels;
            var debugName = TextureDebugName(texture, guestImage.Format);
            var (stagingBuffer, stagingMemory) = CreateTextureStagingBuffer(
                uploadPixels,
                $"{debugName} refresh staging");
            TraceVulkanShader(
                $"vk.texture_refresh addr=0x{texture.Address:X16} " +
                $"size={texture.Width}x{texture.Height} bytes={uploadPixels.Length}");
            resource = new TextureResource
            {
                Address = texture.Address,
                StagingBuffer = stagingBuffer,
                StagingMemory = stagingMemory,
                Image = guestImage.Image,
                View = view,
                Width = guestImage.Width,
                Height = guestImage.Height,
                RowLength = rowLength,
                DstSelect = texture.DstSelect,
                NeedsUpload = true,
                SamplerState = texture.Sampler,
                GuestImage = guestImage,
                CpuContentFingerprint = fingerprint,
                UpdatesCpuContent = true,
            };
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private TextureResource ResolveStorageImageResource(VulkanGuestDrawTexture texture)
        {
            if (texture.Address == 0)
            {
                return CreateStorageScratchResource(texture);
            }

            var guestImage = ResolveStorageGuestImage(texture);
            var vkFormat = GetTextureFormat(texture.Format, texture.NumberType);
            var view = GetOrCreateGuestImageView(
                guestImage,
                vkFormat,
                texture.MipLevel,
                levelCount: 1);
            var resource = new TextureResource
            {
                Address = texture.Address,
                Image = guestImage.Image,
                View = view,
                Width = guestImage.Width,
                Height = guestImage.Height,
                RowLength = guestImage.Width,
                DstSelect = texture.DstSelect,
                IsStorage = true,
                SamplerState = texture.Sampler,
                GuestImage = guestImage,
            };

            if (texture.MipLevel == 0 && texture.RgbaPixels.Length != 0)
            {
                var expectedSize = GetTextureByteCount(
                    texture.Format,
                    texture.Width,
                    texture.Height);
                if ((ulong)texture.RgbaPixels.Length == expectedSize)
                {
                    // First use uploads the guest bytes unconditionally (an
                    // all-zero image is still the guest's real content). An
                    // initialized image accepts dirty refreshes when the CPU
                    // overwrote its guest memory with different, nonzero data;
                    // zeroed guest memory behind a GPU-written storage image
                    // stays untouched -- the GPU content is authoritative.
                    var firstUse =
                        !guestImage.Initialized && !guestImage.InitialUploadPending;
                    var fingerprint =
                        ComputeTextureContentFingerprint(texture.RgbaPixels);
                    var dirtyRefresh = !firstUse &&
                        guestImage.CpuContentFingerprint != fingerprint &&
                        texture.RgbaPixels.AsSpan().IndexOfAnyExcept((byte)0) >= 0;
                    if (firstUse || dirtyRefresh)
                    {
                        resource.StagingBuffer = CreateHostBuffer(
                            texture.RgbaPixels,
                            BufferUsageFlags.TransferSrcBit,
                            out resource.StagingMemory);
                        resource.NeedsUpload = true;
                        resource.CpuContentFingerprint = fingerprint;
                        resource.UpdatesCpuContent = true;
                        guestImage.InitialUploadPending = true;
                        TraceVulkanShader(
                            $"vk.storage_upload addr=0x{texture.Address:X16} " +
                            $"size={texture.Width}x{texture.Height} " +
                            $"bytes={expectedSize} refresh={(dirtyRefresh ? 1 : 0)}");
                    }
                }
            }

            return resource;
        }

        private TextureResource CreateStorageScratchResource(VulkanGuestDrawTexture texture)
        {
            var width = Math.Max(texture.Width, 1);
            var height = Math.Max(texture.Height, 1);
            var vkFormat = GetTextureFormat(texture.Format, texture.NumberType);
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = vkFormat,
                Extent = new Extent3D(width, height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage =
                    ImageUsageFlags.SampledBit |
                    ImageUsageFlags.StorageBit |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(
                _vk.CreateImage(_device, &imageInfo, null, out var image),
                "vkCreateImage(storage scratch)");
            _vk.GetImageMemoryRequirements(_device, image, out var requirements);
            var allocationInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            var memory = AllocateDeviceMemory(allocationInfo, "storage-scratch");
            Check(
                _vk.BindImageMemory(_device, image, memory, 0),
                "vkBindImageMemory(storage scratch)");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = vkFormat,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity),
                SubresourceRange = ColorSubresourceRange(),
            };
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out var view),
                "vkCreateImageView(storage scratch)");
            SetDebugName(ObjectType.Image, image.Handle, $"SharpEmu scratch storage {width}x{height} {vkFormat}");
            SetDebugName(ObjectType.ImageView, view.Handle, $"SharpEmu scratch storage {width}x{height} {vkFormat} view");

            var guestImage = new GuestImageResource
            {
                Address = 0,
                Width = width,
                Height = height,
                MipLevels = 1,
                Format = vkFormat,
                Image = image,
                Memory = memory,
                View = view,
            };

            return new TextureResource
            {
                Address = 0,
                Image = image,
                ImageMemory = memory,
                View = view,
                Width = width,
                Height = height,
                RowLength = width,
                DstSelect = texture.DstSelect,
                OwnsStorage = true,
                IsStorage = true,
                SamplerState = texture.Sampler,
                GuestImage = guestImage,
            };
        }

        private GuestImageResource ResolveStorageGuestImage(VulkanGuestDrawTexture texture)
        {
            if (texture.Address == 0)
            {
                throw new InvalidOperationException("Storage image has no guest address.");
            }

            var format = GetTextureFormat(texture.Format, texture.NumberType);
            var guestImage = GetOrCreateGuestImage(
                new VulkanGuestRenderTarget(
                    texture.Address,
                    texture.Width,
                    texture.Height,
                    texture.Format,
                    texture.NumberType,
                    texture.MipLevels),
                format);
            if (texture.MipLevel >= guestImage.MipLevels)
            {
                throw new InvalidOperationException(
                    $"Storage mip {texture.MipLevel} exceeds image mip count {guestImage.MipLevels}.");
            }

            return guestImage;
        }

        private TextureResource CreateTextureResource(VulkanGuestDrawTexture texture)
        {
            var width = Math.Max(texture.Width, 1);
            var height = Math.Max(texture.Height, 1);
            var rowLength = texture.TileMode == 0
                ? Math.Max(texture.Pitch, width)
                : width;
            var vkFormat = GetTextureFormat(texture.Format, texture.NumberType);

            var expectedSize = GetTextureByteCount(texture.Format, rowLength, height);
            if (_tracedTextureUploads.Add((texture.Address, width, height, vkFormat)))
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.texture addr=0x{texture.Address:X16} " +
                    $"fmt={texture.Format} num={texture.NumberType} vk={vkFormat} " +
                    $"size={width}x{height} row={rowLength} tile={texture.TileMode} " +
                    $"dst=0x{texture.DstSelect:X3} " +
                    $"bytes={texture.RgbaPixels.Length} expected={expectedSize}");
            }
            var pixels = texture.RgbaPixels.Length == (int)expectedSize
                ? texture.RgbaPixels
                : CreateFallbackTexturePixels(texture.Format, rowLength, height, expectedSize);
            DumpTextureUpload(texture, pixels, rowLength, width, height);
            var uploadPixels = texture.Format == 13
                ? ExpandRgb32Pixels(pixels)
                : pixels;
            var contentFingerprint = ComputeTextureContentFingerprint(pixels);

            var supportsAttachmentUsage = !IsBlockCompressedFormat(vkFormat);
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags = supportsAttachmentUsage
                    ? ImageCreateFlags.CreateMutableFormatBit | ImageCreateFlags.CreateExtendedUsageBit
                    : 0,
                ImageType = ImageType.Type2D,
                Format = vkFormat,
                Extent = new Extent3D(width, height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = supportsAttachmentUsage
                    ? ImageUsageFlags.TransferDstBit |
                      ImageUsageFlags.SampledBit |
                      ImageUsageFlags.ColorAttachmentBit |
                      ImageUsageFlags.StorageBit |
                      ImageUsageFlags.TransferSrcBit
                    : ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out var image), "vkCreateImage(texture)");
            _vk.GetImageMemoryRequirements(_device, image, out var imageRequirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = imageRequirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    imageRequirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            DeviceMemory imageMemory = default;
            ImageView view;
            try
            {
                imageMemory = AllocateDeviceMemory(memoryInfo, "texture");
                Check(_vk.BindImageMemory(_device, image, imageMemory, 0), "vkBindImageMemory(texture)");

                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = image,
                    ViewType = ImageViewType.Type2D,
                    Format = vkFormat,
                    Components = ToVkComponentMapping(texture.DstSelect),
                    SubresourceRange = ColorSubresourceRange(),
                };
                Check(_vk.CreateImageView(_device, &viewInfo, null, out view), "vkCreateImageView(texture)");
            }
            catch
            {
                // A partially-created texture never reaches resources.Textures,
                // so the per-draw cleanup cannot free it. Unwind it here.
                _vk.DestroyImage(_device, image, null);
                FreeDeviceMemory(imageMemory);
                throw;
            }

            var debugName = TextureDebugName(texture, vkFormat);
            var (stagingBuffer, stagingMemory) = CreateTextureStagingBuffer(
                uploadPixels,
                $"{debugName} staging");
            SetDebugName(ObjectType.Image, image.Handle, $"{debugName} image");
            SetDebugName(ObjectType.ImageView, view.Handle, $"{debugName} view");
            var resource = new TextureResource
            {
                Address = texture.Address,
                StagingBuffer = stagingBuffer,
                StagingMemory = stagingMemory,
                Image = image,
                ImageMemory = imageMemory,
                View = view,
                Width = width,
                Height = height,
                RowLength = rowLength,
                DstSelect = texture.DstSelect,
                NeedsUpload = true,
                OwnsStorage = true,
                SamplerState = texture.Sampler,
                CpuContentFingerprint = contentFingerprint,
                UpdatesCpuContent = texture.Address != 0,
            };

            var uploadIsLiveRenderTarget = false;
            if (texture.Address != 0)
            {
                lock (_gate)
                {
                    uploadIsLiveRenderTarget =
                        _renderTargetGuestImages.ContainsKey(texture.Address) ||
                        _gpuGuestImages.ContainsKey(texture.Address);
                    if (ShouldTracePresentedGuestImageContentsForDiagnostics() &&
                        _tracedGateRejects.Add(texture.Address))
                    {
                        Console.Error.WriteLine(
                            "[LOADER][TRACE] vk.upload_miss addr=0x" + texture.Address.ToString("X16") +
                            " live_rt=" + uploadIsLiveRenderTarget +
                            " in_rt=" + _renderTargetGuestImages.ContainsKey(texture.Address) +
                            " in_gpu=" + _gpuGuestImages.ContainsKey(texture.Address) +
                            " in_avail=" + _availableGuestImages.ContainsKey(texture.Address) +
                            " has_surface=" + _guestImages.ContainsAddress(texture.Address) +
                            " view_fmt=" + vkFormat);
                    }
                }
            }

            if (texture.Address != 0 &&
                !uploadIsLiveRenderTarget &&
                !_guestImages.ContainsAddress(texture.Address))
            {
                var guestImage = new GuestImageResource
                {
                    Address = texture.Address,
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    Format = vkFormat,
                    Image = image,
                    Memory = imageMemory,
                    View = view,
                    InitialUploadPending = true,
                    IsCpuBacked = true,
                    CpuContentFingerprint = contentFingerprint,
                    MemorySize = imageRequirements.Size,
                    LastUseStamp = _useStamp,
                };
                _guestImages.Add(guestImage);
                resource.OwnsStorage = false;
                resource.GuestImage = guestImage;
                lock (_gate)
                {
                    var guestFormat = VulkanVideoPresenter.GetGuestTextureFormat(
                        texture.Format,
                        texture.NumberType);
                    // Re-check under the registry lock: a producer draw may have
                    // pre-registered this address between the live-render-target
                    // probe above and this takeover. De-registering it here would
                    // permanently strand every later consumer on zeroed guest
                    // uploads, so the CPU upload only claims addresses that still
                    // have no GPU producer.
                    var producerRegistered =
                        _renderTargetGuestImages.ContainsKey(texture.Address) ||
                        _gpuGuestImages.ContainsKey(texture.Address);
                    if (guestFormat != 0 && !producerRegistered)
                    {
                        _availableGuestImages[texture.Address] = guestFormat;
                        // A genuine CPU upload owns this address now; it must
                        // never be captured as an address-referencing texture.
                        ForgetRenderedGuestImageLocked(texture.Address);
                    }
                }
            }

            return resource;
        }

        private (VkBuffer Buffer, DeviceMemory Memory) CreateTextureStagingBuffer(
            byte[] pixels,
            string debugName)
        {
            // Pooled: texture uploads churn 25-50MB host-visible staging buffers
            // every frame; allocating fresh device memory per upload exhausts and
            // fragments the host-visible heap even while device-local use is tiny.
            var buffer = CreateHostBuffer(
                pixels,
                BufferUsageFlags.TransferSrcBit,
                out var memory);
            SetDebugName(ObjectType.Buffer, buffer.Handle, debugName);
            return (buffer, memory);
        }

        private static ulong ComputeTextureContentFingerprint(ReadOnlySpan<byte> pixels)
        {
            const ulong offsetBasis = 14695981039346656037;
            const ulong prime = 1099511628211;

            var h0 = offsetBasis ^ (ulong)pixels.Length;
            var h1 = offsetBasis;
            var h2 = offsetBasis;
            var h3 = offsetBasis;

            var words = MemoryMarshal.Cast<byte, ulong>(pixels);
            var index = 0;
            var blockEnd = words.Length - (words.Length & 3);
            for (; index < blockEnd; index += 4)
            {
                h0 = (h0 ^ words[index]) * prime;
                h1 = (h1 ^ words[index + 1]) * prime;
                h2 = (h2 ^ words[index + 2]) * prime;
                h3 = (h3 ^ words[index + 3]) * prime;
            }

            for (; index < words.Length; index++)
            {
                h0 = (h0 ^ words[index]) * prime;
            }

            var hash = h0;
            hash = (hash ^ h1) * prime;
            hash = (hash ^ h2) * prime;
            hash = (hash ^ h3) * prime;

            foreach (var value in pixels[(words.Length * sizeof(ulong))..])
            {
                hash = (hash ^ value) * prime;
            }

            return hash;
        }

        private void DumpTextureUpload(
            VulkanGuestDrawTexture texture,
            byte[] pixels,
            uint rowLength,
            uint width,
            uint height)
        {
            if (!_dumpTextures ||
                texture.IsFallback ||
                texture.IsStorage ||
                GetTextureBytesPerPixel(texture.Format) != 4 ||
                width == 0 ||
                height == 0 ||
                !_dumpedTextures.Add((texture.Address, width, height, texture.Format)))
            {
                return;
            }

            var rowBytes = checked((int)rowLength * 4);
            var visibleRowBytes = checked((int)width * 4);
            if (pixels.Length < checked(rowBytes * (int)height))
            {
                return;
            }

            var directory = Path.Combine(AppContext.BaseDirectory, "texture-dumps");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"tex-{texture.Address:X16}-{width}x{height}-fmt{texture.Format}-row{rowLength}.bmp");
            WriteRgbaBmp(path, pixels, rowBytes, visibleRowBytes, (int)width, (int)height);
        }

        private static void WriteRgbaBmp(
            string path,
            byte[] rgba,
            int sourceRowBytes,
            int visibleRowBytes,
            int width,
            int height)
        {
            const int fileHeaderSize = 14;
            const int infoHeaderSize = 40;
            const int bytesPerPixel = 4;
            var pixelBytes = checked(width * height * bytesPerPixel);
            var fileSize = fileHeaderSize + infoHeaderSize + pixelBytes;
            var output = new byte[fileSize];

            output[0] = (byte)'B';
            output[1] = (byte)'M';
            WriteUInt32(output, 2, (uint)fileSize);
            WriteUInt32(output, 10, fileHeaderSize + infoHeaderSize);
            WriteUInt32(output, 14, infoHeaderSize);
            WriteInt32(output, 18, width);
            WriteInt32(output, 22, -height);
            WriteUInt16(output, 26, 1);
            WriteUInt16(output, 28, 32);
            WriteUInt32(output, 34, (uint)pixelBytes);

            var destinationOffset = fileHeaderSize + infoHeaderSize;
            for (var y = 0; y < height; y++)
            {
                var sourceOffset = y * sourceRowBytes;
                for (var x = 0; x < visibleRowBytes; x += bytesPerPixel)
                {
                    var destination = destinationOffset + y * visibleRowBytes + x;
                    output[destination + 0] = rgba[sourceOffset + x + 2];
                    output[destination + 1] = rgba[sourceOffset + x + 1];
                    output[destination + 2] = rgba[sourceOffset + x + 0];
                    output[destination + 3] = rgba[sourceOffset + x + 3];
                }
            }

            File.WriteAllBytes(path, output);
        }

        private static void WriteUInt16(byte[] output, int offset, ushort value)
        {
            output[offset + 0] = (byte)value;
            output[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32(byte[] output, int offset, uint value)
        {
            output[offset + 0] = (byte)value;
            output[offset + 1] = (byte)(value >> 8);
            output[offset + 2] = (byte)(value >> 16);
            output[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteInt32(byte[] output, int offset, int value) =>
            WriteUInt32(output, offset, unchecked((uint)value));

        private Sampler CreateSampler(VulkanGuestSampler sampler)
        {
            if (_samplers.TryGetValue(sampler, out var cachedSampler))
            {
                return cachedSampler;
            }

            var minLod = DecodeSamplerMipFilter(sampler) == 0
                ? 0f
                : DecodeSamplerMinLod(sampler);
            var maxLod = DecodeSamplerMipFilter(sampler) == 0
                ? 0f
                : DecodeSamplerMaxLod(sampler);
            var samplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = ToVkFilter(DecodeSamplerMagFilter(sampler)),
                MinFilter = ToVkFilter(DecodeSamplerMinFilter(sampler)),
                MipmapMode = ToVkMipFilter(DecodeSamplerMipFilter(sampler)),
                AddressModeU = ToVkSamplerAddressMode(DecodeSamplerClampX(sampler)),
                AddressModeV = ToVkSamplerAddressMode(DecodeSamplerClampY(sampler)),
                AddressModeW = ToVkSamplerAddressMode(DecodeSamplerClampZ(sampler)),
                MipLodBias = DecodeSamplerLodBias(sampler),
                CompareEnable = DecodeSamplerDepthCompare(sampler) != 0,
                CompareOp = ToVkCompareOp(DecodeSamplerDepthCompare(sampler)),
                MinLod = minLod,
                MaxLod = Math.Max(minLod, maxLod),
                BorderColor = ToVkBorderColor(DecodeSamplerBorderColor(sampler)),
            };
            Sampler vkSampler;
            Check(
                _vk.CreateSampler(_device, &samplerInfo, null, out vkSampler),
                "vkCreateSampler(texture)");
            _samplers.Add(sampler, vkSampler);
            return vkSampler;
        }

        private static ComponentMapping ToVkComponentMapping(uint dstSelect)
        {
            if (dstSelect == 0)
            {
                dstSelect = 0xFAC;
            }

            return new ComponentMapping(
                ToVkComponentSwizzle(dstSelect & 0x7),
                ToVkComponentSwizzle((dstSelect >> 3) & 0x7),
                ToVkComponentSwizzle((dstSelect >> 6) & 0x7),
                ToVkComponentSwizzle((dstSelect >> 9) & 0x7));
        }

        private static ComponentSwizzle ToVkComponentSwizzle(uint selector) =>
            selector switch
            {
                0 => ComponentSwizzle.Zero,
                1 => ComponentSwizzle.One,
                4 => ComponentSwizzle.R,
                5 => ComponentSwizzle.G,
                6 => ComponentSwizzle.B,
                7 => ComponentSwizzle.A,
                _ => ComponentSwizzle.Identity,
            };

        private static byte[] ExpandRgb32Pixels(byte[] pixels)
        {
            var texelCount = pixels.Length / 12;
            var expanded = new byte[checked(texelCount * 16)];
            for (var texel = 0; texel < texelCount; texel++)
            {
                System.Buffer.BlockCopy(pixels, texel * 12, expanded, texel * 16, 12);
                expanded[texel * 16 + 14] = 0x80;
                expanded[texel * 16 + 15] = 0x3F;
            }

            return expanded;
        }

        private GlobalBufferResource CreateGlobalBufferResource(
            VulkanGuestMemoryBuffer guestBuffer)
        {
            var buffer = CreateHostBuffer(
                guestBuffer.Data,
                BufferUsageFlags.StorageBufferBit,
                out var memory);
            var size = (ulong)Math.Max(guestBuffer.Data.Length, sizeof(uint));

            if (ShouldTraceVulkanResources() &&
                _tracedGlobalBuffers.Add((guestBuffer.BaseAddress, guestBuffer.Data.Length)))
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.global_buffer base=0x{guestBuffer.BaseAddress:X16} " +
                    $"bytes={guestBuffer.Data.Length}");
            }
            SetDebugName(
                ObjectType.Buffer,
                buffer.Handle,
                $"SharpEmu global 0x{guestBuffer.BaseAddress:X16} {guestBuffer.Data.Length}b");

            return new GlobalBufferResource
            {
                Buffer = buffer,
                Memory = memory,
                Size = size,
            };
        }

        private VertexBufferResource CreateVertexBufferResource(
            VulkanGuestVertexBuffer guestBuffer)
        {
            var buffer = CreateHostBuffer(
                guestBuffer.Data,
                BufferUsageFlags.VertexBufferBit,
                out var memory);
            var size = (ulong)Math.Max(guestBuffer.Data.Length, sizeof(uint));
            SetDebugName(
                ObjectType.Buffer,
                buffer.Handle,
                $"SharpEmu vertex loc{guestBuffer.Location} " +
                $"0x{guestBuffer.BaseAddress:X16} {guestBuffer.Data.Length}b");
            if (_tracedVertexBufferCount++ < 64)
            {
                TraceVulkanShader(
                    $"vk.vertex_buffer loc={guestBuffer.Location} " +
                    $"base=0x{guestBuffer.BaseAddress:X16} stride={guestBuffer.Stride} " +
                    $"offset={guestBuffer.OffsetBytes} comps={guestBuffer.ComponentCount} " +
                    $"fmt={guestBuffer.DataFormat}/num={guestBuffer.NumberFormat} " +
                    $"bytes={guestBuffer.Data.Length}");
            }

            return new VertexBufferResource
            {
                Buffer = buffer,
                Memory = memory,
                Size = size,
                Location = guestBuffer.Location,
                ComponentCount = guestBuffer.ComponentCount,
                DataFormat = guestBuffer.DataFormat,
                NumberFormat = guestBuffer.NumberFormat,
                Stride = guestBuffer.Stride,
                OffsetBytes = guestBuffer.OffsetBytes,
            };
        }

        private VkBuffer CreateHostBuffer(
            ReadOnlySpan<byte> data,
            BufferUsageFlags usage,
            out DeviceMemory memory)
        {
            var size = (ulong)Math.Max(data.Length, sizeof(uint));
            var capacity = BitOperations.RoundUpToPowerOf2(size);
            var key = new HostBufferPoolKey(usage, capacity);
            HostBufferAllocation? allocation = null;
            lock (_hostBufferPoolGate)
            {
                if (_hostBufferPool.TryGetValue(key, out var available) &&
                    available.TryPop(out var pooled))
                {
                    allocation = pooled;
                    _pooledHostBufferBytes -= capacity;
                }
            }

            if (allocation is null)
            {
                var buffer = CreateBuffer(
                    capacity,
                    usage,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    out var allocatedMemory);
                allocation = new HostBufferAllocation(buffer, allocatedMemory, key);
                lock (_hostBufferPoolGate)
                {
                    _hostBufferAllocations.Add(buffer.Handle, allocation);
                }
            }

            memory = allocation.Memory;
            void* mapped;
            Check(_vk.MapMemory(_device, memory, 0, size, 0, &mapped), "vkMapMemory(host)");
            try
            {
                fixed (byte* source = data)
                {
                    System.Buffer.MemoryCopy(
                        source,
                        mapped,
                        checked((long)size),
                        data.Length);
                }
            }
            finally
            {
                _vk.UnmapMemory(_device, memory);
            }

            return allocation.Buffer;
        }

        private void RecycleHostBuffer(VkBuffer buffer, DeviceMemory memory)
        {
            if (buffer.Handle == 0)
            {
                return;
            }

            lock (_hostBufferPoolGate)
            {
                if (_hostBufferAllocations.TryGetValue(buffer.Handle, out var allocation) &&
                    allocation.Memory.Handle == memory.Handle)
                {
                    if (_pooledHostBufferBytes + allocation.Key.Capacity <=
                        MaxPooledHostBufferBytes)
                    {
                        _pooledHostBufferBytes += allocation.Key.Capacity;
                        if (!_hostBufferPool.TryGetValue(allocation.Key, out var available))
                        {
                            available = new Stack<HostBufferAllocation>();
                            _hostBufferPool.Add(allocation.Key, available);
                        }

                        available.Push(allocation);
                        return;
                    }

                    // Pool is at capacity; drop this buffer entirely.
                    _hostBufferAllocations.Remove(buffer.Handle);
                }
            }

            _vk.DestroyBuffer(_device, buffer, null);
            FreeDeviceMemory(memory);
        }

        private void TrimHostBufferPool()
        {
            lock (_hostBufferPoolGate)
            {
                foreach (var available in _hostBufferPool.Values)
                {
                    while (available.TryPop(out var allocation))
                    {
                        _hostBufferAllocations.Remove(allocation.Buffer.Handle);
                        _vk.DestroyBuffer(_device, allocation.Buffer, null);
                        FreeDeviceMemory(allocation.Memory);
                    }
                }

                _pooledHostBufferBytes = 0;
            }
        }

        private static PrimitiveTopology GetPrimitiveTopology(uint primitiveType) =>
            primitiveType switch
            {
                1 => PrimitiveTopology.PointList,
                2 => PrimitiveTopology.LineList,
                3 => PrimitiveTopology.LineStrip,
                5 => PrimitiveTopology.TriangleFan,
                6 => PrimitiveTopology.TriangleStrip,
                GuestPrimitiveRectList => PrimitiveTopology.TriangleStrip,
                _ => PrimitiveTopology.TriangleList,
            };

        private static Format ToVkVertexFormat(
            uint dataFormat,
            uint numberFormat,
            uint componentCount) =>
            (dataFormat, numberFormat) switch
            {
                (1, 0) => Format.R8Unorm,
                (1, 1) => Format.R8SNorm,
                (1, 4) => Format.R8Uint,
                (1, 5) => Format.R8Sint,
                (1, 9) => Format.R8Srgb,
                (2, 0) => Format.R16Unorm,
                (2, 1) => Format.R16SNorm,
                (2, 4) => Format.R16Uint,
                (2, 5) => Format.R16Sint,
                (2, 7) => Format.R16Sfloat,
                (3, 0) => Format.R8G8Unorm,
                (3, 1) => Format.R8G8SNorm,
                (3, 4) => Format.R8G8Uint,
                (3, 5) => Format.R8G8Sint,
                (3, 9) => Format.R8G8Srgb,
                (4, 4) => Format.R32Uint,
                (4, 5) => Format.R32Sint,
                (4, 7) => Format.R32Sfloat,
                (5, 0) => Format.R16G16Unorm,
                (5, 1) => Format.R16G16SNorm,
                (5, 2) => Format.R16G16Uscaled,
                (5, 3) => Format.R16G16Sscaled,
                (5, 4) => Format.R16G16Uint,
                (5, 5) => Format.R16G16Sint,
                (5, 7) => Format.R16G16Sfloat,
                (6, 7) => Format.B10G11R11UfloatPack32,
                (7, 7) => Format.B10G11R11UfloatPack32,
                (8, 0) => Format.A2B10G10R10UnormPack32,
                (8, 1) => Format.A2B10G10R10SNormPack32,
                (8, 2) => Format.A2B10G10R10UscaledPack32,
                (8, 3) => Format.A2B10G10R10SscaledPack32,
                (8, 4) => Format.A2B10G10R10UintPack32,
                (8, 5) => Format.A2B10G10R10SintPack32,
                (9, 0) => Format.A2R10G10B10UnormPack32,
                (9, 1) => Format.A2R10G10B10SNormPack32,
                (9, 2) => Format.A2R10G10B10UscaledPack32,
                (9, 3) => Format.A2R10G10B10SscaledPack32,
                (9, 4) => Format.A2R10G10B10UintPack32,
                (9, 5) => Format.A2R10G10B10SintPack32,
                (10, 0) => Format.R8G8B8A8Unorm,
                (10, 1) => Format.R8G8B8A8SNorm,
                (10, 2) => Format.R8G8B8A8Uscaled,
                (10, 3) => Format.R8G8B8A8Sscaled,
                (10, 4) => Format.R8G8B8A8Uint,
                (10, 5) => Format.R8G8B8A8Sint,
                (10, 9) => Format.R8G8B8A8Srgb,
                (11, 4) => Format.R32G32Uint,
                (11, 5) => Format.R32G32Sint,
                (11, 7) => Format.R32G32Sfloat,
                (12, 0) => Format.R16G16B16A16Unorm,
                (12, 1) => Format.R16G16B16A16SNorm,
                (12, 2) => Format.R16G16B16A16Uscaled,
                (12, 3) => Format.R16G16B16A16Sscaled,
                (12, 4) => Format.R16G16B16A16Uint,
                (12, 5) => Format.R16G16B16A16Sint,
                (12, 6) => Format.R16G16B16A16SNorm,
                (12, 7) => Format.R16G16B16A16Sfloat,
                (13, 4) => Format.R32G32B32Uint,
                (13, 5) => Format.R32G32B32Sint,
                (13, 7) => Format.R32G32B32Sfloat,
                (14, 4) => Format.R32G32B32A32Uint,
                (14, 5) => Format.R32G32B32A32Sint,
                (14, 7) => Format.R32G32B32A32Sfloat,
                (16, 0) => Format.B5G6R5UnormPack16,
                (17, 0) => Format.R5G5B5A1UnormPack16,
                (19, 0) => Format.R4G4B4A4UnormPack16,
                (34, 7) => Format.E5B9G9R9UfloatPack32,
                _ => ToVkFloatVertexFormat(componentCount),
            };

        private static Format ToVkFloatVertexFormat(uint componentCount) =>
            componentCount switch
            {
                1 => Format.R32Sfloat,
                2 => Format.R32G32Sfloat,
                3 => Format.R32G32B32Sfloat,
                4 => Format.R32G32B32A32Sfloat,
                _ => Format.R32Sfloat,
            };

        private static ulong GetVertexBindingOffset(VertexBufferResource vertexBuffer)
        {
            if (vertexBuffer.OffsetBytes < vertexBuffer.Size)
            {
                return vertexBuffer.OffsetBytes;
            }

            TraceVulkanShader(
                $"vk.vertex_offset_oob loc={vertexBuffer.Location} " +
                $"offset={vertexBuffer.OffsetBytes} size={vertexBuffer.Size}");
            return 0;
        }

        private static uint GetDrawVertexCount(
            uint primitiveType,
            uint vertexCount,
            VulkanGuestIndexBuffer? indexBuffer)
        {
            if (primitiveType == GuestPrimitiveRectList && indexBuffer is null)
            {
                return 4;
            }

            return vertexCount;
        }

        private static BlendFactor ToVkBlendFactor(uint factor) =>
            factor switch
            {
                0 => BlendFactor.Zero,
                1 => BlendFactor.One,
                2 => BlendFactor.SrcColor,
                3 => BlendFactor.OneMinusSrcColor,
                4 => BlendFactor.SrcAlpha,
                5 => BlendFactor.OneMinusSrcAlpha,
                6 => BlendFactor.DstAlpha,
                7 => BlendFactor.OneMinusDstAlpha,
                8 => BlendFactor.DstColor,
                9 => BlendFactor.OneMinusDstColor,
                10 => BlendFactor.SrcAlphaSaturate,
                13 => BlendFactor.ConstantColor,
                14 => BlendFactor.OneMinusConstantColor,
                15 => BlendFactor.Src1Color,
                16 => BlendFactor.OneMinusSrc1Color,
                17 => BlendFactor.Src1Alpha,
                18 => BlendFactor.OneMinusSrc1Alpha,
                19 => BlendFactor.ConstantAlpha,
                20 => BlendFactor.OneMinusConstantAlpha,
                _ => BlendFactor.One,
            };

        private static BlendOp ToVkBlendOp(uint function) =>
            function switch
            {
                0 => BlendOp.Add,
                1 => BlendOp.Subtract,
                2 => BlendOp.Min,
                3 => BlendOp.Max,
                4 => BlendOp.ReverseSubtract,
                _ => BlendOp.Add,
            };

        private static uint DecodeSamplerClampX(VulkanGuestSampler sampler) =>
            sampler.Word0 & 0x7u;

        private static uint DecodeSamplerClampY(VulkanGuestSampler sampler) =>
            (sampler.Word0 >> 3) & 0x7u;

        private static uint DecodeSamplerClampZ(VulkanGuestSampler sampler) =>
            (sampler.Word0 >> 6) & 0x7u;

        private static uint DecodeSamplerDepthCompare(VulkanGuestSampler sampler) =>
            (sampler.Word0 >> 12) & 0x7u;

        private static float DecodeSamplerMinLod(VulkanGuestSampler sampler) =>
            (sampler.Word1 & 0xFFFu) / 256.0f;

        private static float DecodeSamplerMaxLod(VulkanGuestSampler sampler) =>
            ((sampler.Word1 >> 12) & 0xFFFu) / 256.0f;

        private static float DecodeSamplerLodBias(VulkanGuestSampler sampler)
        {
            var raw = sampler.Word2 & 0x3FFFu;
            var signed = (short)((raw ^ 0x2000u) - 0x2000u);
            return signed / 256.0f;
        }

        private static uint DecodeSamplerMagFilter(VulkanGuestSampler sampler) =>
            (sampler.Word2 >> 20) & 0x3u;

        private static uint DecodeSamplerMinFilter(VulkanGuestSampler sampler) =>
            (sampler.Word2 >> 22) & 0x3u;

        private static uint DecodeSamplerMipFilter(VulkanGuestSampler sampler) =>
            (sampler.Word2 >> 26) & 0x3u;

        private static uint DecodeSamplerBorderColor(VulkanGuestSampler sampler) =>
            (sampler.Word3 >> 30) & 0x3u;

        private static SamplerAddressMode ToVkSamplerAddressMode(uint mode) =>
            mode switch
            {
                0 => SamplerAddressMode.Repeat,
                1 => SamplerAddressMode.MirroredRepeat,
                2 => SamplerAddressMode.ClampToEdge,
                3 or 5 or 7 => SamplerAddressMode.MirrorClampToEdge,
                4 or 6 => SamplerAddressMode.ClampToBorder,
                _ => SamplerAddressMode.ClampToEdge,
            };

        private static Filter ToVkFilter(uint filter) =>
            filter is 1 or 3 ? Filter.Linear : Filter.Nearest;

        private static SamplerMipmapMode ToVkMipFilter(uint filter) =>
            filter == 2 ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest;

        private static CompareOp ToVkCompareOp(uint compare) =>
            compare switch
            {
                1 => CompareOp.Less,
                2 => CompareOp.Equal,
                3 => CompareOp.LessOrEqual,
                4 => CompareOp.Greater,
                5 => CompareOp.NotEqual,
                6 => CompareOp.GreaterOrEqual,
                7 => CompareOp.Always,
                _ => CompareOp.Never,
            };

        private static BorderColor ToVkBorderColor(uint color) =>
            color switch
            {
                1 => BorderColor.FloatTransparentBlack,
                2 => BorderColor.FloatOpaqueWhite,
                _ => BorderColor.FloatOpaqueBlack,
            };

        private static ColorComponentFlags ToVkColorWriteMask(uint mask)
        {
            var flags = default(ColorComponentFlags);
            if ((mask & 1u) != 0)
            {
                flags |= ColorComponentFlags.RBit;
            }

            if ((mask & 2u) != 0)
            {
                flags |= ColorComponentFlags.GBit;
            }

            if ((mask & 4u) != 0)
            {
                flags |= ColorComponentFlags.BBit;
            }

            if ((mask & 8u) != 0)
            {
                flags |= ColorComponentFlags.ABit;
            }

            return flags;
        }

        private static VulkanGuestRect ClampScissor(VulkanGuestRect? scissor, Extent2D extent)
        {
            if (scissor is not { } rect)
            {
                return new VulkanGuestRect(0, 0, extent.Width, extent.Height);
            }

            var left = Math.Clamp(rect.X, 0, checked((int)extent.Width));
            var top = Math.Clamp(rect.Y, 0, checked((int)extent.Height));
            var right = Math.Clamp(
                rect.X + checked((int)rect.Width),
                left,
                checked((int)extent.Width));
            var bottom = Math.Clamp(
                rect.Y + checked((int)rect.Height),
                top,
                checked((int)extent.Height));
            return new VulkanGuestRect(
                left,
                top,
                checked((uint)(right - left)),
                checked((uint)(bottom - top)));
        }

        private static Viewport ClampViewport(VulkanGuestViewport? viewport, Extent2D extent)
        {
            if (viewport is not { } rect)
            {
                return new Viewport(0, 0, extent.Width, extent.Height, 0, 1);
            }

            var maxX = (float)extent.Width;
            var maxY = (float)extent.Height;
            var left = Math.Clamp(rect.X, 0f, maxX);
            var right = Math.Clamp(rect.X + rect.Width, left, maxX);
            var yOrigin = Math.Clamp(rect.Y, 0f, maxY);
            var yEnd = Math.Clamp(rect.Y + rect.Height, 0f, maxY);
            var minDepth = Math.Clamp(rect.MinDepth, 0f, 1f);
            var maxDepth = Math.Clamp(rect.MaxDepth, minDepth, 1f);
            return new Viewport(
                left,
                yOrigin,
                right - left,
                yEnd - yOrigin,
                minDepth,
                maxDepth);
        }

        private static byte[] CreateFallbackTexturePixels(uint format, uint width, uint height, ulong expectedSize)
        {
            if (format is 9 or 10 or 56 or 62 or 64)
            {
                return CreateBlackFrame(width, height);
            }

            return new byte[checked((int)expectedSize)];
        }

        // Texture formats are the legacy dfmt namespace (unified IMG_FORMAT
        // values are converted at descriptor decode by Gfx10UnifiedFormat).
        private static ulong GetTextureBytesPerPixel(uint format) =>
            format switch
            {
                1 => 1UL,
                2 => 2UL,
                3 => 2UL,
                4 => 4UL,
                5 => 4UL,
                6 => 4UL,
                7 => 4UL,
                9 => 4UL,
                10 => 4UL,
                11 => 8UL,
                12 => 8UL,
                13 => 12UL,
                14 => 16UL,
                16 => 2UL,
                17 => 2UL,
                19 => 2UL,
                20 => 4UL,
                34 => 4UL,
                _ => 4UL,
            };

        private static ulong GetTextureByteCount(uint format, uint width, uint height)
        {
            var blockBytes = format switch
            {
                // BC1 (169/170) and BC4 (175/176) are 8 bytes per 4x4 block;
                // BC2/BC3/BC5/BC6H/BC7 are 16 bytes per block.
                169 or 170 or 175 or 176 => 8UL,
                171 or 172 or 173 or 174 or
                177 or 178 or 179 or 180 or 181 or 182 => 16UL,
                _ => 0UL,
            };
            return blockBytes == 0
                ? checked((ulong)width * height * GetTextureBytesPerPixel(format))
                : checked(((ulong)width + 3) / 4 * (((ulong)height + 3) / 4) * blockBytes);
        }

        private bool SupportsColorAttachment(Format format)
        {
            _vk.GetPhysicalDeviceFormatProperties(_physicalDevice, format, out var properties);
            return (properties.OptimalTilingFeatures & FormatFeatureFlags.ColorAttachmentBit) != 0;
        }

        private bool SupportsDepthStencilAttachment(Format format)
        {
            _vk.GetPhysicalDeviceFormatProperties(_physicalDevice, format, out var properties);
            return (properties.OptimalTilingFeatures & FormatFeatureFlags.DepthStencilAttachmentBit) != 0;
        }

        // Sampled-texture formats arrive as the legacy dfmt/nfmt pair (the
        // GFX10 unified IMG_FORMAT is normalized by Gfx10UnifiedFormat at
        // descriptor decode). Canonical guest codes (GuestFormatXxx) are also
        // accepted for deferred/aliased bindings.
        internal static Format GetTextureFormat(uint format, uint numberType) =>
            (format, numberType) switch
            {
                (9, _) => Format.A2R10G10B10UnormPack32,
                (GuestFormatR32Uint, _) => Format.R32Uint,
                (GuestFormatR32Sint, _) => Format.R32Sint,
                (GuestFormatR32Sfloat, _) => Format.R32Sfloat,
                (GuestFormatR16G16Uint, _) => Format.R16G16Uint,
                (GuestFormatR16G16Sint, _) => Format.R16G16Sint,
                (GuestFormatR16G16Sfloat, _) => Format.R16G16Sfloat,
                (GuestFormatR8G8B8A8Uint, _) => Format.R8G8B8A8Uint,
                (GuestFormatR8G8B8A8Sint, _) => Format.R8G8B8A8Sint,
                (GuestFormatR16G16B16A16Uint, _) => Format.R16G16B16A16Uint,
                (GuestFormatR16G16B16A16Sint, _) => Format.R16G16B16A16Sint,
                (1, 9) => Format.R8Srgb,
                (1, 4) => Format.R8Uint,
                (1, 5) => Format.R8Sint,
                (1, _) => Format.R8Unorm,
                (2, 0) => Format.R16Unorm,
                (2, 4) => Format.R16Uint,
                (2, 5) => Format.R16Sint,
                (2, _) => Format.R16Sfloat,
                (3, 9) => Format.R8G8Srgb,
                (3, 4) => Format.R8G8Uint,
                (3, 5) => Format.R8G8Sint,
                (3, _) => Format.R8G8Unorm,
                (4, 4) => Format.R32Uint,
                (4, 5) => Format.R32Sint,
                (4, _) => Format.R32Sfloat,
                (5, 0) => Format.R16G16Unorm,
                (5, 4) => Format.R16G16Uint,
                (5, 5) => Format.R16G16Sint,
                (5, _) => Format.R16G16Sfloat,
                (6, _) => Format.B10G11R11UfloatPack32,
                (7, _) => Format.B10G11R11UfloatPack32,
                (10, 4) => Format.R8G8B8A8Uint,
                (10, 5) => Format.R8G8B8A8Sint,
                (10, 9) => Format.R8G8B8A8Srgb,
                (10, _) => Format.R8G8B8A8Unorm,
                (11, 4) => Format.R32G32Uint,
                (11, 5) => Format.R32G32Sint,
                (11, _) => Format.R32G32Sfloat,
                (12, 0) => Format.R16G16B16A16Unorm,
                (12, 4) => Format.R16G16B16A16Uint,
                (12, 5) => Format.R16G16B16A16Sint,
                (12, _) => Format.R16G16B16A16Sfloat,
                (13, 4) => Format.R32G32B32A32Uint,
                (13, 5) => Format.R32G32B32A32Sint,
                (13, _) => Format.R32G32B32A32Sfloat,
                (14, 4) => Format.R32G32B32A32Uint,
                (14, 5) => Format.R32G32B32A32Sint,
                (14, _) => Format.R32G32B32A32Sfloat,
                (16, 0) => Format.B5G6R5UnormPack16,
                (17, 0) => Format.R5G5B5A1UnormPack16,
                (19, 0) => Format.R4G4B4A4UnormPack16,
                (20, _) => Format.R32Uint,
                (34, 7) => Format.E5B9G9R9UfloatPack32,
                (169, _) => Format.BC1RgbaUnormBlock,
                (170, _) => Format.BC1RgbaSrgbBlock,
                (171, _) => Format.BC2UnormBlock,
                (172, _) => Format.BC2SrgbBlock,
                (173, _) => Format.BC3UnormBlock,
                (174, _) => Format.BC3SrgbBlock,
                (175, 1) => Format.BC4SNormBlock,
                (175, _) => Format.BC4UnormBlock,
                (176, _) => Format.BC4SNormBlock,
                (177, 1) => Format.BC5SNormBlock,
                (177, _) => Format.BC5UnormBlock,
                (178, _) => Format.BC5SNormBlock,
                (179, _) => Format.BC6HUfloatBlock,
                (180, _) => Format.BC6HSfloatBlock,
                (181, _) => Format.BC7UnormBlock,
                (182, _) => Format.BC7SrgbBlock,
                _ => Format.R8G8B8A8Unorm,
            };

        private static bool IsBlockCompressedFormat(Format format) =>
            format is Format.BC1RgbaUnormBlock or
                Format.BC1RgbaSrgbBlock or
                Format.BC2UnormBlock or
                Format.BC2SrgbBlock or
                Format.BC3UnormBlock or
                Format.BC3SrgbBlock or
                Format.BC4UnormBlock or
                Format.BC4SNormBlock or
                Format.BC5UnormBlock or
                Format.BC5SNormBlock or
                Format.BC6HUfloatBlock or
                Format.BC6HSfloatBlock or
                Format.BC7UnormBlock or
                Format.BC7SrgbBlock;

        private static bool IsIntegerFormat(Format format) =>
            format is Format.R8Uint or Format.R8Sint or
                Format.R32Uint or Format.R32Sint or
                Format.R16G16Uint or Format.R16G16Sint or
                Format.R8G8B8A8Uint or Format.R8G8B8A8Sint or
                Format.R16G16B16A16Uint or Format.R16G16B16A16Sint;

        private void TraceVkAlloc(ulong size, string tag)
        {
            var gross = Interlocked.Add(ref _grossVkDeviceAlloc, (long)size);
            var n = Interlocked.Increment(ref _vkAllocCount);
            if (size >= 64UL * 1024 * 1024 || (n & 31) == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][VKMEM] {tag} size={size / (1024 * 1024)}MB gross={gross / (1024 * 1024)}MB " +
                    $"live={_deviceMemoryLedger.LiveDeviceLocalBytes / (1024 * 1024)}MB " +
                    $"live_host={_deviceMemoryLedger.LiveHostVisibleBytes / (1024 * 1024)}MB " +
                    $"alloc#{n} images={_guestImages.Count}");
            }
        }

        private DeviceMemory AllocateDeviceMemory(MemoryAllocateInfo allocationInfo, string tag)
        {
            var deviceLocal = IsDeviceLocalMemoryType(allocationInfo.MemoryTypeIndex);
            if (deviceLocal && _deviceMemoryLedger.WouldExceedBudget(allocationInfo.AllocationSize))
            {
                EvictLeastRecentlyUsedGuestImages(
                    _deviceMemoryLedger.BytesOverBudget(allocationInfo.AllocationSize));
            }
            else if (!deviceLocal &&
                     _deviceMemoryLedger.WouldExceedHostVisibleBudget(allocationInfo.AllocationSize))
            {
                ReclaimHostVisibleMemory();
            }

            TraceVkAlloc(allocationInfo.AllocationSize, tag);
            var result = _vk.AllocateMemory(_device, &allocationInfo, null, out var memory);
            if (result is Result.ErrorOutOfDeviceMemory or Result.ErrorOutOfHostMemory)
            {
                var memoryProperties = _memoryProperties;
                var heapIndex = (&memoryProperties.MemoryTypes.Element0)
                    [(int)allocationInfo.MemoryTypeIndex].HeapIndex;
                Console.Error.WriteLine(
                    $"[LOADER][VKMEM] {tag} alloc size={allocationInfo.AllocationSize / (1024 * 1024)}MB " +
                    $"failed with {result} " +
                    $"type={allocationInfo.MemoryTypeIndex} heap={heapIndex} " +
                    $"deviceLocal={deviceLocal} " +
                    $"pooledHost={_pooledHostBufferBytes / (1024 * 1024)}MB " +
                    $"live={_deviceMemoryLedger.LiveDeviceLocalBytes / (1024 * 1024)}MB " +
                    $"live_host={_deviceMemoryLedger.LiveHostVisibleBytes / (1024 * 1024)}MB; " +
                    "reclaiming and retrying");
                if (!_memoryPanic)
                {
                    // First OOM: the driver is under real memory pressure and
                    // may crash natively on later API calls. Shed all optional
                    // diagnostics readback allocations for the rest of the run.
                    _memoryPanic = true;
                    Console.Error.WriteLine(
                        "[LOADER][VKMEM] memory panic: diagnostics readbacks disabled");
                }

                if (deviceLocal)
                {
                    EvictLeastRecentlyUsedGuestImages(ulong.MaxValue);
                }
                else
                {
                    ReclaimHostVisibleMemory();
                    // Both heaps share the same physical RAM on small hosts:
                    // a host-visible OOM with hundreds of MB of live
                    // device-local images means the driver itself is starved.
                    // Freeing idle images relieves that pressure too.
                    EvictLeastRecentlyUsedGuestImages(ulong.MaxValue);
                }

                result = _vk.AllocateMemory(_device, &allocationInfo, null, out memory);
            }

            Check(result, $"vkAllocateMemory({tag})");
            _deviceMemoryLedger.Track(memory.Handle, allocationInfo.AllocationSize, deviceLocal);
            return memory;
        }

        private void FreeDeviceMemory(DeviceMemory memory)
        {
            if (memory.Handle == 0)
            {
                return;
            }

            _deviceMemoryLedger.Untrack(memory.Handle);
            _vk.FreeMemory(_device, memory, null);
        }

        private void EvictLeastRecentlyUsedGuestImages(ulong bytesToFree)
        {
            if (_reclaimInProgress)
            {
                return;
            }

            _reclaimInProgress = true;
            try
            {
                // In-flight submissions may reference any cached image; drain
                // them before destroying anything (same pattern as the
                // replacement path in GetOrCreateGuestImage). This also retires
                // every pending per-draw resource, freeing transient memory.
                CompletePendingPresentation(wait: true);
                WaitForAllGuestSubmissions();

                ulong freed = 0;
                var evicted = 0;
                foreach (var surface in _guestImages.Surfaces
                             .OrderByDescending(entry => entry.IsCpuBacked)
                             .ThenBy(entry => entry.LastUseStamp)
                             .ToArray())
                {
                    if (freed >= bytesToFree)
                    {
                        break;
                    }

                    if (surface.LastUseStamp >= _useStamp)
                    {
                        // Referenced by the draw currently being translated.
                        // Invariant: _useStamp++ runs at the start of every
                        // dispatch/draw/present, and every image resolved into
                        // a partially-built TranslatedDrawResources goes
                        // through GetOrCreateGuestImage (storage images via
                        // ResolveStorageGuestImage), which stamps
                        // LastUseStamp = _useStamp. So a reclaim triggered by
                        // an allocation mid-build can never free an image the
                        // current resource set references.
                        continue;
                    }

                    if (surface.Initialized && !surface.IsCpuBacked)
                    {
                        // A GPU-produced surface (render target / compute store)
                        // is being reclaimed. If a still-needed producer is
                        // evicted before a later pass samples it, that pass
                        // falls through to a zeroed guest upload and renders
                        // black -- the second candidate mechanism behind the
                        // black menu. Log the address so the loss is provable.
                        Console.Error.WriteLine(
                            "[LOADER][TRACE] vk.producer_evicted " +
                            "addr=0x" + surface.Address.ToString("X16") +
                            " last_use=" + surface.LastUseStamp +
                            " use_stamp=" + _useStamp);
                    }

                    freed += surface.MemorySize;
                    evicted++;
                    DestroyGuestImage(surface);
                    if (_guestImages.Remove(surface))
                    {
                        // Last surface at the address: only now may the
                        // address-level provenance go, or later consumers of a
                        // surviving sibling would fall back to guest memory.
                        lock (_gate)
                        {
                            _availableGuestImages.Remove(surface.Address);
                            _gpuGuestImages.Remove(surface.Address);
                            _renderTargetGuestImages.Remove(surface.Address);
                            ForgetRenderedGuestImageLocked(surface.Address);
                        }
                    }
                }

                Console.Error.WriteLine(
                    $"[LOADER][VKMEM] evicted {evicted} guest images ({freed / (1024 * 1024)}MB) " +
                    $"live={_deviceMemoryLedger.LiveDeviceLocalBytes / (1024 * 1024)}MB " +
                    $"cap={_deviceMemoryLedger.BudgetBytes / (1024 * 1024)}MB " +
                    $"images={_guestImages.Count}");
            }
            finally
            {
                _reclaimInProgress = false;
            }
        }

        private void ReclaimHostVisibleMemory()
        {
            if (_reclaimInProgress)
            {
                return;
            }

            _reclaimInProgress = true;
            try
            {
                // Retiring in-flight submissions recycles the per-draw host
                // buffers and texture staging buffers they still hold into the
                // pool; trimming afterwards actually returns the memory.
                CompletePendingPresentation(wait: true);
                WaitForAllGuestSubmissions();
                TrimHostBufferPool();

                Console.Error.WriteLine(
                    "[LOADER][VKMEM] reclaimed host-visible memory " +
                    $"live_host={_deviceMemoryLedger.LiveHostVisibleBytes / (1024 * 1024)}MB " +
                    $"cap_host={_deviceMemoryLedger.HostVisibleBudgetBytes / (1024 * 1024)}MB");
            }
            finally
            {
                _reclaimInProgress = false;
            }
        }

        private VkBuffer CreateBuffer(
            ulong size,
            BufferUsageFlags usage,
            MemoryPropertyFlags memoryFlags,
            out DeviceMemory memory)
        {
            var bufferInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
            };
            Check(_vk.CreateBuffer(_device, &bufferInfo, null, out var buffer), "vkCreateBuffer");

            _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, memoryFlags),
            };
            try
            {
                memory = AllocateDeviceMemory(memoryInfo, "buffer");
            }
            catch
            {
                _vk.DestroyBuffer(_device, buffer, null);
                throw;
            }

            Check(_vk.BindBufferMemory(_device, buffer, memory, 0), "vkBindBufferMemory");
            return buffer;
        }

        private void CreateStagingBuffer(ulong size)
        {
            _stagingBuffer = CreateBuffer(
                size,
                BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _stagingMemory);
            _stagingSize = size;
        }

        private uint FindMemoryType(uint typeBits, MemoryPropertyFlags requiredFlags)
        {
            _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var properties);
            var memoryTypes = &properties.MemoryTypes.Element0;
            for (uint index = 0; index < properties.MemoryTypeCount; index++)
            {
                if ((typeBits & (1u << (int)index)) != 0 &&
                    (memoryTypes[index].PropertyFlags & requiredFlags) == requiredFlags)
                {
                    return index;
                }
            }

            throw new InvalidOperationException("No compatible Vulkan host-visible memory type was found.");
        }

        private const uint MaxComputeZSlicesPerSubmission = 8;

        private void ExecuteComputeDispatch(VulkanComputeGuestDispatch work)
        {
            if (_deviceLost)
            {
                return;
            }

            _useStamp++;

            if (AddressListContains("SHARPEMU_SKIP_COMPUTE_CS", work.ShaderAddress))
            {
                TraceVulkanShader(
                    $"vk.compute_skip cs=0x{work.ShaderAddress:X16} " +
                    $"groups={work.GroupCountX}x{work.GroupCountY}x{work.GroupCountZ} " +
                    $"textures={work.Textures.Count}");
                return;
            }

            TranslatedDrawResources? resources = null;
            CommandBuffer commandBuffer = default;
            var submitted = false;
            try
            {
                EnsureGuestSubmissionCapacity();
                resources = CreateComputeDispatchResources(work);

                var batchCount = Math.Max(
                    1u,
                    (uint)Math.Ceiling(work.GroupCountZ / (double)MaxComputeZSlicesPerSubmission));

                for (var batchIndex = 0u; batchIndex < batchCount; batchIndex++)
                {
                    var zStart = batchIndex * MaxComputeZSlicesPerSubmission;
                    var zCount = Math.Min(MaxComputeZSlicesPerSubmission, work.GroupCountZ - zStart);
                    var isFirstBatch = batchIndex == 0;
                    var isLastBatch = batchIndex == batchCount - 1;

                    commandBuffer = AllocateGuestCommandBuffer();
                    _commandBuffer = commandBuffer;
                    var beginInfo = new CommandBufferBeginInfo
                    {
                        SType = StructureType.CommandBufferBeginInfo,
                        Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                    };
                    Check(
                        _vk.BeginCommandBuffer(_commandBuffer, &beginInfo),
                        "vkBeginCommandBuffer(compute)");

                    BeginDebugLabel(_commandBuffer, resources.DebugName);
                    if (isFirstBatch)
                    {
                        RecordTextureUploads(resources, PipelineStageFlags.ComputeShaderBit);
                        RecordSampledImageTransitions(resources, PipelineStageFlags.ComputeShaderBit);
                        RecordStorageImagesForWrite(resources, PipelineStageFlags.ComputeShaderBit);
                    }

                    _vk.CmdBindPipeline(
                        _commandBuffer,
                        PipelineBindPoint.Compute,
                        resources.Pipeline);
                    if (resources.DescriptorSet.Handle != 0)
                    {
                        var descriptorSet = resources.DescriptorSet;
                        _vk.CmdBindDescriptorSets(
                            _commandBuffer,
                            PipelineBindPoint.Compute,
                            resources.PipelineLayout,
                            0,
                            1,
                            &descriptorSet,
                            0,
                            null);
                    }

                    RecordChunkedComputeDispatch(_commandBuffer, work, zStart, zCount);

                    if (isLastBatch)
                    {
                        RecordStorageImagesForRead(resources, PipelineStageFlags.ComputeShaderBit);
                    }

                    EndDebugLabel(_commandBuffer);
                    Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer(compute)");

                    TraceVulkanShader(
                        $"vk.compute_submit cs=0x{work.ShaderAddress:X16} " +
                        $"batch={batchIndex}/{batchCount} z={zStart}..{zStart + zCount}");
                    if (isLastBatch)
                    {
                        SubmitGuestCommandBuffer(
                            commandBuffer,
                            resources,
                            GetTraceImages(resources));
                        submitted = true;
                    }
                    else
                    {
                        SubmitGuestCommandBufferAndWait(commandBuffer);
                        commandBuffer = default;
                    }
                }

                MarkSampledImagesInitialized(resources);
                MarkStorageImagesInitialized(resources, traceContents: false);
                TraceVulkanShader(
                    $"vk.compute_dispatch groups={work.GroupCountX}x" +
                    $"{work.GroupCountY}x{work.GroupCountZ} " +
                    $"textures={work.Textures.Count} cs=0x{work.ShaderAddress:X16} " +
                    $"batches={batchCount}");
            }
            catch (Exception exception)
            {
                if (TryMarkDeviceLost(exception))
                {
                    return;
                }

                Console.Error.WriteLine(
                    $"[LOADER][ERROR] Vulkan compute dispatch failed " +
                    $"cs=0x{work.ShaderAddress:X16}: {exception.Message}");
            }
            finally
            {
                _commandBuffer = _presentationCommandBuffer;
                if (!submitted && commandBuffer.Handle != 0)
                {
                    _vk.FreeCommandBuffers(
                        _device,
                        _commandPool,
                        1,
                        &commandBuffer);
                }

                if (!submitted && resources is not null)
                {
                    InvalidateGuestImageLayouts(resources);
                    DestroyTranslatedDrawResources(resources);
                }
            }
        }

        private void RecordChunkedComputeDispatch(
            CommandBuffer commandBuffer,
            VulkanComputeGuestDispatch work,
            uint zStart,
            uint zCount)
        {
            const uint maxWorkgroupsPerCommand = 4096;
            var yChunk = Math.Max(
                1u,
                Math.Min(
                    work.GroupCountY,
                    maxWorkgroupsPerCommand / Math.Max(work.GroupCountX, 1u)));
            var commandCount = 0u;

            for (var z = zStart; z < zStart + zCount; z++)
            {
                for (var y = 0u; y < work.GroupCountY; y += yChunk)
                {
                    var countY = Math.Min(yChunk, work.GroupCountY - y);
                    _vk.CmdDispatchBase(
                        commandBuffer,
                        0,
                        y,
                        z,
                        work.GroupCountX,
                        countY,
                        1);
                    commandCount++;
                }
            }

            if (commandCount > 1)
            {
                TraceVulkanShader(
                    $"vk.compute_chunked cs=0x{work.ShaderAddress:X16} " +
                    $"groups={work.GroupCountX}x{work.GroupCountY}x{work.GroupCountZ} " +
                    $"z_range={zStart}..{zStart + zCount} commands={commandCount} y_chunk={yChunk}");
            }
        }

        private void ExecuteOffscreenDraw(VulkanOffscreenGuestDraw work)
        {
            if (_deviceLost || work.Targets.Count == 0)
            {
                return;
            }

            _useStamp++;

            if (work.Targets.Count > _maxColorAttachments)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan skipped MRT draw requesting {work.Targets.Count} color attachments; " +
                    $"the selected device supports {_maxColorAttachments}.");
                AbandonPendingRenderTargets(work);
                return;
            }

            var targetFormats = new VulkanRenderTargetFormat[work.Targets.Count];
            for (var index = 0; index < targetFormats.Length; index++)
            {
                var target = work.Targets[index];
                if (!TryDecodeRenderTargetFormat(
                        target.Format,
                        target.NumberType,
                        target.ComponentSwap,
                        out targetFormats[index]) ||
                    !SupportsColorAttachment(targetFormats[index].Format))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Vulkan skipped MRT draw with unsupported color target " +
                        $"format={target.Format} number_type={target.NumberType}.");
                    AbandonPendingRenderTargets(work);
                    return;
                }
            }

            if (work.Draw.RenderState.Blends.Count != targetFormats.Length)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] Vulkan skipped MRT draw with mismatched attachment/blend counts.");
                AbandonPendingRenderTargets(work);
                return;
            }

            if (targetFormats
                .Select((format, index) => format.IsInteger && work.Draw.RenderState.Blends[index].Enable)
                .Any(invalid => invalid))
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] Vulkan skipped MRT draw with blending enabled for an integer attachment.");
                AbandonPendingRenderTargets(work);
                return;
            }

            if (!_supportsIndependentBlend &&
                work.Draw.RenderState.Blends.Skip(1).Any(blend => blend != work.Draw.RenderState.Blends[0]))
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] Vulkan skipped MRT draw requiring unsupported independentBlend.");
                AbandonPendingRenderTargets(work);
                return;
            }

            var formats = targetFormats.Select(target => target.Format).ToArray();

            var targetAddresses = work.Targets
                .Where(target => target.Address != 0)
                .Select(target => target.Address)
                .ToHashSet();
            if (work.Draw.Textures.Any(texture =>
                    targetAddresses.Contains(texture.Address)))
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan skipped render-target feedback loop " +
                    $"targets={string.Join(',', targetAddresses.Select(address => $"0x{address:X16}"))}");
                AbandonPendingRenderTargets(work);
                return;
            }

            var (renderWidth, renderHeight) = ComputeMrtRenderExtent(work.Targets);
            var aliasedColorSlots = FindAliasedColorSlots(work.Targets);
            var mismatchedExtents = work.Targets.Any(target =>
                target.Width != renderWidth || target.Height != renderHeight);
            if (mismatchedExtents || aliasedColorSlots is not null)
            {
                var layout = string.Join(
                    ',',
                    work.Targets.Select((target, index) =>
                        $"{index}:0x{target.Address:X16}:" +
                        $"{target.Width}x{target.Height}:" +
                        $"f{target.Format}/n{target.NumberType}"));
                if (_tracedIrregularMrtLayouts.Add(layout))
                {
                    Console.Error.WriteLine(
                        "[LOADER][WARN] Vulkan executing irregular MRT draw " +
                        $"mismatched_extents={mismatchedExtents} " +
                        $"aliased_slots={aliasedColorSlots is not null} " +
                        $"render_extent={renderWidth}x{renderHeight} " +
                        $"targets=[{layout}].");
                }
            }

            var targets = new GuestImageResource[work.Targets.Count];
            EnsureGuestSubmissionCapacity();
            for (var index = 0; index < targets.Length; index++)
            {
                var slotTarget = work.Targets[index];
                if (slotTarget.Address == 0 && work.Depth is not null)
                {
                    // Depth-only pass: no real color target is bound. Render
                    // the (write-masked) color into a cached scratch surface so
                    // the shared offscreen path can drive the depth attachment.
                    slotTarget = slotTarget with
                    {
                        Address = AliasedSlotScratchAddress(
                            0xFF, slotTarget.Width, slotTarget.Height),
                    };
                }
                else if (aliasedColorSlots is not null && aliasedColorSlots[index])
                {
                    // A duplicate-address slot is a don't-care binding on real
                    // hardware. Its writes land in a cached scratch surface so
                    // the draw still executes and the first slot at the address
                    // keeps the authoritative content.
                    slotTarget = slotTarget with
                    {
                        Address = AliasedSlotScratchAddress(
                            index, slotTarget.Width, slotTarget.Height),
                    };
                }

                targets[index] = GetOrCreateGuestImage(slotTarget, formats[index]);
            }

            var firstTarget = targets[0];
            TranslatedDrawResources? resources = null;
            CommandBuffer commandBuffer = default;
            var submitted = false;
            RenderPass transientRenderPass = default;
            Framebuffer transientFramebuffer = default;
            GuestImageResource? depthImage = null;
            try
            {
                var extent = new Extent2D(renderWidth, renderHeight);

                // Resolve the depth attachment (if any). A depth surface at
                // least as large as the color target is bound as the pass's
                // depth attachment and, after the pass, transitioned to a
                // sampleable layout so a later deferred-lighting pass reads
                // real depth at this address.
                VulkanGuestDepthTarget? effectiveDepth = null;
                if (work.Depth is { } depthTarget)
                {
                    var repairedDepth = depthTarget;
                    if (depthTarget.Width < renderWidth ||
                        depthTarget.Height < renderHeight)
                    {
                        // Some Gen5 streams leave DB_DEPTH_SIZE_XY at a stale
                        // clear-state value while binding a full-size depth
                        // surface. Prefer the extent of a bound texture at the
                        // same guest address; failing that, treat an exact 1x1
                        // as clear-state and adopt the color target extent.
                        // Taking the stale value literally silently drops the
                        // depth test for the whole draw.
                        var matchingTexture = work.Draw.Textures.FirstOrDefault(
                            texture =>
                                texture.Address == depthTarget.Address &&
                                texture.Width >= renderWidth &&
                                texture.Height >= renderHeight);
                        if (matchingTexture is not null)
                        {
                            repairedDepth = depthTarget with
                            {
                                Width = matchingTexture.Width,
                                Height = matchingTexture.Height,
                            };
                        }
                        else if (depthTarget is { Width: 1, Height: 1 })
                        {
                            repairedDepth = depthTarget with
                            {
                                Width = renderWidth,
                                Height = renderHeight,
                            };
                        }

                        if (!ReferenceEquals(repairedDepth, depthTarget) &&
                            _tracedDepthExtentRepairs.Add(
                                (depthTarget.Address,
                                 repairedDepth.Width,
                                 repairedDepth.Height)))
                        {
                            Console.Error.WriteLine(
                                $"[LOADER][WARN] Vulkan repaired stale guest depth extent " +
                                $"addr=0x{depthTarget.Address:X16} " +
                                $"{depthTarget.Width}x{depthTarget.Height} -> " +
                                $"{repairedDepth.Width}x{repairedDepth.Height}");
                        }
                    }

                    if (repairedDepth.Width >= renderWidth &&
                        repairedDepth.Height >= renderHeight)
                    {
                        // A read-only depth view must never be written even when
                        // DB_DEPTH_CONTROL still carries Z_WRITE_ENABLE.
                        if (repairedDepth.ReadOnly && repairedDepth.WriteEnable)
                        {
                            repairedDepth = repairedDepth with { WriteEnable = false };
                        }

                        depthImage = GetOrCreateGuestDepthImage(repairedDepth);
                        effectiveDepth = repairedDepth;
                    }
                }

                // DB_RENDER_CONTROL.DEPTH_CLEAR_ENABLE makes this a DB clear
                // operation: clear the attachment to the guest clear value and
                // run the draw itself without depth test/write (its interpolated
                // vertex Z is not the guest clear value).
                var clearDepthForDraw = effectiveDepth is { ClearEnable: true };
                var clearDepth =
                    depthImage is { Initialized: false } || clearDepthForDraw;
                var clearDepthValue = effectiveDepth?.ClearDepth ?? 1.0f;
                if (depthImage is { Initialized: false } &&
                    !clearDepthForDraw &&
                    effectiveDepth is { } firstUseDepth)
                {
                    // First use without an explicit guest clear: initialize the
                    // surface to the compare-neutral value for the draw's ZFUNC
                    // so the depth test does not reject every fragment (1.0
                    // rejects everything under reverse-Z Greater tests).
                    clearDepthValue = SelectFirstUseDepthClearValue(
                        firstUseDepth.CompareOp,
                        firstUseDepth.ClearDepth);
                }
                if (clearDepthForDraw && effectiveDepth is not null)
                {
                    effectiveDepth = effectiveDepth with
                    {
                        TestEnable = false,
                        WriteEnable = false,
                        ClearEnable = false,
                    };
                }

                var renderPass = firstTarget.RenderPass;
                var framebuffer = firstTarget.Framebuffer;
                // The cached per-target render pass has no depth attachment, so
                // any draw with depth must assemble a transient pass that pairs
                // the color targets with the depth surface.
                if (targets.Length > 1 || depthImage is not null)
                {
                    (renderPass, framebuffer) = CreateRenderPassAndFramebuffer(
                        formats,
                        targets.Select(target => target.AttachmentView).ToArray(),
                        renderWidth,
                        renderHeight,
                        depthImage?.Format,
                        depthImage?.View ?? default,
                        clearDepth);
                    transientRenderPass = renderPass;
                    transientFramebuffer = framebuffer;
                }

                resources = CreateTranslatedDrawResources(
                    work.Draw,
                    renderPass,
                    formats,
                    extent,
                    effectiveDepth);
                resources.TransientRenderPass = transientRenderPass;
                resources.TransientFramebuffer = transientFramebuffer;
                transientRenderPass = default;
                transientFramebuffer = default;
                resources.DebugName =
                    $"SharpEmu offscreen mrt={targets.Length} " +
                    $"first=0x{work.Targets[0].Address:X16} " +
                    $"{renderWidth}x{renderHeight}";

                commandBuffer = AllocateGuestCommandBuffer();
                _commandBuffer = commandBuffer;
                var beginInfo = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                Check(
                    _vk.BeginCommandBuffer(_commandBuffer, &beginInfo),
                    "vkBeginCommandBuffer(offscreen)");

                BeginDebugLabel(_commandBuffer, resources.DebugName);
                // Translated draws sample textures from the vertex (export)
                // stage as well as the fragment stage; a depth-only pass may
                // sample exclusively from the vertex stage.
                RecordTextureUploads(
                    resources,
                    PipelineStageFlags.VertexShaderBit |
                        PipelineStageFlags.FragmentShaderBit);
                RecordSampledImageTransitions(
                    resources,
                    PipelineStageFlags.VertexShaderBit |
                        PipelineStageFlags.FragmentShaderBit);
                RecordStorageImagesForWrite(resources, PipelineStageFlags.FragmentShaderBit);

                foreach (var target in targets)
                {
                    TransitionGuestImage(
                        target,
                        ImageLayout.ColorAttachmentOptimal,
                        PipelineStageFlags.ColorAttachmentOutputBit,
                        AccessFlags.ColorAttachmentWriteBit);
                }

                if (depthImage is not null)
                {
                    TransitionGuestImage(
                        depthImage,
                        ImageLayout.DepthStencilAttachmentOptimal,
                        PipelineStageFlags.EarlyFragmentTestsBit |
                            PipelineStageFlags.LateFragmentTestsBit,
                        AccessFlags.DepthStencilAttachmentWriteBit |
                            AccessFlags.DepthStencilAttachmentReadBit);
                }

                if (resources.CaptureInvocationCount > 0)
                {
                    RecordNggCaptureDispatch(resources);
                    AgcExports.TraceNgg(
                        $"vk.ngg_compute_draw invocations={resources.CaptureInvocationCount} " +
                        $"outBytes={(ulong)resources.CaptureInvocationCount * 16} " +
                        $"target=0x{work.Targets[0].Address:X16} " +
                        $"{renderWidth}x{renderHeight}");
                }

                RecordTranslatedGraphicsPass(
                    resources,
                    renderPass,
                    framebuffer,
                    extent,
                    depthImage is not null,
                    clearDepthValue);
                RecordStorageImagesForRead(resources, PipelineStageFlags.FragmentShaderBit);

                // NGG debug: copy the just-drawn target into a host buffer in THIS
                // command buffer, so a later pass overwriting it can't hide whether
                // the capture geometry rasterized. SHARPEMU_TRACE_DRAW_TARGETS
                // widens this to EVERY offscreen draw so we can see, per draw,
                // which target first gains real (RGB) content vs stays black.
                var traceAllDrawTargets = string.Equals(
                    Environment.GetEnvironmentVariable("SHARPEMU_TRACE_DRAW_TARGETS"),
                    "1",
                    StringComparison.Ordinal);
                if ((traceAllDrawTargets ||
                        (resources.CaptureInvocationCount > 0 &&
                            string.Equals(
                                Environment.GetEnvironmentVariable("SHARPEMU_NGG_DEBUG"),
                                "1",
                                StringComparison.Ordinal))) &&
                    targets.Length > 0)
                {
                    var t = targets[0];
                    var bpp = GetReadbackBytesPerPixel(t.Format);
                    if (bpp != 0)
                    {
                        var size = (ulong)t.Width * t.Height * bpp;
                        resources.CaptureTargetReadbackBuffer = CreateBuffer(
                            size,
                            BufferUsageFlags.TransferDstBit,
                            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                            out resources.CaptureTargetReadbackMemory);
                        resources.CaptureTargetReadbackSize = size;
                        resources.CaptureTargetBpp = bpp;
                        resources.CaptureTargetAddress = t.Address;
                        resources.CaptureTargetWidth = t.Width;
                        resources.CaptureTargetHeight = t.Height;
                        TransitionGuestImage(
                            t,
                            ImageLayout.TransferSrcOptimal,
                            PipelineStageFlags.TransferBit,
                            AccessFlags.TransferReadBit);
                        var region = new BufferImageCopy
                        {
                            ImageSubresource = new ImageSubresourceLayers
                            {
                                AspectMask = ImageAspectFlags.ColorBit,
                                MipLevel = 0,
                                BaseArrayLayer = 0,
                                LayerCount = 1,
                            },
                            ImageExtent = new Extent3D(t.Width, t.Height, 1),
                        };
                        _vk.CmdCopyImageToBuffer(
                            _commandBuffer,
                            t.Image,
                            ImageLayout.TransferSrcOptimal,
                            resources.CaptureTargetReadbackBuffer,
                            1,
                            &region);
                    }
                }

                foreach (var target in targets)
                {
                    TransitionGuestImage(
                        target,
                        ImageLayout.ShaderReadOnlyOptimal,
                        PipelineStageFlags.FragmentShaderBit,
                        AccessFlags.ShaderReadBit);
                }

                if (depthImage is not null)
                {
                    // Make the just-written depth visible to later passes that
                    // sample this address as a texture.
                    TransitionGuestImage(
                        depthImage,
                        ImageLayout.ShaderReadOnlyOptimal,
                        PipelineStageFlags.FragmentShaderBit,
                        AccessFlags.ShaderReadBit);
                }
                EndDebugLabel(_commandBuffer);

                Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer(offscreen)");
                SubmitGuestCommandBuffer(
                    commandBuffer,
                    resources,
                    GetTraceImages(resources, targets));
                submitted = true;
                foreach (var target in targets)
                {
                    target.Initialized = true;
                    MarkGuestImageContentWritten(target);
                }

                if (depthImage is not null)
                {
                    depthImage.Initialized = true;
                    depthImage.LastUseStamp = _useStamp;
                    // Register the depth address as a live GPU producer so a
                    // later pass sampling it resolves to this rendered surface
                    // instead of falling through to a zeroed guest upload.
                    lock (_gate)
                    {
                        _availableGuestImages[depthImage.Address] = GuestFormatR32Sfloat;
                        _gpuGuestImages[depthImage.Address] = GuestFormatR32Sfloat;
                        _renderTargetGuestImages[depthImage.Address] = GuestFormatR32Sfloat;
                        NoteRenderedGuestImageLocked(
                            depthImage.Address,
                            depthImage.Width,
                            depthImage.Height,
                            work.Draw.Textures.Count);
                    }
                }
                MarkSampledImagesInitialized(resources);
                MarkStorageImagesInitialized(resources, traceContents: false);

                if (CaptureDrawsEnabled)
                {
                    CaptureDrawContents(work, targets, resources);
                }

                if (work.PublishTarget)
                {
                    for (var index = 0; index < targets.Length; index++)
                    {
                        // A duplicate-address slot rendered into scratch never
                        // becomes guest-visible content; publishing its
                        // synthetic address would pollute the flip election.
                        if (aliasedColorSlots is not null && aliasedColorSlots[index])
                        {
                            continue;
                        }

                        var guestTextureFormat = VulkanVideoPresenter.GetGuestTextureFormat(
                            work.Targets[index].Format,
                            work.Targets[index].NumberType);
                        lock (_gate)
                        {
                            // Always publish producer presence -- a target whose
                            // guest format has no canonical mapping (0) still
                            // holds real rendered content that later consumers
                            // must defer to instead of uploading empty guest
                            // memory. Format-keyed registries (flip election)
                            // stay limited to known formats.
                            _gpuGuestImages[targets[index].Address] = guestTextureFormat;
                            _renderTargetGuestImages[targets[index].Address] = guestTextureFormat;
                            if (guestTextureFormat != 0)
                            {
                                _availableGuestImages[targets[index].Address] = guestTextureFormat;
                                NoteRenderedGuestImageLocked(
                                    targets[index].Address,
                                    targets[index].Width,
                                    targets[index].Height,
                                    work.Draw.Textures.Count);
                            }
                        }
                    }
                }

                foreach (var target in targets)
                {
                    if (ShouldTraceGuestImageWriteForDiagnostics(target.Address))
                    {
                        var writeCount = _tracedGuestWriteCounts.TryGetValue(
                            target.Address,
                            out var previousCount)
                            ? previousCount + 1
                            : 1;
                        _tracedGuestWriteCounts[target.Address] = writeCount;
                        if (writeCount <= 3)
                        {
                            _commandBuffer = _presentationCommandBuffer;
                            Check(
                                _vk.QueueWaitIdle(_queue),
                                "vkQueueWaitIdle(guest write trace)");
                            Console.Error.WriteLine(
                                $"[LOADER][TRACE] vk.guest_write_sample " +
                                $"addr=0x{target.Address:X16} write={writeCount} " +
                                $"ps_bytes={work.Draw.PixelSpirv.Length}");
                            TraceGuestImageContents(target);
                        }
                    }
                }
                TraceVulkanShader(
                    $"vk.offscreen_draw mrt={targets.Length} " +
                    $"size={renderWidth}x{renderHeight} " +
                    $"textures={work.Draw.Textures.Count}");
            }
            catch (Exception exception)
            {
                if (TryMarkDeviceLost(exception))
                {
                    return;
                }

                lock (_gate)
                {
                    foreach (var target in work.Targets)
                    {
                        if (!_guestImages.HasInitializedSurface(target.Address))
                        {
                            _availableGuestImages.Remove(target.Address);
                            _gpuGuestImages.Remove(target.Address);
                            _renderTargetGuestImages.Remove(target.Address);
                            ForgetRenderedGuestImageLocked(target.Address);

                            // This target never produced content and its
                            // provenance is now gone: any later pass that
                            // samples this address falls through to a zeroed
                            // guest upload and renders black. Surfacing the
                            // exact dropped producer address is what pins the
                            // black-menu root cause (missing producer) to a
                            // specific G-buffer.
                            Console.Error.WriteLine(
                                "[LOADER][TRACE] vk.producer_dropped_on_throw " +
                                "addr=0x" + target.Address.ToString("X16"));
                        }
                    }
                }

                var failedTargetAddresses = string.Join(
                    ",",
                    work.Targets.Select(t => "0x" + t.Address.ToString("X16")));
                Console.Error.WriteLine(
                    $"[LOADER][ERROR] Vulkan offscreen draw failed " +
                    $"mrt={work.Targets.Count} targets=[{failedTargetAddresses}]: " +
                    $"{exception.Message}");

                if (exception.Message.Contains(
                        nameof(Result.ErrorOutOfDeviceMemory),
                        StringComparison.Ordinal))
                {
                    // Free everything idle so the game's re-submission of this
                    // draw next frame can succeed instead of staying blank.
                    EvictLeastRecentlyUsedGuestImages(ulong.MaxValue);
                }

                // Dump the offending SPIR-V once per distinct shader so a failing
                // translation can be disassembled/validated offline. Base64 keeps
                // it inside the (uploaded) stderr log without touching disk.
                if (work.Targets.Count > 0)
                {
                    var dumpAddress = work.Targets[0].Address;
                    bool firstFailure;
                    lock (_gate)
                    {
                        firstFailure = _dumpedFailedDrawShaders.Add(dumpAddress);
                    }

                    if (firstFailure)
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][SPIRVDUMP] addr=0x{dumpAddress:X16} " +
                            $"vs_bytes={work.Draw.VertexSpirv.Length} " +
                            $"ps_bytes={work.Draw.PixelSpirv.Length}");
                        Console.Error.WriteLine(
                            $"[LOADER][SPIRVDUMP] addr=0x{dumpAddress:X16} vs_b64={Convert.ToBase64String(work.Draw.VertexSpirv)}");
                        Console.Error.WriteLine(
                            $"[LOADER][SPIRVDUMP] addr=0x{dumpAddress:X16} ps_b64={Convert.ToBase64String(work.Draw.PixelSpirv)}");
                    }
                }
            }
            finally
            {
                _commandBuffer = _presentationCommandBuffer;
                if (!submitted && commandBuffer.Handle != 0)
                {
                    _vk.FreeCommandBuffers(
                        _device,
                        _commandPool,
                        1,
                        &commandBuffer);
                }

                if (!submitted && resources is not null)
                {
                    InvalidateGuestImageLayouts(resources, targets);
                    DestroyTranslatedDrawResources(resources);
                }

                if (!submitted && depthImage is not null)
                {
                    InvalidateGuestImageLayout(depthImage);
                }

                if (transientFramebuffer.Handle != 0)
                {
                    _vk.DestroyFramebuffer(_device, transientFramebuffer, null);
                }

                if (transientRenderPass.Handle != 0)
                {
                    _vk.DestroyRenderPass(_device, transientRenderPass, null);
                }
            }
        }

        // Ordered-flip capture. Runs on the render thread in guest-queue order:
        // freezes the resolved source render target into an immutable snapshot
        // with a NO-WAIT guest-queue submit (never QueueWaitIdle / submit-and-
        // wait), then publishes a Presentation carrying the snapshot's version.
        private void ExecuteOrderedGuestFlip(VulkanOrderedGuestFlip work)
        {
            GuestImageResource? source;
            long requiredWorkSequence;
            lock (_gate)
            {
                _guestImages.TryFindPresentable(work.Address, out source!);
                requiredWorkSequence = _enqueuedGuestWorkSequence;
            }

            if (_deviceLost || source is null || !source.Initialized)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] vk.flip_capture_failed version={work.Version} " +
                    $"addr=0x{work.Address:X16} found={(source is not null)} " +
                    $"initialized={(source?.Initialized ?? false)}");
                return;
            }

            EnsureGuestSubmissionCapacity();
            var snapshot = CreateGuestFlipSnapshot(source, work.Version);
            var commandBuffer = AllocateGuestCommandBuffer();
            var submitted = false;
            // Barrier OldLayout must be the source's ACTUAL tracked layout (it may
            // rest in ColorAttachmentOptimal or ShaderReadOnlyOptimal), never a
            // hardcoded guess; TransitionGuestImage reads CurrentLayout, and we
            // restore exactly so later recorded work sees consistent tracking.
            var originalLayout = source.CurrentLayout;
            var originalStage = source.LastStageMask;
            var originalAccess = source.LastAccessMask;
            try
            {
                _commandBuffer = commandBuffer;
                var beginInfo = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                Check(
                    _vk.BeginCommandBuffer(commandBuffer, &beginInfo),
                    "vkBeginCommandBuffer(flip capture)");

                TransitionGuestImage(
                    source,
                    ImageLayout.TransferSrcOptimal,
                    PipelineStageFlags.TransferBit,
                    AccessFlags.TransferReadBit);
                TransitionGuestImage(
                    snapshot,
                    ImageLayout.TransferDstOptimal,
                    PipelineStageFlags.TransferBit,
                    AccessFlags.TransferWriteBit);

                var copy = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers(
                        ImageAspectFlags.ColorBit, 0, 0, 1),
                    DstSubresource = new ImageSubresourceLayers(
                        ImageAspectFlags.ColorBit, 0, 0, 1),
                    Extent = new Extent3D(source.Width, source.Height, 1),
                };
                _vk.CmdCopyImage(
                    commandBuffer,
                    source.Image,
                    ImageLayout.TransferSrcOptimal,
                    snapshot.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &copy);

                // Restore the live target and make the snapshot presentable.
                TransitionGuestImage(source, originalLayout, originalStage, originalAccess);
                TransitionGuestImage(
                    snapshot,
                    ImageLayout.ShaderReadOnlyOptimal,
                    PipelineStageFlags.VertexShaderBit |
                        PipelineStageFlags.FragmentShaderBit |
                        PipelineStageFlags.ComputeShaderBit,
                    AccessFlags.ShaderReadBit);

                Check(
                    _vk.EndCommandBuffer(commandBuffer),
                    "vkEndCommandBuffer(flip capture)");
                // NO-WAIT submit: the snapshot copy and the later present blit run
                // on the same queue, so submission order alone guarantees the copy
                // completes first -- no QueueWaitIdle / SubmitAndWait on this path.
                SubmitGuestCommandBuffer(commandBuffer, new TranslatedDrawResources(), []);
                submitted = true;
                snapshot.Initialized = true;
                _guestImageVersions.Add(work.Version, snapshot);
                _capturedGuestFlipVersions.Add(work.Version);

                lock (_gate)
                {
                    // An actively playing AvPlayer video owns the swapchain; skip
                    // the game's concurrent flip so the clip is not overwritten.
                    // The captured snapshot is reclaimed below as an abandoned
                    // version, so nothing leaks when we do not present it.
                    if (!VideoPresentIsActive())
                    {
                        var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
                        _latestPresentation = new Presentation(
                            null,
                            work.Width,
                            work.Height,
                            sequence,
                            GuestDrawKind.None,
                            TranslatedDraw: null,
                            RequiredGuestWorkSequence: requiredWorkSequence,
                            IsSplash: false,
                            GuestImageAddress: work.Address,
                            GuestImageVersion: work.Version);
                        System.Threading.Monitor.PulseAll(_gate);
                    }
                }

                CollectAbandonedGuestImageVersions();

                if (_guestFlipCaptureTraceCount < 200)
                {
                    _guestFlipCaptureTraceCount++;
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.flip_capture version={work.Version} " +
                        $"addr=0x{work.Address:X16} layout={originalLayout} " +
                        $"size={source.Width}x{source.Height} fmt={source.Format}");
                }
            }
            finally
            {
                _commandBuffer = _presentationCommandBuffer;
                if (!submitted)
                {
                    if (commandBuffer.Handle != 0)
                    {
                        _vk.FreeCommandBuffers(_device, _commandPool, 1, &commandBuffer);
                    }

                    DestroyGuestImage(snapshot);
                }
            }
        }

        // DEVICE_LOCAL TransferSrc|TransferDst|Sampled image matching the source
        // render target's format and extent. Owns its own image + memory; not
        // registered in _guestImages, so only the ordered-flip lifecycle frees it.
        private GuestImageResource CreateGuestFlipSnapshot(
            GuestImageResource source,
            long version)
        {
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = source.Format,
                Extent = new Extent3D(source.Width, source.Height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.TransferSrcBit |
                        ImageUsageFlags.TransferDstBit |
                        ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(
                _vk.CreateImage(_device, &imageInfo, null, out var image),
                "vkCreateImage(flip snapshot)");
            _vk.GetImageMemoryRequirements(_device, image, out var requirements);
            var allocationInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            DeviceMemory memory = default;
            try
            {
                memory = AllocateDeviceMemory(allocationInfo, "flip snapshot");
                Check(
                    _vk.BindImageMemory(_device, image, memory, 0),
                    "vkBindImageMemory(flip snapshot)");
            }
            catch
            {
                if (memory.Handle != 0)
                {
                    FreeDeviceMemory(memory);
                }

                _vk.DestroyImage(_device, image, null);
                throw;
            }

            SetDebugName(
                ObjectType.Image,
                image.Handle,
                $"guest flip v{version} source 0x{source.Address:X16}");
            return new GuestImageResource
            {
                Address = source.Address,
                Width = source.Width,
                Height = source.Height,
                MipLevels = 1,
                Format = source.Format,
                Image = image,
                Memory = memory,
                MemorySize = allocationInfo.AllocationSize,
                CurrentLayout = ImageLayout.Undefined,
                LastUseStamp = _useStamp,
            };
        }

        // Only the newest captured version becomes _latestPresentation; when the
        // guest flips faster than we present, older captured versions strand in
        // the dict. Retire all but the newest MaxPendingGuestFlipVersions so 4K
        // snapshots cannot leak. Ownership transfers to the retire list, which is
        // freed only after a queue drain (RetireGuestFlipSnapshotLater).
        private void CollectAbandonedGuestImageVersions()
        {
            if (_guestImageVersions.Count <= MaxPendingGuestFlipVersions)
            {
                return;
            }

            var versions = _guestImageVersions.Keys.ToArray();
            Array.Sort(versions);
            var removeCount = versions.Length - MaxPendingGuestFlipVersions;
            for (var i = 0; i < removeCount; i++)
            {
                var version = versions[i];
                if (_guestImageVersions.Remove(version, out var abandoned))
                {
                    _capturedGuestFlipVersions.Remove(version);
                    RetireGuestFlipSnapshotLater(abandoned);
                }
            }
        }

        // Defers a snapshot's destruction until the next queue drain: its copy
        // (and, if it was presented, its blit) were submitted on _queue, so it
        // must not be freed until a vkQueueWaitIdle has retired that GPU work.
        private void RetireGuestFlipSnapshotLater(GuestImageResource snapshot)
        {
            _retiringGuestFlipSnapshots.Add(snapshot);
        }

        // Must be called only when the queue is known idle (immediately after a
        // vkQueueWaitIdle, or after a device-wait-idle on teardown).
        private void DrainRetiredGuestFlipSnapshots()
        {
            if (_retiringGuestFlipSnapshots.Count == 0)
            {
                return;
            }

            foreach (var snapshot in _retiringGuestFlipSnapshots)
            {
                DestroyGuestImage(snapshot);
            }

            _retiringGuestFlipSnapshots.Clear();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        // Lazily builds a 1x1 R16G16B16A16Sfloat image cleared to a constant
        // (SHARPEMU_FORCE_EXPOSURE). Bound in place of the game's never-written
        // auto-exposure luminance so the tonemap gets a sane exposure. Cleared
        // once on a self-contained transient command buffer (the member
        // _commandBuffer is busy while a draw is resolving its textures).
        private unsafe GuestImageResource EnsureForcedExposureImage(float value)
        {
            if (_forcedExposureImage is { } cached)
            {
                return cached;
            }

            const ulong forcedExposureAddress = 0xFFFF_FF00_0000_0000UL;
            var target = new VulkanGuestRenderTarget(forcedExposureAddress, 1, 1, 0, 0);
            var image = GetOrCreateGuestImage(target, Format.R16G16B16A16Sfloat);

            var commandBuffer = AllocateGuestCommandBuffer();
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Check(
                _vk.BeginCommandBuffer(commandBuffer, &beginInfo),
                "vkBeginCommandBuffer(forced exposure)");

            if (TryPlanGuestImageTransition(
                    image,
                    ImageLayout.TransferDstOptimal,
                    PipelineStageFlags.TransferBit,
                    AccessFlags.TransferWriteBit,
                    out var toTransfer,
                    out var srcToTransfer))
            {
                var src = srcToTransfer == 0 ? PipelineStageFlags.TopOfPipeBit : srcToTransfer;
                _vk.CmdPipelineBarrier(
                    commandBuffer, src, PipelineStageFlags.TransferBit, 0,
                    0, null, 0, null, 1, &toTransfer);
            }

            var clearColor = new ClearColorValue(value, value, value, value);
            var range = ColorSubresourceRange(0, image.MipLevels);
            _vk.CmdClearColorImage(
                commandBuffer, image.Image, ImageLayout.TransferDstOptimal,
                &clearColor, 1, &range);

            const PipelineStageFlags shaderStages =
                PipelineStageFlags.VertexShaderBit |
                PipelineStageFlags.FragmentShaderBit |
                PipelineStageFlags.ComputeShaderBit;
            if (TryPlanGuestImageTransition(
                    image,
                    ImageLayout.ShaderReadOnlyOptimal,
                    shaderStages,
                    AccessFlags.ShaderReadBit,
                    out var toRead,
                    out var srcToRead))
            {
                var src = srcToRead == 0 ? PipelineStageFlags.TopOfPipeBit : srcToRead;
                _vk.CmdPipelineBarrier(
                    commandBuffer, src, shaderStages, 0,
                    0, null, 0, null, 1, &toRead);
            }

            Check(
                _vk.EndCommandBuffer(commandBuffer),
                "vkEndCommandBuffer(forced exposure)");
            SubmitGuestCommandBufferAndWait(commandBuffer);
            Console.Error.WriteLine(
                $"[LOADER][INFO] forced_exposure luminance image ready value={value}");
            _forcedExposureImage = image;
            return image;
        }

        private GuestImageResource GetOrCreateGuestImage(
            VulkanGuestRenderTarget target,
            Format format)
        {
            var mipLevels = ClampMipLevels(target.Width, target.Height, target.MipLevels);
            if (_guestImages.TryGetExact(target.Address, format, out var existing))
            {
                if (existing.Width == target.Width &&
                    existing.Height == target.Height &&
                    existing.MipLevels == mipLevels)
                {
                    existing.IsCpuBacked = false;
                    existing.CpuContentFingerprint = 0;
                    existing.LastUseStamp = _useStamp;
                    if (existing.RenderPass.Handle == 0)
                    {
                        var (promotedRenderPass, promotedFramebuffer) = CreateRenderPassAndFramebuffer(
                            existing.Format,
                            existing.AttachmentView,
                            existing.Width,
                            existing.Height);
                        existing.RenderPass = promotedRenderPass;
                        existing.Framebuffer = promotedFramebuffer;
                        var promotedName = GuestImageDebugName(target, format);
                        SetDebugName(ObjectType.RenderPass, promotedRenderPass.Handle, $"{promotedName} renderpass");
                        SetDebugName(ObjectType.Framebuffer, promotedFramebuffer.Handle, $"{promotedName} framebuffer");
                    }

                    _guestImages.Promote(existing);
                    return existing;
                }

                // Same-format geometry change: a genuine reallocation of this
                // surface. Different-format siblings at the address stay
                // alive -- a later pass sampling their format still binds real
                // rendered content -- and the address-level provenance
                // survives because the surface is recreated immediately below.
                CompletePendingPresentation(wait: true);
                WaitForAllGuestSubmissions();
                DestroyGuestImage(existing);
                _guestImages.Remove(existing);
            }

            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags =
                    ImageCreateFlags.CreateMutableFormatBit |
                    ImageCreateFlags.CreateExtendedUsageBit,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D(target.Width, target.Height, 1),
                MipLevels = mipLevels,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage =
                    ImageUsageFlags.ColorAttachmentBit |
                    ImageUsageFlags.SampledBit |
                    ImageUsageFlags.StorageBit |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out var image), "vkCreateImage(offscreen)");
            _vk.GetImageMemoryRequirements(_device, image, out var requirements);
            var allocationInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            var memory = AllocateDeviceMemory(allocationInfo, "offscreen");
            Check(_vk.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory(offscreen)");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = format,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity),
                SubresourceRange = ColorSubresourceRange(0, mipLevels),
            };
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out var view),
                "vkCreateImageView(offscreen)");

            var mipViews = new ImageView[mipLevels];
            for (uint mipLevel = 0; mipLevel < mipLevels; mipLevel++)
            {
                viewInfo.SubresourceRange = ColorSubresourceRange(mipLevel, 1);
                ImageView mipView;
                Check(
                    _vk.CreateImageView(
                        _device,
                        &viewInfo,
                        null,
                        out mipView),
                    "vkCreateImageView(offscreen mip)");
                mipViews[mipLevel] = mipView;
            }

            var (renderPass, framebuffer) = CreateRenderPassAndFramebuffer(
                format,
                mipViews[0],
                target.Width,
                target.Height);

            var resource = new GuestImageResource
            {
                Address = target.Address,
                Width = target.Width,
                Height = target.Height,
                MipLevels = mipLevels,
                Format = format,
                Image = image,
                Memory = memory,
                View = view,
                MipViews = mipViews,
                RenderPass = renderPass,
                Framebuffer = framebuffer,
                MemorySize = allocationInfo.AllocationSize,
                LastUseStamp = _useStamp,
            };
            var debugName = GuestImageDebugName(target, format);
            SetDebugName(ObjectType.Image, image.Handle, $"{debugName} image");
            SetDebugName(ObjectType.ImageView, view.Handle, $"{debugName} view");
            for (var mipLevel = 0; mipLevel < mipViews.Length; mipLevel++)
            {
                SetDebugName(
                    ObjectType.ImageView,
                    mipViews[mipLevel].Handle,
                    $"{debugName} mip{mipLevel}");
            }
            SetDebugName(ObjectType.RenderPass, renderPass.Handle, $"{debugName} renderpass");
            SetDebugName(ObjectType.Framebuffer, framebuffer.Handle, $"{debugName} framebuffer");
            _guestImages.Add(resource);
            return resource;
        }

        // Resolves (or creates) the depth surface for an offscreen draw. The
        // image carries a real depth format so fixed-function depth test/write
        // work, and a depth-aspect sampled view so a later deferred-lighting
        // pass that samples this guest address reads real depth in .r instead
        // of an empty (black) upload. No render pass/framebuffer is built here:
        // the offscreen draw always assembles a transient one that pairs this
        // depth attachment with the color targets of the specific draw.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private GuestImageResource? GetOrCreateGuestDepthImage(VulkanGuestDepthTarget depth)
        {
            if (depth.Address == 0 || depth.Width == 0 || depth.Height == 0)
            {
                return null;
            }

            var depthFormat = depth.Format == 3 ? Format.D32Sfloat : Format.D16Unorm;
            if (!SupportsDepthStencilAttachment(depthFormat))
            {
                depthFormat = Format.D32Sfloat;
            }

            if (!SupportsDepthStencilAttachment(depthFormat))
            {
                return null;
            }

            if (_guestImages.TryGetExact(depth.Address, depthFormat, out var existing))
            {
                if (existing.IsDepth &&
                    existing.Width == depth.Width &&
                    existing.Height == depth.Height)
                {
                    existing.LastUseStamp = _useStamp;
                    _guestImages.Promote(existing);
                    return existing;
                }

                CompletePendingPresentation(wait: true);
                WaitForAllGuestSubmissions();
                DestroyGuestImage(existing);
                _guestImages.Remove(existing);
            }

            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = depthFormat,
                Extent = new Extent3D(depth.Width, depth.Height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                // Depth formats do not support StorageBit; the surface is only
                // ever a depth attachment and a sampled texture.
                Usage =
                    ImageUsageFlags.DepthStencilAttachmentBit |
                    ImageUsageFlags.SampledBit |
                    ImageUsageFlags.TransferSrcBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out var image), "vkCreateImage(depth)");
            _vk.GetImageMemoryRequirements(_device, image, out var requirements);
            var allocationInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            var memory = AllocateDeviceMemory(allocationInfo, "depth");
            Check(_vk.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory(depth)");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = depthFormat,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity),
                SubresourceRange = DepthSubresourceRange(0, 1),
            };
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out var view),
                "vkCreateImageView(depth)");

            var resource = new GuestImageResource
            {
                Address = depth.Address,
                Width = depth.Width,
                Height = depth.Height,
                MipLevels = 1,
                Format = depthFormat,
                Image = image,
                Memory = memory,
                View = view,
                // No per-mip views: depth exposes only its single depth-aspect
                // View, and DestroyGuestImage frees View and every MipView, so
                // aliasing them here would double-free.
                MipViews = [],
                MemorySize = allocationInfo.AllocationSize,
                LastUseStamp = _useStamp,
                IsDepth = true,
            };
            var debugName =
                $"SharpEmu depth 0x{depth.Address:X16} {depth.Width}x{depth.Height} {depthFormat}";
            SetDebugName(ObjectType.Image, image.Handle, $"{debugName} image");
            SetDebugName(ObjectType.ImageView, view.Handle, $"{debugName} view");
            _guestImages.Add(resource);
            return resource;
        }

        private (RenderPass RenderPass, Framebuffer Framebuffer) CreateRenderPassAndFramebuffer(
            Format format,
            ImageView attachmentView,
            uint width,
            uint height) =>
            CreateRenderPassAndFramebuffer([format], [attachmentView], width, height);

        private (RenderPass RenderPass, Framebuffer Framebuffer) CreateRenderPassAndFramebuffer(
            IReadOnlyList<Format> formats,
            IReadOnlyList<ImageView> attachmentViews,
            uint width,
            uint height,
            Format? depthFormat = null,
            ImageView depthView = default,
            bool clearDepth = false)
        {
            if (formats.Count == 0 || formats.Count != attachmentViews.Count)
            {
                throw new InvalidOperationException("render target formats and views must have matching counts");
            }

            var hasDepth = depthFormat.HasValue;
            var attachmentCount = formats.Count + (hasDepth ? 1 : 0);
            var attachments = stackalloc AttachmentDescription[attachmentCount];
            var colorReferences = stackalloc AttachmentReference[formats.Count];
            var views = stackalloc ImageView[attachmentCount];
            for (var index = 0; index < formats.Count; index++)
            {
                attachments[index] = new AttachmentDescription
                {
                    Format = formats[index],
                    Samples = SampleCountFlags.Count1Bit,
                    LoadOp = AttachmentLoadOp.Load,
                    StoreOp = AttachmentStoreOp.Store,
                    StencilLoadOp = AttachmentLoadOp.DontCare,
                    StencilStoreOp = AttachmentStoreOp.DontCare,
                    InitialLayout = ImageLayout.ColorAttachmentOptimal,
                    FinalLayout = ImageLayout.ColorAttachmentOptimal,
                };
                colorReferences[index] = new AttachmentReference
                {
                    Attachment = (uint)index,
                    Layout = ImageLayout.ColorAttachmentOptimal,
                };
                views[index] = attachmentViews[index];
            }

            var depthReference = new AttachmentReference
            {
                Attachment = (uint)formats.Count,
                Layout = ImageLayout.DepthStencilAttachmentOptimal,
            };
            if (hasDepth)
            {
                attachments[formats.Count] = new AttachmentDescription
                {
                    Format = depthFormat!.Value,
                    Samples = SampleCountFlags.Count1Bit,
                    LoadOp = clearDepth ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                    StoreOp = AttachmentStoreOp.Store,
                    StencilLoadOp = AttachmentLoadOp.DontCare,
                    StencilStoreOp = AttachmentStoreOp.DontCare,
                    InitialLayout = ImageLayout.DepthStencilAttachmentOptimal,
                    FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
                };
                views[formats.Count] = depthView;
            }

            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = (uint)formats.Count,
                PColorAttachments = colorReferences,
                PDepthStencilAttachment = hasDepth ? &depthReference : null,
            };
            var renderPassInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = (uint)attachmentCount,
                PAttachments = attachments,
                SubpassCount = 1,
                PSubpasses = &subpass,
            };
            Check(
                _vk.CreateRenderPass(_device, &renderPassInfo, null, out var renderPass),
                "vkCreateRenderPass(offscreen)");

            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = (uint)attachmentCount,
                PAttachments = views,
                Width = width,
                Height = height,
                Layers = 1,
            };
            Check(
                _vk.CreateFramebuffer(_device, &framebufferInfo, null, out var framebuffer),
                "vkCreateFramebuffer(offscreen)");

            return (renderPass, framebuffer);
        }

        private static uint ClampMipLevels(uint width, uint height, uint requestedMipLevels)
        {
            var largestDimension = Math.Max(width, height);
            uint maximumMipLevels = 1;
            while (largestDimension > 1)
            {
                largestDimension >>= 1;
                maximumMipLevels++;
            }

            return Math.Min(Math.Max(requestedMipLevels, 1u), maximumMipLevels);
        }

        private void DestroyGuestImage(GuestImageResource resource)
        {
            foreach (var view in resource.FormatViews.Values)
            {
                if (view.Handle != 0)
                {
                    _vk.DestroyImageView(_device, view, null);
                }
            }
            resource.FormatViews.Clear();

            if (resource.Framebuffer.Handle != 0)
            {
                _vk.DestroyFramebuffer(_device, resource.Framebuffer, null);
            }

            if (resource.RenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.RenderPass, null);
            }

            if (resource.View.Handle != 0)
            {
                _vk.DestroyImageView(_device, resource.View, null);
            }

            foreach (var mipView in resource.MipViews)
            {
                if (mipView.Handle != 0)
                {
                    _vk.DestroyImageView(_device, mipView, null);
                }
            }

            if (resource.Image.Handle != 0)
            {
                _vk.DestroyImage(_device, resource.Image, null);
            }

            FreeDeviceMemory(resource.Memory);
        }

        private static uint GetGuestTextureFormat(Format format) =>
            format switch
            {
                Format.A2R10G10B10UnormPack32 or
                    Format.A2B10G10R10UnormPack32 => 9,
                Format.R8Unorm => 36,
                Format.R8Uint => 49,
                Format.R8G8Unorm => 3,
                Format.B10G11R11UfloatPack32 => 7,
                Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb => 56,
                Format.R32G32Sfloat => 75,
                Format.R32G32B32A32Sfloat => 14,
                Format.R16G16Unorm => 5,
                Format.R16G16B16A16Unorm => 12,
                Format.R32Uint => GuestFormatR32Uint,
                Format.R32Sint => GuestFormatR32Sint,
                Format.R32Sfloat => GuestFormatR32Sfloat,
                Format.R16G16Uint => GuestFormatR16G16Uint,
                Format.R16G16Sint => GuestFormatR16G16Sint,
                Format.R16G16Sfloat => GuestFormatR16G16Sfloat,
                Format.R8G8B8A8Uint => GuestFormatR8G8B8A8Uint,
                Format.R8G8B8A8Sint => GuestFormatR8G8B8A8Sint,
                Format.R16G16B16A16Uint => GuestFormatR16G16B16A16Uint,
                Format.R16G16B16A16Sint => GuestFormatR16G16B16A16Sint,
                Format.R16G16B16A16Sfloat => 71,
                _ => 0,
            };

        private bool TryGetOrCreateGuestImageView(
            GuestImageResource resource,
            Format format,
            uint mipLevel,
            uint levelCount,
            uint dstSelect,
            out ImageView view)
        {
            try
            {
                view = GetOrCreateGuestImageView(resource, format, mipLevel, levelCount, dstSelect);
                return true;
            }
            catch (Exception exception)
            {
                view = default;
                TraceVulkanShader(
                    $"vk.texture_alias_view_failed addr=0x{resource.Address:X16} " +
                    $"image_format={resource.Format} view_format={format}: {exception.Message}");
                return false;
            }
        }

        private ImageView GetOrCreateGuestImageView(
            GuestImageResource resource,
            Format format,
            uint mipLevel,
            uint levelCount,
            uint dstSelect = 0xFAC)
        {
            if (mipLevel >= resource.MipLevels)
            {
                throw new InvalidOperationException(
                    $"View mip {mipLevel} exceeds image mip count {resource.MipLevels}.");
            }

            // Depth surfaces only ever expose their single depth-aspect view; a
            // color-format / color-aspect alias of a depth image is invalid.
            if (resource.IsDepth)
            {
                return resource.View;
            }

            levelCount = Math.Max(levelCount, 1);
            levelCount = Math.Min(levelCount, resource.MipLevels - mipLevel);
            if (format == resource.Format && dstSelect == 0xFAC)
            {
                if (mipLevel == 0 && levelCount == resource.MipLevels)
                {
                    return resource.View;
                }

                if (levelCount == 1)
                {
                    return resource.MipViews[mipLevel];
                }
            }

            if (!IsCompatibleViewFormat(resource.Format, format))
            {
                throw new InvalidOperationException(
                    $"Incompatible image view format {format} for image {resource.Format}.");
            }

            var key = (format, mipLevel, levelCount, dstSelect);
            if (resource.FormatViews.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = resource.Image,
                ViewType = ImageViewType.Type2D,
                Format = format,
                Components = ToVkComponentMapping(dstSelect),
                SubresourceRange = ColorSubresourceRange(mipLevel, levelCount),
            };
            ImageView view;
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out view),
                "vkCreateImageView(guest alias)");
            resource.FormatViews.Add(key, view);
            SetDebugName(
                ObjectType.ImageView,
                view.Handle,
                $"SharpEmu guest 0x{resource.Address:X16} alias {format} mip{mipLevel}+{levelCount}");
            TraceVulkanShader(
                $"vk.texture_alias_view addr=0x{resource.Address:X16} " +
                $"image_format={resource.Format} view_format={format} " +
                $"mip={mipLevel} levels={levelCount} dst=0x{dstSelect:X3}");
            return view;
        }

        private void UpdatePerformanceHud()
        {
            if (!_performanceHudEnabled || !OperatingSystem.IsWindows())
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            if (_performanceHudLastTimestamp != 0 &&
                Stopwatch.GetElapsedTime(_performanceHudLastTimestamp, now).TotalSeconds <
                    PerformanceHudSampleSeconds)
            {
                return;
            }

            try
            {
                using var process = Process.GetCurrentProcess();
                var processCpu = process.TotalProcessorTime;
                var currentThreadCpu = new Dictionary<int, TimeSpan>();
                var currentThreadIds = new HashSet<int>();
                var hottestThreadId = 0;
                var hottestThreadCpuSeconds = 0.0;

                foreach (ProcessThread thread in process.Threads)
                {
                    using (thread)
                    {
                        try
                        {
                            var threadId = thread.Id;
                            var cpu = thread.TotalProcessorTime;
                            currentThreadIds.Add(threadId);
                            currentThreadCpu[threadId] = cpu;
                            if (_performanceHudThreadCpu.TryGetValue(threadId, out var previousCpu))
                            {
                                var deltaSeconds = Math.Max(0.0, (cpu - previousCpu).TotalSeconds);
                                if (deltaSeconds > hottestThreadCpuSeconds)
                                {
                                    hottestThreadCpuSeconds = deltaSeconds;
                                    hottestThreadId = threadId;
                                }
                            }
                        }
                        catch (InvalidOperationException)
                        {
                        }
                    }
                }

                if (_performanceHudLastTimestamp != 0)
                {
                    var elapsedSeconds = Math.Max(
                        Stopwatch.GetElapsedTime(_performanceHudLastTimestamp, now).TotalSeconds,
                        0.001);
                    var processCpuPercent = Math.Max(
                        0.0,
                        (processCpu - _performanceHudLastProcessCpu).TotalSeconds /
                            elapsedSeconds /
                            Math.Max(Environment.ProcessorCount, 1) *
                            100.0);
                    var hottestThreadPercent = hottestThreadCpuSeconds / elapsedSeconds * 100.0;
                    var presentedFrames = _performanceHudPresentedFrames;
                    var fps = (presentedFrames - _performanceHudLastPresentedFrames) / elapsedSeconds;
                    var hotName = hottestThreadId == 0
                        ? "idle"
                        : GetPerformanceThreadName(hottestThreadId);
                    long guestBacklog;
                    int queuedGuestWork;
                    lock (_gate)
                    {
                        guestBacklog = Math.Max(
                            0,
                            _enqueuedGuestWorkSequence - _completedGuestWorkSequence);
                        queuedGuestWork = _pendingGuestWork.Count;
                    }

                    var gpuInFlight = _pendingGuestSubmissions.Count +
                        (_presentationInFlight ? 1 : 0);
                    var readCount = Interlocked.Read(
                        ref Agc.Gen5ShaderScalarEvaluator.GlobalMemoryReadCount);
                    var readBytes = Interlocked.Read(
                        ref Agc.Gen5ShaderScalarEvaluator.GlobalMemoryReadBytes);
                    var readHits = Interlocked.Read(
                        ref Agc.Gen5ShaderScalarEvaluator.GlobalMemoryReadCacheHits);
                    var readPvmBytes = Interlocked.Read(
                        ref Agc.Gen5ShaderScalarEvaluator.GlobalMemoryReadPvmBytes);
                    var readLibcBytes = Interlocked.Read(
                        ref Agc.Gen5ShaderScalarEvaluator.GlobalMemoryReadLibcBytes);
                    var readsPerSecond =
                        (readCount - _performanceHudLastReadCount) / elapsedSeconds;
                    var readMbPerSecond =
                        (readBytes - _performanceHudLastReadBytes) /
                        elapsedSeconds /
                        (1024.0 * 1024.0);
                    var readHitsPerSecond =
                        (readHits - _performanceHudLastReadHits) / elapsedSeconds;
                    var readPvmMbPerSecond =
                        (readPvmBytes - _performanceHudLastReadPvmBytes) /
                        elapsedSeconds /
                        (1024.0 * 1024.0);
                    var readLibcMbPerSecond =
                        (readLibcBytes - _performanceHudLastReadLibcBytes) /
                        elapsedSeconds /
                        (1024.0 * 1024.0);
                    _performanceHudLastReadCount = readCount;
                    _performanceHudLastReadBytes = readBytes;
                    _performanceHudLastReadHits = readHits;
                    _performanceHudLastReadPvmBytes = readPvmBytes;
                    _performanceHudLastReadLibcBytes = readLibcBytes;
                    _window.Title =
                        $"FPS {fps:0.0} CPU {processCpuPercent:0}% | " +
                        $"HOT {hotName}#{hottestThreadId} {hottestThreadPercent:0}% | " +
                        $"WORK {guestBacklog} (q{queuedGuestWork}/gpu{gpuInFlight}) | " +
                        $"RD {readsPerSecond:0}/s {readMbPerSecond:0}MB/s h{readHitsPerSecond:0}/s " +
                        $"P{readPvmMbPerSecond:0} L{readLibcMbPerSecond:0} | " +
                        VideoOutExports.GetWindowTitle();
                    _performanceHudLastPresentedFrames = presentedFrames;
                }

                _performanceHudThreadCpu.Clear();
                foreach (var (threadId, cpu) in currentThreadCpu)
                {
                    _performanceHudThreadCpu[threadId] = cpu;
                }

                foreach (var staleThreadId in _performanceHudThreadNames.Keys
                             .Where(threadId => !currentThreadIds.Contains(threadId))
                             .ToArray())
                {
                    _performanceHudThreadNames.Remove(staleThreadId);
                }

                _performanceHudLastProcessCpu = processCpu;
                _performanceHudLastTimestamp = now;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _performanceHudLastTimestamp = now;
            }
        }

        private string GetPerformanceThreadName(int threadId)
        {
            if (_performanceHudThreadNames.TryGetValue(threadId, out var cached))
            {
                return cached;
            }

            var name = "tid";
            var handle = OpenThread(ThreadQueryLimitedInformation, false, (uint)threadId);
            if (handle != 0)
            {
                try
                {
                    if (GetThreadDescription(handle, out var description) >= 0 && description != 0)
                    {
                        try
                        {
                            var described = Marshal.PtrToStringUni(description);
                            if (!string.IsNullOrWhiteSpace(described))
                            {
                                name = described.Length <= 28 ? described : described[..28];
                            }
                        }
                        finally
                        {
                            LocalFree(description);
                        }
                    }
                }
                finally
                {
                    CloseHandle(handle);
                }
            }

            _performanceHudThreadNames[threadId] = name;
            return name;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

        [DllImport("kernel32.dll")]
        private static extern int GetThreadDescription(nint thread, out nint description);

        [DllImport("kernel32.dll")]
        private static extern nint LocalFree(nint memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);

        private void WaitForRenderWork()
        {
            var gpuWorkInFlight = _pendingGuestSubmissions.Count > 0 || _presentationInFlight;
            lock (_gate)
            {
                if (_closed ||
                    _pendingGuestWork.Count > 0 ||
                    (_latestPresentation is { } latest &&
                     latest.Sequence != _presentedSequence &&
                     latest.RequiredGuestWorkSequence <= _completedGuestWorkSequence))
                {
                    return;
                }

                System.Threading.Monitor.Wait(_gate, gpuWorkInFlight ? 1 : 8);
            }
        }

        private void Render(double _)
        {
            if (_closeRequested)
            {
                _window.Close();
                return;
            }

            if (!_vulkanReady)
            {
                return;
            }

            WaitForRenderWork();
            UpdatePerformanceHud();

            _commandBuffer = _presentationCommandBuffer;
            if (!_deviceLost)
            {
                CollectCompletedGuestSubmissions(waitForOldest: false);
            }

            var completedWork = 0;
            while (completedWork < MaxGuestWorkPerRender &&
                   TryTakeGuestWork(out var work))
            {
                try
                {
                    switch (work)
                    {
                        case VulkanOffscreenGuestDraw offscreenDraw:
                            ExecuteOffscreenDraw(offscreenDraw);
                            break;
                        case VulkanComputeGuestDispatch computeDispatch:
                            ExecuteComputeDispatch(computeDispatch);
                            break;
                        case VulkanOrderedGuestFlip orderedFlip:
                            ExecuteOrderedGuestFlip(orderedFlip);
                            break;
                    }
                }
                finally
                {
                    CompleteGuestWork();
                }

                completedWork++;
            }

            if (!TryTakePresentation(_presentedSequence, out var presentation))
            {
                return;
            }

            if (presentation.Pixels is null &&
                presentation.DrawKind != GuestDrawKind.FullscreenBarycentric &&
                presentation.TranslatedDraw is null &&
                presentation.GuestImageAddress == 0)
            {
                return;
            }

            CompletePendingPresentation(wait: true);
            _useStamp++;

            byte[]? pixels = null;
            if (presentation.Pixels is { } sourcePixels)
            {
                pixels = presentation.Width == _extent.Width && presentation.Height == _extent.Height
                    ? sourcePixels
                    : ScaleBgra(
                        sourcePixels,
                        presentation.Width,
                        presentation.Height,
                        _extent.Width,
                        _extent.Height);
                if ((ulong)pixels.Length > _stagingSize)
                {
                    return;
                }
            }

            TranslatedDrawResources? translatedResources = null;
            GuestImageResource? presentedGuestImage = null;
            if (presentation.GuestImageVersion != 0 &&
                _guestImageVersions.Remove(presentation.GuestImageVersion, out var snapshot))
            {
                // Ordered-flip capture: present the frozen snapshot, not the live
                // (now-overwritten) render target at GuestImageAddress. Ownership
                // moves out of the dict into the retire list right now, so every
                // early return below still frees it exactly once; the retire list
                // is only drained after this frame's vkQueueWaitIdle, keeping the
                // snapshot alive for the blit that reads it.
                _capturedGuestFlipVersions.Remove(presentation.GuestImageVersion);
                presentedGuestImage = snapshot;
                RetireGuestFlipSnapshotLater(snapshot);
            }
            else if (presentation.GuestImageAddress != 0)
            {
                if (!_guestImages.TryFindPresentable(
                        presentation.GuestImageAddress,
                        out var presentable))
                {
                    return;
                }

                presentedGuestImage = presentable;
            }
            if (presentedGuestImage is not null)
            {
                presentedGuestImage.LastUseStamp = _useStamp;
                _directPresentationCount++;
                if (ShouldTracePresentedGuestImageContentsForDiagnostics() &&
                    _directPresentationCount is 1 or 30 or 120)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.present_sample frame={_directPresentationCount} " +
                        $"addr=0x{presentedGuestImage.Address:X16}");
                    TraceGuestImageContents(presentedGuestImage);
                }
            }

            if (presentation.TranslatedDraw is { } translatedDraw)
            {
                try
                {
                    translatedResources = CreateTranslatedDrawResources(
                        translatedDraw,
                        _renderPass,
                        [_swapchainFormat],
                        _extent);
                    if (ShouldTracePresentedGuestImageContentsForDiagnostics() &&
                        !_firstGuestDrawPresented)
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][TRACE] vk.translated_draw kind={presentation.DrawKind} " +
                            $"textures={translatedResources.Textures.Length}");
                        foreach (var boundTexture in translatedResources.Textures)
                        {
                            if (boundTexture.GuestImage is { } guestImage &&
                                _tracedGuestImageContents.Add((guestImage.Address, guestImage.Format)))
                            {
                                TraceGuestImageContents(guestImage);
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    _presentedSequence = presentation.Sequence;
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] Vulkan VideoOut translated draw setup failed: {exception.Message}");
                    return;
                }
            }

            uint imageIndex;
            var acquireResult = _swapchainApi.AcquireNextImage(
                _device,
                _swapchain,
                ulong.MaxValue,
                _imageAvailable,
                default,
                &imageIndex);
            if (acquireResult == Result.ErrorOutOfDateKhr)
            {
                RecreateSwapchainResources("vkAcquireNextImageKHR", acquireResult);
                if (translatedResources is not null)
                {
                    DestroyTranslatedDrawResources(translatedResources);
                }

                return;
            }

            CheckSwapchainResult(acquireResult, "vkAcquireNextImageKHR");
            var recreateAfterPresent = acquireResult == Result.SuboptimalKhr;

            if (pixels is not null)
            {
                void* mapped;
                Check(
                    _vk.MapMemory(_device, _stagingMemory, 0, (ulong)pixels.Length, 0, &mapped),
                    "vkMapMemory");
                fixed (byte* source = pixels)
                {
                    System.Buffer.MemoryCopy(source, mapped, pixels.Length, pixels.Length);
                }
                _vk.UnmapMemory(_device, _stagingMemory);
            }

            Check(_vk.ResetCommandBuffer(_commandBuffer, 0), "vkResetCommandBuffer");
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Check(_vk.BeginCommandBuffer(_commandBuffer, &beginInfo), "vkBeginCommandBuffer");

            PipelineStageFlags waitStage;
            if (pixels is not null)
            {
                RecordUpload(imageIndex);
                waitStage = PipelineStageFlags.TransferBit;
            }
            else if (presentation.DrawKind == GuestDrawKind.FullscreenBarycentric)
            {
                var clearValue = default(ClearValue);
                var renderPassInfo = new RenderPassBeginInfo
                {
                    SType = StructureType.RenderPassBeginInfo,
                    RenderPass = _renderPass,
                    Framebuffer = _framebuffers[imageIndex],
                    RenderArea = new Rect2D(new Offset2D(0, 0), _extent),
                    ClearValueCount = 1,
                    PClearValues = &clearValue,
                };
                _vk.CmdBeginRenderPass(
                    _commandBuffer,
                    &renderPassInfo,
                    SubpassContents.Inline);
                _vk.CmdBindPipeline(
                    _commandBuffer,
                    PipelineBindPoint.Graphics,
                    _barycentricPipeline);
                _vk.CmdDraw(_commandBuffer, 3, 1, 0, 0);
                _vk.CmdEndRenderPass(_commandBuffer);
                waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
            }
            else if (presentedGuestImage is not null)
            {
                RecordGuestImageBlit(imageIndex, presentedGuestImage);
                waitStage = PipelineStageFlags.TransferBit;
            }
            else if (translatedResources is not null)
            {
                RecordTranslatedDraw(imageIndex, translatedResources);
                waitStage = PipelineStageFlags.AllCommandsBit;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unsupported translated guest draw: {presentation.DrawKind}.");
            }

            Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer");

            var imageAvailable = _imageAvailable;
            var commandBuffer = _commandBuffer;
            var renderFinished = _renderFinished;
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &imageAvailable,
                PWaitDstStageMask = &waitStage,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = &renderFinished,
            };
            var presentationFence = _presentationFence;
            Check(
                _vk.ResetFences(_device, 1, &presentationFence),
                "vkResetFences(presentation)");
            Check(
                _vk.QueueSubmit(_queue, 1, &submitInfo, presentationFence),
                "vkQueueSubmit");
            _presentationInFlight = true;
            _pendingPresentationResources = translatedResources;
            translatedResources = null;

            var swapchain = _swapchain;
            var presentInfo = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &renderFinished,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &imageIndex,
            };
            var presentResult = _swapchainApi.QueuePresent(_queue, &presentInfo);
            if (presentResult == Result.ErrorOutOfDateKhr)
            {
                Check(_vk.QueueWaitIdle(_queue), "vkQueueWaitIdle");
                CompletePendingPresentation(wait: false);
                CollectCompletedGuestSubmissions(waitForOldest: false);
                if (translatedResources is not null)
                {
                    DestroyTranslatedDrawResources(translatedResources);
                }

                RecreateSwapchainResources("vkQueuePresentKHR", presentResult);
                return;
            }

            CheckSwapchainResult(presentResult, "vkQueuePresentKHR");
            recreateAfterPresent |= presentResult == Result.SuboptimalKhr;
            Check(_vk.QueueWaitIdle(_queue), "vkQueueWaitIdle");
            // Queue is now idle: any ordered-flip snapshot whose copy/blit just
            // completed (or that was abandoned this frame) is safe to free.
            DrainRetiredGuestFlipSnapshots();
            Interlocked.Increment(ref _presentedFrameTotal);
            VideoOutExports.ReportPresentedFrame();
            _performanceHudPresentedFrames++;
            if (_swapchainReadbackPending)
            {
                CompletePendingPresentation(wait: true);
                TraceSwapchainReadback();
            }
            CollectCompletedGuestSubmissions(waitForOldest: false);

            _imageInitialized[imageIndex] = true;
            _presentedSequence = presentation.Sequence;
            if (presentation.IsSplash && !_splashPresented)
            {
                _splashPresented = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan VideoOut presented splash: " +
                    $"{presentation.Width}x{presentation.Height}");
            }
            else if (!presentation.IsSplash && !_firstFramePresented)
            {
                _firstFramePresented = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan VideoOut presented first frame: " +
                    $"{presentation.Width}x{presentation.Height}");
            }

            if (pixels is null && !_firstGuestDrawPresented)
            {
                _firstGuestDrawPresented = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan VideoOut presented guest frame: " +
                    (presentedGuestImage is not null
                        ? $"image=0x{presentedGuestImage.Address:X16} " +
                          $"{presentedGuestImage.Width}x{presentedGuestImage.Height}"
                        : presentation.TranslatedDraw is null
                        ? $"{presentation.DrawKind}"
                        : $"shader textures={presentation.TranslatedDraw.Textures.Count}"));
            }

            if (recreateAfterPresent)
            {
                RecreateSwapchainResources("present suboptimal", Result.SuboptimalKhr);
            }
        }

        private void TraceGuestImageContents(GuestImageResource image)
        {
            if (_reclaimInProgress)
            {
                // Reclaim drains in-flight submissions, and collecting those
                // submissions traces their images -- which would allocate a
                // fresh host-visible readback buffer, the very memory class
                // being reclaimed, re-entering CreateBuffer mid-reclaim.
                TraceVulkanShader(
                    $"vk.guest_image addr=0x{image.Address:X16} " +
                    "readback=skipped_reclaim");
                return;
            }

            var bytesPerPixel = GetReadbackBytesPerPixel(image.Format);
            if (bytesPerPixel == 0)
            {
                TraceVulkanShader(
                    $"vk.guest_image addr=0x{image.Address:X16} " +
                    $"format={image.Format} readback=unsupported");
                return;
            }

            var byteCount = checked((ulong)image.Width * image.Height * bytesPerPixel);
            var buffer = CreateBuffer(
                byteCount,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out var memory);
            try
            {
                Check(
                    _vk.ResetCommandBuffer(_commandBuffer, 0),
                    "vkResetCommandBuffer(guest readback)");
                var beginInfo = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                Check(
                    _vk.BeginCommandBuffer(_commandBuffer, &beginInfo),
                    "vkBeginCommandBuffer(guest readback)");

                TransitionGuestImage(
                    image,
                    ImageLayout.TransferSrcOptimal,
                    PipelineStageFlags.TransferBit,
                    AccessFlags.TransferReadBit);

                var region = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        LayerCount = 1,
                    },
                    ImageExtent = new Extent3D(image.Width, image.Height, 1),
                };
                _vk.CmdCopyImageToBuffer(
                    _commandBuffer,
                    image.Image,
                    ImageLayout.TransferSrcOptimal,
                    buffer,
                    1,
                    &region);

                TransitionGuestImage(
                    image,
                    ImageLayout.ShaderReadOnlyOptimal,
                    PipelineStageFlags.VertexShaderBit |
                    PipelineStageFlags.FragmentShaderBit |
                    PipelineStageFlags.ComputeShaderBit,
                    AccessFlags.ShaderReadBit);

                Check(
                    _vk.EndCommandBuffer(_commandBuffer),
                    "vkEndCommandBuffer(guest readback)");
                var commandBuffer = _commandBuffer;
                var submitInfo = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                };
                Check(
                    _vk.QueueSubmit(_queue, 1, &submitInfo, default),
                    "vkQueueSubmit(guest readback)");
                Check(
                    _vk.QueueWaitIdle(_queue),
                    "vkQueueWaitIdle(guest readback)");

                void* mapped;
                Check(
                    _vk.MapMemory(_device, memory, 0, byteCount, 0, &mapped),
                    "vkMapMemory(guest readback)");
                try
                {
                    var bytes = new ReadOnlySpan<byte>(mapped, checked((int)byteCount));
                    var nonzeroBytes = 0L;
                    ulong hash = 14695981039346656037UL;
                    foreach (var value in bytes)
                    {
                        nonzeroBytes += value == 0 ? 0 : 1;
                        hash = (hash ^ value) * 1099511628211UL;
                    }

                    var nonblackPixels = CountNonblackPixels(
                        bytes,
                        image.Format,
                        bytesPerPixel);
                    // Feed the observed color signal to the flip resolver so a
                    // color-bearing target can outrank an all-black composite.
                    NoteGuestImageContentObserved(
                        image.Address,
                        image.Width,
                        image.Height,
                        nonblackPixels > 0);
                    var centerOffset = checked(
                        ((int)(image.Height / 2) * (int)image.Width +
                         (int)(image.Width / 2)) *
                        (int)bytesPerPixel);
                    var center = Convert.ToHexString(
                        bytes.Slice(centerOffset, (int)bytesPerPixel));
                    var imageLine =
                        $"vk.guest_image addr=0x{image.Address:X16} " +
                        $"size={image.Width}x{image.Height} format={image.Format} " +
                        $"nonzero_bytes={nonzeroBytes}/{byteCount} " +
                        $"nonblack_pixels={nonblackPixels}/{(ulong)image.Width * image.Height} " +
                        $"center={center} hash=0x{hash:X16}";
                    // An explicitly targeted address (SHARPEMU_TRACE_GUEST_IMAGE_ADDRS)
                    // always logs, independent of the broader vk-shader trace gate,
                    // so a single address can be watched every frame without the
                    // full guest-image trace flooding the log.
                    if (ShouldTraceGuestImageAddressForDiagnostics(image.Address))
                    {
                        Console.Error.WriteLine("[LOADER][TRACE] " + imageLine);
                    }
                    else
                    {
                        TraceVulkanShader(imageLine);
                    }
                    var isRgba8 = image.Format == Format.R8G8B8A8Unorm;
                    var isHdr16 = image.Format == Format.R16G16B16A16Sfloat;
                    var isHdr32 = image.Format == Format.R32G32B32A32Sfloat;
                    if (nonblackPixels > 0 && (isRgba8 || isHdr16 || isHdr32))
                    {
                        const int outWidth = 960;
                        const int outHeight = 540;
                        var srcW = (int)image.Width;
                        var srcH = (int)image.Height;
                        var rgb = new byte[outWidth * outHeight * 3];
                        for (var oy = 0; oy < outHeight; oy++)
                        {
                            var sy = oy * srcH / outHeight;
                            for (var ox = 0; ox < outWidth; ox++)
                            {
                                var sx = ox * srcW / outWidth;
                                var di = (oy * outWidth + ox) * 3;
                                if (isRgba8)
                                {
                                    var si = (sy * srcW + sx) * 4;
                                    rgb[di] = bytes[si];
                                    rgb[di + 1] = bytes[si + 1];
                                    rgb[di + 2] = bytes[si + 2];
                                }
                                else if (isHdr16)
                                {
                                    var si = (sy * srcW + sx) * 8;
                                    for (var c = 0; c < 3; c++)
                                    {
                                        var h = BitConverter.UInt16BitsToHalf(
                                            BitConverter.ToUInt16(bytes.Slice(si + c * 2, 2)));
                                        var v = (float)h;
                                        v = v <= 0f ? 0f : v >= 1f ? 1f : v;
                                        rgb[di + c] = (byte)(v * 255f + 0.5f);
                                    }
                                }
                                else
                                {
                                    var si = (sy * srcW + sx) * 16;
                                    for (var c = 0; c < 3; c++)
                                    {
                                        var v = BitConverter.ToSingle(bytes.Slice(si + c * 4, 4));
                                        v = v <= 0f ? 0f : v >= 1f ? 1f : v;
                                        rgb[di + c] = (byte)(v * 255f + 0.5f);
                                    }
                                }
                            }
                        }

                        Console.Error.WriteLine(
                            "[LOADER][GIMGDUMP] addr=0x" + image.Address.ToString("X16") +
                            " w=" + outWidth + " h=" + outHeight + " fmt=RGB b64=" +
                            Convert.ToBase64String(rgb));
                    }

                    DumpGuestImageBytes(image, bytes);
                }
                finally
                {
                    _vk.UnmapMemory(_device, memory);
                }
            }
            finally
            {
                _vk.DestroyBuffer(_device, buffer, null);
                FreeDeviceMemory(memory);
            }
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

        // Reuses the vk.guest_image readback machinery (GetReadbackBytesPerPixel +
        // CmdCopyImageToBuffer against image.Image + CountNonblackPixels) exactly
        // as TraceGuestImageContents does, but returns the counts instead of
        // logging so a single capture line can combine several images. The caller
        // must have rebound _commandBuffer to a resettable buffer and drained the
        // queue first (see CaptureDrawContents). Returns false only when the
        // format has no readback layout (bytesPerPixel == 0).
        private bool TryReadbackGuestImageNonblack(
            GuestImageResource image,
            out long nonblackPixels,
            out ulong pixelCount)
        {
            pixelCount = (ulong)image.Width * image.Height;
            nonblackPixels = 0;
            var bytesPerPixel = GetReadbackBytesPerPixel(image.Format);
            if (bytesPerPixel == 0)
            {
                return false;
            }

            var byteCount = checked((ulong)image.Width * image.Height * bytesPerPixel);
            var buffer = CreateBuffer(
                byteCount,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out var memory);
            try
            {
                Check(
                    _vk.ResetCommandBuffer(_commandBuffer, 0),
                    "vkResetCommandBuffer(capture readback)");
                var beginInfo = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                Check(
                    _vk.BeginCommandBuffer(_commandBuffer, &beginInfo),
                    "vkBeginCommandBuffer(capture readback)");

                TransitionGuestImage(
                    image,
                    ImageLayout.TransferSrcOptimal,
                    PipelineStageFlags.TransferBit,
                    AccessFlags.TransferReadBit);

                var region = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        LayerCount = 1,
                    },
                    ImageExtent = new Extent3D(image.Width, image.Height, 1),
                };
                _vk.CmdCopyImageToBuffer(
                    _commandBuffer,
                    image.Image,
                    ImageLayout.TransferSrcOptimal,
                    buffer,
                    1,
                    &region);

                TransitionGuestImage(
                    image,
                    ImageLayout.ShaderReadOnlyOptimal,
                    PipelineStageFlags.VertexShaderBit |
                    PipelineStageFlags.FragmentShaderBit |
                    PipelineStageFlags.ComputeShaderBit,
                    AccessFlags.ShaderReadBit);

                Check(
                    _vk.EndCommandBuffer(_commandBuffer),
                    "vkEndCommandBuffer(capture readback)");
                var commandBuffer = _commandBuffer;
                var submitInfo = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                };
                Check(
                    _vk.QueueSubmit(_queue, 1, &submitInfo, default),
                    "vkQueueSubmit(capture readback)");
                Check(
                    _vk.QueueWaitIdle(_queue),
                    "vkQueueWaitIdle(capture readback)");

                void* mapped;
                Check(
                    _vk.MapMemory(_device, memory, 0, byteCount, 0, &mapped),
                    "vkMapMemory(capture readback)");
                try
                {
                    var bytes = new ReadOnlySpan<byte>(mapped, checked((int)byteCount));
                    nonblackPixels = CountNonblackPixels(bytes, image.Format, bytesPerPixel);
                }
                finally
                {
                    _vk.UnmapMemory(_device, memory);
                }
            }
            finally
            {
                _vk.DestroyBuffer(_device, buffer, null);
                FreeDeviceMemory(memory);
            }

            return true;
        }

        private static void DumpGuestImageBytes(
            GuestImageResource image,
            ReadOnlySpan<byte> bytes)
        {
            var directory =
                Environment.GetEnvironmentVariable("SHARPEMU_GUEST_IMAGE_DUMP_DIR");
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"0x{image.Address:X16}-{image.Width}x{image.Height}-{image.Format}.rgba");
            File.WriteAllBytes(path, bytes.ToArray());
        }

        private static uint GetReadbackBytesPerPixel(Format format) =>
            format switch
            {
                Format.R8Unorm or
                Format.R8Uint or
                Format.R8Sint => 1,
                Format.R8G8Unorm or
                Format.R8G8Uint or
                Format.R8G8Sint => 2,
                Format.R32Uint or
                Format.R32Sint or
                Format.R32Sfloat or
                Format.R16G16Uint or
                Format.R16G16Sint or
                Format.R16G16Sfloat or
                Format.R8G8B8A8Uint or
                Format.R8G8B8A8Sint or
                Format.R8G8B8A8Unorm or
                Format.A2R10G10B10UnormPack32 or
                Format.A2B10G10R10UnormPack32 => 4,
                Format.R16G16B16A16Uint or
                Format.R16G16B16A16Sint or
                Format.R16G16B16A16Sfloat => 8,
                Format.R32G32Uint or
                Format.R32G32Sint or
                Format.R32G32Sfloat => 8,
                Format.R32G32B32A32Uint or
                Format.R32G32B32A32Sint or
                Format.R32G32B32A32Sfloat => 16,
                _ => 0,
            };

        private static long CountNonblackPixels(
            ReadOnlySpan<byte> bytes,
            Format format,
            uint bytesPerPixel)
        {
            var count = 0L;
            for (var offset = 0; offset < bytes.Length; offset += (int)bytesPerPixel)
            {
                var pixel = bytes.Slice(offset, (int)bytesPerPixel);
                var hasColor = format switch
                {
                    Format.A2R10G10B10UnormPack32 or
                    Format.A2B10G10R10UnormPack32 =>
                        (BitConverter.ToUInt32(pixel) & 0x3FFFFFFFu) != 0,
                    Format.R8G8B8A8Uint or
                    Format.R8G8B8A8Sint or
                    Format.R8G8B8A8Unorm =>
                        pixel[0] != 0 || pixel[1] != 0 || pixel[2] != 0,
                    Format.R16G16B16A16Uint or
                    Format.R16G16B16A16Sint or
                    Format.R16G16B16A16Sfloat =>
                        pixel[..6].IndexOfAnyExcept((byte)0) >= 0,
                    _ => pixel.IndexOfAnyExcept((byte)0) >= 0,
                };
                count += hasColor ? 1 : 0;
            }

            return count;
        }

        private void RecordTranslatedDraw(uint imageIndex, TranslatedDrawResources resources)
        {
            BeginDebugLabel(_commandBuffer, "SharpEmu swapchain draw");
            RecordTextureUploads(resources, PipelineStageFlags.FragmentShaderBit);
            RecordSampledImageTransitions(resources, PipelineStageFlags.FragmentShaderBit);
            RecordStorageImagesForWrite(resources, PipelineStageFlags.FragmentShaderBit);
            RecordTranslatedGraphicsPass(
                resources,
                _renderPass,
                _framebuffers[imageIndex],
                _extent);
            RecordStorageImagesForRead(resources, PipelineStageFlags.FragmentShaderBit);
            EndDebugLabel(_commandBuffer);
        }

        private void TransitionGuestImage(
            GuestImageResource image,
            ImageLayout newLayout,
            PipelineStageFlags dstStageMask,
            AccessFlags dstAccessMask)
        {
            if (!TryPlanGuestImageTransition(
                    image,
                    newLayout,
                    dstStageMask,
                    dstAccessMask,
                    out var barrier,
                    out var srcStageMask))
            {
                return;
            }

            _vk.CmdPipelineBarrier(
                _commandBuffer,
                srcStageMask,
                dstStageMask,
                0,
                0,
                null,
                0,
                null,
                1,
                &barrier);
        }

        private static bool BindsGuestImageAsStorage(
            TranslatedDrawResources resources,
            GuestImageResource guestImage)
        {
            foreach (var texture in resources.Textures)
            {
                if (texture.IsStorage && texture.GuestImage == guestImage)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Transitions every aliased sampled texture (a rendered or
        /// compute-written VkImage bound with no upload) to the layout its
        /// descriptor declares. Render targets already rest in
        /// ShaderReadOnlyOptimal with fragment-read scope and skip for free;
        /// compute-written images arrive from compute scope and get the
        /// barrier that makes their contents visible to the sampling stage.
        /// Images the draw also binds as storage are driven to General by
        /// RecordStorageImagesForWrite and their descriptors declare General
        /// (the same predicate as the descriptor write), so they are skipped.
        /// </summary>
        private void RecordSampledImageTransitions(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            var transitioned = new HashSet<GuestImageResource>();
            foreach (var texture in resources.Textures)
            {
                if (texture.IsStorage ||
                    texture.NeedsUpload ||
                    texture.GuestImage is not { } guestImage ||
                    !transitioned.Add(guestImage) ||
                    BindsGuestImageAsStorage(resources, guestImage))
                {
                    continue;
                }

                TransitionGuestImage(
                    guestImage,
                    ImageLayout.ShaderReadOnlyOptimal,
                    shaderStage,
                    AccessFlags.ShaderReadBit);
            }
        }

        private static void InvalidateGuestImageLayouts(
            TranslatedDrawResources resources,
            IReadOnlyList<GuestImageResource>? renderTargets = null)
        {
            foreach (var texture in resources.Textures)
            {
                if (texture.GuestImage is { } guestImage)
                {
                    InvalidateGuestImageLayout(guestImage);
                }
            }

            foreach (var target in renderTargets ?? [])
            {
                InvalidateGuestImageLayout(target);
            }
        }

        private void RecordTextureUploads(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            foreach (var texture in resources.Textures)
            {
                if (!texture.NeedsUpload)
                {
                    continue;
                }

                if (texture.CopySource is { } copySource)
                {
                    RecordCopyOnSample(texture, copySource, shaderStage);
                    continue;
                }

                if (texture.GuestImage is { } guestImage)
                {
                    TransitionGuestImage(
                        guestImage,
                        ImageLayout.TransferDstOptimal,
                        PipelineStageFlags.TransferBit,
                        AccessFlags.TransferWriteBit);
                }
                else
                {
                    // A per-draw owned image, freshly created in Undefined.
                    var toTransfer = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = 0,
                        DstAccessMask = AccessFlags.TransferWriteBit,
                        OldLayout = ImageLayout.Undefined,
                        NewLayout = ImageLayout.TransferDstOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = texture.Image,
                        SubresourceRange = ColorSubresourceRange(),
                    };
                    _vk.CmdPipelineBarrier(
                        _commandBuffer,
                        PipelineStageFlags.TopOfPipeBit,
                        PipelineStageFlags.TransferBit,
                        0,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &toTransfer);
                }

                var copyRegion = new BufferImageCopy
                {
                    BufferRowLength = texture.RowLength > texture.Width
                        ? texture.RowLength
                        : 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        LayerCount = 1,
                    },
                    ImageExtent = new Extent3D(texture.Width, texture.Height, 1),
                };
                _vk.CmdCopyBufferToImage(
                    _commandBuffer,
                    texture.StagingBuffer,
                    texture.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &copyRegion);

                if (texture.GuestImage is { } uploadedImage)
                {
                    TransitionGuestImage(
                        uploadedImage,
                        ImageLayout.ShaderReadOnlyOptimal,
                        shaderStage,
                        AccessFlags.ShaderReadBit);
                }
                else
                {
                    var toShaderRead = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = AccessFlags.TransferWriteBit,
                        DstAccessMask = AccessFlags.ShaderReadBit,
                        OldLayout = ImageLayout.TransferDstOptimal,
                        NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = texture.Image,
                        SubresourceRange = ColorSubresourceRange(),
                    };
                    _vk.CmdPipelineBarrier(
                        _commandBuffer,
                        PipelineStageFlags.TransferBit,
                        shaderStage,
                        0,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &toShaderRead);
                }
            }
        }

        private void RecordCopyOnSample(
            TextureResource texture,
            GuestImageResource copySource,
            PipelineStageFlags shaderStage)
        {
            // Bring the producer image into TransferSrc scope.
            TransitionGuestImage(
                copySource,
                ImageLayout.TransferSrcOptimal,
                PipelineStageFlags.TransferBit,
                AccessFlags.TransferReadBit);

            // Our freshly-created destination image is in Undefined.
            var toTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = texture.Image,
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toTransfer);

            var sameSize =
                copySource.Width == texture.Width &&
                copySource.Height == texture.Height;
            var subresource = new ImageSubresourceLayers(
                ImageAspectFlags.ColorBit,
                0,
                0,
                1);
            if (copySource.Format == texture.Format && sameSize)
            {
                var copyRegion = new ImageCopy
                {
                    SrcSubresource = subresource,
                    DstSubresource = subresource,
                    Extent = new Extent3D(texture.Width, texture.Height, 1),
                };
                _vk.CmdCopyImage(
                    _commandBuffer,
                    copySource.Image,
                    ImageLayout.TransferSrcOptimal,
                    texture.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &copyRegion);
            }
            else
            {
                var sourceOffsets = new ImageBlit.SrcOffsetsBuffer
                {
                    Element0 = new Offset3D(0, 0, 0),
                    Element1 = new Offset3D(
                        checked((int)copySource.Width),
                        checked((int)copySource.Height),
                        1),
                };
                var destinationOffsets = new ImageBlit.DstOffsetsBuffer
                {
                    Element0 = new Offset3D(0, 0, 0),
                    Element1 = new Offset3D(
                        checked((int)texture.Width),
                        checked((int)texture.Height),
                        1),
                };
                var blitRegion = new ImageBlit
                {
                    SrcSubresource = subresource,
                    SrcOffsets = sourceOffsets,
                    DstSubresource = subresource,
                    DstOffsets = destinationOffsets,
                };
                // Integer (uint/sint) formats do not support linear filtering
                // (VUID-vkCmdBlitImage-filter-02001); they must blit with
                // nearest. Only non-integer color formats get linear scaling.
                var blitFilter =
                    IsIntegerFormat(copySource.Format) || IsIntegerFormat(texture.Format)
                        ? Filter.Nearest
                        : Filter.Linear;
                _vk.CmdBlitImage(
                    _commandBuffer,
                    copySource.Image,
                    ImageLayout.TransferSrcOptimal,
                    texture.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &blitRegion,
                    blitFilter);
            }

            var toShaderRead = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = texture.Image,
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                shaderStage,
                0,
                0,
                null,
                0,
                null,
                1,
                &toShaderRead);
        }

        private void RecordStorageImagesForWrite(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            var transitioned = new HashSet<GuestImageResource>();
            foreach (var texture in resources.Textures)
            {
                if (!texture.IsStorage ||
                    texture.GuestImage is not { } guestImage ||
                    !transitioned.Add(guestImage))
                {
                    continue;
                }

                // The write access keeps the plan from eliding this barrier,
                // so back-to-back dispatches writing the same image still get
                // their write-after-write ordering.
                TransitionGuestImage(
                    guestImage,
                    ImageLayout.General,
                    shaderStage,
                    AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
            }
        }

        private void RecordStorageImagesForRead(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            var transitioned = new HashSet<GuestImageResource>();
            foreach (var texture in resources.Textures)
            {
                if (!texture.IsStorage ||
                    texture.GuestImage is not { } guestImage ||
                    !transitioned.Add(guestImage))
                {
                    continue;
                }

                TransitionGuestImage(
                    guestImage,
                    ImageLayout.ShaderReadOnlyOptimal,
                    shaderStage,
                    AccessFlags.ShaderReadBit);
            }
        }

        // Stamp an image as holding freshly written content. Alias selection
        // ranks candidates by this generation first, so the most recently
        // produced sibling wins over a re-bound but stale same-address variant.
        private void MarkGuestImageContentWritten(GuestImageResource image)
        {
            image.ContentGeneration =
                Interlocked.Increment(ref _guestImageContentGeneration);
        }

        private void MarkStorageImagesInitialized(
            TranslatedDrawResources resources,
            bool traceContents = true)
        {
            List<GuestImageResource>? traceImages = null;
            lock (_gate)
            {
                foreach (var texture in resources.Textures)
                {
                    if (!texture.IsStorage ||
                        texture.Address == 0 ||
                        texture.GuestImage is not { } guestImage)
                    {
                        continue;
                    }

                    guestImage.Initialized = true;
                    MarkGuestImageContentWritten(guestImage);
                    guestImage.InitialUploadPending = false;
                    if (texture.UpdatesCpuContent)
                    {
                        guestImage.CpuContentFingerprint = texture.CpuContentFingerprint;
                    }

                    var format = GetGuestTextureFormat(guestImage.Format);
                    if (format != 0)
                    {
                        _availableGuestImages[texture.Address] = format;
                        _gpuGuestImages[texture.Address] = format;
                        _renderTargetGuestImages[texture.Address] = format;
                        NoteRenderedGuestImageLocked(
                            texture.Address,
                            guestImage.Width,
                            guestImage.Height);
                    }

                    if (traceContents &&
                        ShouldTraceGuestImageContents(guestImage))
                    {
                        traceImages ??= [];
                        traceImages.Add(guestImage);
                    }
                }
            }

            if (traceImages is null)
            {
                return;
            }

            foreach (var image in traceImages)
            {
                TraceGuestImageContents(image);
            }
        }

        private void MarkSampledImagesInitialized(
            TranslatedDrawResources resources)
        {
            lock (_gate)
            {
                foreach (var texture in resources.Textures)
                {
                    if (!texture.NeedsUpload ||
                        texture.IsStorage ||
                        texture.Address == 0 ||
                        texture.GuestImage is not { } guestImage)
                    {
                        continue;
                    }

                    guestImage.Initialized = true;
                    MarkGuestImageContentWritten(guestImage);
                    guestImage.InitialUploadPending = false;
                    if (texture.UpdatesCpuContent)
                    {
                        guestImage.CpuContentFingerprint = texture.CpuContentFingerprint;
                    }
                }
            }
        }

        private bool ShouldTraceGuestImageContents(GuestImageResource image)
        {
            if (_memoryPanic)
            {
                // After a first vkAllocateMemory OOM every optional readback
                // (each allocates a per-image host-visible buffer) is shed to
                // keep the starved driver alive.
                return false;
            }

            if (image.Address == 0)
            {
                return false;
            }

            var addressMatched = ShouldTraceGuestImageAddressForDiagnostics(image.Address);
            var broadTrace =
                ShouldTraceGuestImageContentsForDiagnostics() &&
                image.Width >= 1280 &&
                image.Height >= 720;
            // Re-read explicitly targeted addresses (SHARPEMU_TRACE_GUEST_IMAGE_ADDRS)
            // every frame, skipping the per-(addr,format) dedup, so a target filled
            // by a late NGG draw is not masked by an earlier empty read. Scoped to
            // the address list to avoid the every-image readback stalling the queue.
            if (addressMatched)
            {
                return true;
            }

            return broadTrace &&
                   _tracedGuestImageContents.Add((image.Address, image.Format));
        }

        private static bool ShouldTraceGuestImageContentsForDiagnostics() =>
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES"),
                "1",
                StringComparison.Ordinal);

        private static bool ShouldTraceGuestImageAddressForDiagnostics(ulong address)
        {
            return AddressListContains(
                "SHARPEMU_TRACE_GUEST_IMAGE_ADDRS",
                address);
        }

        private static bool ShouldTraceGuestImageWriteForDiagnostics(ulong address)
        {
            return AddressListContains(
                "SHARPEMU_TRACE_GUEST_WRITES",
                address);
        }

        private static bool AddressListContains(
            string environmentVariable,
            ulong address)
        {
            var addresses = Environment.GetEnvironmentVariable(environmentVariable);
            if (string.IsNullOrWhiteSpace(addresses))
            {
                return false;
            }

            foreach (var token in addresses.Split(
                         [',', ';', ' ', '\t'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token == "*")
                {
                    return true;
                }

                var span = token.AsSpan();
                if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    span = span[2..];
                }

                if (ulong.TryParse(
                        span,
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed) &&
                    parsed == address)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ShouldTraceVulkanResources() =>
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_VK_RESOURCES"),
                "1",
                StringComparison.Ordinal);

        private void RecordTranslatedGraphicsPass(
            TranslatedDrawResources resources,
            RenderPass renderPass,
            Framebuffer framebuffer,
            Extent2D extent,
            bool hasDepth = false,
            float clearDepthValue = 1.0f)
        {
            // One clear value per attachment: the color attachments (LoadOp.Load)
            // ignore theirs, and the trailing depth attachment (index colorCount)
            // clears to the guest clear value when its LoadOp is Clear. Providing
            // the depth value unconditionally is harmless when the LoadOp is Load.
            var colorCount = Math.Max(resources.Blends.Length, 1);
            var clearCount = hasDepth ? colorCount + 1 : colorCount;
            var clearValues = stackalloc ClearValue[clearCount];
            for (var index = 0; index < colorCount; index++)
            {
                clearValues[index] = default;
            }

            if (hasDepth)
            {
                clearValues[colorCount] = new ClearValue
                {
                    DepthStencil = new ClearDepthStencilValue(clearDepthValue, 0),
                };
            }

            var renderPassInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = renderPass,
                Framebuffer = framebuffer,
                RenderArea = new Rect2D(new Offset2D(0, 0), extent),
                ClearValueCount = (uint)clearCount,
                PClearValues = clearValues,
            };
            _vk.CmdBeginRenderPass(
                _commandBuffer,
                &renderPassInfo,
                SubpassContents.Inline);
            _vk.CmdBindPipeline(
                _commandBuffer,
                PipelineBindPoint.Graphics,
                resources.Pipeline);
            if (resources.DescriptorSet.Handle != 0)
            {
                var descriptorSet = resources.DescriptorSet;
                _vk.CmdBindDescriptorSets(
                    _commandBuffer,
                    PipelineBindPoint.Graphics,
                    resources.PipelineLayout,
                    0,
                    1,
                    &descriptorSet,
                    0,
                    null);
            }

            var drawScissor = ClampScissor(resources.Scissor, extent);
            if (resources.CaptureInvocationCount > 0)
            {
                var vp = ClampViewport(resources.Viewport, extent);
                AgcExports.TraceNgg(
                    $"vk.ngg_raster extent={extent.Width}x{extent.Height} " +
                    $"scissor={drawScissor.X},{drawScissor.Y},{drawScissor.Width}x{drawScissor.Height} " +
                    $"rawScissor={(resources.Scissor.HasValue ? $"{resources.Scissor.Value.Width}x{resources.Scissor.Value.Height}" : "null")} " +
                    $"viewport={vp.X},{vp.Y},{vp.Width}x{vp.Height} " +
                    $"vtxCount={resources.VertexCount} idx32={resources.Index32Bit} " +
                    $"vbufs={resources.VertexBuffers.Length} blends={resources.Blends.Length} " +
                    $"hasDepth={hasDepth} idxHandle={(resources.IndexBuffer.Handle != 0)} " +
                    $"pipeline={(resources.Pipeline.Handle != 0)} inst={resources.InstanceCount} " +
                    $"skip={(drawScissor.Width == 0 || drawScissor.Height == 0)}");
            }

            if (drawScissor.Width == 0 || drawScissor.Height == 0)
            {
                _vk.CmdEndRenderPass(_commandBuffer);
                return;
            }

            var drawViewport = ClampViewport(resources.Viewport, extent);
            _vk.CmdSetViewport(_commandBuffer, 0, 1, &drawViewport);
            if (resources.VertexBuffers.Length != 0)
            {
                var buffers = stackalloc VkBuffer[resources.VertexBuffers.Length];
                var offsets = stackalloc ulong[resources.VertexBuffers.Length];
                for (var index = 0; index < resources.VertexBuffers.Length; index++)
                {
                    buffers[index] = resources.VertexBuffers[index].Buffer;
                    offsets[index] = GetVertexBindingOffset(resources.VertexBuffers[index]);
                }

                _vk.CmdBindVertexBuffers(
                    _commandBuffer,
                    0,
                    (uint)resources.VertexBuffers.Length,
                    buffers,
                    offsets);
            }

            var scissor = new Rect2D(
                new Offset2D(drawScissor.X, drawScissor.Y),
                new Extent2D(drawScissor.Width, drawScissor.Height));
            _vk.CmdSetScissor(_commandBuffer, 0, 1, &scissor);

            if (resources.IndexBuffer.Handle != 0)
            {
                _vk.CmdBindIndexBuffer(
                    _commandBuffer,
                    resources.IndexBuffer,
                    0,
                    resources.Index32Bit ? IndexType.Uint32 : IndexType.Uint16);
                if (resources.HasIndirectArgs && resources.IndirectArgsBuffer.Handle != 0)
                {
                    // The args buffer is host-visible and CPU-seeded before this
                    // submit, so the queue submit's implicit host-write barrier
                    // makes it visible -- no in-pass buffer barrier (which Vulkan
                    // forbids inside a render pass instance) is needed.
                    _vk.CmdDrawIndexedIndirect(
                        _commandBuffer,
                        resources.IndirectArgsBuffer,
                        resources.IndirectArgsOffset,
                        1,
                        resources.IndirectArgsStride);
                    if (!_tracedNativeIndirectDraw)
                    {
                        _tracedNativeIndirectDraw = true;
                        Console.Error.WriteLine(
                            "[LOADER][TRACE] vk.draw_indexed_indirect_native recorded " +
                            $"argsOffset=0x{resources.IndirectArgsOffset:X} " +
                            $"stride={resources.IndirectArgsStride} " +
                            $"index32={(resources.Index32Bit ? 1 : 0)}");
                    }
                }
                else
                {
                    _vk.CmdDrawIndexed(
                        _commandBuffer,
                        resources.VertexCount,
                        resources.InstanceCount,
                        0,
                        0,
                        0);
                }
            }
            else
            {
                _vk.CmdDraw(
                    _commandBuffer,
                    resources.VertexCount,
                    resources.InstanceCount,
                    0,
                    0);
            }
            _vk.CmdEndRenderPass(_commandBuffer);
        }

        private void DestroyTranslatedDrawResources(TranslatedDrawResources resources)
        {
            if (resources.CaptureReadbackBuffer.Handle != 0)
            {
                // The fence has signalled by the time resources are destroyed, so
                // the readback holds the compute's actual output. Log the first
                // few captured vec4 positions to see if the compute wrote real
                // clip-space or zeros.
                void* mapped = null;
                var floatCount = (int)Math.Min(resources.CaptureReadbackSize / sizeof(float), 16UL);
                if (_vk.MapMemory(
                        _device, resources.CaptureReadbackMemory, 0,
                        resources.CaptureReadbackSize, 0, &mapped) == Result.Success)
                {
                    var floats = new ReadOnlySpan<float>(mapped, floatCount);
                    var parts = new string[floatCount];
                    for (var i = 0; i < floatCount; i++)
                    {
                        parts[i] = floats[i].ToString("0.###", CultureInfo.InvariantCulture);
                    }

                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.ngg_capture_out first16=[{string.Join(",", parts)}]");
                    _vk.UnmapMemory(_device, resources.CaptureReadbackMemory);
                }

                _vk.DestroyBuffer(_device, resources.CaptureReadbackBuffer, null);
                FreeDeviceMemory(resources.CaptureReadbackMemory);
            }

            if (resources.CaptureTargetReadbackBuffer.Handle != 0)
            {
                void* mapped = null;
                if (_vk.MapMemory(
                        _device, resources.CaptureTargetReadbackMemory, 0,
                        resources.CaptureTargetReadbackSize, 0, &mapped) == Result.Success)
                {
                    var bytes = new ReadOnlySpan<byte>(
                        mapped, checked((int)resources.CaptureTargetReadbackSize));
                    long nonzero = 0;
                    foreach (var b in bytes)
                    {
                        if (b != 0)
                        {
                            nonzero++;
                        }
                    }

                    var bpp = (int)resources.CaptureTargetBpp;
                    // Count pixels whose RGB (not just alpha) is non-zero, so an
                    // opaque-black target (alpha=1, rgb=0) is not mistaken for
                    // real content -- the exact false positive that made the
                    // composite chain look filled when it was black.
                    long nonblackPixels = 0;
                    var pixelCount = bpp > 0 ? bytes.Length / bpp : 0;
                    var rgbBytes = Math.Max(bpp - (bpp / 4), 0);
                    for (var p = 0; p < pixelCount; p++)
                    {
                        var pixel = bytes.Slice(p * bpp, bpp);
                        for (var k = 0; k < rgbBytes; k++)
                        {
                            if (pixel[k] != 0)
                            {
                                nonblackPixels++;
                                break;
                            }
                        }
                    }

                    NoteGuestImageContentObserved(
                        resources.CaptureTargetAddress,
                        resources.CaptureTargetWidth,
                        resources.CaptureTargetHeight,
                        nonblackPixels > 0);
                    var center = resources.CaptureTargetReadbackSize >= (ulong)bpp
                        ? Convert.ToHexString(
                            bytes.Slice((bytes.Length / 2) & ~(bpp - 1), bpp))
                        : "";
                    Console.Error.WriteLine(
                        "[LOADER][TRACE] vk.ngg_target_readback " +
                        $"addr=0x{resources.CaptureTargetAddress:X16} " +
                        $"size={resources.CaptureTargetWidth}x{resources.CaptureTargetHeight} " +
                        $"nonblack_pixels={nonblackPixels}/{pixelCount} " +
                        $"nonzero_bytes={nonzero}/{resources.CaptureTargetReadbackSize} " +
                        $"center={center}");
                    _vk.UnmapMemory(_device, resources.CaptureTargetReadbackMemory);
                }

                _vk.DestroyBuffer(_device, resources.CaptureTargetReadbackBuffer, null);
                FreeDeviceMemory(resources.CaptureTargetReadbackMemory);
            }

            if (resources.TransientFramebuffer.Handle != 0)
            {
                _vk.DestroyFramebuffer(_device, resources.TransientFramebuffer, null);
            }

            if (resources.TransientRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resources.TransientRenderPass, null);
            }

            foreach (var texture in resources.Textures)
            {
                if (texture is null)
                {
                    continue;
                }

                if (texture.OwnsStorage && texture.View.Handle != 0)
                {
                    _vk.DestroyImageView(_device, texture.View, null);
                }

                if (texture.OwnsStorage && texture.Image.Handle != 0)
                {
                    _vk.DestroyImage(_device, texture.Image, null);
                }

                if (texture.OwnsStorage)
                {
                    FreeDeviceMemory(texture.ImageMemory);
                }

                // Only reached after the submission fence signaled (or before
                // submission on unwind), so the pooled staging buffer is idle.
                RecycleHostBuffer(texture.StagingBuffer, texture.StagingMemory);

                if (texture.NeedsUpload &&
                    texture.GuestImage is { Initialized: false } guestImage)
                {
                    guestImage.InitialUploadPending = false;
                }
            }

            foreach (var globalBuffer in resources.GlobalMemoryBuffers)
            {
                if (globalBuffer is null)
                {
                    continue;
                }

                RecycleHostBuffer(globalBuffer.Buffer, globalBuffer.Memory);
            }

            foreach (var vertexBuffer in resources.VertexBuffers)
            {
                if (vertexBuffer is null || vertexBuffer.External)
                {
                    continue;
                }

                RecycleHostBuffer(vertexBuffer.Buffer, vertexBuffer.Memory);
            }

            RecycleHostBuffer(resources.IndexBuffer, resources.IndexMemory);
            RecycleHostBuffer(resources.IndirectArgsBuffer, resources.IndirectArgsMemory);

            if (!resources.PipelineCached && resources.Pipeline.Handle != 0)
            {
                _vk.DestroyPipeline(_device, resources.Pipeline, null);
            }

            if (resources.DescriptorPool.Handle != 0)
            {
                _vk.DestroyDescriptorPool(_device, resources.DescriptorPool, null);
            }

            if (!resources.DescriptorLayoutCached &&
                resources.PipelineLayout.Handle != 0)
            {
                _vk.DestroyPipelineLayout(_device, resources.PipelineLayout, null);
            }

            if (!resources.DescriptorLayoutCached &&
                resources.DescriptorSetLayout.Handle != 0)
            {
                _vk.DestroyDescriptorSetLayout(_device, resources.DescriptorSetLayout, null);
            }

            // The capture-compute child owns the NGG prepass pipeline/descriptor
            // pool and every capture buffer (the K host inputs plus the
            // device-local position output). Releasing it here means it is freed
            // on exactly the same fence as the graphics resources it feeds.
            if (resources.CaptureCompute is not null)
            {
                DestroyTranslatedDrawResources(resources.CaptureCompute);
                resources.CaptureCompute = null;
            }
        }

        private void RecordUpload(uint imageIndex)
        {
            var oldLayout = _imageInitialized[imageIndex]
                ? ImageLayout.PresentSrcKhr
                : ImageLayout.Undefined;
            var toTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = _imageInitialized[imageIndex] ? AccessFlags.MemoryReadBit : 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = oldLayout,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                _imageInitialized[imageIndex]
                    ? PipelineStageFlags.BottomOfPipeBit
                    : PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toTransfer);

            var copyRegion = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    LayerCount = 1,
                },
                ImageExtent = new Extent3D(_extent.Width, _extent.Height, 1),
            };
            _vk.CmdCopyBufferToImage(
                _commandBuffer,
                _stagingBuffer,
                _swapchainImages[imageIndex],
                ImageLayout.TransferDstOptimal,
                1,
                &copyRegion);

            var toPresent = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.MemoryReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.PresentSrcKhr,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.BottomOfPipeBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toPresent);
        }

        private void RecordGuestImageBlit(
            uint imageIndex,
            GuestImageResource source)
        {
            _totalPresentCount++;
            if (RenderDocCapture.IsActive)
            {
                if (!RenderDocClock.IsRunning)
                {
                    RenderDocClock.Start();
                }
                else if (RenderDocClock.Elapsed.TotalSeconds >= RenderDocCaptureDelaySeconds)
                {
                    RenderDocCapture.TriggerNextFrame();
                }
            }

            // SHARPEMU_DUMP_SWAPCHAIN enables the swapchain readback + FRAMEDUMP
            // independently of the (OOM-prone) per-guest-image readback, and
            // captures far more frames, so a late-appearing intro/menu frame is
            // actually sampled instead of only the first few black loading frames.
            var dumpSwapchain = string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SWAPCHAIN"),
                "1",
                StringComparison.Ordinal);
            var traceDestination =
                (ShouldTracePresentedGuestImageContentsForDiagnostics() || dumpSwapchain) &&
                _swapchainCaptureCount < 400 &&
                (_totalPresentCount <= 40 || _totalPresentCount % 8 == 0);
            if (traceDestination)
            {
                _swapchainCaptureCount++;
            }

            _tracedPresentedSwapchain |= traceDestination;
            BeginDebugLabel(
                _commandBuffer,
                $"SharpEmu present image 0x{source.Address:X16}");

            TransitionGuestImage(
                source,
                ImageLayout.TransferSrcOptimal,
                PipelineStageFlags.TransferBit,
                AccessFlags.TransferReadBit);

            var destinationToTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = _imageInitialized[imageIndex]
                    ? AccessFlags.MemoryReadBit
                    : 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = _imageInitialized[imageIndex]
                    ? ImageLayout.PresentSrcKhr
                    : ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &destinationToTransfer);

            var sourceOffsets = new ImageBlit.SrcOffsetsBuffer
            {
                Element0 = new Offset3D(0, 0, 0),
                Element1 = new Offset3D(
                    checked((int)source.Width),
                    checked((int)source.Height),
                    1),
            };
            var destinationOffsets = new ImageBlit.DstOffsetsBuffer
            {
                Element0 = new Offset3D(0, 0, 0),
                Element1 = new Offset3D(
                    checked((int)_extent.Width),
                    checked((int)_extent.Height),
                    1),
            };
            var region = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(
                    ImageAspectFlags.ColorBit,
                    0,
                    0,
                    1),
                SrcOffsets = sourceOffsets,
                DstSubresource = new ImageSubresourceLayers(
                    ImageAspectFlags.ColorBit,
                    0,
                    0,
                    1),
                DstOffsets = destinationOffsets,
            };
            _vk.CmdBlitImage(
                _commandBuffer,
                source.Image,
                ImageLayout.TransferSrcOptimal,
                _swapchainImages[imageIndex],
                ImageLayout.TransferDstOptimal,
                1,
                &region,
                Filter.Nearest);

            if (traceDestination)
            {
                var destinationToReadback = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = _swapchainImages[imageIndex],
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &destinationToReadback);

                var copyRegion = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        LayerCount = 1,
                    },
                    ImageExtent = new Extent3D(_extent.Width, _extent.Height, 1),
                };
                _vk.CmdCopyImageToBuffer(
                    _commandBuffer,
                    _swapchainImages[imageIndex],
                    ImageLayout.TransferSrcOptimal,
                    _stagingBuffer,
                    1,
                    &copyRegion);
                _swapchainReadbackPending = true;
            }

            TransitionGuestImage(
                source,
                ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags.VertexShaderBit |
                PipelineStageFlags.FragmentShaderBit |
                PipelineStageFlags.ComputeShaderBit,
                AccessFlags.ShaderReadBit);

            var destinationToPresent = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = traceDestination
                    ? AccessFlags.TransferReadBit
                    : AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.MemoryReadBit,
                OldLayout = traceDestination
                    ? ImageLayout.TransferSrcOptimal
                    : ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.PresentSrcKhr,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.AllCommandsBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &destinationToPresent);
            EndDebugLabel(_commandBuffer);
        }

        private void TraceSwapchainReadback()
        {
            _swapchainReadbackPending = false;
            var byteCount = checked((ulong)_extent.Width * _extent.Height * 4);
            void* mapped;
            Check(
                _vk.MapMemory(_device, _stagingMemory, 0, byteCount, 0, &mapped),
                "vkMapMemory(swapchain readback)");
            try
            {
                var bytes = new ReadOnlySpan<byte>(mapped, checked((int)byteCount));
                var nonzeroBytes = 0L;
                var nonblackPixels = 0L;
                ulong hash = 14695981039346656037UL;
                for (var offset = 0; offset < bytes.Length; offset += 4)
                {
                    var b0 = bytes[offset];
                    var b1 = bytes[offset + 1];
                    var b2 = bytes[offset + 2];
                    var b3 = bytes[offset + 3];
                    nonzeroBytes += b0 == 0 ? 0 : 1;
                    nonzeroBytes += b1 == 0 ? 0 : 1;
                    nonzeroBytes += b2 == 0 ? 0 : 1;
                    nonzeroBytes += b3 == 0 ? 0 : 1;
                    nonblackPixels += b0 != 0 || b1 != 0 || b2 != 0 ? 1 : 0;
                    hash = (hash ^ b0) * 1099511628211UL;
                    hash = (hash ^ b1) * 1099511628211UL;
                    hash = (hash ^ b2) * 1099511628211UL;
                    hash = (hash ^ b3) * 1099511628211UL;
                }

                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.swapchain_image size={_extent.Width}x{_extent.Height} " +
                    $"format={_swapchainFormat} nonzero_bytes={nonzeroBytes}/{byteCount} " +
                    $"nonblack_pixels={nonblackPixels}/{(ulong)_extent.Width * _extent.Height} " +
                    $"hash=0x{hash:X16}");

                // When an explicit swapchain dump is requested, dump the frame
                // regardless of the nonblack heuristic -- the heuristic itself may
                // be undercounting, so capture the raw pixels to see the truth.
                var forceDump = string.Equals(
                    Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SWAPCHAIN"),
                    "1",
                    StringComparison.Ordinal);
                var contentThreshold = (long)_extent.Width * _extent.Height / 50;
                if (_frameDumpBudget > 0 && (forceDump || nonblackPixels > contentThreshold))
                {
                    _frameDumpBudget--;
                    const int outWidth = 384;
                    const int outHeight = 216;
                    var srcWidth = (int)_extent.Width;
                    var srcHeight = (int)_extent.Height;
                    var isBgra =
                        _swapchainFormat == Format.B8G8R8A8Unorm ||
                        _swapchainFormat == Format.B8G8R8A8Srgb;
                    var rgb = new byte[outWidth * outHeight * 3];
                    for (var oy = 0; oy < outHeight; oy++)
                    {
                        var sy = oy * srcHeight / outHeight;
                        for (var ox = 0; ox < outWidth; ox++)
                        {
                            var sx = ox * srcWidth / outWidth;
                            var si = (sy * srcWidth + sx) * 4;
                            var di = (oy * outWidth + ox) * 3;
                            rgb[di] = isBgra ? bytes[si + 2] : bytes[si];
                            rgb[di + 1] = bytes[si + 1];
                            rgb[di + 2] = isBgra ? bytes[si] : bytes[si + 2];
                        }
                    }

                    Console.Error.WriteLine(
                        $"[LOADER][FRAMEDUMP] frame={_swapchainCaptureCount} w={outWidth} h={outHeight} " +
                        $"fmt=RGB nonblack={nonblackPixels} b64={Convert.ToBase64String(rgb)}");
                }
            }
            finally
            {
                _vk.UnmapMemory(_device, _stagingMemory);
            }
        }

        private Extent2D ChooseExtent(SurfaceCapabilitiesKHR capabilities)
        {
            if (capabilities.CurrentExtent.Width != uint.MaxValue)
            {
                var fallbackWidth = _extent.Width != 0
                    ? _extent.Width
                    : DefaultWindowWidth;
                var fallbackHeight = _extent.Height != 0
                    ? _extent.Height
                    : DefaultWindowHeight;
                return new Extent2D(
                    ClampSurfaceExtent(
                        capabilities.CurrentExtent.Width,
                        fallbackWidth,
                        capabilities.MinImageExtent.Width,
                        capabilities.MaxImageExtent.Width),
                    ClampSurfaceExtent(
                        capabilities.CurrentExtent.Height,
                        fallbackHeight,
                        capabilities.MinImageExtent.Height,
                        capabilities.MaxImageExtent.Height));
            }

            var size = _window.FramebufferSize;
            return new Extent2D(
                ClampSurfaceExtent(
                    (uint)Math.Max(size.X, 1),
                    DefaultWindowWidth,
                    capabilities.MinImageExtent.Width,
                    capabilities.MaxImageExtent.Width),
                ClampSurfaceExtent(
                    (uint)Math.Max(size.Y, 1),
                    DefaultWindowHeight,
                    capabilities.MinImageExtent.Height,
                    capabilities.MaxImageExtent.Height));
        }

        private static uint ClampSurfaceExtent(
            uint value,
            uint fallback,
            uint minimum,
            uint maximum)
        {
            value = value <= 1 && fallback > 1 ? fallback : value;
            minimum = Math.Max(minimum, 1u);
            maximum = Math.Max(maximum, minimum);
            return Math.Clamp(value, minimum, maximum);
        }

        private static SurfaceFormatKHR ChooseSurfaceFormat(IReadOnlyList<SurfaceFormatKHR> formats)
        {
            foreach (var format in formats)
            {
                if (format.Format is Format.B8G8R8A8Srgb or Format.B8G8R8A8Unorm &&
                    format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                {
                    return format;
                }
            }

            return formats.Count > 0
                ? formats[0]
                : throw new InvalidOperationException("The Vulkan surface exposes no pixel formats.");
        }

        private static CompositeAlphaFlagsKHR ChooseCompositeAlpha(CompositeAlphaFlagsKHR supported)
        {
            foreach (var candidate in new[]
                     {
                         CompositeAlphaFlagsKHR.OpaqueBitKhr,
                         CompositeAlphaFlagsKHR.PreMultipliedBitKhr,
                         CompositeAlphaFlagsKHR.PostMultipliedBitKhr,
                         CompositeAlphaFlagsKHR.InheritBitKhr,
                     })
            {
                if ((supported & candidate) != 0)
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("The Vulkan surface exposes no composite alpha mode.");
        }

        private static ImageSubresourceRange ColorSubresourceRange(
            uint baseMipLevel = 0,
            uint levelCount = 1) =>
            new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = baseMipLevel,
                LevelCount = levelCount,
                LayerCount = 1,
            };

        private static ImageSubresourceRange DepthSubresourceRange(
            uint baseMipLevel = 0,
            uint levelCount = 1) =>
            new()
            {
                AspectMask = ImageAspectFlags.DepthBit,
                BaseMipLevel = baseMipLevel,
                LevelCount = levelCount,
                LayerCount = 1,
            };

        private static byte[] ScaleBgra(byte[] source, uint sourceWidth, uint sourceHeight, uint width, uint height)
        {
            var destination = new byte[checked((int)(width * height * 4))];
            for (uint y = 0; y < height; y++)
            {
                var sourceY = (uint)(((ulong)y * sourceHeight) / height);
                for (uint x = 0; x < width; x++)
                {
                    var sourceX = (uint)(((ulong)x * sourceWidth) / width);
                    var sourceOffset = checked((int)(((ulong)sourceY * sourceWidth + sourceX) * 4));
                    var destinationOffset = checked((int)(((ulong)y * width + x) * 4));
                    source.AsSpan(sourceOffset, 4).CopyTo(destination.AsSpan(destinationOffset, 4));
                }
            }

            return destination;
        }

        private void DisposeVulkan()
        {
            if (!_vulkanReady)
            {
                return;
            }

            if (_debugUtils is not null && _debugMessenger.Handle != 0)
            {
                _debugUtils.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
            }
            _vulkanReady = false;
            _vk.DeviceWaitIdle(_device);
            CompletePendingPresentation(wait: false);
            CollectCompletedGuestSubmissions(waitForOldest: false);
            SavePipelineCache();
            foreach (var pipeline in _computePipelines.Values)
            {
                _vk.DestroyPipeline(_device, pipeline, null);
            }
            _computePipelines.Clear();
            foreach (var pipeline in _graphicsPipelines.Values)
            {
                _vk.DestroyPipeline(_device, pipeline, null);
            }
            _graphicsPipelines.Clear();
            if (_pipelineCache.Handle != 0)
            {
                _vk.DestroyPipelineCache(_device, _pipelineCache, null);
                _pipelineCache = default;
            }
            foreach (var layout in _descriptorLayouts.Values)
            {
                _vk.DestroyPipelineLayout(_device, layout.PipelineLayout, null);
                if (layout.DescriptorSetLayout.Handle != 0)
                {
                    _vk.DestroyDescriptorSetLayout(
                        _device,
                        layout.DescriptorSetLayout,
                        null);
                }
            }
            _descriptorLayouts.Clear();
            foreach (var sampler in _samplers.Values)
            {
                _vk.DestroySampler(_device, sampler, null);
            }
            _samplers.Clear();
            _shaderDigests.Clear();
            foreach (var allocation in _hostBufferAllocations.Values)
            {
                _vk.DestroyBuffer(_device, allocation.Buffer, null);
                FreeDeviceMemory(allocation.Memory);
            }
            _hostBufferAllocations.Clear();
            _hostBufferPool.Clear();
            _pooledHostBufferBytes = 0;
            foreach (var guestImage in _guestImages.Surfaces)
            {
                DestroyGuestImage(guestImage);
            }
            _guestImages.Clear();
            // Ordered-flip snapshots are not registered in _guestImages; free the
            // captured-but-unpresented ones and the retire list. The device is
            // idle here (DeviceWaitIdle above), so this is safe.
            foreach (var snapshot in _guestImageVersions.Values)
            {
                DestroyGuestImage(snapshot);
            }
            _guestImageVersions.Clear();
            _capturedGuestFlipVersions.Clear();
            DrainRetiredGuestFlipSnapshots();
            lock (_gate)
            {
                _availableGuestImages.Clear();
                _gpuGuestImages.Clear();
                _renderTargetGuestImages.Clear();
                _latestPresentableGuestImageAddress = 0;
                _frameCompositeCandidateAddress = 0;
                _frameCompositeCandidateScore = 0;
                _lastFrameCompositeAddress = 0;
                _displayBufferCopySources.Clear();
            }
            DestroySwapchainResources();
            if (_device.Handle != 0)
            {
                _vk.DestroyDevice(_device, null);
                _device = default;
            }
            if (_surface.Handle != 0)
            {
                _surfaceApi.DestroySurface(_instance, _surface, null);
                _surface = default;
            }
            if (_instance.Handle != 0)
            {
                _vk.DestroyInstance(_instance, null);
                _instance = default;
            }
        }

        private void RecreateSwapchainResources(string operation, Result result)
        {
            if (_device.Handle == 0)
            {
                return;
            }

            Check(
                _surfaceApi.GetPhysicalDeviceSurfaceCapabilities(
                    _physicalDevice,
                    _surface,
                    out var capabilities),
                "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
            var framebufferSize = _window.FramebufferSize;
            var hasFixedExtent = capabilities.CurrentExtent.Width != uint.MaxValue;
            var surfaceWidth = hasFixedExtent
                ? capabilities.CurrentExtent.Width
                : (uint)Math.Max(framebufferSize.X, 0);
            var surfaceHeight = hasFixedExtent
                ? capabilities.CurrentExtent.Height
                : (uint)Math.Max(framebufferSize.Y, 0);
            if (surfaceWidth <= 1 || surfaceHeight <= 1)
            {
                if (!_swapchainRecreateDeferred)
                {
                    _swapchainRecreateDeferred = true;
                    Console.Error.WriteLine(
                        $"[LOADER][INFO] Vulkan VideoOut deferred swapchain recreation: " +
                        $"surface={surfaceWidth}x{surfaceHeight}");
                }

                return;
            }

            _swapchainRecreateDeferred = false;
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan VideoOut recreating swapchain after {operation}: {result}");
            _vk.DeviceWaitIdle(_device);
            CompletePendingPresentation(wait: false);
            CollectCompletedGuestSubmissions(waitForOldest: false);
            DestroySwapchainResources();
            CreateSwapchain();
            CreateCommandResources();
            CreateGuestDrawResources();
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan VideoOut recreated swapchain: " +
                $"{_extent.Width}x{_extent.Height}, format={_swapchainFormat}");
        }

        private void DestroySwapchainResources()
        {
            if (_stagingBuffer.Handle != 0)
            {
                _vk.DestroyBuffer(_device, _stagingBuffer, null);
                _stagingBuffer = default;
            }
            if (_stagingMemory.Handle != 0)
            {
                FreeDeviceMemory(_stagingMemory);
                _stagingMemory = default;
                _stagingSize = 0;
            }
            if (_imageAvailable.Handle != 0)
            {
                _vk.DestroySemaphore(_device, _imageAvailable, null);
                _imageAvailable = default;
            }
            if (_renderFinished.Handle != 0)
            {
                _vk.DestroySemaphore(_device, _renderFinished, null);
                _renderFinished = default;
            }
            if (_presentationFence.Handle != 0)
            {
                _vk.DestroyFence(_device, _presentationFence, null);
                _presentationFence = default;
            }
            if (_barycentricPipeline.Handle != 0)
            {
                _vk.DestroyPipeline(_device, _barycentricPipeline, null);
                _barycentricPipeline = default;
            }
            if (_pipelineLayout.Handle != 0)
            {
                _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
                _pipelineLayout = default;
            }
            foreach (var framebuffer in _framebuffers)
            {
                if (framebuffer.Handle != 0)
                {
                    _vk.DestroyFramebuffer(_device, framebuffer, null);
                }
            }
            if (_renderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, _renderPass, null);
                _renderPass = default;
            }
            foreach (var imageView in _swapchainImageViews)
            {
                if (imageView.Handle != 0)
                {
                    _vk.DestroyImageView(_device, imageView, null);
                }
            }
            if (_commandPool.Handle != 0)
            {
                _vk.DestroyCommandPool(_device, _commandPool, null);
                _commandPool = default;
                _commandBuffer = default;
                _presentationCommandBuffer = default;
            }
            if (_swapchain.Handle != 0)
            {
                _swapchainApi.DestroySwapchain(_device, _swapchain, null);
                _swapchain = default;
            }

            _swapchainImages = [];
            _swapchainImageViews = [];
            _framebuffers = [];
            _imageInitialized = [];
        }

        private static void CheckSwapchainResult(Result result, string operation)
        {
            if (result is Result.Success or Result.SuboptimalKhr)
            {
                return;
            }

            throw new InvalidOperationException($"{operation} failed with {result}.");
        }

        private static void Check(Result result, string operation)
        {
            if (result != Result.Success)
            {
                throw new InvalidOperationException($"{operation} failed with {result}.");
            }
        }

        private bool TryMarkDeviceLost(Exception exception)
        {
            if (!exception.Message.Contains(nameof(Result.ErrorDeviceLost), StringComparison.Ordinal))
            {
                return false;
            }

            _deviceLost = true;
            if (!_deviceLostLogged)
            {
                _deviceLostLogged = true;
                Console.Error.WriteLine(
                    "[LOADER][ERROR] Vulkan device lost; dropping subsequent guest GPU work. " +
                    exception.Message);
            }

            return true;
        }

        private static void TraceVulkanShader(string message)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"), "1", StringComparison.Ordinal) &&
                !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC_SHADER"), "1", StringComparison.Ordinal))
            {
                return;
            }

            Console.Error.WriteLine($"[LOADER][TRACE] {message}");
        }
    }
}
