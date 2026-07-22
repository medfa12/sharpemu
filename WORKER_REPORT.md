# NGS2 oracle implementation report

The requested function sizes select the PS4-compatible ABI in `libSceNgs2.sprx`, not the separate native ABI in `libSceNgs2.native.sprx`. This matters on Gen5: the compatibility system option is 0x40 bytes, uses a 16-byte name, and stores its common fields at 0x18 rather than using the native 0x90-byte layout.

## Default-bus failure analysis

The gating signal is the low byte of the voice state word. `sceNgs2VoiceGetStateFlags` reads the word at the voice slot's `+0x38` and returns only its low byte (`0xa0d3..0xa0e1`). Rack-specific setup establishes `IN_USE` (`1`); generic voice events place work in high pending bits, and a successful system render makes the resulting low-byte state visible. The old HLE accepted a non-setup rack command as setup, used incompatible event values, accepted a render with no output buffers, and returned an invented stopped value of `0x0b`. That could make bus setup appear unsuccessful or internally inconsistent.

The implementation now exposes a coherent progression: new voice `0`, successful rack setup `IN_USE` (`1`), rendered play `IN_USE|PLAYING` (`3`), pause `IN_USE|PAUSED` (`5`), and stop `IN_USE|STOPPED` (`9`).

## Per-function contracts

### `i0VnXM-C9fc` — `sceNgs2SystemRender`

Oracle: `libSceNgs2.sprx:0xe8b0`, 2317 bytes.

- Validates the system handle first and returns `0x804a0230` when invalid.
- Requires 1..16 `SceNgs2RenderBufferInfo` entries; null, zero count, and count above 16 return `0x804a0207` (`0xe9a6..0xe9af`).
- Reads each 0x18-byte entry as buffer `+0`, byte size `+8`, waveform type `+0x10`, and channels `+0x14`.
- Accepts channel counts 1, 2, 6, and 8, and compatibility waveform types `0x12`, `0x13`, `0x18`, and `0x19`. It returns `0x804a0052` or `0x804a0402` for the respective validation failures.
- Requires enough storage for `grainSamples * channels * bytesPerSample`, returns the firmware buffer address/size errors, clears outputs on failure, and writes exactly the required output extent on success.
- A successful tick advances rack/system render counts and commits pending voice events. Output is silence until a mixer backend is connected, but buffer writes and graph-state side effects match the firmware contract.

Previously, zero outputs succeeded, format/channel fields were ignored, arbitrary supplied sizes were cleared, and voice transitions occurred without a firmware-valid render contract.

### `uu94irFOGpA` — `sceNgs2VoiceControl`

Oracle: `libSceNgs2.sprx:0x8560`, 6888 bytes.

- Null parameter and invalid voice return `0x804a0309` and `0x804a0300`.
- Parses the exact 8-byte linked header: `uint16 size`, `int16 next`, `uint32 id`.
- Enforces exact generic sizes observed at the dispatch sites: 24, 16, 16, 16, 24, 12, and 32 bytes for IDs 1..7. Invalid ID/size and cycles return `0x804a0308`, `0x804a030a`, and `0x804a030b`.
- Event dispatch at `0x8bad..0x8bc7` accepts compatibility event ordinals 1..5. The jump targets queue play (`0x8bd0`), stop (`0x979f`), stop-immediate (`0x98ee`), kill (`0x8cf0`), and pause (`0x8d30`).
- Rack-specific IDs are checked against the owning rack. Setup ID 0 uses the SDK-defined rack-specific structure size and is the operation that establishes `IN_USE`; unsupported controls do not silently initialize a voice.

Previously, minimum rather than exact generic sizes were accepted, PS5-native bit values were used for this compatibility entry point, any matching rack parameter initialized a voice, and custom mastering was omitted.

### `eF8yRCC6W64` — `sceNgs2GeomApply`

Oracle: `libSceNgs2.sprx:0x2ae0`, 3363 bytes.

- Returns listener/source/output address errors `0x804a0921`, `0x804a0922`, and `0x804a0053` in argument order.
- Accepts flags 1..0x1f but rejects volume-matrix plus ambisonics (`(flags & 0x14) == 0x14`) with `0x804a0923` (`0x2b24..0x2b42`).
- Uses SDK offsets for the 0x60-byte listener work, 0x68-byte source, and 0x134-byte output attribute.
- Validates rolloff model/distances at `0x3002..0x303d` and cone levels/angles from `0x3549`, returning `0x804a0920` and `0x804a0924`.
- Computes transformed position, distance/cone attenuation, Doppler pitch, matrix or ambisonic levels, and A3D position/volume, writing only fields selected by the flags.

