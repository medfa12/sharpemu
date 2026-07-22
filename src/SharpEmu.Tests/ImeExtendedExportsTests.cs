// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ime;
using SharpEmu.Libs.Mouse;
using Xunit;

namespace SharpEmu.Tests;

public sealed class ImeExtendedExportsTests : IDisposable
{
    private const ulong ParamAddress = 0x1000;
    private const ulong InputAddress = 0x2000;
    private const ulong WorkAddress = 0x4000;
    private const ulong OutputAddress = 0xA000;

    private const int ErrorDialogNotFinished = unchecked((int)0x80BC0106);

    public ImeExtendedExportsTests()
    {
        ImeDialogExports.ResetForTests();
    }

    public void Dispose()
    {
        ImeDialogExports.ResetForTests();
    }

    private static CpuContext NewContext(out SparseGuestMemory memory)
    {
        memory = new SparseGuestMemory();
        return new CpuContext(memory, Generation.Gen5);
    }

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    private static void WriteImeParam(SparseGuestMemory memory, bool dialog)
    {
        memory.TryWrite(ParamAddress, new byte[0x60]);
        memory.WriteUInt32(ParamAddress + 0x04, 1);
        memory.WriteUInt32(ParamAddress + 0x24, 32);
        memory.WriteUInt64(ParamAddress + 0x28, InputAddress);
        memory.TryWrite(InputAddress, new byte[66]);

        if (!dialog)
        {
            memory.WriteUInt64(ParamAddress + 0x40, WorkAddress);
            memory.WriteUInt64(ParamAddress + 0x50, 0x100000);
        }
    }

    [Fact]
    public void AssignedExports_AreRegisteredForGen5()
    {
        string[] imeNids =
        [
            "mN+ZoSN-8hQ", "uTW+63goeJs", "Lf3DeGWC6xg", "zHuMUGb-AQI", "OTb0Mg+1i1k",
            "TmVP8LzcFcY", "Ho5NVQzpKHo", "P5dPeiLwm-M", "tKLmVIUkpyM", "NYDsL9a0oEo",
            "l01GKoyiQrY", "E2OcGgi-FPY", "JAiMBkOTYKI", "JoPdCUXOzMU", "FuEl46uHDyo",
            "E+f1n8e8DAw", "evjOsE18yuI", "wVkehxutK-U", "T6FYjZXG93o", "ziPDcIjO0Vk",
            "VkqLPArfFdc", "oYkJlMK51SA", "ua+13Hk9kKs", "3Hx2Uw9xnv8", "RPydv-Jr1bc",
            "16UI54cWRQk", "WmYDzdC4EHI", "TQaogSaqkEk", "WLxUN2WMim8", "ieCNrVrzKd4",
            "TXYHFRuL8UY", "oOwl47ouxoM", "gtoTsGM9vEY", "wTKF4mUlSew", "rM-1hkuOhh0",
            "42xMaQ+GLeQ", "ZmmV6iukhyo", "EQBusz6Uhp8", "LBicRa-hj3A", "-IAOwd2nO7g",
            "qDagOjvJdNk", "tNOlmxee-Nk", "rASXozKkQ9g", "idvMaIu5H+k", "ga5GOgThbjo",
            "RuSca8rS6yA", "J7COZrgSFRA", "WqAayyok5p0", "O7Fdd+Oc-qQ", "fwcPR7+7Rks"
        ];
        string[] dialogNids =
        [
            "oBmw4xrmfKs", "UFcyYDf+e88", "bX4H+sxPI-o", "fy6ntM25pEc", "8jqzzPioYl8",
            "wqsJvRXwl58", "CRD+jSErEJQ", "x01jxu+vxlc", "IADmD4tScBY", "NUeBrN7hzf0",
            "KR6QDasuKco", "oe92cnJQ9HE", "IoKIpNf9EK0", "-2WqB87KKGg", "gyTyVn+bXMw"
        ];
        string[] mouseNids =
        [
            "Ymyy1HSSJLQ", "BRXOoXQtb+k", "WiGKINCZWkc", "eDQTFHbgeTU", "jJP1vYMEPd4",
            "QA9Qupz3Zjw", "1FeceR5YhAo", "crkFfp-cmFo", "ghLUU2Z5Lcg", "6aANndpS0Wo"
        ];

        var manager = new ModuleManager();
        manager.RegisterFromAssembly(typeof(ImeExports).Assembly, Generation.Gen5);

        foreach (var nid in imeNids.Concat(dialogNids).Concat(mouseNids))
        {
            Assert.True(manager.TryGetExport(nid, out _), $"Missing assigned export {nid}");
        }

        Assert.Equal(50, imeNids.Length);
        Assert.Equal(15, dialogNids.Length);
        Assert.Equal(10, mouseNids.Length);
    }

