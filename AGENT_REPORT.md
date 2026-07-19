# Per-draw GPU input/output capture (DIRECTION B-TOOLING)

Authoritative per-draw capture of the REAL bound VkImage contents (never guest
memory) for every offscreen graphics draw, so a human can find the first
post-process/composite pass whose inputs carry content but whose output VkImage
is black -- the cascade root.

## Enable it

```
SHARPEMU_CAPTURE_DRAWS=1
```

Env gate: `CaptureDrawsEnabled`, VulkanVideoPresenter.cs:2801 (read once at type
init). Call site: VulkanVideoPresenter.cs:8082-8085, right after the offscreen
draw is submitted and its targets marked written.

## Extract the lines

```
grep '\[LOADER\]\[CAPTURE\] capture.draw' emulator.log
```

Fullscreen post-process / composite passes are the `vtx=3` lines:

```
grep '\[LOADER\]\[CAPTURE\] capture.draw' emulator.log | grep ' vtx=3 '
```

## Sample output line (format)

```
[LOADER][CAPTURE] capture.draw seq=42 ps=0x00000000C0F03A80 vtx=3 mrt=1 inputs=2 out0=[addr=0x00000000E1200000 fmt=R16G16B16A16Sfloat size=1920x1080 nonblack=0/2073600] in0=[addr=0x00000000E1000000 fmt=R8G8B8A8Unorm size=1920x1080 nonblack=1734221/2073600 src=alias] in1=[addr=0x00000000E1100000 fmt=R16G16B16A16Sfloat size=1920x1080 nonblack=0/2073600 src=copy]
```

Per token:
- `seq` = presenter-side monotonic draw id (execution order on the single render
  thread; field `_captureDrawSeq`).
- `ps` = pixel-shader guest address (threaded from AgcExports, correlates with
  `agc.shader_draw ps=...`). `0x0000000000000000` if the submit path did not
  supply it (only `SubmitOffscreenTranslatedDraw` carries it today).
- `vtx` = vertex count (`3` = fullscreen triangle post-process).
- `mrt` = color target count; `inputs` = sampled (non-storage) texture count.
- `outN=[...]` = each bound OUTPUT render target.
- `inN=[...]` = each bound sampled INPUT texture.
- Inside each `[...]`: `addr` = guest address of the VkImage read; `bind` =
  binding address if it differs from the resolved image's address; `fmt` =
  VkFormat; `size` = WxH; `nonblack=x/y` = nonblack pixels / total, read from the
  REAL VkImage; `src` (inputs only) = `alias` (native-format alias binds the
  guest-image cache surface directly), `copy` (copy-on-sample producer image), or
  `cpu_upload` (pure guest-memory upload -- reported WITHOUT a VkImage count,
  since guest memory is the genuine source there and would reintroduce the old
  ambiguity if conflated with GPU content). `nonblack=unsupported` appears when
  the format has no readback layout (`GetReadbackBytesPerPixel` returns 0).

The cascade root is the lowest-`seq` `vtx=3` line where every `inN` has
`nonblack>0` but `out0` has `nonblack=0`.

## Reused readback code (this is the VkImage path, not guest memory)

The counts come from `TryReadbackGuestImageNonblack` (VulkanVideoPresenter.cs:9984),
which performs the SAME readback as `TraceGuestImageContents` (the code behind
`vk.guest_image`, VulkanVideoPresenter.cs:9644):

- `GetReadbackBytesPerPixel(image.Format)` for the layout,
- `TransitionGuestImage(image, TransferSrcOptimal, ...)`,
- `_vk.CmdCopyImageToBuffer(_commandBuffer, image.Image, TransferSrcOptimal, ...)`
  -- VulkanVideoPresenter.cs:10032 -- copying the bound **VkImage** (`image.Image`)
  into a host-visible buffer, mirroring TraceGuestImageContents' copy at
  VulkanVideoPresenter.cs:9702,
- `CountNonblackPixels(bytes, image.Format, bytesPerPixel)` -- the identical
  routine TraceGuestImageContents uses (VulkanVideoPresenter.cs:9723 region).

MEASURED: the copy source is `image.Image` (the VkImage), cited at
VulkanVideoPresenter.cs:10032 and matching the `vk.guest_image` copy at
VulkanVideoPresenter.cs:9702. Guest memory is never read on this path.

The input surface chosen for each texture is the GuestImageResource the sampler
actually reads: `texture.GuestImage` (native-format alias) or, failing that,
`texture.CopySource` (copy-on-sample producer) -- both GuestImageResources with a
live VkImage. See `CaptureDrawContents`, VulkanVideoPresenter.cs:9872.

## Layout / timing-safety argument

INFERRED (from the code structure, not a runtime observation):

- `CaptureDrawContents` runs AFTER `SubmitGuestCommandBuffer` for the draw
  (VulkanVideoPresenter.cs:8082 is past the submit at ~8049). It sets
  `_commandBuffer = _presentationCommandBuffer` and issues ONE
  `vkQueueWaitIdle(_queue)`. On a single graphics queue this drains THIS draw and
  every earlier queued draw, so the output targets hold their final content and
  each sampled input's producing pass has completed before any readback runs.
- This is the exact established pattern already used mid-frame by the
  `vk.guest_write_sample` trace, which sets `_commandBuffer =
  _presentationCommandBuffer`, `vkQueueWaitIdle`, then calls
  `TraceGuestImageContents` (VulkanVideoPresenter.cs ~8129-8137). We reuse the
  presentation command buffer as the out-of-band readback scratch; the draw's own
  command buffer is already submitted and pending free, so rebinding
  `_commandBuffer` is safe.
- Each per-image readback transitions the image TransferSrcOptimal -> copy ->
  ShaderReadOnlyOptimal via `TransitionGuestImage`, which updates the image's
  tracked layout, leaving it in a sampleable layout consistent with what later
  passes expect (same transitions TraceGuestImageContents performs).
- Guarded off when `_reclaimInProgress` or `_memoryPanic` (mirrors the
  TraceGuestImageContents / ShouldTraceGuestImageContents guards) so the extra
  host-visible readback buffers are not allocated during memory reclaim/OOM.

Cost: one `vkQueueWaitIdle` per captured draw plus one submit+wait per image.
This is a heavyweight diagnostic (serializes the queue per draw); it is intended
to run only under `SHARPEMU_CAPTURE_DRAWS=1`, never in normal play.

## What a boot must show to prove it captures real VkImage content

I cannot run the game (needs the GPU VM the human drives), so end-to-end
correctness is decided by a boot. To prove the numbers come from the real
VkImage and not guest memory, a boot log should show:

1. `[LOADER][CAPTURE] capture.draw` lines appear (one per offscreen draw) with
   monotonically increasing `seq`.
2. At least one `inN=... src=alias` (or `src=copy`) input with `nonblack>0` on a
   render target whose guest address is a GPU producer -- i.e. a nonzero count
   where the OLD `shader_draw ... texels=0` guest-memory probe read zero. That
   divergence is the proof the count is the VkImage, not guest memory.
3. Cross-check: a `capture.draw outN=[addr=0x... nonblack=N/...]` should agree
   with the `vk.guest_image addr=0x... nonblack_pixels=N/...` line for the same
   address/frame (same readback machinery), confirming consistency.

The intended payoff: the first `vtx=3` line with all inputs `nonblack>0` and
`out0 nonblack=0` pins the cascade-root post-process pass.
