# Astro Bot tonemap/display investigation

## Result

The black display has two independent blockers. The VM only produces a visible
display when both are addressed. The combined, title-scoped switch is:

```text
SHARPEMU_ASTRO_TONEMAP_FIX=1
```

With `SHARPEMU_CAPTURE_DRAWS=1`, the first real tonemap draw changed from:

```text
ps=0x500641900
in0=0x53B9F0000 R16G16B16A16Sfloat nonblack=2071334/3326976
in1=0x532830000 1x1 R16G16B16A16Sfloat nonblack=0/1
out0=0x507410000 A2R10G10B10UnormPack32 nonblack=0/8294400
```

to:

```text
ps=0x500641900
in0=0x53B9F0000 R16G16B16A16Sfloat nonblack=2071334/3326976
in1=forced 1x1 R16G16B16A16Sfloat nonblack=1/1
out0=0x507410000 A2R10G10B10UnormPack32 nonblack=8294400/8294400
```

Later 4K HDR inputs (`5164625/8294400`) also produced fully nonblack display
targets, and the following UI/compositor draws sampled those display buffers.

The address in this build is `0x500641900`, not the older `0x500640200`; it is
the same tonemap shader relocated by preceding allocations.

## Cascade root and exact zero

The tonemap draw is submitted and rasterizes the full display. Forced packed
exports produced `8294400/8294400`, and exporting the raw HDR sample produced
`4257182/8294400`. The blocker is therefore shader value computation, not NGG
submission, MRT routing, A2R10G10B10 support, or raster coverage.

The first bad values are the cbuffer scalars consumed by the gamut block:

- shader PC `0x2F0`: `s_buffer_load_dwordx8 s[0:7], s[28:31], null offset:48`
- expected `s0=0.1915199`, `s1=-0.5699999`, `s2=-0.3359999`
- translated runtime values before the fix: `s0=s1=s2=0`
- first NaN results: the reciprocal/FMA candidates at PCs `0x564`, `0x57C`,
  and `0x598`

The guest buffer itself is correct. For example, the draw bound a 160-byte
cbuffer at `0x502DF5520`; byte 48 contains the three values above, and byte 116
contains `0x3F000000` (`0.5`), not zero.

The zero originates in `Gen5SpirvTranslator.TryEmitScalarMemory`. Scalar
evaluation follows one scalar branch and records only PC `0x38` for the s28
cbuffer. The other reachable SMEM PCs (`0x204`, `0x274`, `0x29C`, `0x2C4`,
`0x2F0`, `0x300`, `0x324`, `0x344`, `0x354`, `0x530`, `0x60C`, `0x614`, and
`0x774`) have no entry in `_bufferBindingByPc`; the old compiler deliberately
stores zero into every destination of such a load.

Recovering the omitted s28 loads makes the gamut checkpoint content-bearing
(about `7.7M/8.29M` pixels), but the final grade still cancels to black. The
second input, `0x532830000`, is an unwritten 1x1 auto-exposure texture. Its
RGBA sample is zero; the shader subtracts the gamut value and adds it back
weighted by that sample, producing zero at PCs `0x634..0x63C`. Substituting
the existing conservative `0.25` exposure image fixes the second blocker.
This explains why forcing exposure alone was previously measured as ineffective:
the missing cbuffer bindings independently produced zero/NaN values.

## Changes

- GFX10 SMEM source encoding 125 is decoded as architectural `NULL` (constant
  zero), not mutable `s125`. This matches the ISA and Kyty's operand decoder.
- Missing scalar-memory PCs can recover the nearest binding with the same
  descriptor SGPR. Generic recovery remains opt-in as
  `SHARPEMU_RECOVER_UNBOUND_SMEM=1`; the combined Astro switch applies it only
  to the known tonemap shader placements.
- `SHARPEMU_ASTRO_TONEMAP_FIX=1` also selects the existing 0.25 1x1 exposure
  image when no explicit `SHARPEMU_FORCE_EXPOSURE` value is supplied.
- `scripts/vm-astro.sh` now preserves exact environment values. The generated
  Windows `set NAME=VALUE &&` form previously appended a space to values, so
  comparisons against `"1"` silently failed.
- Added decoder, binding-selection, tie-breaking, and forced-exposure tests.

## Proper infrastructure follow-up

The nearest-PC recovery is intentionally gated. The general fix belongs in
`Gen5ShaderScalarEvaluator.cs`:

1. Build a CFG worklist for resource discovery instead of recording resources
   only along the evaluator's selected scalar path.
2. Propagate abstract SGPR/descriptor state through both successors of every
   scalar conditional branch. Join identical descriptor values; mark differing
   values as a candidate set rather than replacing them with zero.
3. For every reachable `Gen5ScalarMemoryControl`, resolve its scalar-base
   descriptor and add that instruction PC to the matching
   `Gen5GlobalMemoryBinding.InstructionPcs`.
4. At ambiguous joins, emit a runtime descriptor-base selection among candidate
   bindings (or add descriptor-buffer indexing) instead of choosing by PC.
5. Once all reachable PCs are bound, remove `TryRecoverScalarMemoryBinding` and
   the recovery part of `SHARPEMU_ASTRO_TONEMAP_FIX`.

The exposure fallback should likewise be removed after the actual producer is
implemented. Trace writes to guest surface `0x532830000`, ensure the relevant
compute dispatch is submitted, and publish its storage image through the same
guest-image registry used by sampled textures so the following tonemap aliases
the produced image rather than uploading zero-filled guest memory. If the title
expects compute output in guest memory, add compute-image readback/writeback at
the dispatch synchronization point before the sampled alias is resolved.

## Verification

- `SharpEmu.Libs.csproj` build: passed.
- Targeted decoder and new regression tests: passed.
- Full solution test result is recorded in the final handoff after the cleanup
  commit.
- VM authoritative before/after logs were saved locally as
  `/tmp/tonemap-r1-capture.log` and `/tmp/tonemap-r1-combined-fix.log`.