    [Fact]
    public void ImeParamInit_ClearsFullStructureAndSetsInvalidUser()
    {
        var ctx = NewContext(out var memory);
        memory.TryWrite(ParamAddress, Enumerable.Repeat((byte)0xA5, 0x60).ToArray());
        ctx[CpuRegister.Rdi] = ParamAddress;

        ImeExports.ImeParamInit(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(ParamAddress, out var userId));
        Assert.True(ctx.TryReadUInt64(ParamAddress + 0x58, out var tail));
        Assert.Equal(uint.MaxValue, userId);
        Assert.Equal(0UL, tail);
    }

    [Fact]
    public void ImeOpenAndSetText_UpdateGuestInputBuffer()
    {
        var ctx = NewContext(out var memory);
        WriteImeParam(memory, dialog: false);
        ctx[CpuRegister.Rdi] = ParamAddress;
        ImeExports.ImeOpen(ctx);
        Assert.Equal(0, Result(ctx));

        memory.WriteUInt16(OutputAddress, 'O');
        memory.WriteUInt16(OutputAddress + 2, 'K');
        ctx[CpuRegister.Rdi] = OutputAddress;
        ctx[CpuRegister.Rsi] = 2;
        ImeExports.ImeSetText(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(InputAddress, out var text));
        Assert.True(ctx.TryReadUInt16(InputAddress + 4, out var terminator));
        Assert.Equal(0x004B004FU, text);
        Assert.Equal((ushort)0, terminator);

        ImeExports.FinalizeImeModule(ctx);
    }

    [Fact]
    public void ImePanelSize_UsesPs5DimensionsAndHighResolutionOption()
    {
        var ctx = NewContext(out var memory);
        memory.TryWrite(ParamAddress, new byte[0x60]);
        memory.WriteUInt32(ParamAddress + 0x04, 4);
        memory.WriteUInt32(ParamAddress + 0x20, 0x4000);
        ctx[CpuRegister.Rdi] = ParamAddress;
        ctx[CpuRegister.Rsi] = OutputAddress;
        ctx[CpuRegister.Rdx] = OutputAddress + 4;

        ImeExports.ImeGetPanelSize(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(OutputAddress, out var width));
        Assert.True(ctx.TryReadUInt32(OutputAddress + 4, out var height));
        Assert.Equal(0x2E4U, width);
        Assert.Equal(0x324U, height);
    }

    [Fact]
    public void DialogLifecycle_ReportsAbortedResultThenTerminates()
    {
        var ctx = NewContext(out var memory);
        WriteImeParam(memory, dialog: true);
        ctx[CpuRegister.Rdi] = ParamAddress;

        ImeDialogExports.ImeDialogInit(ctx);
        Assert.Equal(0, Result(ctx));
        ImeDialogExports.ImeDialogGetStatus(ctx);
        Assert.Equal(1, Result(ctx));

        ctx[CpuRegister.Rdi] = OutputAddress;
        ImeDialogExports.ImeDialogGetResult(ctx);
        Assert.Equal(ErrorDialogNotFinished, Result(ctx));

        ImeDialogExports.ImeDialogAbort(ctx);
        Assert.Equal(0, Result(ctx));
        ctx[CpuRegister.Rdi] = OutputAddress;
        ImeDialogExports.ImeDialogGetResult(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(OutputAddress, out var endStatus));
        Assert.True(ctx.TryReadUInt64(OutputAddress + 8, out var reserved));
        Assert.Equal(2U, endStatus);
        Assert.Equal(0UL, reserved);

        ImeDialogExports.ImeDialogTerm(ctx);
        Assert.Equal(0, Result(ctx));
        ImeDialogExports.ImeDialogGetStatus(ctx);
        Assert.Equal(0, Result(ctx));
    }
}