Previously, it only checked three pointers, cleared the complete output, wrote pitch 1.0, and returned success for invalid flags and source geometry.

### System setup/create helpers and exports

Exports: `koBbCMvOKWw` at `0xd980`; `mPYgU4oYpuY` at `0xdaa0`. Internal setup/Create2 helper: `0xd740`.

There are no separately exported `sceNgs2SystemSetupHandle` or `sceNgs2SystemCreate2` NIDs in the firmware index. Both public create paths call the internal helper at `0xd740`. It requires option size 0x40 (`0xd775..0xd77c`), validates common fields through `0xf4f0`, reports null/short storage as `0x804a0207`/`0x804a0209`, and installs a non-null handle. The HLE now follows that compatibility layout, exact 64-step grain constraints, discrete sample-rate set, output/allocator validation order, and preserves `SceNgs2ContextBufferInfo` for later info queries.

Previously, Gen5 was forced onto the unrelated native 0x90-byte option layout and creation collapsed distinct storage failures into `0x804a0206`.

### Rack setup/create helpers and exports

Exports: `cLV4aiT9JpA` at `0x7710`; `U546k6orxQo` at `0x78b0`. Internal setup/Create2 helper: `0x7460`.

As with systems, setup/Create2 are internal helpers rather than separate exports. The implementation now uses compatibility option offsets, validates supported rack IDs before creation, distinguishes buffer-info/address/size errors, preserves context-buffer info, and supports sampler, submixer, reverb, mastering, custom sampler, custom submixer, and custom mastering (`0x1000`, `0x2000`, `0x2001`, `0x3000`, `0x4001`, `0x4002`, `0x4003`).

Previously, Gen5 rack sizes/offsets came from the native ABI, validation order differed, buffer failures were collapsed, and rack `0x4003` was rejected.

### `rEh728kXk3w` — `sceNgs2VoiceGetStateFlags`

Oracle: `libSceNgs2.sprx:0xa050`, 255 bytes.

- Null output returns `0x804a0053`.
- Invalid voice writes zero to the output and returns `0x804a0300`.
- Valid voice writes the low byte of the internal state word as a zero-extended `uint32_t`.

Previously, invalid handles left stale output data, and the synthetic state model returned an invalid stopped combination.

### `-TOuuAQ-buE` — `sceNgs2VoiceGetState`

Oracle: `libSceNgs2.sprx:0xa150`, 701 bytes.

- Null output returns `0x804a0053`; sizes below four return `0x804a0054`; invalid voice returns `0x804a0300`.
- The SDK base state is flags at `+0` and error code at `+4`. Four-byte callers receive flags only; eight-byte callers receive both.

Previously, the HLE invented a 48-byte decoded-sample/decoded-byte structure and accepted unrelated sizes up to 0x400.

### `vU7TQ62pItw` — `sceNgs2SystemGetInfo`

Oracle: `libSceNgs2.sprx:0xe320`, 529 bytes.

- Requires a non-null output and at least 0x88 bytes, then validates the handle.
- Writes name `+0x00`, handle `+0x10`, context buffer info `+0x18`, UID `+0x58`, minimum/maximum grains `+0x5c/+0x60`, state flags `+0x64`, rack count `+0x68`, last render ratio/tick `+0x6c/+0x70`, render count `+0x78`, sample rate `+0x80`, and grain samples `+0x84`.

Previously, minimum grain was reported as 1, context-buffer and last-render fields were omitted, and allocator-backed info was inconsistent with its handle.

### `l4Q2dWEH6UM` — `sceNgs2SystemSetGrainSamples`

Oracle: `libSceNgs2.sprx:0xe540`, 434 bytes.

Only 64..1024 in 64-sample steps and no more than the system maximum are accepted. Invalid values return `0x804a0051`; invalid system returns `0x804a0230`. Previously, any positive value up to the maximum succeeded.

### `-tbc2SxQD60` — `sceNgs2SystemSetSampleRate`

Oracle: `libSceNgs2.sprx:0xe700`, 428 bytes.

The accepted rates are exactly 11025, 12000, 22050, 24000, 44100, 48000, 88200, 96000, 176400, and 192000 Hz. Other values return `0x804a0201`; invalid system returns `0x804a0230`. Previously, every integer from 8000 through 192000 was accepted.

## Tests

`AjmNgs2Tests` now pins the compatibility option offsets/sizes, system-info offsets and rack count, discrete setter errors, empty-render error, invalid-voice output clearing, geometry flag error, exact setup/play/stop progression, eight-byte voice state, and successful output clearing for a firmware-valid render format.
