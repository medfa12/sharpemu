// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Agc;

internal static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private bool TryEmitVectorAlu(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Opcode == "VNop")
            {
                return true;
            }

            if (instruction.Opcode.StartsWith("VCmp", StringComparison.Ordinal))
            {
                return TryEmitVectorCompare(instruction, out error);
            }

            if (instruction.Opcode == "VReadfirstlaneB32")
            {
                if (instruction.Destinations.Count == 0 ||
                    instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister ||
                    instruction.Sources.Count == 0)
                {
                    error = "invalid read-first-lane operands";
                    return false;
                }

                var laneValue = GetRawSource(instruction, 0);
                if (_subgroupInvocationIdInput != 0)
                {
                    // SPIR-V's BroadcastFirst uses the first host-active
                    // invocation. Guest EXEC is modeled as data, so obtain the
                    // guest-active mask explicitly and read the value from its
                    // first set lane. GroupNonUniformBroadcast is illegal here
                    // (its lane id must be constant before SPIR-V 1.5; a computed
                    // id is silent undefined behavior on NVIDIA), so use
                    // GroupNonUniformShuffle, which accepts a dynamic id. When
                    // EXEC is 0, FindILsb yields 0xFFFFFFFF; hardware
                    // readfirstlane reads lane 0 in that case, so clamp.
                    var activeLanes = _module.AddInstruction(
                        SpirvOp.GroupNonUniformBallot,
                        _uvec4Type,
                        UInt(3),
                        Load(_boolType, _exec));
                    var activeLow = _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _uintType,
                        activeLanes,
                        0);
                    var firstActiveLane = Ext(73, _uintType, activeLow);
                    var noActiveLanes = _module.AddInstruction(
                        SpirvOp.IEqual,
                        _boolType,
                        activeLow,
                        UInt(0));
                    var sourceLane = _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        noActiveLanes,
                        UInt(0),
                        firstActiveLane);
                    laneValue = _module.AddInstruction(
                        SpirvOp.GroupNonUniformShuffle,
                        _uintType,
                        UInt(3),
                        laneValue,
                        sourceLane);
                }

                StoreS(instruction.Destinations[0].Value, laneValue);
                return true;
            }

            if (instruction.Opcode is "VMovrelsB32" or "VMovreldB32" or "VMovrelsdB32")
            {
                return TryEmitRelativeMove(instruction, out error);
            }

            if (instruction.Opcode == "VReadlaneB32")
            {
                return TryEmitReadlane(instruction, out error);
            }

            if (!TryGetVectorDestination(instruction, out var destination))
            {
                error = "missing vector destination";
                return false;
            }

            uint result;
            switch (instruction.Opcode)
            {
                case "VMovB32":
                    result = GetRawSource(instruction, 0);
                    break;
                case "VWritelaneB32":
                {
                    // VDST[lane(SSRC1)] = SSRC0. Only the addressed lane takes
                    // the new value, and the write ignores EXEC.
                    var previous = LoadV(destination);
                    var lane = BitwiseAnd(
                        GetRawSource(instruction, 1),
                        UInt(RdnaWaveLaneCount - 1));
                    var currentLane = _subgroupInvocationIdInput != 0
                        ? BitwiseAnd(
                            Load(_uintType, _subgroupInvocationIdInput),
                            UInt(RdnaWaveLaneCount - 1))
                        : UInt(0);
                    var isTargetLane = _module.AddInstruction(
                        SpirvOp.IEqual,
                        _boolType,
                        currentLane,
                        lane);
                    StoreV(
                        destination,
                        _module.AddInstruction(
                            SpirvOp.Select,
                            _uintType,
                            isTargetLane,
                            GetRawSource(instruction, 0),
                            previous),
                        guardWithExec: false);
                    return true;
                }
                case "VCndmaskB32":
                {
                    var condition = instruction.Sources.Count > 2
                        ? IsCurrentLaneSet(GetRawSource64(instruction, 2))
                        : Load(_boolType, _vcc);
                    result = _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        condition,
                        GetRawSource(instruction, 1),
                        GetRawSource(instruction, 0));
                    break;
                }
                case "VCvtU32F32":
                    result = _module.AddInstruction(
                        SpirvOp.ConvertFToU,
                        _uintType,
                        GetFloatSource(instruction, 0));
                    break;
                case "VCvtI32F32":
                case "VCvtRpiI32F32":
                case "VCvtFlrI32F32":
                {
                    var source = GetFloatSource(instruction, 0);
                    if (instruction.Opcode == "VCvtRpiI32F32")
                    {
                        source = Ext(9, _floatType, source);
                    }
                    else if (instruction.Opcode == "VCvtFlrI32F32")
                    {
                        source = Ext(8, _floatType, source);
                    }

                    result = Bitcast(
                        _uintType,
                        _module.AddInstruction(SpirvOp.ConvertFToS, _intType, source));
                    break;
                }
                case "VCvtF32I32":
                {
                    var signed = Bitcast(_intType, GetRawSource(instruction, 0));
                    result = Bitcast(
                        _uintType,
                        _module.AddInstruction(SpirvOp.ConvertSToF, _floatType, signed));
                    break;
                }
                case "VCvtF32U32":
                    result = Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.ConvertUToF,
                            _floatType,
                            GetRawSource(instruction, 0)));
                    break;
                case "VCvtF32Ubyte0":
                case "VCvtF32Ubyte1":
                case "VCvtF32Ubyte2":
                case "VCvtF32Ubyte3":
                {
                    var shift = (uint)(instruction.Opcode[^1] - '0') * 8;
                    var raw = ShiftRightLogical(GetRawSource(instruction, 0), UInt(shift));
                    raw = BitwiseAnd(raw, UInt(0xFF));
                    result = Bitcast(
                        _uintType,
                        _module.AddInstruction(SpirvOp.ConvertUToF, _floatType, raw));
                    break;
                }
                case "VCvtF16F32":
                {
                    var vector = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        _vec2Type,
                        GetFloatSource(instruction, 0),
                        Float(0));
                    result = BitwiseAnd(Ext(58, _uintType, vector), UInt(0xFFFF));
                    break;
                }
                case "VCvtF32F16":
                {
                    var unpacked = Ext(62, _vec2Type, GetRawSource(instruction, 0));
                    var value = _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _floatType,
                        unpacked,
                        0);
                    result = Bitcast(_uintType, value);
                    break;
                }
                case "VCvtOffF32I4":
                    result = EmitCvtOffF32I4(instruction);
                    break;
                case "VCvtPkU8F32":
                {
                    var converted = _module.AddInstruction(
                        SpirvOp.ConvertFToU,
                        _uintType,
                        GetFloatSource(instruction, 0));
                    var offset = ShiftLeftLogical(
                        BitwiseAnd(GetRawSource(instruction, 1), UInt(3)),
                        UInt(3));
                    result = _module.AddInstruction(
                        SpirvOp.BitFieldInsert,
                        _uintType,
                        GetRawSource(instruction, 2),
                        converted,
                        offset,
                        UInt(8));
                    break;
                }
                case "VRcpF32":
                case "VRcpIflagF32":
                    result = EmitFloatResult(
                        instruction,
                        _module.AddInstruction(
                            SpirvOp.FDiv,
                            _floatType,
                            Float(1),
                            GetFloatSource(instruction, 0)));
                    break;
                case "VLogF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(30, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VLdexpF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(
                            53,
                            _floatType,
                            GetFloatSource(instruction, 0),
                            Bitcast(_intType, GetRawSource(instruction, 1))));
                    break;
                case "VExpF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(29, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VRsqF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(32, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VFractF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(10, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VTruncF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(3, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VCeilF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(9, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VRndneF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(2, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VFloorF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(8, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VSqrtF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(31, _floatType, GetFloatSource(instruction, 0)));
                    break;
                case "VSinF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(
                            13,
                            _floatType,
                            _module.AddInstruction(
                                SpirvOp.FMul,
                                _floatType,
                                GetFloatSource(instruction, 0),
                                Float(MathF.Tau))));
                    break;
                case "VCosF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(
                            14,
                            _floatType,
                            _module.AddInstruction(
                                SpirvOp.FMul,
                                _floatType,
                                GetFloatSource(instruction, 0),
                                Float(MathF.Tau))));
                    break;
                case "VAddF32":
                    result = EmitFloatBinary(instruction, SpirvOp.FAdd);
                    break;
                case "VSubF32":
                    result = EmitFloatBinary(instruction, SpirvOp.FSub);
                    break;
                case "VSubrevF32":
                    result = EmitFloatBinary(instruction, SpirvOp.FSub, reverse: true);
                    break;
                case "VMulF32":
                    result = EmitFloatBinary(instruction, SpirvOp.FMul);
                    break;
                case "VMinF32":
                    result = EmitFloatExtBinary(instruction, 37);
                    break;
                case "VMaxF32":
                    result = EmitFloatExtBinary(instruction, 40);
                    break;
                case "VMadF32":
                case "VFmaF32":
                case "VMadMkF32":
                case "VMadAkF32":
                case "VFmamkF32":
                case "VFmaakF32":
                    result = EmitFloatResult(
                        instruction,
                        Ext(
                            50,
                            _floatType,
                            GetFloatSource(instruction, 0),
                            GetFloatSource(instruction, 1),
                            GetFloatSource(instruction, 2)));
                    break;
                case "VMacF32":
                case "VFmacF32":
                {
                    var addend = Bitcast(_floatType, LoadV(destination));
                    result = EmitFloatResult(
                        instruction,
                        Ext(
                            50,
                            _floatType,
                            GetFloatSource(instruction, 0),
                            GetFloatSource(instruction, 1),
                            addend));
                    break;
                }
                case "VMin3F32":
                    result = EmitFloatTernaryExt(instruction, 37);
                    break;
                case "VMax3F32":
                    result = EmitFloatTernaryExt(instruction, 40);
                    break;
                case "VAndB32":
                    result = EmitIntegerBinary(instruction, SpirvOp.BitwiseAnd);
                    break;
                case "VOrB32":
                    result = EmitIntegerBinary(instruction, SpirvOp.BitwiseOr);
                    break;
                case "VXorB32":
                    result = EmitIntegerBinary(instruction, SpirvOp.BitwiseXor);
                    break;
                case "VXnorB32":
                    result = _module.AddInstruction(
                        SpirvOp.Not,
                        _uintType,
                        EmitIntegerBinary(instruction, SpirvOp.BitwiseXor));
                    break;
                case "VNotB32":
                    result = _module.AddInstruction(
                        SpirvOp.Not,
                        _uintType,
                        GetRawSource(instruction, 0));
                    break;
                case "VBfrevB32":
                    result = _module.AddInstruction(
                        SpirvOp.BitReverse,
                        _uintType,
                        GetRawSource(instruction, 0));
                    break;
                case "VFfblB32":
                    result = Bitcast(
                        _uintType,
                        Ext(
                            73,
                            _intType,
                            Bitcast(_intType, GetRawSource(instruction, 0))));
                    break;
                case "VAddI32":
                case "VAddU32":
                    result = EmitIntegerBinary(instruction, SpirvOp.IAdd);
                    break;
                case "VAddcU32":
                    result = EmitAddWithCarry(instruction);
                    break;
                case "VSubI32":
                case "VSubU32":
                    result = EmitIntegerBinary(instruction, SpirvOp.ISub);
                    break;
                case "VSubrevI32":
                case "VSubrevU32":
                    result = EmitIntegerBinary(instruction, SpirvOp.ISub, reverse: true);
                    break;
                case "VSubbU32":
                    result = EmitSubtractWithBorrow(instruction, reverse: false);
                    break;
                case "VSubbrevU32":
                    result = EmitSubtractWithBorrow(instruction, reverse: true);
                    break;
                case "VMulLoU32":
                case "VMulLoI32":
                case "VMulU32U24":
                    result = EmitIntegerBinary(instruction, SpirvOp.IMul);
                    break;
                case "VMulHiU32":
                case "VMulHiU32U24":
                {
                    var left = GetRawSource(instruction, 0);
                    var right = GetRawSource(instruction, 1);
                    if (instruction.Opcode == "VMulHiU32U24")
                    {
                        left = BitwiseAnd(left, UInt(0x00FF_FFFF));
                        right = BitwiseAnd(right, UInt(0x00FF_FFFF));
                    }

                    var wideLeft = _module.AddInstruction(
                        SpirvOp.UConvert,
                        _ulongType,
                        left);
                    var wideRight = _module.AddInstruction(
                        SpirvOp.UConvert,
                        _ulongType,
                        right);
                    var product = _module.AddInstruction(
                        SpirvOp.IMul,
                        _ulongType,
                        wideLeft,
                        wideRight);
                    result = _module.AddInstruction(
                        SpirvOp.UConvert,
                        _uintType,
                        ShiftRightLogical64(
                            product,
                            _module.Constant64(_ulongType, 32)));
                    break;
                }
                case "VMulHiI32":
                {
                    var wideLeft = _module.AddInstruction(
                        SpirvOp.SConvert,
                        _longType,
                        Bitcast(_intType, GetRawSource(instruction, 0)));
                    var wideRight = _module.AddInstruction(
                        SpirvOp.SConvert,
                        _longType,
                        Bitcast(_intType, GetRawSource(instruction, 1)));
                    var product = _module.AddInstruction(
                        SpirvOp.IMul,
                        _longType,
                        wideLeft,
                        wideRight);
                    result = _module.AddInstruction(
                        SpirvOp.UConvert,
                        _uintType,
                        ShiftRightLogical64(
                            Bitcast(_ulongType, product),
                            _module.Constant64(_ulongType, 32)));
                    break;
                }
                case "VBcntU32B32":
                    result = IAdd(
                        _module.AddInstruction(
                            SpirvOp.BitCount,
                            _uintType,
                            GetRawSource(instruction, 0)),
                        GetRawSource(instruction, 1));
                    break;
                case "VMadU32U24":
                {
                    var left = BitwiseAnd(
                        GetRawSource(instruction, 0),
                        UInt(0x00FF_FFFF));
                    var right = BitwiseAnd(
                        GetRawSource(instruction, 1),
                        UInt(0x00FF_FFFF));
                    result = IAdd(
                        _module.AddInstruction(
                            SpirvOp.IMul,
                            _uintType,
                            left,
                            right),
                        GetRawSource(instruction, 2));
                    break;
                }
                case "VMadU32U16":
                {
                    var left = BitwiseAnd(
                        GetRawSource(instruction, 0),
                        UInt(0xFFFF));
                    var right = BitwiseAnd(
                        GetRawSource(instruction, 1),
                        UInt(0xFFFF));
                    result = IAdd(
                        _module.AddInstruction(
                            SpirvOp.IMul,
                            _uintType,
                            left,
                            right),
                        GetRawSource(instruction, 2));
                    break;
                }
                case "VLshrB32":
                    result = EmitIntegerBinary(instruction, SpirvOp.ShiftRightLogical);
                    break;
                case "VLshrrevB32":
                    result = EmitIntegerBinary(
                        instruction,
                        SpirvOp.ShiftRightLogical,
                        reverse: true);
                    break;
                case "VLshlB32":
                    result = EmitIntegerBinary(instruction, SpirvOp.ShiftLeftLogical);
                    break;
                case "VLshlrevB32":
                    result = EmitIntegerBinary(
                        instruction,
                        SpirvOp.ShiftLeftLogical,
                        reverse: true);
                    break;
                case "VAshrI32":
                case "VAshrrevI32":
                {
                    var reverse = instruction.Opcode == "VAshrrevI32";
                    var left = GetRawSource(instruction, reverse ? 1 : 0);
                    var right = GetRawSource(instruction, reverse ? 0 : 1);
                    result = ShiftRightArithmetic(left, right);
                    break;
                }
                case "VLshlAddU32":
                {
                    var shifted = ShiftLeftLogical(
                        GetRawSource(instruction, 0),
                        BitwiseAnd(GetRawSource(instruction, 1), UInt(31)));
                    result = IAdd(shifted, GetRawSource(instruction, 2));
                    break;
                }
                case "VLshlOrU32":
                {
                    var shifted = ShiftLeftLogical(
                        GetRawSource(instruction, 0),
                        BitwiseAnd(GetRawSource(instruction, 1), UInt(31)));
                    result = BitwiseOr(
                        shifted,
                        GetRawSource(instruction, 2));
                    break;
                }
                case "VAndOrB32":
                    result = BitwiseOr(
                        BitwiseAnd(
                            GetRawSource(instruction, 0),
                            GetRawSource(instruction, 1)),
                        GetRawSource(instruction, 2));
                    break;
                case "VOr3U32":
                    result = BitwiseOr(
                        BitwiseOr(
                            GetRawSource(instruction, 0),
                            GetRawSource(instruction, 1)),
                        GetRawSource(instruction, 2));
                    break;
                case "VPermlane16B32":
                    result = EmitPermlane16(instruction, exchangeRows: false);
                    break;
                case "VPermlanex16B32":
                    result = EmitPermlane16(instruction, exchangeRows: true);
                    break;
                case "VAddLshlU32":
                {
                    var added = IAdd(
                        GetRawSource(instruction, 0),
                        GetRawSource(instruction, 1));
                    result = ShiftLeftLogical(added, GetRawSource(instruction, 2));
                    break;
                }
                case "VAdd3U32":
                    result = IAdd(
                        IAdd(
                            GetRawSource(instruction, 0),
                            GetRawSource(instruction, 1)),
                        GetRawSource(instruction, 2));
                    break;
                case "VMinU32":
                    result = Ext(
                        38,
                        _uintType,
                        GetRawSource(instruction, 0),
                        GetRawSource(instruction, 1));
                    break;
                case "VMaxU32":
                    result = Ext(
                        41,
                        _uintType,
                        GetRawSource(instruction, 0),
                        GetRawSource(instruction, 1));
                    break;
                case "VMin3U32":
                    result = Ext(
                        38,
                        _uintType,
                        Ext(
                            38,
                            _uintType,
                            GetRawSource(instruction, 0),
                            GetRawSource(instruction, 1)),
                        GetRawSource(instruction, 2));
                    break;
                case "VMax3U32":
                    result = Ext(
                        41,
                        _uintType,
                        Ext(
                            41,
                            _uintType,
                            GetRawSource(instruction, 0),
                            GetRawSource(instruction, 1)),
                        GetRawSource(instruction, 2));
                    break;
                case "VMinI32":
                case "VMaxI32":
                {
                    var signedResult = Ext(
                        instruction.Opcode == "VMinI32" ? 39u : 42u,
                        _intType,
                        Bitcast(_intType, GetRawSource(instruction, 0)),
                        Bitcast(_intType, GetRawSource(instruction, 1)));
                    result = Bitcast(_uintType, signedResult);
                    break;
                }
                case "VMin3I32":
                case "VMax3I32":
                {
                    var operation = instruction.Opcode == "VMin3I32" ? 39u : 42u;
                    var left = Bitcast(
                        _intType,
                        GetRawSource(instruction, 0));
                    var middle = Bitcast(
                        _intType,
                        GetRawSource(instruction, 1));
                    var right = Bitcast(
                        _intType,
                        GetRawSource(instruction, 2));
                    result = Bitcast(
                        _uintType,
                        Ext(
                            operation,
                            _intType,
                            Ext(operation, _intType, left, middle),
                            right));
                    break;
                }
                case "VMed3U32":
                {
                    var left = GetRawSource(instruction, 0);
                    var middle = GetRawSource(instruction, 1);
                    var right = GetRawSource(instruction, 2);
                    var low = Ext(38, _uintType, left, middle);
                    var high = Ext(41, _uintType, left, middle);
                    result = Ext(
                        41,
                        _uintType,
                        low,
                        Ext(38, _uintType, high, right));
                    break;
                }
                case "VSadU32":
                {
                    var left = GetRawSource(instruction, 0);
                    var right = GetRawSource(instruction, 1);
                    var difference = _module.AddInstruction(
                        SpirvOp.ISub,
                        _uintType,
                        Ext(41, _uintType, left, right),
                        Ext(38, _uintType, left, right));
                    result = IAdd(difference, GetRawSource(instruction, 2));
                    break;
                }
                case "VMed3I32":
                {
                    var left = Bitcast(_intType, GetRawSource(instruction, 0));
                    var middle = Bitcast(_intType, GetRawSource(instruction, 1));
                    var right = Bitcast(_intType, GetRawSource(instruction, 2));
                    var low = Ext(39, _intType, left, middle);
                    var high = Ext(42, _intType, left, middle);
                    result = Bitcast(
                        _uintType,
                        Ext(
                            42,
                            _intType,
                            low,
                            Ext(39, _intType, high, right)));
                    break;
                }
                case "VMed3F32":
                {
                    var left = GetFloatSource(instruction, 0);
                    var middle = GetFloatSource(instruction, 1);
                    var right = GetFloatSource(instruction, 2);
                    var low = Ext(37, _floatType, left, middle);
                    var high = Ext(40, _floatType, left, middle);
                    result = EmitFloatResult(
                        instruction,
                        Ext(
                            40,
                            _floatType,
                            low,
                            Ext(37, _floatType, high, right)));
                    break;
                }
                case "VCubeidF32":
                    result = EmitCubeCoordinate(instruction, CubeCoordinate.Id);
                    break;
                case "VCubescF32":
                    result = EmitCubeCoordinate(instruction, CubeCoordinate.Sc);
                    break;
                case "VCubetcF32":
                    result = EmitCubeCoordinate(instruction, CubeCoordinate.Tc);
                    break;
                case "VCubemaF32":
                    result = EmitCubeCoordinate(instruction, CubeCoordinate.Ma);
                    break;
                case "VAddCoU32":
                {
                    var left = GetRawSource(instruction, 0);
                    var right = GetRawSource(instruction, 1);
                    result = IAdd(left, right);
                    var carry = _module.AddInstruction(
                        SpirvOp.ULessThan,
                        _boolType,
                        result,
                        left);
                    StoreCarryOut(instruction, carry);
                    break;
                }
                case "VSubCoU32":
                case "VSubrevCoU32":
                {
                    var reverse = instruction.Opcode == "VSubrevCoU32";
                    var left = GetRawSource(instruction, reverse ? 1 : 0);
                    var right = GetRawSource(instruction, reverse ? 0 : 1);
                    result = _module.AddInstruction(SpirvOp.ISub, _uintType, left, right);
                    var borrow = _module.AddInstruction(
                        SpirvOp.ULessThan,
                        _boolType,
                        left,
                        right);
                    StoreCarryOut(instruction, borrow);
                    break;
                }
                case "VBfeU32":
                {
                    var width = BitwiseAnd(GetRawSource(instruction, 2), UInt(31));
                    result = _module.AddInstruction(
                        SpirvOp.BitFieldUExtract,
                        _uintType,
                        GetRawSource(instruction, 0),
                        BitwiseAnd(GetRawSource(instruction, 1), UInt(31)),
                        width);
                    break;
                }
                case "VBfeI32":
                {
                    var offset = BitwiseAnd(GetRawSource(instruction, 1), UInt(31));
                    var width = BitwiseAnd(GetRawSource(instruction, 2), UInt(31));
                    result = Bitcast(
                        _uintType,
                        _module.AddInstruction(
                            SpirvOp.BitFieldSExtract,
                            _intType,
                            Bitcast(_intType, GetRawSource(instruction, 0)),
                            offset,
                            width));
                    break;
                }
                case "VBfiB32":
                {
                    var mask = GetRawSource(instruction, 0);
                    var insert = GetRawSource(instruction, 1);
                    var source = GetRawSource(instruction, 2);
                    result = _module.AddInstruction(
                        SpirvOp.BitwiseOr,
                        _uintType,
                        BitwiseAnd(mask, insert),
                        BitwiseAnd(
                            _module.AddInstruction(SpirvOp.Not, _uintType, mask),
                            source));
                    break;
                }
                case "VCvtPkrtzF16F32":
                {
                    var first = TruncateFloat32ForPack(GetFloatSource(instruction, 0));
                    var second = TruncateFloat32ForPack(GetFloatSource(instruction, 1));
                    if (_forcePackedStoreExecValues)
                    {
                        // Journal probe E16: pack an EXEC indicator instead of
                        // the computed halves so a readback shows whether the
                        // pack executed with an active lane (1.0) or a
                        // suppressed one (0.5). The store bypasses the EXEC
                        // guard on purpose; a guarded store would erase the
                        // very signal this probe measures.
                        var probe = _module.AddInstruction(
                            SpirvOp.Select,
                            _floatType,
                            Load(_boolType, _exec),
                            Float(1f),
                            Float(0.5f));
                        var probeVector = _module.AddInstruction(
                            SpirvOp.CompositeConstruct,
                            _vec2Type,
                            probe,
                            probe);
                        StoreV(
                            destination,
                            Ext(58, _uintType, probeVector),
                            guardWithExec: false);
                        return true;
                    }

                    var vector = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        _vec2Type,
                        first,
                        second);
                    result = Ext(58, _uintType, vector);
                    break;
                }
                case "VCvtPknormI16F32":
                case "VCvtPknormU16F32":
                {
                    // Pack two floats into snorm16/unorm16 halves; the
                    // GLSL.std.450 pack ops match the ISA clamp+round-ne.
                    var vector = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        _vec2Type,
                        GetFloatSource(instruction, 0),
                        GetFloatSource(instruction, 1));
                    result = Ext(
                        instruction.Opcode == "VCvtPknormI16F32" ? 56u : 57u,
                        _uintType,
                        vector);
                    break;
                }
                case "VMbcntLoU32B32":
                {
                    // D = popcount(S0 & lanes-below-me) + S1; a wave32 keeps
                    // every lane in the low half, so this covers the wave.
                    var lane = _subgroupInvocationIdInput != 0
                        ? BitwiseAnd(
                            Load(_uintType, _subgroupInvocationIdInput),
                            UInt(RdnaWaveLaneCount - 1))
                        : UInt(0);
                    var below = _module.AddInstruction(
                        SpirvOp.ISub,
                        _uintType,
                        ShiftLeftLogical(UInt(1), lane),
                        UInt(1));
                    var count = _module.AddInstruction(
                        SpirvOp.BitCount,
                        _uintType,
                        BitwiseAnd(GetRawSource(instruction, 0), below));
                    result = IAdd(count, GetRawSource(instruction, 1));
                    break;
                }
                case "VMbcntHiU32B32":
                {
                    // D = popcount(S0 & EXEC_HI-lanes-below-me) + S1. A wave32
                    // keeps every lane in the low half, so EXEC_HI is empty and
                    // the high popcount is always zero: the op just forwards S1
                    // (the running mbcnt accumulator seeded by v_mbcnt_lo).
                    result = GetRawSource(instruction, 1);
                    break;
                }
                case "VCvtPkU16U32":
                case "VCvtPkI16I32":
                    result = BitwiseOr(
                        BitwiseAnd(GetRawSource(instruction, 0), UInt(0xFFFF)),
                        ShiftLeftLogical(
                            BitwiseAnd(GetRawSource(instruction, 1), UInt(0xFFFF)),
                            UInt(16)));
                    break;
                case "VPkAddF16":
                case "VPkMulF16":
                case "VPkMinF16":
                case "VPkMaxF16":
                case "VPkFmaF16":
                    if (!TryEmitPackedF16(instruction, out result, out error))
                    {
                        return false;
                    }

                    break;
                case "VFmaMixF32":
                case "VFmaMixloF16":
                case "VFmaMixhiF16":
                    if (!TryEmitFmaMix(instruction, destination, out result, out error))
                    {
                        return false;
                    }

                    break;
                default:
                    error = $"unsupported vector opcode {instruction.Opcode}";
                    return false;
            }

            StoreV(destination, result);
            return true;
        }

        // Packed f16 (VOP3P) arithmetic. Each source register holds two f16 values,
        // one per result lane. Every f16<->f32 conversion is done with the explicit
        // integer sequences below (EmitHalfToFloat / EmitFloatToHalf) instead of
        // GLSL UnpackHalf2x16 / PackHalf2x16, whose subnormal and rounding behaviour
        // is implementation-defined without float-controls execution modes. The two
        // lanes are computed independently: each operand half is widened exactly to
        // f32, op_sel/op_sel_hi pick the source half and neg_lo/neg_hi negate it, the
        // op runs in f32, and the result is rounded back to f16 with round-to-nearest-
        // even. For add and mul this is bit-exact to a true f16 op (the f32 result
        // rounds losslessly to f16 by the double-rounding theorem; a f16 product even
        // fits in f32 exactly). min/max carry no rounding, so they are exact once the
        // conversions are. v_pk_fma_f16 cannot be reproduced by a plain f32
        // multiply-add plus a pack (that double-rounds), so it goes through the
        // round-to-odd sequence in EmitPackedF16FusedMultiplyAdd instead.
        private bool TryEmitPackedF16(
            Gen5ShaderInstruction instruction,
            out uint result,
            out string error)
        {
            result = 0;
            error = string.Empty;
            if (instruction.Control is not Gen5Vop3pControl control)
            {
                error = $"missing vop3p control for {instruction.Opcode}";
                return false;
            }

            var sourceCount = instruction.Opcode == "VPkFmaF16" ? 3 : 2;
            for (var index = 0; index < sourceCount; index++)
            {
                var source = instruction.Sources[index];
                if (source.Kind is not (Gen5OperandKind.VectorRegister or Gen5OperandKind.ScalarRegister))
                {
                    error =
                        $"unsupported vop3p operand {source} for {instruction.Opcode} (registers only)";
                    return false;
                }
            }

            var low = EmitPackedF16Lane(instruction, control, highLane: false);
            var high = EmitPackedF16Lane(instruction, control, highLane: true);
            result = BitwiseOr(low, ShiftLeftLogical(high, UInt(16)));
            return true;
        }

        // V_FMA_MIX_F32 / _MIXLO_F16 / _MIXHI_F16 (VOP3P opcodes 0x20 / 0x21 /
        // 0x22). Unlike the packed v_pk_* ops these compute a single f32
        // fma(a, b, c): each source is independently read as either a full f32
        // or one f16 half widened to f32. For the mix ops neg_hi means absolute
        // value and neg means negate, applied in that order. _MIXLO / _MIXHI
        // narrow the result and replace only the selected half of vdst.
        private bool TryEmitFmaMix(
            Gen5ShaderInstruction instruction,
            uint destination,
            out uint result,
            out string error)
        {
            result = 0;
            error = string.Empty;
            if (instruction.Control is not Gen5Vop3pControl control)
            {
                error = $"missing vop3p control for {instruction.Opcode}";
                return false;
            }

            var product = Bitcast(
                _uintType,
                Ext(
                    50,
                    _floatType,
                    EmitFmaMixOperand(instruction, control, 0),
                    EmitFmaMixOperand(instruction, control, 1),
                    EmitFmaMixOperand(instruction, control, 2)));
            if (control.Clamp)
            {
                product = EmitClampToUnitInterval(product);
            }

            if (instruction.Opcode == "VFmaMixF32")
            {
                result = product;
                return true;
            }

            var half = EmitFloatToHalf(product);
            var existing = LoadV(destination);
            result = instruction.Opcode == "VFmaMixloF16"
                ? BitwiseOr(BitwiseAnd(existing, UInt(0xFFFF_0000)), half)
                : BitwiseOr(
                    BitwiseAnd(existing, UInt(0x0000_FFFF)),
                    ShiftLeftLogical(half, UInt(16)));
            return true;
        }

        private uint EmitFmaMixOperand(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control,
            int index)
        {
            var source = instruction.Sources[index];
            var readAsHalf =
                ((control.OpSelHiMask >> index) & 1) != 0 &&
                source.Kind is Gen5OperandKind.VectorRegister or Gen5OperandKind.ScalarRegister;

            uint value;
            if (readAsHalf)
            {
                var raw = GetRawSource(instruction, index);
                var half = ((control.OpSelMask >> index) & 1) != 0
                    ? ShiftRightLogical(raw, UInt(16))
                    : raw;
                value = Bitcast(_floatType, EmitHalfToFloat(half));
            }
            else
            {
                value = GetFloatSource(instruction, index);
            }

            if (((control.NegHiMask >> index) & 1) != 0)
            {
                value = Ext(4, _floatType, value);
            }

            if (((control.NegLoMask >> index) & 1) != 0)
            {
                value = _module.AddInstruction(SpirvOp.FNegate, _floatType, value);
            }

            return value;
        }

        private uint EmitClampToUnitInterval(uint valueBits)
        {
            var value = Bitcast(_floatType, valueBits);
            var aboveZero = _module.AddInstruction(
                SpirvOp.FOrdGreaterThan,
                _boolType,
                value,
                Float(0));
            var lowerBounded = _module.AddInstruction(
                SpirvOp.Select,
                _floatType,
                aboveZero,
                value,
                Float(0));
            var belowOne = _module.AddInstruction(
                SpirvOp.FOrdLessThan,
                _boolType,
                lowerBounded,
                Float(1));
            var clamped = _module.AddInstruction(
                SpirvOp.Select,
                _floatType,
                belowOne,
                lowerBounded,
                Float(1));
            return Bitcast(_uintType, clamped);
        }

        // Computes one result lane (low or high) as a packed 16-bit f16 value.
        // Clamp is applied to the f32 result before narrowing. Since the bounds
        // are exact f16 values and the clamp is monotonic, this produces the same
        // f16 result as clamping after the conversion.
        private uint EmitPackedF16Lane(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control,
            bool highLane)
        {
            var left = EmitPackedF16Operand(instruction, control, 0, highLane);
            var right = EmitPackedF16Operand(instruction, control, 1, highLane);
            uint value;
            if (instruction.Opcode == "VPkFmaF16")
            {
                var addend = EmitPackedF16Operand(instruction, control, 2, highLane);
                value = EmitPackedF16FusedMultiplyAdd(left, right, addend);
            }
            else
            {
                value = Bitcast(_uintType, instruction.Opcode switch
                {
                    "VPkAddF16" => _module.AddInstruction(SpirvOp.FAdd, _floatType, left, right),
                    "VPkMulF16" => _module.AddInstruction(SpirvOp.FMul, _floatType, left, right),
                    "VPkMinF16" => EmitPackedF16MinMax(left, right, isMax: false),
                    "VPkMaxF16" => EmitPackedF16MinMax(left, right, isMax: true),
                    _ => left,
                });
            }

            if (control.Clamp)
            {
                value = EmitClampToUnitInterval(value);
            }

            return EmitFloatToHalf(value);
        }

        // Fused f16 multiply-add with a single rounding, emulated in f32 without the
        // Float16 capability. The f32 product of two widened f16 values is exact
        // (11-bit significands, and the exponent stays inside the f32 normal range:
        // any non-zero product magnitude is in [2^-48, 2^33]), so only the addition
        // rounds. An f32 add then an f16 pack would round twice; instead the add is
        // corrected to round-to-odd, which a following round-to-nearest-even pack
        // turns into the exactly-once-rounded fused result (innocuous double rounding
        // holds because f32 carries 24 significand bits >= 11 + 2).
        //
        // sum = RN(product + addend); Knuth's 2Sum recovers the exact residual
        // (product + addend) - sum from four more RN ops. 2Sum is exact for any two
        // finite f32 inputs; no intermediate here can overflow (|product| < 2^33,
        // |addend| < 2^16) and none can enter the f32 subnormal range (every finite
        // value in play is a multiple of 2^-48 by construction), so implementation
        // f32 denorm-flush modes never see a denormal. If the residual says the sum
        // was inexact and the sum's significand is even, step one ulp towards the
        // true value: consecutive floats have consecutive sign-magnitude encodings,
        // so that neighbour is the enclosing float with the odd significand.
        //
        // Inf/NaN inputs make the residual NaN (e.g. sum - addend = Inf - Inf); the
        // ordered compare below is then false and the IEEE sum passes through
        // unchanged. A residual of zero also covers the exact-sum case, where the
        // parity fix must not fire. Returns the round-to-odd f32 bit pattern.
        private uint EmitPackedF16FusedMultiplyAdd(uint left, uint right, uint addend)
        {
            var product = EmitPreciseFloat(SpirvOp.FMul, left, right);
            var sum = EmitPreciseFloat(SpirvOp.FAdd, product, addend);

            var productPart = EmitPreciseFloat(SpirvOp.FSub, sum, addend);
            var addendPart = EmitPreciseFloat(SpirvOp.FSub, sum, productPart);
            var productError = EmitPreciseFloat(SpirvOp.FSub, product, productPart);
            var addendError = EmitPreciseFloat(SpirvOp.FSub, addend, addendPart);
            var residual = EmitPreciseFloat(SpirvOp.FAdd, productError, addendError);

            var sumBits = Bitcast(_uintType, sum);
            var residualBits = Bitcast(_uintType, residual);
            var inexact = _module.AddInstruction(
                SpirvOp.FOrdNotEqual, _boolType, residual, Float(0));
            var evenSignificand = Equal(BitwiseAnd(sumBits, UInt(1)), 0);
            var adjust = _module.AddInstruction(
                SpirvOp.LogicalAnd, _boolType, inexact, evenSignificand);

            // Residual sign relative to the sum picks the step direction: same sign
            // means the true value lies away from zero (encoding + 1), opposite sign
            // means towards zero (encoding - 1). The sum cannot be zero here (any
            // inexact sum has magnitude >= 2^-48) and cannot be the largest finite
            // value (its significand is odd), so the step never crosses zero or Inf.
            var towardZero = IsNotZero(
                BitwiseAnd(BitwiseXor(sumBits, residualBits), UInt(0x8000_0000)));
            var stepped = SelectU(
                towardZero,
                ISubU(sumBits, UInt(1)),
                IAdd(sumBits, UInt(1)));
            return SelectU(adjust, stepped, sumBits);
        }

        // A float op the driver must evaluate exactly as written. The 2Sum
        // residual above is error-free only op by op; without NoContraction
        // driver compilers fold the sequence (e.g. contract product+sum into an
        // f32 fma and simplify the rebuilt terms), collapsing the residual to
        // zero. Observed on AMD RDNA3 Windows: the pinned midpoint case decays
        // to the double-rounded result unless every op in the chain is marked.
        private uint EmitPreciseFloat(SpirvOp operation, uint left, uint right)
        {
            var value = _module.AddInstruction(operation, _floatType, left, right);
            _module.AddDecoration(value, SpirvDecoration.NoContraction);
            return value;
        }

        // Reads source `index`, selects the half feeding this lane (op_sel / op_sel_hi),
        // widens it exactly to f32 and applies the lane's negate modifier (neg_lo / neg_hi).
        private uint EmitPackedF16Operand(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control,
            int index,
            bool highLane)
        {
            var raw = GetRawSource(instruction, index);
            var selectMask = highLane ? control.OpSelHiMask : control.OpSelMask;
            var half = ((selectMask >> index) & 1) != 0
                ? ShiftRightLogical(raw, UInt(16))
                : raw;
            var value = Bitcast(_floatType, EmitHalfToFloat(half));
            var negateMask = highLane ? control.NegHiMask : control.NegLoMask;
            if (((negateMask >> index) & 1) != 0)
            {
                value = _module.AddInstruction(SpirvOp.FNegate, _floatType, value);
            }

            return value;
        }

        // fminnum_like / fmaxnum_like: if one operand is NaN return the other; if both
        // are NaN return a NaN; otherwise the ordered smaller/larger. The ordering of
        // -0/+0 is unspecified under these opcodes, so the ordered compare is enough.
        private uint EmitPackedF16MinMax(uint left, uint right, bool isMax)
        {
            var compare = _module.AddInstruction(
                isMax ? SpirvOp.FOrdGreaterThan : SpirvOp.FOrdLessThan,
                _boolType,
                left,
                right);
            var numeric = _module.AddInstruction(
                SpirvOp.Select, _floatType, compare, left, right);
            var leftNan = _module.AddInstruction(SpirvOp.IsNan, _boolType, left);
            var rightNan = _module.AddInstruction(SpirvOp.IsNan, _boolType, right);
            var withRight = _module.AddInstruction(
                SpirvOp.Select, _floatType, rightNan, left, numeric);
            return _module.AddInstruction(
                SpirvOp.Select, _floatType, leftNan, right, withRight);
        }

        // Widens an f16 value held in the low 16 bits of `halfBits` to an f32 bit
        // pattern, exactly (subnormals normalised, Inf/NaN and signed zero preserved).
        // Mirrors the branchless HalfToFloat reference validated against System.Half.
        private uint EmitHalfToFloat(uint halfBits)
        {
            var sign = ShiftLeftLogical(BitwiseAnd(halfBits, UInt(0x8000)), UInt(16));
            var exponent = BitwiseAnd(ShiftRightLogical(halfBits, UInt(10)), UInt(0x1F));
            var mantissa = BitwiseAnd(halfBits, UInt(0x3FF));

            var normal = BitwiseOr(
                ShiftLeftLogical(IAdd(exponent, UInt(112)), UInt(23)),
                ShiftLeftLogical(mantissa, UInt(13)));
            var infinityNan = BitwiseOr(UInt(0x7F80_0000), ShiftLeftLogical(mantissa, UInt(13)));

            // Subnormal: normalise the mantissa. FindUMsb of (mantissa | 1) keeps the
            // op defined when mantissa is 0; that lane is discarded by the select below.
            var highBit = Ext(75, _uintType, BitwiseOr(mantissa, UInt(1)));
            var shift = ISubU(UInt(23), highBit);
            var subFraction = BitwiseAnd(ShiftLeftLogical(mantissa, shift), UInt(0x7F_FFFF));
            var subnormal = SelectU(
                IsNotZero(mantissa),
                BitwiseOr(ShiftLeftLogical(IAdd(highBit, UInt(103)), UInt(23)), subFraction),
                UInt(0));

            var magnitude = SelectU(
                Equal(exponent, 0),
                subnormal,
                SelectU(Equal(exponent, 31), infinityNan, normal));
            return BitwiseOr(sign, magnitude);
        }

        // Narrows an f32 bit pattern to an f16 value in the low 16 bits, rounding to
        // nearest even (subnormals, overflow-to-Inf and NaN/Inf handled). Mirrors the
        // branchless FloatToHalf reference validated exhaustively against System.Half.
        private uint EmitFloatToHalf(uint bits)
        {
            var sign = BitwiseAnd(ShiftRightLogical(bits, UInt(16)), UInt(0x8000));
            var absolute = BitwiseAnd(bits, UInt(0x7FFF_FFFF));

            var isInfinityNan = UCmp(SpirvOp.UGreaterThanEqual, absolute, UInt(0x7F80_0000));
            var isNan = UCmp(SpirvOp.UGreaterThan, absolute, UInt(0x7F80_0000));
            var infinityNan = BitwiseOr(
                BitwiseOr(sign, UInt(0x7C00)),
                SelectU(isNan, UInt(0x200), UInt(0)));

            var exponent = ShiftRightLogical(absolute, UInt(23));
            var mantissa = BitwiseAnd(absolute, UInt(0x7F_FFFF));
            var significand = BitwiseOr(mantissa, UInt(0x80_0000));

            // Normal path: round the 24-bit significand down to 11 bits (>> 13) with
            // round-to-nearest-even; the carry folds naturally into the exponent.
            var roundBit = BitwiseAnd(ShiftRightLogical(significand, UInt(13)), UInt(1));
            var rounded = ShiftRightLogical(IAdd(IAdd(significand, UInt(0xFFF)), roundBit), UInt(13));
            var halfExponent = ISubU(exponent, UInt(112));
            var normalBits = IAdd(ShiftLeftLogical(halfExponent, UInt(10)), ISubU(rounded, UInt(0x400)));
            var normal = SelectU(
                UCmp(SpirvOp.UGreaterThanEqual, exponent, UInt(113)),
                SelectU(UCmp(SpirvOp.UGreaterThanEqual, normalBits, UInt(0x7C00)), UInt(0x7C00), normalBits),
                UInt(0));

            // Subnormal path: value = round(significand >> (126 - exponent)) with RNE.
            // The shift is clamped to 25 so it stays defined; on this path it is >= 14.
            var distance = ISubU(UInt(126), exponent);
            var shift = SelectU(UCmp(SpirvOp.UGreaterThan, distance, UInt(25)), UInt(25), distance);
            var shiftMask = ISubU(ShiftLeftLogical(UInt(1), shift), UInt(1));
            var halfWay = ShiftLeftLogical(UInt(1), ISubU(shift, UInt(1)));
            var lowBits = BitwiseAnd(significand, shiftMask);
            var quotient = ShiftRightLogical(significand, shift);
            var roundUp = _module.AddInstruction(
                SpirvOp.LogicalOr,
                _boolType,
                UCmp(SpirvOp.UGreaterThan, lowBits, halfWay),
                _module.AddInstruction(
                    SpirvOp.LogicalAnd,
                    _boolType,
                    Equal(lowBits, halfWay),
                    IsNotZero(BitwiseAnd(quotient, UInt(1)))));
            var subnormal = IAdd(quotient, SelectU(roundUp, UInt(1), UInt(0)));

            var isSubnormal = UCmp(SpirvOp.ULessThanEqual, exponent, UInt(112));
            var finite = SelectU(isSubnormal, subnormal, normal);
            return SelectU(isInfinityNan, infinityNan, BitwiseOr(sign, finite));
        }

        private uint SelectU(uint condition, uint whenTrue, uint whenFalse) =>
            _module.AddInstruction(SpirvOp.Select, _uintType, condition, whenTrue, whenFalse);

        private uint UCmp(SpirvOp operation, uint left, uint right) =>
            _module.AddInstruction(operation, _boolType, left, right);

        private uint Equal(uint value, uint constant) =>
            _module.AddInstruction(SpirvOp.IEqual, _boolType, value, UInt(constant));

        private uint ISubU(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.ISub, _uintType, left, right);

        private bool TryEmitRelativeMove(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            // v_movrels_b32:  VDST = VGPR[SRC0 + M0]
            // v_movreld_b32:  VGPR[VDST + M0] = SRC0
            // v_movrelsd_b32: VGPR[VDST + M0] = VGPR[SRC0 + M0]
            // M0 is scalar register 124; the register file is a private array,
            // so a runtime-indexed access chain implements the relative move
            // per lane without any cross-lane traffic.
            error = string.Empty;
            if (instruction.Destinations.Count == 0 ||
                instruction.Destinations[0].Kind != Gen5OperandKind.VectorRegister ||
                instruction.Sources.Count == 0)
            {
                error = $"invalid relative-move operands for {instruction.Opcode}";
                return false;
            }

            var relativeSource =
                instruction.Opcode is "VMovrelsB32" or "VMovrelsdB32";
            if (relativeSource &&
                instruction.Sources[0].Kind != Gen5OperandKind.VectorRegister)
            {
                error = $"relative-move source must be a VGPR for {instruction.Opcode}";
                return false;
            }

            var m0 = LoadS(124);
            var value = relativeSource
                ? Load(
                    _uintType,
                    VectorPointerAt(IAdd(UInt(instruction.Sources[0].Value), m0)))
                : GetRawSource(instruction, 0);
            var destination = instruction.Destinations[0].Value;
            if (instruction.Opcode == "VMovrelsB32")
            {
                StoreV(destination, value);
                return true;
            }

            var pointer = VectorPointerAt(IAdd(UInt(destination), m0));
            var previous = Load(_uintType, pointer);
            Store(
                pointer,
                _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    Load(_boolType, _exec),
                    value,
                    previous));
            return true;
        }

        private bool TryEmitReadlane(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Destinations.Count == 0 ||
                instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister ||
                instruction.Sources.Count < 2)
            {
                error = "invalid read-lane operands";
                return false;
            }

            // SDST = VSRC0[lane(SSRC1)], independent of EXEC. Broadcast needs
            // a constant lane id before SPIR-V 1.5, so use the dynamic-id
            // GroupNonUniformShuffle exactly like VReadfirstlaneB32.
            var value = GetRawSource(instruction, 0);
            if (_subgroupInvocationIdInput != 0)
            {
                var lane = BitwiseAnd(
                    GetRawSource(instruction, 1),
                    UInt(RdnaWaveLaneCount - 1));
                value = _module.AddInstruction(
                    SpirvOp.GroupNonUniformShuffle,
                    _uintType,
                    UInt(3),
                    value,
                    lane);
            }

            StoreS(instruction.Destinations[0].Value, value);
            return true;
        }

        private bool TryEmitVectorCompare(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            uint condition = _module.ConstantBool(false);
            var opcode = instruction.Opcode;
            if (opcode is "VCmpClassF32" or "VCmpxClassF32")
            {
                var source = GetFloatSource(instruction, 0);
                var raw = GetRawSource(instruction, 0);
                var mask = GetRawSource(instruction, 1);
                var negative = IsNotZero(BitwiseAnd(raw, UInt(0x8000_0000)));
                var positive = _module.AddInstruction(
                    SpirvOp.LogicalNot,
                    _boolType,
                    negative);
                var nan = _module.AddInstruction(SpirvOp.IsNan, _boolType, source);
                var infinity =
                    _module.AddInstruction(SpirvOp.IsInf, _boolType, source);
                var zero = _module.AddInstruction(
                    SpirvOp.FOrdEqual,
                    _boolType,
                    source,
                    Float(0));
                var absolute = Ext(4, _floatType, source);
                var nonzero = _module.AddInstruction(
                    SpirvOp.FOrdGreaterThan,
                    _boolType,
                    absolute,
                    Float(0));
                var belowNormal = _module.AddInstruction(
                    SpirvOp.FOrdLessThan,
                    _boolType,
                    absolute,
                    Bitcast(_floatType, UInt(0x0080_0000)));
                var subnormal = _module.AddInstruction(
                    SpirvOp.LogicalAnd,
                    _boolType,
                    nonzero,
                    belowNormal);
                var special = _module.AddInstruction(
                    SpirvOp.LogicalOr,
                    _boolType,
                    nan,
                    _module.AddInstruction(
                        SpirvOp.LogicalOr,
                        _boolType,
                        infinity,
                        _module.AddInstruction(
                            SpirvOp.LogicalOr,
                            _boolType,
                            zero,
                            subnormal)));
                var normal = _module.AddInstruction(
                    SpirvOp.LogicalNot,
                    _boolType,
                    special);

                uint MaskedClass(uint bits, uint value)
                {
                    var enabled = IsNotZero(BitwiseAnd(mask, UInt(bits)));
                    return _module.AddInstruction(
                        SpirvOp.LogicalAnd,
                        _boolType,
                        enabled,
                        value);
                }

                uint SignedClass(uint negativeBit, uint positiveBit, uint value)
                {
                    var negativeClass = MaskedClass(
                        negativeBit,
                        _module.AddInstruction(
                            SpirvOp.LogicalAnd,
                            _boolType,
                            negative,
                            value));
                    var positiveClass = MaskedClass(
                        positiveBit,
                        _module.AddInstruction(
                            SpirvOp.LogicalAnd,
                            _boolType,
                            positive,
                            value));
                    return _module.AddInstruction(
                        SpirvOp.LogicalOr,
                        _boolType,
                        negativeClass,
                        positiveClass);
                }

                condition = MaskedClass(0x003, nan);
                condition = _module.AddInstruction(
                    SpirvOp.LogicalOr,
                    _boolType,
                    condition,
                    SignedClass(0x004, 0x200, infinity));
                condition = _module.AddInstruction(
                    SpirvOp.LogicalOr,
                    _boolType,
                    condition,
                    SignedClass(0x008, 0x100, normal));
                condition = _module.AddInstruction(
                    SpirvOp.LogicalOr,
                    _boolType,
                    condition,
                    SignedClass(0x010, 0x080, subnormal));
                condition = _module.AddInstruction(
                    SpirvOp.LogicalOr,
                    _boolType,
                    condition,
                    SignedClass(0x020, 0x040, zero));
            }
            else if (opcode is "VCmpFF32" or "VCmpxFF32" or "VCmpFI32" or "VCmpFU32")
            {
                condition = _module.ConstantBool(false);
            }
            else if (opcode is "VCmpTruF32" or "VCmpxTruF32" or "VCmpTI32" or "VCmpTU32")
            {
                condition = _module.ConstantBool(true);
            }
            else if (opcode is "VCmpOF32" or "VCmpxOF32" or "VCmpUF32" or "VCmpxUF32")
            {
                // The ordered/unordered predicates only test whether either
                // operand is NaN. SPIR-V's OpOrdered/OpUnordered are Kernel-only,
                // so build the same result from OpIsNan, which needs no extra
                // capability: unordered = isnan(a) || isnan(b), ordered = !that.
                var left = GetFloatSource(instruction, 0);
                var right = GetFloatSource(instruction, 1);
                var nanLeft = _module.AddInstruction(SpirvOp.IsNan, _boolType, left);
                var nanRight = _module.AddInstruction(SpirvOp.IsNan, _boolType, right);
                var unordered = _module.AddInstruction(
                    SpirvOp.LogicalOr,
                    _boolType,
                    nanLeft,
                    nanRight);
                condition = opcode is "VCmpUF32" or "VCmpxUF32"
                    ? unordered
                    : _module.AddInstruction(SpirvOp.LogicalNot, _boolType, unordered);
            }
            else if (opcode is not ("VCmpClassF32" or "VCmpxClassF32") &&
                     opcode.EndsWith("F32", StringComparison.Ordinal))
            {
                var left = GetFloatSource(instruction, 0);
                var right = GetFloatSource(instruction, 1);
                var operation = opcode switch
                {
                    "VCmpLtF32" or "VCmpxLtF32" => SpirvOp.FOrdLessThan,
                    "VCmpEqF32" or "VCmpxEqF32" => SpirvOp.FOrdEqual,
                    "VCmpLeF32" or "VCmpxLeF32" => SpirvOp.FOrdLessThanEqual,
                    "VCmpGtF32" or "VCmpxGtF32" => SpirvOp.FOrdGreaterThan,
                    "VCmpLgF32" or "VCmpxLgF32" => SpirvOp.FOrdNotEqual,
                    "VCmpGeF32" or "VCmpxGeF32" => SpirvOp.FOrdGreaterThanEqual,
                    "VCmpNeqF32" or "VCmpxNeqF32" => SpirvOp.FUnordNotEqual,
                    "VCmpNlgF32" or "VCmpxNlgF32" => SpirvOp.FUnordEqual,
                    "VCmpNltF32" or "VCmpxNltF32" => SpirvOp.FUnordGreaterThanEqual,
                    "VCmpNleF32" or "VCmpxNleF32" => SpirvOp.FUnordGreaterThan,
                    "VCmpNgtF32" or "VCmpxNgtF32" => SpirvOp.FUnordLessThanEqual,
                    "VCmpNgeF32" or "VCmpxNgeF32" => SpirvOp.FUnordLessThan,
                    _ => SpirvOp.Nop,
                };
                if (operation == SpirvOp.Nop)
                {
                    error = $"unsupported float compare {opcode}";
                    return false;
                }

                condition = _module.AddInstruction(operation, _boolType, left, right);
            }
            else if (opcode is not ("VCmpClassF32" or "VCmpxClassF32"))
            {
                var left = GetRawSource(instruction, 0);
                var right = GetRawSource(instruction, 1);
                var signed = opcode.EndsWith("I32", StringComparison.Ordinal);
                if (signed)
                {
                    left = Bitcast(_intType, left);
                    right = Bitcast(_intType, right);
                }

                var operation = opcode switch
                {
                    "VCmpEqI32" or "VCmpxEqI32" or
                    "VCmpEqU32" or "VCmpxEqU32" => SpirvOp.IEqual,
                    "VCmpNeI32" or "VCmpxNeI32" or
                    "VCmpNeU32" or "VCmpxNeU32" => SpirvOp.INotEqual,
                    "VCmpLtI32" or "VCmpxLtI32" => SpirvOp.SLessThan,
                    "VCmpLeI32" or "VCmpxLeI32" => SpirvOp.SLessThanEqual,
                    "VCmpGtI32" or "VCmpxGtI32" => SpirvOp.SGreaterThan,
                    "VCmpGeI32" or "VCmpxGeI32" => SpirvOp.SGreaterThanEqual,
                    "VCmpLtU32" or "VCmpxLtU32" => SpirvOp.ULessThan,
                    "VCmpLeU32" or "VCmpxLeU32" => SpirvOp.ULessThanEqual,
                    "VCmpGtU32" or "VCmpxGtU32" => SpirvOp.UGreaterThan,
                    "VCmpGeU32" or "VCmpxGeU32" => SpirvOp.UGreaterThanEqual,
                    _ => SpirvOp.Nop,
                };
                if (operation == SpirvOp.Nop)
                {
                    error = $"unsupported integer compare {opcode}";
                    return false;
                }

                condition = _module.AddInstruction(operation, _boolType, left, right);
            }

            // Vector compares fully overwrite the destination mask, but only
            // lanes enabled by EXEC can pass the test.
            var activeCondition = MaskWithExec(condition);

            // On gfx10, VCmpx writes EXEC only and preserves VCC; the sdst
            // operand was removed from the cmpx encodings on this generation,
            // so the SDWA scalar destination must never be written here.
            if (opcode.StartsWith("VCmpx", StringComparison.Ordinal))
            {
                StoreWaveMask(126, activeCondition);
            }
            else
            {
                var destination = instruction.Control is
                    Gen5SdwaControl { ScalarDestination: { } register }
                    ? register
                    : 106u;
                StoreWaveMask(destination, activeCondition);
            }

            return true;
        }

        private bool TryEmitScalarAlu(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Encoding == Gen5ShaderEncoding.Sopc)
            {
                return TryEmitScalarCompare(instruction, out error);
            }

            if (instruction.Destinations.Count == 0 ||
                instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister)
            {
                error = "missing scalar destination";
                return false;
            }

            var destination = instruction.Destinations[0].Value;
            if (instruction.Encoding == Gen5ShaderEncoding.Sopk)
            {
                var immediate = unchecked((uint)(short)(instruction.Words[0] & 0xFFFF));
                if (instruction.Opcode.StartsWith("SCmpk", StringComparison.Ordinal))
                {
                    return TryEmitScalarCompareK(instruction, destination, immediate, out error);
                }

                var current = LoadS(destination);
                var value = instruction.Opcode switch
                {
                    "SMovkI32" => UInt(immediate),
                    "SAddkI32" => IAdd(current, UInt(immediate)),
                    "SMulkI32" => _module.AddInstruction(
                        SpirvOp.IMul,
                        _uintType,
                        current,
                        UInt(immediate)),
                    _ => 0u,
                };
                if (value == 0)
                {
                    error = $"unsupported scalar immediate {instruction.Opcode}";
                    return false;
                }

                StoreS(destination, value);
                return true;
            }

            if (instruction.Opcode == "SGetpcB64")
            {
                var pc = _state.Program.Address +
                    instruction.Pc +
                    (ulong)(instruction.Words.Count * sizeof(uint));
                StoreS(destination, UInt((uint)pc));
                StoreS(destination + 1, UInt((uint)(pc >> 32)));
                return true;
            }

            if (instruction.Opcode.EndsWith("B64", StringComparison.Ordinal) ||
                instruction.Opcode is "SWqmB64" or "SBfeU64" or "SBfeI64")
            {
                return TryEmitScalar64(instruction, destination, out error);
            }

            var left = GetRawSource(instruction, 0);
            if (instruction.Opcode.EndsWith("SaveexecB32", StringComparison.Ordinal))
            {
                return TryEmitSaveexec32(instruction.Opcode, destination, left, out error);
            }

            uint result;
            switch (instruction.Opcode)
            {
                case "SMovB32":
                    result = left;
                    break;
                case "SNotB32":
                    result = _module.AddInstruction(SpirvOp.Not, _uintType, left);
                    StoreS(destination, result);
                    Store(_scc, IsNotZero(result));
                    return true;
                case "SAbsI32":
                    // GLSLstd450 SAbs(5); SCC reports a nonzero result.
                    result = Bitcast(
                        _uintType,
                        Ext(5, _intType, Bitcast(_intType, left)));
                    StoreS(destination, result);
                    Store(_scc, IsNotZero(result));
                    return true;
                case "SWqmB32":
                {
                    if (_subgroupInvocationIdInput == 0)
                    {
                        // Scalar path: a lone lane is its own quad.
                        StoreS(destination, left);
                        Store(_scc, IsNotZero(left));
                        return true;
                    }

                    // Whole-quad-mode: reconstruct the full mask, widen to
                    // whole quads, then keep the current lane's expanded bit.
                    var laneSet = IsCurrentLaneSet(
                        _module.AddInstruction(SpirvOp.UConvert, _ulongType, left));
                    var wqm = WholeQuadModeExpand(laneSet, out var expandedFullMask);
                    StoreS(
                        destination,
                        _module.AddInstruction(SpirvOp.UConvert, _uintType, wqm));
                    Store(_scc, IsNotZero64(expandedFullMask));
                    return true;
                }
                case "SBrevB32":
                    result = _module.AddInstruction(SpirvOp.BitReverse, _uintType, left);
                    StoreS(destination, result);
                    Store(_scc, IsNotZero(result));
                    return true;
                case "SBcnt1I32B32":
                    result = _module.AddInstruction(SpirvOp.BitCount, _uintType, left);
                    StoreS(destination, result);
                    Store(_scc, IsNotZero(result));
                    return true;
                case "SFF1I32B32":
                    result = Ext(73, _uintType, left);
                    StoreS(destination, result);
                    Store(_scc, IsNotZero(result));
                    return true;
                case "SBitset1B32":
                    result = _module.AddInstruction(
                        SpirvOp.BitFieldInsert,
                        _uintType,
                        LoadS(destination),
                        UInt(1),
                        BitwiseAnd(left, UInt(31)),
                        UInt(1));
                    StoreS(destination, result);
                    return true;
                default:
                {
                    if (instruction.Sources.Count < 2)
                    {
                        error = $"missing scalar source for {instruction.Opcode}";
                        return false;
                    }

                    var right = GetRawSource(instruction, 1);
                    switch (instruction.Opcode)
                    {
                        case "SAddU32":
                            result = IAdd(left, right);
                            Store(_scc, _module.AddInstruction(
                                SpirvOp.ULessThan,
                                _boolType,
                                result,
                                left));
                            break;
                        case "SSubU32":
                            result = _module.AddInstruction(
                                SpirvOp.ISub,
                                _uintType,
                                left,
                                right);
                            Store(_scc, _module.AddInstruction(
                                SpirvOp.UGreaterThan,
                                _boolType,
                                right,
                                left));
                            break;
                        case "SAddI32":
                            result = IAdd(left, right);
                            Store(_scc, SignedAddOverflow(left, right, result));
                            break;
                        case "SSubI32":
                            result = _module.AddInstruction(
                                SpirvOp.ISub,
                                _uintType,
                                left,
                                right);
                            Store(_scc, SignedSubOverflow(left, right, result));
                            break;
                        case "SAddcU32":
                        {
                            var carryIn = _module.AddInstruction(
                                SpirvOp.Select,
                                _uintType,
                                Load(_boolType, _scc),
                                UInt(1),
                                UInt(0));
                            var partial = IAdd(left, right);
                            result = IAdd(partial, carryIn);
                            var firstCarry = _module.AddInstruction(
                                SpirvOp.ULessThan,
                                _boolType,
                                partial,
                                left);
                            var secondCarry = _module.AddInstruction(
                                SpirvOp.ULessThan,
                                _boolType,
                                result,
                                partial);
                            Store(
                                _scc,
                                _module.AddInstruction(
                                    SpirvOp.LogicalOr,
                                    _boolType,
                                    firstCarry,
                                    secondCarry));
                            break;
                        }
                        case "SSubbU32":
                        {
                            var borrow = _module.AddInstruction(
                                SpirvOp.Select,
                                _uintType,
                                Load(_boolType, _scc),
                                UInt(1),
                                UInt(0));
                            var partial = _module.AddInstruction(
                                SpirvOp.ISub,
                                _uintType,
                                left,
                                right);
                            result = _module.AddInstruction(
                                SpirvOp.ISub,
                                _uintType,
                                partial,
                                borrow);
                            var firstBorrow = _module.AddInstruction(
                                SpirvOp.UGreaterThan,
                                _boolType,
                                right,
                                left);
                            var secondBorrow = _module.AddInstruction(
                                SpirvOp.LogicalAnd,
                                _boolType,
                                _module.AddInstruction(
                                    SpirvOp.IEqual,
                                    _boolType,
                                    borrow,
                                    UInt(1)),
                                _module.AddInstruction(
                                    SpirvOp.IEqual,
                                    _boolType,
                                    right,
                                    left));
                            Store(
                                _scc,
                                _module.AddInstruction(
                                    SpirvOp.LogicalOr,
                                    _boolType,
                                    firstBorrow,
                                    secondBorrow));
                            break;
                        }
                        case "SMulI32":
                            result = _module.AddInstruction(
                                SpirvOp.IMul,
                                _uintType,
                                left,
                                right);
                            break;
                        case "SMulHiU32":
                        {
                            var wideLeft = _module.AddInstruction(
                                SpirvOp.UConvert,
                                _ulongType,
                                left);
                            var wideRight = _module.AddInstruction(
                                SpirvOp.UConvert,
                                _ulongType,
                                right);
                            var product = _module.AddInstruction(
                                SpirvOp.IMul,
                                _ulongType,
                                wideLeft,
                                wideRight);
                            result = _module.AddInstruction(
                                SpirvOp.UConvert,
                                _uintType,
                                ShiftRightLogical64(
                                    product,
                                    _module.Constant64(_ulongType, 32)));
                            break;
                        }
                        case "SMulHiI32":
                        {
                            var wideLeft = _module.AddInstruction(
                                SpirvOp.SConvert,
                                _longType,
                                Bitcast(_intType, left));
                            var wideRight = _module.AddInstruction(
                                SpirvOp.SConvert,
                                _longType,
                                Bitcast(_intType, right));
                            var product = _module.AddInstruction(
                                SpirvOp.IMul,
                                _longType,
                                wideLeft,
                                wideRight);
                            result = _module.AddInstruction(
                                SpirvOp.UConvert,
                                _uintType,
                                ShiftRightLogical64(
                                    Bitcast(_ulongType, product),
                                    _module.Constant64(_ulongType, 32)));
                            break;
                        }
                        case "SAndB32":
                            result = BitwiseAnd(left, right);
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SOrB32":
                            result = _module.AddInstruction(
                                SpirvOp.BitwiseOr,
                                _uintType,
                                left,
                                right);
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SXorB32":
                            result = _module.AddInstruction(
                                SpirvOp.BitwiseXor,
                                _uintType,
                                left,
                                right);
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SAndn2B32":
                            result = BitwiseAnd(
                                left,
                                _module.AddInstruction(SpirvOp.Not, _uintType, right));
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SOrn2B32":
                            result = _module.AddInstruction(
                                SpirvOp.BitwiseOr,
                                _uintType,
                                left,
                                _module.AddInstruction(SpirvOp.Not, _uintType, right));
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SNandB32":
                            result = _module.AddInstruction(
                                SpirvOp.Not,
                                _uintType,
                                BitwiseAnd(left, right));
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SNorB32":
                            result = _module.AddInstruction(
                                SpirvOp.Not,
                                _uintType,
                                _module.AddInstruction(
                                    SpirvOp.BitwiseOr,
                                    _uintType,
                                    left,
                                    right));
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SXnorB32":
                            result = _module.AddInstruction(
                                SpirvOp.Not,
                                _uintType,
                                _module.AddInstruction(
                                    SpirvOp.BitwiseXor,
                                    _uintType,
                                    left,
                                    right));
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SLshlB32":
                            result = ShiftLeftLogical(left, right);
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SLshrB32":
                            result = ShiftRightLogical(
                                left,
                                BitwiseAnd(right, UInt(31)));
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SAshrI32":
                            result = ShiftRightArithmetic(left, right);
                            Store(_scc, IsNotZero(result));
                            break;
                        case "SBfmB32":
                            result = _module.AddInstruction(
                                SpirvOp.BitFieldInsert,
                                _uintType,
                                UInt(0),
                                UInt(uint.MaxValue),
                                BitwiseAnd(right, UInt(31)),
                                BitwiseAnd(left, UInt(31)));
                            break;
                        case "SBfeU32":
                        case "SBfeI32":
                        {
                            var offset = BitwiseAnd(right, UInt(31));
                            var requestedWidth = BitwiseAnd(
                                ShiftRightLogical(right, UInt(16)),
                                UInt(0x7F));
                            var remaining = _module.AddInstruction(
                                SpirvOp.ISub,
                                _uintType,
                                UInt(32),
                                offset);
                            var width = Ext(
                                38,
                                _uintType,
                                requestedWidth,
                                remaining);
                            result = instruction.Opcode == "SBfeI32"
                                ? Bitcast(
                                    _uintType,
                                    _module.AddInstruction(
                                        SpirvOp.BitFieldSExtract,
                                        _intType,
                                        Bitcast(_intType, left),
                                        offset,
                                        width))
                                : _module.AddInstruction(
                                    SpirvOp.BitFieldUExtract,
                                    _uintType,
                                    left,
                                    offset,
                                    width);
                            Store(_scc, IsNotZero(result));
                            break;
                        }
                        case "SAbsdiffI32":
                        {
                            var wideLeft = _module.AddInstruction(
                                SpirvOp.SConvert,
                                _longType,
                                Bitcast(_intType, left));
                            var wideRight = _module.AddInstruction(
                                SpirvOp.SConvert,
                                _longType,
                                Bitcast(_intType, right));
                            var difference = _module.AddInstruction(
                                SpirvOp.ISub,
                                _longType,
                                wideLeft,
                                wideRight);
                            result = _module.AddInstruction(
                                SpirvOp.UConvert,
                                _uintType,
                                Ext(5, _longType, difference));
                            Store(_scc, IsNotZero(result));
                            break;
                        }
                        case "SPackLlB32B16":
                            result = BitwiseOr(
                                BitwiseAnd(left, UInt(0xFFFF)),
                                ShiftLeftLogical(right, UInt(16)));
                            break;
                        case "SPackLhB32B16":
                            result = BitwiseOr(
                                BitwiseAnd(left, UInt(0xFFFF)),
                                BitwiseAnd(right, UInt(0xFFFF0000)));
                            break;
                        case "SPackHhB32B16":
                            result = BitwiseOr(
                                ShiftRightLogical(left, UInt(16)),
                                BitwiseAnd(right, UInt(0xFFFF0000)));
                            break;
                        case "SCselectB32":
                            result = _module.AddInstruction(
                                SpirvOp.Select,
                                _uintType,
                                Load(_boolType, _scc),
                                left,
                                right);
                            break;
                        case "SMinU32":
                            result = Ext(38, _uintType, left, right);
                            Store(
                                _scc,
                                _module.AddInstruction(
                                    SpirvOp.ULessThan,
                                    _boolType,
                                    left,
                                    right));
                            break;
                        case "SMinI32":
                            result = Bitcast(
                                _uintType,
                                Ext(39, _intType, Bitcast(_intType, left), Bitcast(_intType, right)));
                            Store(
                                _scc,
                                _module.AddInstruction(
                                    SpirvOp.SLessThan,
                                    _boolType,
                                    Bitcast(_intType, left),
                                    Bitcast(_intType, right)));
                            break;
                        case "SMaxU32":
                            result = Ext(41, _uintType, left, right);
                            Store(
                                _scc,
                                _module.AddInstruction(
                                    SpirvOp.UGreaterThan,
                                    _boolType,
                                    left,
                                    right));
                            break;
                        case "SMaxI32":
                            result = Bitcast(
                                _uintType,
                                Ext(42, _intType, Bitcast(_intType, left), Bitcast(_intType, right)));
                            Store(
                                _scc,
                                _module.AddInstruction(
                                    SpirvOp.SGreaterThan,
                                    _boolType,
                                    Bitcast(_intType, left),
                                    Bitcast(_intType, right)));
                            break;
                        case "SLshl1AddU32":
                        case "SLshl2AddU32":
                        case "SLshl3AddU32":
                        case "SLshl4AddU32":
                        {
                            var shift = (uint)(instruction.Opcode[5] - '0');
                            result = IAdd(
                                ShiftLeftLogical(left, UInt(shift)),
                                right);
                            break;
                        }
                        default:
                            error = $"unsupported scalar opcode {instruction.Opcode}";
                            return false;
                    }

                    break;
                }
            }

            StoreS(destination, result);
            return true;
        }

        private bool TryEmitScalarCompare(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count < 2)
            {
                error = "missing scalar compare source";
                return false;
            }

            var left = GetRawSource(instruction, 0);
            var right = GetRawSource(instruction, 1);
            if (instruction.Opcode is "SBitcmp0B32" or "SBitcmp1B32")
            {
                var shifted = ShiftRightLogical(
                    left,
                    BitwiseAnd(right, UInt(31)));
                var isSet = IsNotZero(BitwiseAnd(shifted, UInt(1)));
                Store(
                    _scc,
                    instruction.Opcode == "SBitcmp1B32"
                        ? isSet
                        : _module.AddInstruction(
                            SpirvOp.LogicalNot,
                            _boolType,
                            isSet));
                return true;
            }

            if (instruction.Opcode is "SCmpEqU64" or "SCmpLgU64")
            {
                Store(
                    _scc,
                    _module.AddInstruction(
                        instruction.Opcode == "SCmpEqU64"
                            ? SpirvOp.IEqual
                            : SpirvOp.INotEqual,
                        _boolType,
                        GetRawSource64(instruction, 0),
                        GetRawSource64(instruction, 1)));
                return true;
            }

            var operation = instruction.Opcode switch
            {
                "SCmpEqI32" or "SCmpEqU32" => SpirvOp.IEqual,
                "SCmpLgI32" or "SCmpLgU32" => SpirvOp.INotEqual,
                "SCmpGtI32" => SpirvOp.SGreaterThan,
                "SCmpGeI32" => SpirvOp.SGreaterThanEqual,
                "SCmpLtI32" => SpirvOp.SLessThan,
                "SCmpLeI32" => SpirvOp.SLessThanEqual,
                "SCmpGtU32" => SpirvOp.UGreaterThan,
                "SCmpGeU32" => SpirvOp.UGreaterThanEqual,
                "SCmpLtU32" => SpirvOp.ULessThan,
                "SCmpLeU32" => SpirvOp.ULessThanEqual,
                _ => SpirvOp.Nop,
            };
            if (operation == SpirvOp.Nop)
            {
                error = $"unsupported scalar compare {instruction.Opcode}";
                return false;
            }

            if (instruction.Opcode.EndsWith("I32", StringComparison.Ordinal))
            {
                left = Bitcast(_intType, left);
                right = Bitcast(_intType, right);
            }

            Store(_scc, _module.AddInstruction(operation, _boolType, left, right));
            return true;
        }

        private bool TryEmitScalarCompareK(
            Gen5ShaderInstruction instruction,
            uint destination,
            uint immediate,
            out string error)
        {
            error = string.Empty;
            var left = LoadS(destination);
            var right = UInt(immediate);
            var operation = instruction.Opcode switch
            {
                "SCmpkEqI32" or "SCmpkEqU32" => SpirvOp.IEqual,
                "SCmpkLgI32" or "SCmpkLgU32" => SpirvOp.INotEqual,
                "SCmpkGtI32" => SpirvOp.SGreaterThan,
                "SCmpkGeI32" => SpirvOp.SGreaterThanEqual,
                "SCmpkLtI32" => SpirvOp.SLessThan,
                "SCmpkLeI32" => SpirvOp.SLessThanEqual,
                "SCmpkGtU32" => SpirvOp.UGreaterThan,
                "SCmpkGeU32" => SpirvOp.UGreaterThanEqual,
                "SCmpkLtU32" => SpirvOp.ULessThan,
                "SCmpkLeU32" => SpirvOp.ULessThanEqual,
                _ => SpirvOp.Nop,
            };
            if (operation == SpirvOp.Nop)
            {
                error = $"unsupported scalar immediate compare {instruction.Opcode}";
                return false;
            }

            if (instruction.Opcode.EndsWith("I32", StringComparison.Ordinal))
            {
                left = Bitcast(_intType, left);
                right = Bitcast(_intType, right);
            }

            Store(_scc, _module.AddInstruction(operation, _boolType, left, right));
            return true;
        }

        private bool TryEmitSaveexec32(
            string opcode,
            uint destination,
            uint source,
            out string error)
        {
            // GFX10 wave32 saveexec: D = EXEC_LO; EXEC_LO = op(S0, EXEC_LO);
            // SCC = (new EXEC_LO != 0). The 32-lane wave model keeps each
            // lane's own bit in the low word, mirroring the B64 handling.
            error = string.Empty;
            var oldExec = _module.AddInstruction(
                SpirvOp.UConvert,
                _uintType,
                BooleanToLaneMask(Load(_boolType, _exec)));
            var notSource = _module.AddInstruction(SpirvOp.Not, _uintType, source);
            var notOldExec = _module.AddInstruction(SpirvOp.Not, _uintType, oldExec);
            var newExec = opcode switch
            {
                "SAndSaveexecB32" => BitwiseAnd(source, oldExec),
                "SOrSaveexecB32" => BitwiseOr(source, oldExec),
                "SXorSaveexecB32" => BitwiseXor(source, oldExec),
                "SAndn2SaveexecB32" => BitwiseAnd(source, notOldExec),
                "SOrn2SaveexecB32" => BitwiseOr(source, notOldExec),
                "SNandSaveexecB32" => _module.AddInstruction(
                    SpirvOp.Not,
                    _uintType,
                    BitwiseAnd(source, oldExec)),
                "SNorSaveexecB32" => _module.AddInstruction(
                    SpirvOp.Not,
                    _uintType,
                    BitwiseOr(source, oldExec)),
                "SXnorSaveexecB32" => _module.AddInstruction(
                    SpirvOp.Not,
                    _uintType,
                    BitwiseXor(source, oldExec)),
                "SAndn1SaveexecB32" => BitwiseAnd(notSource, oldExec),
                "SOrn1SaveexecB32" => BitwiseOr(notSource, oldExec),
                _ => 0u,
            };
            if (newExec == 0)
            {
                error = $"unsupported scalar opcode {opcode}";
                return false;
            }

            StoreS(destination, oldExec);
            StoreS(126, newExec);
            Store(_scc, IsNotZero(newExec));
            return true;
        }

        private bool TryEmitScalar64(
            Gen5ShaderInstruction instruction,
            uint destination,
            out string error)
        {
            error = string.Empty;
            var left = GetRawSource64(instruction, 0);
            if (instruction.Opcode == "SFF1I32B64")
            {
                // D = index of the lowest set bit of the 64-bit source, or -1
                // when the source is zero. The destination is a single 32-bit
                // SGPR; SCC is not written (matching the ISA, unlike the
                // legacy SFF1I32B32 handling above).
                var low = _module.AddInstruction(SpirvOp.UConvert, _uintType, left);
                var high = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _uintType,
                    ShiftRightLogical64(left, _module.Constant64(_ulongType, 32)));
                var lowLsb = Ext(73, _uintType, low);
                var highLsb = IAdd(Ext(73, _uintType, high), UInt(32));
                var highOrNone = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    IsNotZero(high),
                    highLsb,
                    UInt(0xFFFF_FFFF));
                var result = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    IsNotZero(low),
                    lowLsb,
                    highOrNone);
                StoreS(destination, result);
                return true;
            }

            if (instruction.Opcode.EndsWith("SaveexecB64", StringComparison.Ordinal))
            {
                var oldExec = BooleanToLaneMask(Load(_boolType, _exec));
                var notLeft = _module.AddInstruction(SpirvOp.Not, _ulongType, left);
                var newExec = instruction.Opcode switch
                {
                    "SAndSaveexecB64" => _module.AddInstruction(
                        SpirvOp.BitwiseAnd, _ulongType, oldExec, left),
                    "SOrSaveexecB64" => _module.AddInstruction(
                        SpirvOp.BitwiseOr, _ulongType, oldExec, left),
                    "SXorSaveexecB64" => _module.AddInstruction(
                        SpirvOp.BitwiseXor, _ulongType, oldExec, left),
                    "SAndn2SaveexecB64" => _module.AddInstruction(
                        SpirvOp.BitwiseAnd,
                        _ulongType,
                        left,
                        _module.AddInstruction(
                            SpirvOp.Not,
                            _ulongType,
                            oldExec)),
                    "SAndn1SaveexecB64" => _module.AddInstruction(
                        SpirvOp.BitwiseAnd,
                        _ulongType,
                        notLeft,
                        oldExec),
                    "SOrn1SaveexecB64" => _module.AddInstruction(
                        SpirvOp.BitwiseOr,
                        _ulongType,
                        notLeft,
                        oldExec),
                    "SOrn2SaveexecB64" => _module.AddInstruction(
                        SpirvOp.BitwiseOr,
                        _ulongType,
                        left,
                        _module.AddInstruction(
                            SpirvOp.Not,
                            _ulongType,
                            oldExec)),
                    "SNandSaveexecB64" => _module.AddInstruction(
                        SpirvOp.Not,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseAnd,
                            _ulongType,
                            left,
                            oldExec)),
                    "SNorSaveexecB64" => _module.AddInstruction(
                        SpirvOp.Not,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseOr,
                            _ulongType,
                            left,
                            oldExec)),
                    "SXnorSaveexecB64" => _module.AddInstruction(
                        SpirvOp.Not,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseXor,
                            _ulongType,
                            left,
                            oldExec)),
                    _ => 0u,
                };
                if (newExec == 0)
                {
                    error =
                        $"unsupported scalar 64-bit opcode {instruction.Opcode}";
                    return false;
                }

                StoreS64(destination, oldExec);
                StoreS64(126, newExec);
                Store(_scc, IsNotZero64(newExec));
                return true;
            }

            if (instruction.Opcode is "SLshlB64" or "SLshrB64")
            {
                if (instruction.Sources.Count < 2)
                {
                    error = "missing scalar 64-bit shift source";
                    return false;
                }

                var shift = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    GetRawSource(instruction, 1));
                var shiftedValue = instruction.Opcode == "SLshlB64"
                    ? ShiftLeftLogical64(left, shift)
                    : ShiftRightLogical64(left, shift);
                StoreS64(destination, shiftedValue);
                Store(_scc, IsNotZero64(shiftedValue));
                return true;
            }

            if (instruction.Opcode is "SBfeU64" or "SBfeI64")
            {
                if (instruction.Sources.Count < 2)
                {
                    error = "missing scalar 64-bit bitfield source";
                    return false;
                }

                var control = GetRawSource(instruction, 1);
                var offset = BitwiseAnd(control, UInt(63));
                var requestedWidth = BitwiseAnd(
                    ShiftRightLogical(control, UInt(16)),
                    UInt(0x7F));
                var remaining = _module.AddInstruction(
                    SpirvOp.ISub,
                    _uintType,
                    UInt(64),
                    offset);
                var width = Ext(
                    38,
                    _uintType,
                    requestedWidth,
                    remaining);
                var offset64 = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    offset);
                var width64 = _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    width);
                var one64 = _module.Constant64(_ulongType, 1);
                var shifted = ShiftRightLogical64(left, offset64);
                var partialMask = _module.AddInstruction(
                    SpirvOp.ISub,
                    _ulongType,
                    ShiftLeftLogical64(one64, width64),
                    one64);
                var fullWidth = _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    width,
                    UInt(64));
                var mask = _module.AddInstruction(
                    SpirvOp.Select,
                    _ulongType,
                    fullWidth,
                    _module.Constant64(_ulongType, ulong.MaxValue),
                    partialMask);
                var extracted = _module.AddInstruction(
                    SpirvOp.BitwiseAnd,
                    _ulongType,
                    shifted,
                    mask);
                if (instruction.Opcode == "SBfeI64")
                {
                    var signShift = _module.AddInstruction(
                        SpirvOp.ISub,
                        _uintType,
                        width,
                        UInt(1));
                    var signBit = ShiftLeftLogical64(
                        one64,
                        _module.AddInstruction(
                            SpirvOp.UConvert,
                            _ulongType,
                            signShift));
                    var signExtended = _module.AddInstruction(
                        SpirvOp.ISub,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseXor,
                            _ulongType,
                            extracted,
                            signBit),
                        signBit);
                    extracted = _module.AddInstruction(
                        SpirvOp.Select,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.IEqual,
                            _boolType,
                            width,
                            UInt(0)),
                        _module.Constant64(_ulongType, 0),
                        signExtended);
                }

                StoreS64(destination, extracted);
                Store(_scc, IsNotZero64(extracted));
                return true;
            }

            if (instruction.Opcode == "SWqmB64")
            {
                if (_subgroupInvocationIdInput == 0)
                {
                    // No subgroup lane id (scalar path): a lone lane is its own
                    // quad, so s_wqm degenerates to a move.
                    StoreS64(destination, left);
                    Store(_scc, IsNotZero64(left));
                    return true;
                }

                // Expand exec (or any saved mask) to whole quads so covered
                // quads keep their helper lanes active for the body.
                var laneSet = IsWaveMaskActive(left);
                var wqm = WholeQuadModeExpand(laneSet, out var expandedFullMask);
                StoreS64(destination, wqm);
                Store(_scc, IsNotZero64(expandedFullMask));
                return true;
            }

            uint value;
            if (instruction.Opcode == "SMovB64")
            {
                value = left;
            }
            else if (instruction.Opcode == "SNotB64")
            {
                value = _module.AddInstruction(SpirvOp.Not, _ulongType, left);
            }
            else
            {
                if (instruction.Sources.Count < 2)
                {
                    error = "missing scalar 64-bit source";
                    return false;
                }

                var right = GetRawSource64(instruction, 1);
                value = instruction.Opcode switch
                {
                    "SAndB64" => _module.AddInstruction(
                        SpirvOp.BitwiseAnd, _ulongType, left, right),
                    "SOrB64" => _module.AddInstruction(
                        SpirvOp.BitwiseOr, _ulongType, left, right),
                    "SXorB64" => _module.AddInstruction(
                        SpirvOp.BitwiseXor, _ulongType, left, right),
                    "SNandB64" => _module.AddInstruction(
                        SpirvOp.Not,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseAnd, _ulongType, left, right)),
                    "SNorB64" => _module.AddInstruction(
                        SpirvOp.Not,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseOr, _ulongType, left, right)),
                    "SXnorB64" => _module.AddInstruction(
                        SpirvOp.Not,
                        _ulongType,
                        _module.AddInstruction(
                            SpirvOp.BitwiseXor, _ulongType, left, right)),
                    "SAndn1B64" => _module.AddInstruction(
                        SpirvOp.BitwiseAnd,
                        _ulongType,
                        _module.AddInstruction(SpirvOp.Not, _ulongType, left),
                        right),
                    "SAndn2B64" => _module.AddInstruction(
                        SpirvOp.BitwiseAnd,
                        _ulongType,
                        left,
                        _module.AddInstruction(SpirvOp.Not, _ulongType, right)),
                    "SOrn1B64" => _module.AddInstruction(
                        SpirvOp.BitwiseOr,
                        _ulongType,
                        _module.AddInstruction(SpirvOp.Not, _ulongType, left),
                        right),
                    "SOrn2B64" => _module.AddInstruction(
                        SpirvOp.BitwiseOr,
                        _ulongType,
                        left,
                        _module.AddInstruction(SpirvOp.Not, _ulongType, right)),
                    "SCselectB64" => _module.AddInstruction(
                        SpirvOp.Select,
                        _ulongType,
                        Load(_boolType, _scc),
                        left,
                        right),
                    _ => 0,
                };
                if (value == 0)
                {
                    error = $"unsupported scalar 64-bit opcode {instruction.Opcode}";
                    return false;
                }
            }

            StoreS64(destination, value);
            if (instruction.Opcode is
                "SNotB64" or
                "SAndB64" or
                "SOrB64" or
                "SXorB64" or
                "SAndn1B64" or
                "SAndn2B64" or
                "SOrn1B64" or
                "SOrn2B64" or
                "SNandB64" or
                "SNorB64" or
                "SXnorB64")
            {
                Store(_scc, IsNotZero64(value));
            }

            return true;
        }

        private uint GetRawSource(
            Gen5ShaderInstruction instruction,
            int sourceIndex)
        {
            if ((uint)sourceIndex >= instruction.Sources.Count)
            {
                throw new InvalidOperationException($"missing source {sourceIndex}");
            }

            var operand = instruction.Sources[sourceIndex];
            uint value = operand.Kind switch
            {
                Gen5OperandKind.VectorRegister => LoadV(operand.Value),
                Gen5OperandKind.ScalarRegister => LoadS(operand.Value),
                Gen5OperandKind.LiteralConstant => UInt(operand.Value),
                // Encoded source 253 reads SCC as a 0/1 value.
                Gen5OperandKind.EncodedConstant when operand.Value == 253 =>
                    _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        Load(_boolType, _scc),
                        UInt(1),
                        UInt(0)),
                Gen5OperandKind.EncodedConstant when TryDecodeInlineConstant(
                    operand.Value,
                    out var inline) => UInt(inline),
                _ => throw new InvalidOperationException($"unsupported source {operand}"),
            };

            if (instruction.Control is Gen5SdwaControl sdwa)
            {
                var selector = sourceIndex switch
                {
                    0 => sdwa.Source0Select,
                    1 => sdwa.Source1Select,
                    _ => 6u,
                };
                value = selector switch
                {
                    0 => BitwiseAnd(value, UInt(0xFF)),
                    1 => BitwiseAnd(ShiftRightLogical(value, UInt(8)), UInt(0xFF)),
                    2 => BitwiseAnd(ShiftRightLogical(value, UInt(16)), UInt(0xFF)),
                    3 => BitwiseAnd(ShiftRightLogical(value, UInt(24)), UInt(0xFF)),
                    4 => BitwiseAnd(value, UInt(0xFFFF)),
                    5 => BitwiseAnd(ShiftRightLogical(value, UInt(16)), UInt(0xFFFF)),
                    _ => value,
                };
            }

            return value;
        }

        private uint GetFloatSource(
            Gen5ShaderInstruction instruction,
            int sourceIndex)
        {
            var operand = instruction.Sources[sourceIndex];
            uint value;
            if (operand.Kind == Gen5OperandKind.EncodedConstant &&
                operand.Value is >= 128 and <= 192)
            {
                value = Float(operand.Value - 128);
            }
            else if (operand.Kind == Gen5OperandKind.EncodedConstant &&
                     operand.Value is >= 193 and <= 208)
            {
                value = Float(-(operand.Value - 192));
            }
            else
            {
                value = Bitcast(_floatType, GetRawSource(instruction, sourceIndex));
            }

            uint absoluteMask = 0;
            uint negateMask = 0;
            switch (instruction.Control)
            {
                case Gen5Vop3Control control:
                    absoluteMask = control.AbsoluteMask;
                    negateMask = control.NegateMask;
                    break;
                case Gen5SdwaControl control:
                    absoluteMask = control.AbsoluteMask;
                    negateMask = control.NegateMask;
                    break;
                case Gen5DppControl control:
                    absoluteMask = control.AbsoluteMask;
                    negateMask = control.NegateMask;
                    break;
            }

            if ((absoluteMask & (1u << sourceIndex)) != 0)
            {
                value = Ext(4, _floatType, value);
            }

            if ((negateMask & (1u << sourceIndex)) != 0)
            {
                value = _module.AddInstruction(SpirvOp.FNegate, _floatType, value);
            }

            return value;
        }

        private uint GetRawSource64(
            Gen5ShaderInstruction instruction,
            int sourceIndex)
        {
            var operand = instruction.Sources[sourceIndex];
            if (operand.Kind == Gen5OperandKind.ScalarRegister)
            {
                return LoadS64(operand.Value);
            }

            var low = GetRawSource(instruction, sourceIndex);
            return _module.AddInstruction(SpirvOp.UConvert, _ulongType, low);
        }

        private uint LoadS64(uint register)
        {
            var low = _module.AddInstruction(SpirvOp.UConvert, _ulongType, LoadS(register));
            var high = _module.AddInstruction(
                SpirvOp.UConvert,
                _ulongType,
                LoadS(register + 1));
            high = ShiftLeftLogical64(high, _module.Constant64(_ulongType, 32));
            return _module.AddInstruction(SpirvOp.BitwiseOr, _ulongType, low, high);
        }

        private void StoreS64(uint register, uint value)
        {
            StoreS(
                register,
                _module.AddInstruction(SpirvOp.UConvert, _uintType, value));
            var high = ShiftRightLogical64(
                value,
                _module.Constant64(_ulongType, 32));
            StoreS(
                register + 1,
                _module.AddInstruction(SpirvOp.UConvert, _uintType, high));
        }

        private uint EmitFloatBinary(
            Gen5ShaderInstruction instruction,
            SpirvOp operation,
            bool reverse = false)
        {
            var left = GetFloatSource(instruction, reverse ? 1 : 0);
            var right = GetFloatSource(instruction, reverse ? 0 : 1);
            return EmitFloatResult(
                instruction,
                _module.AddInstruction(operation, _floatType, left, right));
        }

        private uint EmitFloatExtBinary(
            Gen5ShaderInstruction instruction,
            uint operation) =>
            EmitFloatResult(
                instruction,
                Ext(
                    operation,
                    _floatType,
                    GetFloatSource(instruction, 0),
                    GetFloatSource(instruction, 1)));

        private uint EmitFloatTernaryExt(
            Gen5ShaderInstruction instruction,
            uint operation)
        {
            var first = Ext(
                operation,
                _floatType,
                GetFloatSource(instruction, 0),
                GetFloatSource(instruction, 1));
            return EmitFloatResult(
                instruction,
                Ext(operation, _floatType, first, GetFloatSource(instruction, 2)));
        }

        private uint EmitIntegerBinary(
            Gen5ShaderInstruction instruction,
            SpirvOp operation,
            bool reverse = false)
        {
            var left = GetRawSource(instruction, reverse ? 1 : 0);
            var right = GetRawSource(instruction, reverse ? 0 : 1);
            if (operation == SpirvOp.ShiftLeftLogical)
            {
                return ShiftLeftLogical(left, right);
            }

            if (operation == SpirvOp.ShiftRightLogical)
            {
                return ShiftRightLogical(left, right);
            }

            if (operation == SpirvOp.ShiftRightArithmetic)
            {
                return ShiftRightArithmetic(left, right);
            }

            return _module.AddInstruction(operation, _uintType, left, right);
        }

        private enum CubeCoordinate
        {
            Id,
            Sc,
            Tc,
            Ma,
        }

        private uint EmitCvtOffF32I4(Gen5ShaderInstruction instruction)
        {
            var index = BitwiseAnd(GetRawSource(instruction, 0), UInt(15));
            ReadOnlySpan<float> table =
            [
                0.0f,
                0.0625f,
                0.1250f,
                0.1875f,
                0.2500f,
                0.3125f,
                0.3750f,
                0.4375f,
                -0.5000f,
                -0.4375f,
                -0.3750f,
                -0.3125f,
                -0.2500f,
                -0.1875f,
                -0.1250f,
                -0.0625f,
            ];

            var result = UInt(BitConverter.SingleToUInt32Bits(table[^1]));
            for (var tableIndex = table.Length - 2; tableIndex >= 0; tableIndex--)
            {
                var matches = _module.AddInstruction(
                    SpirvOp.IEqual,
                    _boolType,
                    index,
                    UInt((uint)tableIndex));
                result = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    matches,
                    UInt(BitConverter.SingleToUInt32Bits(table[tableIndex])),
                    result);
            }

            return result;
        }

        private uint EmitCubeCoordinate(
            Gen5ShaderInstruction instruction,
            CubeCoordinate coordinate)
        {
            var x = GetFloatSource(instruction, 0);
            var y = GetFloatSource(instruction, 1);
            var z = GetFloatSource(instruction, 2);
            var nx = _module.AddInstruction(SpirvOp.FNegate, _floatType, x);
            var ny = _module.AddInstruction(SpirvOp.FNegate, _floatType, y);
            var nz = _module.AddInstruction(SpirvOp.FNegate, _floatType, z);
            var ax = Ext(4, _floatType, x);
            var ay = Ext(4, _floatType, y);
            var az = Ext(4, _floatType, z);
            var amaxXY = Ext(40, _floatType, ax, ay);
            var amax = Ext(40, _floatType, az, amaxXY);
            var ma = _module.AddInstruction(
                SpirvOp.FMul,
                _floatType,
                Float(2),
                amax);
            if (coordinate == CubeCoordinate.Ma)
            {
                return EmitFloatResult(instruction, ma);
            }

            var isZMax = _module.AddInstruction(
                SpirvOp.FOrdGreaterThanEqual,
                _boolType,
                az,
                amaxXY);
            var yGreaterOrEqualX = _module.AddInstruction(
                SpirvOp.FOrdGreaterThanEqual,
                _boolType,
                ay,
                ax);
            var isYMax = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                _module.AddInstruction(SpirvOp.LogicalNot, _boolType, isZMax),
                yGreaterOrEqualX);
            if (coordinate == CubeCoordinate.Id)
            {
                var isZNeg = _module.AddInstruction(
                    SpirvOp.FOrdLessThan,
                    _boolType,
                    z,
                    Float(0));
                var isYNeg = _module.AddInstruction(
                    SpirvOp.FOrdLessThan,
                    _boolType,
                    y,
                    Float(0));
                var isXNeg = _module.AddInstruction(
                    SpirvOp.FOrdLessThan,
                    _boolType,
                    x,
                    Float(0));
                var zCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isZNeg,
                    Float(5),
                    Float(4));
                var yCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isYNeg,
                    Float(3),
                    Float(2));
                var xCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isXNeg,
                    Float(1),
                    Float(0));
                var xyCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    yGreaterOrEqualX,
                    yCase,
                    xCase);
                return EmitFloatResult(
                    instruction,
                    _module.AddInstruction(
                        SpirvOp.Select,
                        _floatType,
                        isZMax,
                        zCase,
                        xyCase));
            }

            if (coordinate == CubeCoordinate.Sc)
            {
                var isZNeg = _module.AddInstruction(
                    SpirvOp.FOrdLessThan,
                    _boolType,
                    z,
                    Float(0));
                var isXNeg = _module.AddInstruction(
                    SpirvOp.FOrdLessThan,
                    _boolType,
                    x,
                    Float(0));
                var zCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isZNeg,
                    nx,
                    x);
                var xCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isXNeg,
                    z,
                    nz);
                var nonZCase = _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isYMax,
                    x,
                    xCase);
                return EmitFloatResult(
                    instruction,
                    _module.AddInstruction(
                        SpirvOp.Select,
                        _floatType,
                        isZMax,
                        zCase,
                        nonZCase));
            }

            var tcIsYNeg = _module.AddInstruction(
                SpirvOp.FOrdLessThan,
                _boolType,
                y,
                Float(0));
            var tcYCase = _module.AddInstruction(
                SpirvOp.Select,
                _floatType,
                tcIsYNeg,
                nz,
                z);
            return EmitFloatResult(
                instruction,
                _module.AddInstruction(
                    SpirvOp.Select,
                    _floatType,
                    isYMax,
                    tcYCase,
                    ny));
        }

        private uint EmitAddWithCarry(Gen5ShaderInstruction instruction)
        {
            var left = GetRawSource(instruction, 0);
            var right = GetRawSource(instruction, 1);
            var carryIn = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                GetCarryInCondition(instruction),
                UInt(1),
                UInt(0));
            var partial = IAdd(left, right);
            var result = IAdd(partial, carryIn);
            var carry = _module.AddInstruction(
                SpirvOp.LogicalOr,
                _boolType,
                _module.AddInstruction(SpirvOp.ULessThan, _boolType, partial, left),
                _module.AddInstruction(SpirvOp.ULessThan, _boolType, result, partial));
            StoreCarryOut(instruction, carry);
            return result;
        }

        // The VOP2 carry ops read/write VCC implicitly; the VOP3B forms name
        // an explicit carry-in lane mask in src2 (the carry-out goes through
        // StoreCarryOut's ScalarDestination handling).
        private uint GetCarryInCondition(Gen5ShaderInstruction instruction) =>
            instruction.Sources.Count > 2
                ? IsCurrentLaneSet(GetRawSource64(instruction, 2))
                : Load(_boolType, _vcc);

        private uint EmitSubtractWithBorrow(
            Gen5ShaderInstruction instruction,
            bool reverse)
        {
            var left = GetRawSource(instruction, reverse ? 1 : 0);
            var right = GetRawSource(instruction, reverse ? 0 : 1);
            var borrowIn = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                GetCarryInCondition(instruction),
                UInt(1),
                UInt(0));
            var partial = _module.AddInstruction(SpirvOp.ISub, _uintType, left, right);
            var result = _module.AddInstruction(
                SpirvOp.ISub,
                _uintType,
                partial,
                borrowIn);
            var borrow = _module.AddInstruction(
                SpirvOp.LogicalOr,
                _boolType,
                _module.AddInstruction(SpirvOp.ULessThan, _boolType, left, right),
                _module.AddInstruction(
                    SpirvOp.ULessThan,
                    _boolType,
                    partial,
                    borrowIn));
            StoreCarryOut(instruction, borrow);
            return result;
        }

        private void StoreCarryOut(
            Gen5ShaderInstruction instruction,
            uint carry)
        {
            // The carry-out is a per-lane ballot like VCC, not a scalar 0/1:
            // an explicit sdst pair receives the same EXEC-masked lane mask.
            var activeCarry = MaskWithExec(carry);
            if (instruction.Control is Gen5Vop3Control { ScalarDestination: { } register })
            {
                StoreWaveMask(register, activeCarry);
                return;
            }

            StoreWaveMask(106, activeCarry);
        }

        private uint EmitPermlane16(
            Gen5ShaderInstruction instruction,
            bool exchangeRows)
        {
            var value = GetRawSource(instruction, 0);
            var selectorLow = GetRawSource(instruction, 1);
            var selectorHigh = GetRawSource(instruction, 2);
            var lane = Load(_uintType, _subgroupInvocationIdInput);
            var localLane = BitwiseAnd(lane, UInt(15));
            var lowHalf = _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                localLane,
                UInt(8));
            var lowShift = ShiftLeftLogical(localLane, UInt(2));
            var highLane = _module.AddInstruction(
                SpirvOp.ISub,
                _uintType,
                localLane,
                UInt(8));
            var highShift = ShiftLeftLogical(highLane, UInt(2));
            var lowSelector = BitwiseAnd(
                ShiftRightLogical(selectorLow, lowShift),
                UInt(15));
            var highSelector = BitwiseAnd(
                ShiftRightLogical(selectorHigh, highShift),
                UInt(15));
            var selector = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                lowHalf,
                lowSelector,
                highSelector);
            var rowBase = BitwiseAnd(lane, UInt(0xFFFF_FFF0));
            if (exchangeRows)
            {
                rowBase = BitwiseXor(rowBase, UInt(16));
            }

            var targetLane = IAdd(rowBase, selector);
            return _module.AddInstruction(
                SpirvOp.GroupNonUniformShuffle,
                _uintType,
                UInt(3),
                value,
                targetLane);
        }

        private uint EmitFloatResult(
            Gen5ShaderInstruction instruction,
            uint value)
        {
            uint outputModifier = 0;
            var clamp = false;
            switch (instruction.Control)
            {
                case Gen5Vop3Control control:
                    outputModifier = control.OutputModifier;
                    clamp = control.Clamp;
                    break;
                case Gen5SdwaControl control:
                    outputModifier = control.OutputModifier;
                    clamp = control.Clamp;
                    break;
            }

            value = outputModifier switch
            {
                1 => _module.AddInstruction(SpirvOp.FMul, _floatType, value, Float(2)),
                2 => _module.AddInstruction(SpirvOp.FMul, _floatType, value, Float(4)),
                3 => _module.AddInstruction(SpirvOp.FMul, _floatType, value, Float(0.5f)),
                _ => value,
            };
            if (clamp)
            {
                value = Ext(43, _floatType, value, Float(0), Float(1));
            }

            return Bitcast(_uintType, value);
        }

        private uint TruncateFloat32ForPack(uint value)
        {
            var raw = BitwiseAnd(
                Bitcast(_uintType, value),
                UInt(0xFFFF_E000));
            return Bitcast(_floatType, raw);
        }

        private uint Ext(uint operation, uint resultType, params uint[] operands)
        {
            var values = new uint[2 + operands.Length];
            values[0] = _glsl;
            values[1] = operation;
            operands.CopyTo(values, 2);
            return _module.AddInstruction(SpirvOp.ExtInst, resultType, values);
        }

        private uint IsNotZero(uint value) =>
            _module.AddInstruction(SpirvOp.INotEqual, _boolType, value, UInt(0));

        private uint IsNotZero64(uint value) =>
            _module.AddInstruction(
                SpirvOp.INotEqual,
                _boolType,
                value,
                _module.Constant64(_ulongType, 0));

        private uint SignBit(uint value) =>
            ShiftRightLogical(value, UInt(31));

        private uint SignedAddOverflow(uint left, uint right, uint result)
        {
            var leftSign = SignBit(left);
            var rightSign = SignBit(right);
            var resultSign = SignBit(result);
            var sameSourceSign = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                leftSign,
                rightSign);
            var resultSignChanged = _module.AddInstruction(
                SpirvOp.INotEqual,
                _boolType,
                leftSign,
                resultSign);
            return _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                sameSourceSign,
                resultSignChanged);
        }

        private uint SignedSubOverflow(uint left, uint right, uint result)
        {
            var leftSign = SignBit(left);
            var rightSign = SignBit(right);
            var resultSign = SignBit(result);
            var differentSourceSign = _module.AddInstruction(
                SpirvOp.INotEqual,
                _boolType,
                leftSign,
                rightSign);
            var resultSignChanged = _module.AddInstruction(
                SpirvOp.INotEqual,
                _boolType,
                leftSign,
                resultSign);
            return _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                differentSourceSign,
                resultSignChanged);
        }

        private static bool TryDecodeInlineConstant(uint encoded, out uint value)
        {
            if (encoded == 125)
            {
                value = 0;
                return true;
            }

            if (encoded is >= 128 and <= 192)
            {
                value = encoded - 128;
                return true;
            }

            if (encoded is >= 193 and <= 208)
            {
                value = unchecked((uint)-(int)(encoded - 192));
                return true;
            }

            var floatingPoint = encoded switch
            {
                240 => 0.5f,
                241 => -0.5f,
                242 => 1.0f,
                243 => -1.0f,
                244 => 2.0f,
                245 => -2.0f,
                246 => 4.0f,
                247 => -4.0f,
                248 => 1.0f / (2.0f * MathF.PI),
                _ => float.NaN,
            };
            if (float.IsNaN(floatingPoint))
            {
                value = 0;
                return false;
            }

            value = BitConverter.SingleToUInt32Bits(floatingPoint);
            return true;
        }
    }
}
