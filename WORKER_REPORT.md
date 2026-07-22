# AudioOut2 output-path implementation report

Oracle: decrypted PS5 4.03 `libSceAudioOut.sprx`. Struct and symbol names are from the SDK 4.00 `audio_out2.h` and `audio_out2/error.h` headers.

## `8XTArSPyWHk` — `sceAudioOut2PortSetAttributes`

- Firmware contract: the public implementation starts at `0x411E0`. It rejects a null attribute pointer or zero attribute count with `SCE_AUDIO_OUT2_ERROR_INVALID_PARAM` (`0x80268001`) at `0x41202-0x4120F`, resolves the context and port, and returns `SCE_AUDIO_OUT2_ERROR_INVALID_PORT` (`0x80268009`) for a bad port. Attributes use the SDK's 24-byte `SceAudioOut2Attribute` stride: `attributeId` at `+0`, `value` at `+8`, and `valueSize` at `+0x10`. The PCM special case at `0x413D5-0x4142E` requires a value of at least eight bytes and a non-null nested `SceAudioOut2Pcm.data` pointer. The lower validator at `0x15CA0` handles the SDK IDs PCM, gain, priority, position, spread, passthrough, reset-state, application-specific, ambisonics, restricted, mix-to-main gain, and debug-name.
- Implementation: validates every attribute before applying its observable state, accepts firmware ID 47 as the PCM alias, enforces channel-sized gain arrays and the firmware's finite/range checks, and records the current PCM data pointer. Ports now retain their creation shape and ownership instead of being bare tuples.
- Previous behavior: any nonzero handle succeeded without reading the attribute array and therefore accepted null PCM buffers, malformed sizes, invalid values, and unknown ports.

## `PE2zHMqLSHs` — `sceAudioOut2ContextAdvance`

- Firmware contract: the high-level function starts at `0x27890`. It obtains queue availability before processing ports (`0x2793B-0x2795A`) and calls the low-level advance path at `0x27D79-0x27D88`. `sceAudioOut2LoContextAdvance` returns `SCE_AUDIO_OUT2_ERROR_NOT_READY` (`0x80268008`) when `producer - consumer + staged + 1` exceeds capacity, otherwise it increments the staged count at internal context `+0xC34` (`0xF9C9`). Queue-level reporting includes this staged count.
- Implementation: `Advance` stages exactly one entry, returns NOT_READY when committed plus staged entries reach `queueDepth`, and exposes the staged entry immediately through `GetQueueLevel`.
- Previous behavior: `Advance` was an unconditional success stub and did not move queue state.

## `NZu1Z2k14DM` — `sceAudioOut2LoContextSetAttributes`

- Firmware contract: starts at `0xEF50`; a null attribute pointer returns INVALID_PARAM at `0xEF87`, while a count of zero is accepted. It walks 24-byte `SceAudioOut2Attribute` records (`0xEFE0-0xEFE8`) and prevalidates the complete array. The jump table accepts IDs 0, 1, 2, 3, 30, and 31. SDK IDs are `DOWNMIX_SPREAD_RADIUS` (four-byte float in `[0.1, 2.0]`), `DOWNMIX_SPREAD_HEIGHT_AWARE`, `DOWNMIX_FOLLOW_SPEAKER_SETTING`, and `AMBISONICS_DOWNMIX_SPREAD_HEIGHT_AWARE_OFF`; internal IDs 30 and 31 validate a four-byte port limit and a finite nonnegative float.
- Implementation: added the missing export, preserves the all-or-nothing validation order, and stores the modeled context attributes only after every record succeeds.
- Previous behavior: the NID was not exported.

## `R7d0F1g2qsU` — `sceAudioOut2ContextGetQueueLevel`

- Firmware contract: the seven-byte public thunk at `0x27880` enters the shared implementation at `0x283C0`. Either output pointer may be null, but not both (`0x283DA-0x283E8`). The low-level level is `producer - consumer + staged`; the high-level wrapper converts grains to queue entries, writes requested outputs, and then returns NOT_READY when fewer than one complete entry is available (`0x28449-0x28485`).
- Implementation: reports `committed + pending` as level and `queueDepth - level` as availability, supports either output independently, writes the outputs before returning NOT_READY on a full queue, and drains committed entries at the configured `numGrains / 48000` cadence.
- Previous behavior: required the level pointer, used a fixed depth, and did not represent firmware's staged-versus-committed state. The boot-visible level behavior remains monotonic and advancing, but full-queue calls that request availability now return the firmware's NOT_READY code after writing `available = 0`.

## `aII9h5nli9U` — `sceAudioOut2ContextPush`

