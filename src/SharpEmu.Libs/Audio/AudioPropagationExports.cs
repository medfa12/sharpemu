// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Audio;

/// <summary>
/// Minimal libSceAudioPropagation (3D audio propagation) surface. No real
/// propagation is simulated; the goal is to hand out opaque handles and sane
/// out-params so titles that gate scene setup on room/source creation (Astro
/// Bot blocks on a room-ready semaphore) can proceed.
///
/// Out-param positions below were confirmed by disassembling Astro Bot's
/// call sites (eboot 0x800F42754+, 0x800F4B0E0+, 0x800F4C340+, 0x800F513A6+,
/// 0x800F45620+), not guessed from SDK headers:
/// - SystemQueryMemory(param rdi, memInfo rsi): required size is a u64 at
///   memInfo+0x18 inside a 0x40-byte caller-zeroed struct (the caller reads
///   it back as the alloc size and memset length).
/// - SystemCreate(param rdi, memInfo rsi, out u64* system rdx).
/// - RoomCreate(system rdi, out u64* room rsi); rdx holds a leftover rodata
///   pointer from the previous call and must never be written.
/// - PortalCreate(system rdi, param rsi, out u64* portal rdx).
/// - SourceCreate(system rdi, out u64* source rsi).
/// - SystemRegisterMaterial(system rdi, param rsi, out u64* material rdx);
///   the game stores the material handle into a std::map, so it must be a
///   real write of exactly 8 bytes.
/// - SourceGetAudioPathCount(source rdi, out u32* count rsi).
/// - System/SourceGetRays(handle rdi, rayArray rsi, in-out u32* count rdx);
///   the caller pre-zeroes the ray array, so reporting zero rays is safe.
///
/// Disable gate: a long-running Astro Bot session dies ~20-35 min in with an
/// AV inside memcpy on SceSndzAudioOutMain when the propagation output bus
/// object's render-buffer pointer (bus+0x18) has been stomped with float
/// data ({x, y, z, 1.0f} vec4-shaped writes at bus+0x10..0x1F). Until the
/// direct writer is caught, the whole subsystem can be switched off by
/// failing every handle-creating call with NOT_IMPLEMENTED. Disassembly of
/// every Astro Bot call site (SystemQueryMemory/SystemCreate at 0x800F427DF
/// and 0x800F42845, RoomCreate at 0x800F4B177, plus the copy-out sanity
/// checks at 0x800F44B75/0x800F44BB7) shows the same compiled pattern on
/// failure: stash the code in r9d, call the logging hook at 0x800001aa0,
/// then jump straight back into the success path - no assert, no abort, no
/// skipped semaphore posts - so failing creates is safe for boot progress.
/// The zero-count getters keep succeeding even when disabled because their
/// zero writes are the already-proven-benign contract the game consumes.
/// Set SHARPEMU_DISABLE_AUDIO_PROPAGATION=0 to hand out handles again;
/// unset or any other value leaves the subsystem disabled.
/// </summary>
public static class AudioPropagationExports
{
    // Disabled unless the environment variable is explicitly "0". Read per
    // call: creates are rare (game init), and tests flip the variable at
    // runtime.
    private static bool PropagationDisabled =>
        !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_AUDIO_PROPAGATION"),
            "0",
            StringComparison.Ordinal);

    // Handed back through SystemQueryMemory; the game allocates this many
    // bytes (16-byte aligned) and passes the block to SystemCreate. We never
    // touch the block, so any modest non-zero size is fine.
    private const ulong SystemMemorySize = 0x10000;

    // Offset of the required-size u64 inside the caller's memory-info struct
    // (0x40 bytes, zeroed by the caller; +0x00 magic, +0x08 alignment,
    // +0x10 memory pointer filled in by the caller before SystemCreate).
    private const ulong QueryMemorySizeOffset = 0x18;

    // Tag bits keep handle types tellable apart in traces; the low half is a
    // shared monotonic counter so no two live handles ever collide.
    private const ulong SystemHandleTag = 0x4150_0001_0000_0000;   // 'AP' 1
    private const ulong RoomHandleTag = 0x4150_0002_0000_0000;     // 'AP' 2
    private const ulong PortalHandleTag = 0x4150_0003_0000_0000;   // 'AP' 3
    private const ulong SourceHandleTag = 0x4150_0004_0000_0000;   // 'AP' 4
    private const ulong MaterialHandleTag = 0x4150_0005_0000_0000; // 'AP' 5

    private static long _nextHandle;

    private static ulong NextHandle(ulong tag)
    {
        return tag | (ulong)Interlocked.Increment(ref _nextHandle);
    }

    // Cached once; a per-call Environment.GetEnvironmentVariable is a P/Invoke
    // plus a transient string on paths the audio threads hit continuously.
    private static readonly bool _traceAudio =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AUDIO"), "1", StringComparison.Ordinal);

    private static void Trace(string message)
    {
        if (_traceAudio)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] audioprop.{message}");
        }
    }

    [SysAbiExport(
        Nid = "7xyAxrusLko",
        ExportName = "sceAudioPropagationSystemQueryMemory",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SystemQueryMemory(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var memoryInfoAddress = ctx[CpuRegister.Rsi];
        if (paramAddress == 0 || memoryInfoAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // The caller zeroes a 0x40-byte info struct and, on success, reads
        // the u64 at +0x18 back as both the allocation size (u32) and the
        // memset length (u64). Only that slot may be written.
        Trace($"system_query_memory param=0x{paramAddress:X} info=0x{memoryInfoAddress:X} -> size=0x{SystemMemorySize:X}");
        return ctx.TryWriteUInt64(memoryInfoAddress + QueryMemorySizeOffset, SystemMemorySize)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "aNEqtSHdUSo",
        ExportName = "sceAudioPropagationSystemCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SystemCreate(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var memoryInfoAddress = ctx[CpuRegister.Rsi];
        var outSystemAddress = ctx[CpuRegister.Rdx];
        if (PropagationDisabled)
        {
            // The out-handle slot is caller-zeroed and must stay untouched so
            // the game keeps carrying a null system handle.
            Trace($"system_create param=0x{paramAddress:X} info=0x{memoryInfoAddress:X} -> disabled");
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED);
        }
        if (paramAddress == 0 || memoryInfoAddress == 0 || outSystemAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = NextHandle(SystemHandleTag);
        Trace($"system_create param=0x{paramAddress:X} info=0x{memoryInfoAddress:X} -> handle=0x{handle:X}");
        return ctx.TryWriteUInt64(outSystemAddress, handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "8bI5h8req30",
        ExportName = "sceAudioPropagationRoomCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int RoomCreate(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var outRoomAddress = ctx[CpuRegister.Rsi];
        if (PropagationDisabled)
        {
            Trace($"room_create system=0x{systemHandle:X} -> disabled");
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED);
        }
        if (outRoomAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // rdx carries a stale rodata pointer from the caller's previous call
        // and is not an argument here; writing through it would corrupt the
        // image, so only the rsi slot (roomObj+8, pre-zeroed) is touched.
        var handle = NextHandle(RoomHandleTag);
        Trace($"room_create system=0x{systemHandle:X} -> handle=0x{handle:X}");
        return ctx.TryWriteUInt64(outRoomAddress, handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "b-dYXrjSNZU",
        ExportName = "sceAudioPropagationPortalCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int PortalCreate(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var paramAddress = ctx[CpuRegister.Rsi];
        var outPortalAddress = ctx[CpuRegister.Rdx];
        if (PropagationDisabled)
        {
            Trace($"portal_create system=0x{systemHandle:X} -> disabled");
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED);
        }
        if (paramAddress == 0 || outPortalAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = NextHandle(PortalHandleTag);
        Trace($"portal_create system=0x{systemHandle:X} param=0x{paramAddress:X} -> handle=0x{handle:X}");
        return ctx.TryWriteUInt64(outPortalAddress, handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "d84otraxt2s",
        ExportName = "sceAudioPropagationSourceCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SourceCreate(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var outSourceAddress = ctx[CpuRegister.Rsi];
        if (PropagationDisabled)
        {
            Trace($"source_create system=0x{systemHandle:X} -> disabled");
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED);
        }
        if (outSourceAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = NextHandle(SourceHandleTag);
        Trace($"source_create system=0x{systemHandle:X} -> handle=0x{handle:X}");
        return ctx.TryWriteUInt64(outSourceAddress, handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "CPLV6G-eXmk",
        ExportName = "sceAudioPropagationSystemRegisterMaterial",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SystemRegisterMaterial(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var paramAddress = ctx[CpuRegister.Rsi];
        var outMaterialAddress = ctx[CpuRegister.Rdx];
        if (PropagationDisabled)
        {
            Trace($"system_register_material system=0x{systemHandle:X} -> disabled");
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED);
        }
        if (paramAddress == 0 || outMaterialAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // The game reads the u64 back and inserts it into a std::map keyed
        // by material name, so this must be a real, unique handle.
        var handle = NextHandle(MaterialHandleTag);
        Trace($"system_register_material system=0x{systemHandle:X} param=0x{paramAddress:X} -> handle=0x{handle:X}");
        return ctx.TryWriteUInt64(outMaterialAddress, handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "XKCN4gpeYsM",
        ExportName = "sceAudioPropagationSystemUnregisterMaterial",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SystemUnregisterMaterial(CpuContext ctx)
    {
        Trace($"system_unregister_material system=0x{ctx[CpuRegister.Rdi]:X} material=0x{ctx[CpuRegister.Rsi]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "VlBT16890mA",
        ExportName = "sceAudioPropagationSystemSetRays",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SystemSetRays(CpuContext ctx)
    {
        Trace($"system_set_rays system=0x{ctx[CpuRegister.Rdi]:X} rays=0x{ctx[CpuRegister.Rsi]:X} count=0x{ctx[CpuRegister.Rdx]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "ht-QXT3zGxo",
        ExportName = "sceAudioPropagationSystemGetRays",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SystemGetRays(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var rayArrayAddress = ctx[CpuRegister.Rsi];
        var countAddress = ctx[CpuRegister.Rdx];
        if (rayArrayAddress == 0 || countAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // The caller pre-zeroes every ray entry and rescans the array after
        // the call, so leaving the array untouched and reporting zero rays
        // through the in-out u32 count is fully consistent.
        Trace($"system_get_rays system=0x{systemHandle:X} rays=0x{rayArrayAddress:X} -> count=0");
        return ctx.TryWriteUInt32(countAddress, 0)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "kIdb+iQUzCs",
        ExportName = "sceAudioPropagationSystemSetAttributes",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SystemSetAttributes(CpuContext ctx)
    {
        Trace($"system_set_attributes system=0x{ctx[CpuRegister.Rdi]:X} attrs=0x{ctx[CpuRegister.Rsi]:X} count=0x{ctx[CpuRegister.Rdx]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "GrA9ke1QT+E",
        ExportName = "sceAudioPropagationSystemQueryInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SystemQueryInfo(CpuContext ctx)
    {
        // Not imported by any traced title; the out struct layout is unknown,
        // so succeed without writing rather than risk clobbering guest state.
        Trace($"system_query_info system=0x{ctx[CpuRegister.Rdi]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "B2KI2AachWE",
        ExportName = "sceAudioPropagationSystemLock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SystemLock(CpuContext ctx)
    {
        Trace($"system_lock system=0x{ctx[CpuRegister.Rdi]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "x5VPqg5iyAk",
        ExportName = "sceAudioPropagationSystemDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SystemDestroy(CpuContext ctx)
    {
        Trace($"system_destroy system=0x{ctx[CpuRegister.Rdi]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "S0JwP2AFTTE",
        ExportName = "sceAudioPropagationRoomDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int RoomDestroy(CpuContext ctx)
    {
        Trace($"room_destroy room=0x{ctx[CpuRegister.Rdi]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "ZQXE-xS6MTE",
        ExportName = "sceAudioPropagationPortalDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int PortalDestroy(CpuContext ctx)
    {
        Trace($"portal_destroy portal=0x{ctx[CpuRegister.Rdi]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "wkseM3LWPuc",
        ExportName = "sceAudioPropagationSourceDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SourceDestroy(CpuContext ctx)
    {
        Trace($"source_destroy source=0x{ctx[CpuRegister.Rdi]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "-wsUTr31yeg",
        ExportName = "sceAudioPropagationSourceSetAttributes",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SourceSetAttributes(CpuContext ctx)
    {
        Trace($"source_set_attributes source=0x{ctx[CpuRegister.Rdi]:X} attrs=0x{ctx[CpuRegister.Rsi]:X} count=0x{ctx[CpuRegister.Rdx]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "5vzOS2pHMFc",
        ExportName = "sceAudioPropagationSourceSetAudioPaths",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SourceSetAudioPaths(CpuContext ctx)
    {
        Trace($"source_set_audio_paths source=0x{ctx[CpuRegister.Rdi]:X} paths=0x{ctx[CpuRegister.Rsi]:X} count=0x{ctx[CpuRegister.Rdx]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "PBcrVpEqUVY",
        ExportName = "sceAudioPropagationSourceCalculateAudioPaths",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SourceCalculateAudioPaths(CpuContext ctx)
    {
        // Inputs only (source, ray array, ray count, listener, path count);
        // the caller re-reads its own path-count field afterwards, which
        // SourceGetAudioPathCount keeps at zero.
        Trace($"source_calculate_audio_paths source=0x{ctx[CpuRegister.Rdi]:X} rays=0x{ctx[CpuRegister.Rsi]:X} count=0x{ctx[CpuRegister.Rdx]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "aKJZx7wCma8",
        ExportName = "sceAudioPropagationSourceGetRays",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SourceGetRays(CpuContext ctx)
    {
        var sourceHandle = ctx[CpuRegister.Rdi];
        var rayArrayAddress = ctx[CpuRegister.Rsi];
        var countAddress = ctx[CpuRegister.Rdx];
        if (rayArrayAddress == 0 || countAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Same shape as SystemGetRays: caller-zeroed ray array plus an
        // in-out u32 count (Astro Bot pre-sets it to its array capacity).
        Trace($"source_get_rays source=0x{sourceHandle:X} rays=0x{rayArrayAddress:X} -> count=0");
        return ctx.TryWriteUInt32(countAddress, 0)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "G+QLTfyLMYk",
        ExportName = "sceAudioPropagationSourceGetAudioPathCount",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SourceGetAudioPathCount(CpuContext ctx)
    {
        var sourceHandle = ctx[CpuRegister.Rdi];
        var outCountAddress = ctx[CpuRegister.Rsi];
        if (outCountAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // The caller compares the u32 against zero and skips its per-path
        // loop entirely when no paths exist, which is exactly what we want.
        Trace($"source_get_audio_path_count source=0x{sourceHandle:X} -> count=0");
        return ctx.TryWriteUInt32(outCountAddress, 0)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "eEeKqFeNI3o",
        ExportName = "sceAudioPropagationSourceGetAudioPath",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SourceGetAudioPath(CpuContext ctx)
    {
        // Unreachable while SourceGetAudioPathCount reports zero paths; the
        // out struct size is unknown, so succeed without writing anything.
        Trace($"source_get_audio_path source=0x{ctx[CpuRegister.Rdi]:X} index=0x{ctx[CpuRegister.Rsi]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "hhz9pITnC8k",
        ExportName = "sceAudioPropagationSourceRender",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SourceRender(CpuContext ctx)
    {
        // (system, render-param array, count). The output buffers belong to
        // the game; leaving them untouched yields silence, which is fine for
        // a stub with zero audio paths.
        Trace($"source_render system=0x{ctx[CpuRegister.Rdi]:X} params=0x{ctx[CpuRegister.Rsi]:X} count=0x{ctx[CpuRegister.Rdx]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "WXMhENV2NcA",
        ExportName = "sceAudioPropagationPortalSetAttributes",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int PortalSetAttributes(CpuContext ctx)
    {
        Trace($"portal_set_attributes portal=0x{ctx[CpuRegister.Rdi]:X} attrs=0x{ctx[CpuRegister.Rsi]:X} count=0x{ctx[CpuRegister.Rdx]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "BbOT4vBwAjs",
        ExportName = "sceAudioPropagationResetAttributes",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int ResetAttributes(CpuContext ctx)
    {
        Trace($"reset_attributes handle=0x{ctx[CpuRegister.Rdi]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "gCmQm6dvMxw",
        ExportName = "sceAudioPropagationReportApi",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int ReportApi(CpuContext ctx)
    {
        Trace($"report_api arg=0x{ctx[CpuRegister.Rdi]:X}");
        return ctx.SetReturn(0);
    }
}
