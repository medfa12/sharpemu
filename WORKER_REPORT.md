# AGC command-buffer value contract report

The implementation and tests are limited to `AgcExports.cs` and its existing matching xUnit file. No DCB parser or draw-translation code was changed.

## AcquireMem

- `57labkp+rSQ` (`sceAgcDcbAcquireMem`): `libSceAgc.sprx` `0x2eb0-0x3164` allocates each 32-byte packet with four-byte alignment (`0x30d9-0x30e5`), advances `m_upCursor`, and returns the first packet when the compatibility pair is emitted (`0x313d-0x314b`). The packet fields come from `0x30ee-0x3136`: opcode `0x58`, masked engine/CB-DB/GCR values, rounded/clamped range, address split at bits 8 and 40, and `min(pollCycles >> 4, 0xffff)`. The old HLE rejected unaligned/range inputs that firmware only masks, did not align the returned packet, divided polling by 40, emitted a private NOP encoding, and could only advance 32 bytes. The implementation now follows the normal initialized firmware mode, including its conditional two-packet path.
- `KT-hTp-Ch14` (`sceAgcAcbAcquireMem`): `0x1f0-0x45a` has the same aligned allocation and return rule. Its compatibility packet is zero-ranged (`0x2ce-0x311`) and the final packet is at `0x3ce-0x429`. The old HLE had the same validation, polling, encoding, and advancement errors as the DCB variant.
- `-vnlTPPXPrw` / `ewobAQeMo5k` (DCB/ACB AcquireMem GetSize): `0xbc40-0xbc57` and `0xbf50-0xbf67` return 64 bytes when the initialized packet mode is 1 and 32 otherwise. Normal initialization selects mode 1 when neither title workaround is enabled (`0x77c4-0x77de`), which is the mode modeled here. These exports were missing.

The firmware also has title-workaround modes 0 and 2. Modeling those dynamically requires wiring the out-of-scope `sceAgcInit` title-workaround state into this file; this assignment uses the normal mode exercised by Astro Bot.

## Payload helpers

- `V++UgBtQhn0` (NOP payload-address helper): `0xb640-0xb66b` returns `command+8` for nonzero type, `command+4` for a normal type-0 packet, and null for the `0x3fff` count sentinel. The existing arithmetic was retained.
- `CQsSq6l6+kA` (`sceAgcGetDataPacketPayloadAddress` in the oracle index): `0xb620-0xb631` always stores `command+4+(type != 0 ? 4 : 0)`. This distinct helper was missing and is now exported without replacing the existing `V++UgBtQhn0` ABI.
- `s+VGAMDQ0AQ` (`sceAgcGetDataPacketPayloadRange`): `0xb670-0xb6c2` writes `MemoryRange::m_base` at `+0` and `m_size` at `+8`, matching `agc/memoryrange.h`. Type nonzero produces `{command+8, (header >> 14) & 0xfffc}`; type zero produces an empty range for the sentinel or `{command+4, 4 * (count+1)}` otherwise. This export was missing.

## Wait size and address patches

- `43WJ08sSugE` / `idlaArvdXEs` (`sceAgcDcbWaitOnAddressGetSize` / `sceAgcAcbWaitOnAddressGetSize`): `0xbe60-0xbeb3` and `0xc010-0xc063` combine `sceAgcDriverUserDataGetPacketSize(4)==4` and `(0)==3`, then return 56 bytes for low-byte size 0, 64 for 1, and 0 otherwise. Both exports were missing.
- `3KDcnM3lrcU` (`sceAgcWaitRegMemPatchAddress`): `0xb730-0xb7f6` accepts a four-DWORD opcode-`0x79` wrapper, patches its address fragments at `+8` (16 bits) and `+12` (32 bits), then patches the nested packet at wrapper `+16`. Nested opcode `0x3c` preserves two low bits; opcode `0x93` preserves three; the high DWORD preserves bits 18-31. Invalid packets return `0x8a6c000c`, including the firmware's partial wrapper write before a bad nested-opcode result. The old HLE instead accepted unrelated direct/custom wait packets and wrote a raw 64-bit value at the wrong offset.

## Other patch functions

- `Qrj4c+61z4A`, `6lNcCp+fxi4`, `vcmNN+AAXnY` (Sh/Uc/Cx indirect set-address): `0xb150`, `0xb2f0`, and `0xb220` validate opcode bytes `0x63`, `0x64`, and `0x9f`; patch the low address at `+4` while preserving bits 0-1; and write the high DWORD at `+8`. The old shared helper performed no packet validation and wrote raw halves at `+8/+12`.
- `YWTKOju587o` (`sceAgcCondExecPatchSetCommandAddress`): `0xb410-0xb447` uses opcode `0x22` and the same `+4/+8`, low-two-bit-preserving layout. It was missing.
- `cdDRpqcFGbU` (`sceAgcDmaDataPatchSetSrcAddressOrOffsetOrImmediate`): `0xb700-0xb724` validates opcode `0x50` and writes the full value at `+8`. It was missing.
- `0fWWK5uG9rQ` (`sceAgcQueueEndOfPipeActionPatchAddress`): `0xb9f0-0xba44` validates opcode `0x49`, rejects data-selector value 4, patches the low address at `+12` while preserving bits 0-1, and writes the high DWORD at `+16`. The old HLE validated a private NOP packet and overwrote a raw 64-bit field without preserving the low bits.

All patch-family invalid-packet branches now return the firmware value `0x8a6c000c` rather than the generic kernel invalid-argument code.

## Tests

`AgcExportsTests.cs` pins aligned cursor advancement for one- and two-packet AcquireMem paths, AcquireMem and WaitOnAddress sizes, payload base/size fields, every requested patch offset, preserved alignment bits, the two WaitRegMem nested opcode layouts, and the firmware invalid-packet code.
