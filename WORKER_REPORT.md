# AGC command-buffer value regression report

The implementation changes are limited to `AgcExports.cs` and its matching xUnit test file. No DCB parser or draw-translation code was changed.

## AcquireMem value contracts

- `57labkp+rSQ` (`sceAgcDcbAcquireMem`): `libSceAgc.sprx` `0x2eb0-0x3164` allocates each 32-byte packet at four-byte alignment (`0x30d9-0x30e5`) and returns the first packet when the conditional compatibility pair is emitted (`0x313d-0x314b`). Packet construction at `0x30ee-0x3136` masks the engine/CB-DB/GCR fields, splits range and base address at bits 8 and 40, and writes `min(pollCycles >> 4, 0xffff)`. The old HLE rejected values that firmware masks, emitted a private NOP packet, did not align the result, and divided polling by 40. The implementation now matches the normal initialized firmware mode and advances across both packets when required.
- `KT-hTp-Ch14` (`sceAgcAcbAcquireMem`): `0x1f0-0x45a` uses the same aligned allocation and first-packet return contract. Its compatibility packet is zero-ranged (`0x2ce-0x311`) and its final packet is written at `0x3ce-0x429`. The same old validation, encoding, alignment, polling, and advancement divergences were corrected.
- `-vnlTPPXPrw` / `ewobAQeMo5k` (`sceAgcDcbAcquireMemGetSize` / `sceAgcAcbAcquireMemGetSize`): `0xbc40-0xbc57` and `0xbf50-0xbf67` return 64 bytes in initialized packet mode 1 and 32 otherwise. SharpEmu models normal initialization mode 1, so both previously missing exports return 64.

## Lenient address patch contracts

The retail patchers validate hardware packet opcodes and return `0x8a6c000c` for a mismatch. SharpEmu's emitters intentionally use private NOP-wrapped layouts, so applying those validation branches rejected valid emulator-produced packets. The implementation preserves each firmware address-field intent but never rejects a readable command solely because its opcode differs.

- `3KDcnM3lrcU` (`sceAgcWaitRegMemPatchAddress`): firmware `0xb730-0xb7f6` patches an opcode-`0x79` wrapper and its nested wait. SharpEmu emits direct opcode-`0x3c` waits with the address at `+8`, or private NOP-wrapped 32/64-bit waits with the address at `+4`; the patcher now selects those layouts and uses `+4` as the defensive fallback for an unknown readable header. The former opcode rejection is gone.
- `0fWWK5uG9rQ` (`sceAgcQueueEndOfPipeActionPatchAddress`): firmware `0xb9f0-0xba44` writes at `+12/+16` and preserves address-word bits 0-1. SharpEmu's `CbReleaseMem` uses the same field offsets, so the patcher retains those low mode bits without enforcing firmware opcode `0x49` or its data-selector rejection.
- `Qrj4c+61z4A`, `6lNcCp+fxi4`, `vcmNN+AAXnY` (Sh/Uc/Cx indirect set-address): firmware `0xb150-0xb187`, `0xb2f0-0xb327`, and `0xb220-0xb257` use hardware opcode-specific fields at `+4/+8`. SharpEmu's indirect emitters instead keep register count at `+4` and the untagged table pointer at `+8/+12`; the shared patcher writes that actual emulator field, performs no opcode check, and now permits a null replacement address.
- `YWTKOju587o` (`sceAgcCondExecPatchSetCommandAddress`): firmware `0xb410-0xb447` writes `+4/+8` while preserving low bits 0-1. The previously missing export implements that field contract without opcode validation.
- `cdDRpqcFGbU` (`sceAgcDmaDataPatchSetSrcAddressOrOffsetOrImmediate`): firmware `0xb700-0xb724` writes its hardware packet source at `+8`. SharpEmu's `DcbDmaData` and `AcbDmaData` emit control at `+8`, byte count at `+12`, destination at `+16`, and source at `+24`; the previously missing patcher therefore writes the emulator source field at `+24` and does not validate the hardware opcode.

All seven patch NIDs return errors only for a null command pointer or inaccessible guest memory. No path returns the firmware invalid-packet value `0x8a6c000c` for a packet-shape mismatch.

## Tests

`AgcExportsTests.cs` pins aligned one/two-packet AcquireMem output, the bit-8/bit-40 field split, shifted/clamped polling, both 64-byte GetSize exports, each SharpEmu patch-field offset, low-mode-bit preservation, unknown-opcode success, and null-command-pointer errors.
