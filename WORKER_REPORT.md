# AGC driver render-state initialization report

Oracle: decrypted PS5 4.03 `libSceAgcDriver.sprx`. SDK naming and public contracts were checked against the 4.00 AGC headers in `inspiration/ps5-sdk-4.00/sdk/target/include/agc/`, especially `baselayer.h`, `drawcommandbuffer.h`, `asynccommandbuffer.h`, and `resourceregistration.h`.

## `nR6xhiFsOoc` — `sceAgcDriverNotifyDefaultStates`

- Firmware contract: arguments are three arrays of 8-byte register/value entries (`rdi`, `rsi`, `rdx`) and their counts (`ecx`, `r8d`, `r9d`). The function finds discontinuities between register offsets (`0x33BB-0x345A`), allocates three arrays of offset ranges (`0x3470-0x3494`), builds default Cx/Sh/Uc command streams, and publishes the resulting command addresses, DWORD counts, and type byte in driver globals (`0x36C5-0x37D7`). It returns `0`; allocation failure returns `0x8A6D0005` (`0x3499-0x3571`). It does not write through any guest output pointer.
- Implementation: records bounded copies of all three register/value lists per guest memory and returns the exact success/OOM contract. SharpEmu does not expose the firmware's kernel-owned default-state command buffers, so no invented GPU addresses are published. Existing SharpEmu register defaults continue to supply reset state.
- Previous behavior: export was missing and resolved as `NOT_FOUND`.

## `lYz7vbL4W4A` — `sceAgcDriverPatchClearState`

- Firmware contract: `rdi` is an array of 8-byte register/value pairs and `esi` is its count. Count zero succeeds (`0x2FA2`, `0x3098`); a count greater than `0x400` returns `0x8A6D0003` before reading the array (`0x2FAA-0x2FB5`). Register offsets above `0x3FF` are ignored (`0x30CC-0x30D7`). Changed values are sent to the driver ioctl path; that path must return `1` for success, otherwise the function returns `0x8A6D0003` (`0x326B`, `0x32FC-0x3307`).
- Implementation: pins the count boundary and return values, filters to the 0x400-register hardware space, and records the clear-state patch intent. There is no host kernel clear-state ioctl to invoke, so the hardware submission side effect is deliberately not fabricated.
- Previous behavior: export was missing and resolved as `NOT_FOUND`.

## `zP4ZNlXLBVg` — `sceAgcDriverCreateQueue`

- Firmware contract: `edi` is the queue selector, `rsi` is `Queue**`, and `rdx` optionally points to a 0x20-byte queue-options block. A null output pointer or selector class `((selector >> 5) & 3) == 3` returns `0x8A6D0000` (`0x1ED8-0x1EF2`). On success the function writes a driver-owned queue pointer through `rsi` (`0x2105` or `0x2345`) and returns `0` (`0x2108`). With no options, the graphics descriptor contains `0x30` at `+0`, the selector at `+4`, `0x20000` at `+8`, and zero at `+0x10` (`0x1F74-0x1F86`, `0x20A4`). Async handles point eight bytes into an 0x88-byte record (`0x2251`, `0x2345`); observable fields include the selector at handle `+4`, the record base at `+0x38`, active value `1` at `+0x68`, and zero at `+0x78` (`0x2256-0x2266`, `0x22EC-0x22F1`).
- Implementation: allocates a stable guest-visible queue record, copies the optional 0x20-byte options block, writes the grounded default fields and pointer relationship, and returns the exact validated result codes. Hardware ring mappings and queue ioctls are not synthesized.
- Previous behavior: export was missing and resolved as `NOT_FOUND`.

## `QcmHLO2n7mk` — `sceAgcDriverSuspendPointSubmit`

- Firmware contract: `rdi` points to a submit descriptor with command address at `+0`, DWORD count at `+8`, and queue byte at `+0xC` (`0x1C45-0x1C5A`, repeated at `0x1D84-0x1D99`). The firmware serializes normal and interrupt graphics-queue submissions through two locks and returns the selected submission callback's result; unavailable submission state begins with `0x8A6D0000` (`0x1BF5`, `0x1C02-0x1E8D`). `rsi` is forwarded to the submission callback.
- Implementation: validates the descriptor and routes its address/count through `AgcExports.DriverSubmitDcb`, so queued GPU commands have the same observable effects as normal SharpEmu DCB submission. The unavailable driver callback/routing metadata and dual hardware queues have no HLE equivalent; invalid descriptors return `0x8A6D0000`.
- Previous behavior: export was missing and resolved as `NOT_FOUND`.

## `cwbxjPSJ7WQ` — `sceAgcDriverSetFlip`

- Firmware contract: `rdi` points to the command cursor, `esi` supplies the packet size in DWORDs, `ecx` is the video-out handle, `r8d` is the display-buffer index, `r9d` is the flip mode, and the stack argument is the 64-bit flip argument. Only indices `[-2, 15]` are accepted; others return `0x8029000A` (`0x6DBB-0x6DF0`). The function appends the EOP-flip sequence and padding and updates `*rdi` to the end (`0x6EE6-0x6F8D`). The public AGC wrapper requests `0x40` DWORDs through `sceAgcDriverGetSetFlipPacketSizeInDwords`, whose firmware body at `0x6D80` returns `0x40`.
- Implementation: emits SharpEmu's established `RFlip` HLE packet followed by a valid padding NOP, preserving all five flip fields for the AGC command processor, and advances the cursor by the requested size. It pins the firmware index error without prematurely submitting a host flip.
- Previous behavior: this NID was absent; only the higher-level `sceAgcDcbSetFlip` export existed.

## `W5z4eZrjEas` — `sceAgcDriverRegisterResource`

- Firmware observation: the decrypted six-byte body at `0x67B0` is `mov eax, 0x8A6C9018; ret`. The SDK `resourceregistration.h` documents that resource-registration APIs return `SCE_AGC_ERROR_RESOURCE_REGISTRATION_NO_PA_DEBUG` when PA Debug is disabled, consistent with a constant non-success retail body.
- Implementation: per the assignment's explicit compatibility requirement, the export now returns `0` unconditionally and does not validate inputs or write a resource handle. This is an intentional assignment-directed divergence from the supplied 4.03 oracle value, recorded here so it is not mistaken for an oracle-derived success code.
- Previous behavior: implemented a synthetic registry and returned generic `INVALID_ARGUMENT` for common calls before initialization, causing repeated game-visible rejection.

## Tests and verification

- Added reflection coverage for all five missing NIDs and their `libSceAgcDriver` registration.
- Added contract tests for the patch-count boundary, queue rejection branches and descriptor offsets, suspend descriptor submission, flip packet fields/cursor movement/index bounds, and resource-registration no-write success behavior.
- `.dotnet-home/dotnet build src/SharpEmu.Libs/SharpEmu.Libs.csproj -v q --nologo`: succeeded with 0 warnings and 0 errors.
- `.dotnet-home/dotnet test SharpEmu.slnx -v q --nologo`: 1,144 passed, 0 failed, 0 skipped.