- Firmware contract: starts at `0x27700` and accepts only `SCE_AUDIO_OUT2_BLOCKING_ASYNC` (0) or `SYNC` (1), returning INVALID_PARAM otherwise (`0x27727-0x2773A`). The low-level path commits the staged count to the producer and clears staging (`0xFAD0-0xFAE0`). Its no-staged-data NOT_READY result is converted to success by the public wrapper (`0x2776C-0x2777A` and `0x277F2-0x27800`). Sync mode waits while the committed queue occupies its capacity; async mode returns after committing.
- Implementation: commits all pending `Advance` entries, makes an empty push a successful no-op, and preserves the real-time blocking clock for sync pushes while committed entries drain.
- Previous behavior: `Push` itself enqueued entries, so it could invent audio without `Advance` and could not reproduce the firmware poll loop.

## Context creation and memory

### `0x6o1VVAYSY` — `sceAudioOut2ContextCreate`

- Firmware contract: starts at `0x26CB0`. The SDK `SceAudioOut2ContextParam` fields read at `0x26D75-0x26DD3` are `numGrains +0x10` (at least `0x100`, multiple of `0x100`), `maxPorts +0` (at most 32), `maxObjectPorts +4`, `guaranteeObjectPorts +8`, `queueDepth +0x0C` (nonzero), and `flags +0x14`. Object contexts require zero guaranteed object ports and `MAIN` or `SUB` flags. Firmware writes `SCE_AUDIO_OUT2_CONTEXT_HANDLE_INVALID` before low-level allocation (`0x26F45-0x26F5A`). The shared memory-size routine at `0xB901-0xB985` includes 21 high-level internal ports, `0xB60` bytes per port record, port bitsets, and a 128-byte-aligned object table; the default `{maxPorts=8, queueDepth=4, numGrains=512}` shape requires `0x14A0C` bytes. Low-level create rejects a shorter buffer at `0xBE4A-0xBE57`.
- Implementation: validates the named SDK fields, derives and enforces the oracle memory size, writes the invalid output handle before the size/allocation branch, and stores `maxPorts`, `queueDepth`, and `numGrains` in the context state. `sceAudioOut2ContextQueryMemory` now uses the same calculation. `sceAudioOut2ContextResetParam` writes the full 0x40-byte SDK struct with firmware defaults `{maxPorts=8, queueDepth=1, numGrains=0x100}` from `0xE670-0xE69F`.
- Previous behavior: accepted any nonzero parameter block and buffer size, used a fixed `0x10000` query result, and discarded queue configuration.

## Port creation and state

### `JK2wamZPzwM` — `sceAudioOut2PortCreate`

- Firmware contract: the seven-byte export at `0x40F50` tail-calls the shared high-level creator at `0x40B10`. It consumes SDK `SceAudioOut2PortParam` fields `portType +0`, `dataFormat +4`, `samplingFreq +8`, `flags +0x0C`, and `userHandle +0x10`. Low-level create writes `SCE_AUDIO_OUT2_PORT_HANDLE_INVALID` before field validation (`0x15681`). Valid types are ordinary 0 through 6 and object types `0x100`, `0x102`, and `0x104`; formats encode float/I16 in bits 0-6, standard layout in bit 7, and channels in bits 8-11; frequency must be 48000. The successful handle is `(contextLow32 << 32) | 0x80000000 | portId` (`0x40EAF-0x40ED5`).
- Implementation: validates the tracked context/user, type-specific channel counts, format, frequency, flags, and per-context port capacity; writes the invalid handle on validated entry; then builds the firmware-shaped handle and retains the port state.
- Previous behavior: accepted incomplete parameters, did not validate user/context ownership or capacity, and minted unrelated sequential handles.

### `gatEUKG+Ea4` — `sceAudioOut2PortGetState`

- Firmware contract verified at `0x41F80`: state is returned for a resolved port and invalid ports produce `SCE_AUDIO_OUT2_ERROR_INVALID_PORT`. The SDK `SceAudioOut2PortState` layout is `output +0`, `numChannels +2`, `volume +4`, `rerouteCounter +6`, `flags +8`, then reserved storage through size `0x40`.
- Implementation: retains the existing boot-compatible state shape, now derives output/channels from validated port creation data and rejects unknown handles instead of fabricating state.

The oracle index contains no `sceAudioOut2ContextNew`, `sceAudioOut2PortOpen`, or `sceAudioOut2ContextGetInfo` exports in this firmware. The actual indexed creation/state APIs above were verified instead.

## Tests and verification

- Added contract tests for the 0x40-byte context parameter layout, exact default memory size, insufficient-buffer invalid-handle behavior, context and port validation errors, PCM attribute validation, low-context attributes, staged/committed queue transitions, full-queue NOT_READY behavior, optional queue outputs, and stale handles.
- `.dotnet-home/dotnet build src/SharpEmu.Libs/SharpEmu.Libs.csproj -v q --nologo`: succeeded with 0 warnings and 0 errors.
- `.dotnet-home/dotnet test SharpEmu.slnx -v q --nologo`: 1,136 passed, 0 failed, 0 skipped (144 `SharpEmu.Libs.Tests` plus 992 `SharpEmu.Tests`).
