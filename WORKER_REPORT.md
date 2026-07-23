# AudioOut2 default-bus state report

Scope: `sceAudioOut2PortGetState` and `sceAudioOut2GetSpeakerInfo` in PS5 4.03 `libSceAudioOut.sprx`, with names and layouts from SDK 4.00 `audio_out2.h`.

## `gatEUKG+Ea4` — `sceAudioOut2PortGetState`

The oracle index places the 808-byte public function at `0x41F80`. The retained disassembly shows that it rejects a null output pointer at `0x41FBF-0x41FC2`, resolves the context from the high 32 bits of the port handle at `0x41FC8-0x41FFB`, and calls `sceAudioOut2LoPortGetState` with the original handle and caller's output buffer at `0x42006-0x42018`. A negative lower-level result is propagated at `0x4201B-0x4201D`. This is a dynamic state query, not a constant-return stub.

SDK 4.00 `audio_out2.h:235-253` defines the 0x40-byte `SceAudioOut2PortState` contract:

- `output` at `+0x00`; `PRIMARY` is `1 << 0`, `HEADPHONE` is `1 << 6`.
- `numChannels` at `+0x02`.
- `volume` at `+0x04` and `rerouteCounter` at `+0x06`.
- `flags` at `+0x08`; `SCE_AUDIO_OUT2_PORT_STATE_FLAG_3D_AVAILABLE` is `1 << 0`.
- padding and six reserved `uint64_t` values occupy `+0x0C..+0x3F`.

The old HLE returned MAIN and BGM with `output=PRIMARY`, but copied their eight-channel input-bed width into `numChannels` and left `flags=0`. That described an eight-channel physical endpoint while `GetSpeakerInfo` described only front-left/front-right, and it advertised no 3D-capable output.

The new retail-default model returns the two ordinary beds Astro queries (MAIN type 0 and BGM type 1) as the same connected endpoint:

- `output=PRIMARY`, `numChannels=2`, `volume=127`, `rerouteCounter=0`, `flags=3D_AVAILABLE`.
- all padding/reserved bytes are zero.
- pad-speaker and object ports retain their own channel shape and do not advertise `3D_AVAILABLE`, so they cannot create extra default speaker groups.

## `DImz2Ft9E2g` — `sceAudioOut2GetSpeakerInfo`

The oracle index places this nontrivial 626-byte device-state query at `0x203F0`. SDK 4.00 declares the signature as `sceAudioOut2GetSpeakerInfo(SceAudioOut2SpeakerInfo *outInfo, uint32_t flags)` at `audio_out2.h:395` and defines the 0x50-byte result at `audio_out2.h:287-319`:

- `type` at `+0x00`; `SCE_AUDIO_OUT2_SPEAKER_TYPE_TV` is 0, AV receiver is 1, sound bar is 2, and headphone is 3.
- `availableBits` at `+0x04`; speaker indices 0 and 1 are front-left and front-right.
- `flags` at `+0x08`; `SCE_AUDIO_OUT2_SPEAKER_INFO_FLAG_3D_AVAILABLE` is `1 << 0`.
- sixteen `{ int16_t azimuth, int16_t elevation }` pairs begin at `+0x10`, making the complete structure 0x50 bytes.

The HLE already returned the standard TV stereo geometry used by the secondary KytyPS5 implementation: `type=TV`, `availableBits=0x3`, and horizontal angles `{-30,0}` / `{+30,0}`. Its divergence was `flags=0`. The implementation now sets the SDK-defined 3D capability and keeps every unused speaker slot and reserved field zero.

The resulting exact modeled bytes are: `type=0`, `availableBits=3`, `flags=1`, angles `(-30,0)` and `(30,0)`, and zeros elsewhere through `+0x4F`.

## Why this produces one default bus

Astro creates two eight-channel input ports before polling state: MAIN and BGM. They are not two physical speaker systems. Both now resolve to the same `PRIMARY` two-channel endpoint and both agree with the single TV layout returned by `GetSpeakerInfo`. `availableBits=0x3` identifies the two channels inside that one layout; it does not describe two output groups. The matching bit-0 capability makes that one group eligible, while pad/object states keep the bit clear. The expected engine result is therefore one eligible, deduplicated primary speaker group and `defaultBusses.size()==1`.

## Static limitation and confirmation experiment

The complete device-dependent lower-level write paths were not available in the retained oracle output, so static evidence does not prove whether Astro's decisive branch is `PortState.flags`, `SpeakerInfo.flags`, or the previous 8-versus-2 channel mismatch. The implementation intentionally makes all three observations mutually consistent rather than selecting only one guessed field.

The supervisor should run one 150-second Astro boot with `SHARPEMU_LOG_AUDIO=1`. The first state cycle should show type 0 and type 1 as `output=0x1 channels=2 flags=0x1`. In the same run, compare `(Select-String 'defaultBusses').Count` with the baseline and confirm that the `SoundManager.cpp:306` assertion disappears (ideally count 0). If it remains, that single log is sufficient to distinguish an unobserved engine predicate from an AudioOut2 state-coherency failure.

## Tests

- `AudioOut2ExportsTests` pins every populated field offset, both 3D capability bits, stereo speaker angles and stride, zeroed reserved storage, and non-primary capability clearing.
- The older `AudioOut2Tests` expectation was updated from the obsolete zero speaker flag to the SDK bit-0 value.
- Repo-local library build: succeeded with 0 warnings and 0 errors.
- Full solution: 159 `SharpEmu.Libs.Tests` and 995 `SharpEmu.Tests` passed; 0 failed and 0 skipped.
