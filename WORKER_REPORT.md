# Worker report: compute guest-memory writeback

## Scope

No exported firmware NID was changed in this assignment. The target is the AGC-to-Vulkan compute execution path and its guest-memory visibility contract, so there is no oracle function address to cite. The implementation is grounded in the existing decoded shader metadata, Vulkan submission lifecycle, and the Kyty writeback model cited in `DESIGN_SPEC.json`.

## Observable contract

- `SHARPEMU_COMPUTE_WRITEBACK` enables the feature only when its value is exactly `1` (`VulkanVideoPresenter.cs:240-245`). When unset or set to another value, the old compute submission gate and resource behavior remain in effect.
- A storage image is an output only for decoded `ImageStore*` or `ImageAtomic*` instructions (`AgcExports.cs:6789-6812`). An `ImageLoad*` binding remains storage-capable for Vulkan descriptor purposes but is not copied to guest memory.
- A global/storage buffer is an output only when one of that binding's recorded instruction PCs resolves to `BufferStore*` or `BufferAtomic*` in the decoded shader program (`AgcExports.cs:6217-6257`). Read-only buffer bindings are not copied back.
- The submitting AGC thread captures the guest-memory interface while `CpuContext` is still in scope and passes it with the compute work item (`AgcExports.cs:6880-6899`; `VulkanVideoPresenter.cs:1298-1358`). The render thread therefore does not need a `CpuContext`.
- The requested auto-exposure image contract is the layout-invariant mip-0, non-arrayed 1x1 output. `R16Sfloat` has a two-byte readback. Larger images are deliberately excluded because copying an optimal-tiled Vulkan image into linear bytes is not sufficient to reproduce an arbitrary PS5 guest tiled layout (`VulkanVideoPresenter.cs:261-313`, `5335-5381`).
- Writable global buffers are limited to 4096 bytes. Their original guest base address and exact byte length are retained on the host-visible/coherent Vulkan buffer (`VulkanVideoPresenter.cs:243-259`, `7117-7146`).
- The last compute command buffer records image-to-buffer copies and explicit compute/transfer-write to host-read barriers after `CmdDispatchBase` and before submission (`VulkanVideoPresenter.cs:8243-8259`, `8307-8396`).
- Guest memory is written only after `vkGetFenceStatus` confirms submission completion and before the submission resources are destroyed (`VulkanVideoPresenter.cs:4502-4546`). Each attempt logs `[LOADER][WRITEBACK]` with kind, guest address, byte size, nonblack state, and write result (`VulkanVideoPresenter.cs:4555-4617`).
- Image readback buffers are released through the existing host-buffer pool during translated-resource destruction (`VulkanVideoPresenter.cs:11896-11901`).

## Implementation versus the old stubbed behavior

Previously, compute execution and GPU image publication worked, but global buffers were upload-only and destroyed after the fence, while storage-image pixels stayed solely in device-local images. No completed compute output reached guest RAM.

The new enabled path:

1. Classifies shader-written image and buffer bindings.
2. Captures the guest-memory sink, base address, and exact bounded byte size at submit/resource-creation time.
3. Copies a writable 1x1 image into a host-visible staging buffer, or directly maps a writable host-visible global buffer.
4. Waits for the existing submission fence, then calls the guest `TryWrite` primitive before cleanup.

This directly covers Astro Bot's 1x1 `R16F` auto-exposure luminance at the game-provided address. It does not synthesize an exposure value and does not use the forced-exposure stopgap.

## Tests and verification

`ComputeWritebackTests.cs` pins:

- exact environment-gate parsing and default-off sink selection;
- captured guest-memory identity and address write behavior;
- the 1x1 `R16Sfloat` two-byte layout;
- rejection of larger/unsupported image layouts;
- the 4096-byte writable-buffer bound;
- store/atomic versus load-only global-binding classification, including instruction-PC matching.

Local gate:

- `.dotnet-home/dotnet build src/SharpEmu.Libs/SharpEmu.Libs.csproj -v q --nologo`: succeeded, 0 warnings, 0 errors.
- `.dotnet-home/dotnet test SharpEmu.slnx -v q --nologo`: succeeded, 1169 passed, 0 failed, 0 skipped.

## Deliberately deferred generic work

Generic multi-pixel storage-image writeback still needs pitch-aware packing and PS5 tile-mode retile logic before copying linear Vulkan readback bytes into guest RAM. The current 1x1 restriction is exact for auto-exposure and prevents corrupting larger tiled guest surfaces. Buffer writeback can be widened later after per-instruction written-range analysis replaces the current whole-binding size bound.
