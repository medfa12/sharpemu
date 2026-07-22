// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Agc;

internal static partial class Gen5SpirvTranslator
{
    private const uint ScalarRegisterCount = 256;
    private const uint VectorRegisterCount = 512;
    private const uint LdsDwordCount = 8192;
    private const uint RdnaWaveLaneCount = 32;

    public static bool TryCompilePixelShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        Gen5PixelOutputKind outputKind,
        out Gen5SpirvShader shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int scalarRegisterBufferIndex = -1,
        IReadOnlyList<uint>? pixelInputControls = null,
        uint pixelInputEnable = 0,
        uint pixelInputAddress = 0,
        ulong pixelShaderAddress = 0) =>
        TryCompilePixelShader(
            state,
            evaluation,
            [new Gen5PixelOutputBinding(0, 0, outputKind)],
            out shader,
            out error,
            globalBufferBase,
            totalGlobalBufferCount,
            imageBindingBase,
            scalarRegisterBufferIndex,
            pixelInputControls,
            pixelInputEnable,
            pixelInputAddress,
            pixelShaderAddress);

    public static bool TryCompilePixelShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        IReadOnlyList<Gen5PixelOutputBinding> outputs,
        out Gen5SpirvShader shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int scalarRegisterBufferIndex = -1,
        IReadOnlyList<uint>? pixelInputControls = null,
        uint pixelInputEnable = 0,
        uint pixelInputAddress = 0,
        ulong pixelShaderAddress = 0)
    {
        if (outputs.Count > 8 || outputs.Any(output => output.GuestSlot > 7))
        {
            shader = default!;
            error = "pixel outputs must contain at most eight guest slots in the 0..7 range";
            return false;
        }

        if (outputs.Select(output => output.GuestSlot).Distinct().Count() != outputs.Count ||
            outputs.Select(output => output.HostLocation).Distinct().Count() != outputs.Count)
        {
            shader = default!;
            error = "pixel output guest slots and host locations must be unique";
            return false;
        }

        if (!outputs
                .OrderBy(output => output.HostLocation)
                .Select((output, index) => output.HostLocation == (uint)index)
                .All(isDense => isDense))
        {
            shader = default!;
            error = "pixel output host locations must be dense in the 0..N-1 range";
            return false;
        }

        var context = new CompilationContext(
            Gen5SpirvStage.Pixel,
            state,
            evaluation,
            outputs,
            1,
            1,
            1,
            globalBufferBase,
            totalGlobalBufferCount,
            imageBindingBase,
            scalarRegisterBufferIndex,
            pixelInputControls: pixelInputControls,
            pixelInputEnable: pixelInputEnable,
            pixelInputAddress: pixelInputAddress,
            pixelShaderAddress: pixelShaderAddress);
        return context.TryCompile(out shader, out error);
    }

    public static bool TryCompileVertexShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        out Gen5SpirvShader shader,
        out string error,
        int globalBufferBase = 0,
        int totalGlobalBufferCount = -1,
        int imageBindingBase = 0,
        int scalarRegisterBufferIndex = -1,
        bool instanceIdFromVertexIndex = false,
        IReadOnlyCollection<uint>? requiredVertexOutputLocations = null,
        IReadOnlyList<uint>? pixelInputControls = null)
    {
        var context = new CompilationContext(
            Gen5SpirvStage.Vertex,
            state,
            evaluation,
            [],
            1,
            1,
            1,
            globalBufferBase,
            totalGlobalBufferCount,
            imageBindingBase,
            scalarRegisterBufferIndex,
            instanceIdFromVertexIndex,
            computeCapture: null,
            requiredVertexOutputLocations: requiredVertexOutputLocations,
            pixelInputControls: pixelInputControls);
        return context.TryCompile(out shader, out error);
    }

    /// <summary>
    /// The vertex-output Locations a pixel shader interpolates (one per distinct
    /// interpolation attribute). Paired with a vertex shader so its output
    /// interface can be padded to cover every fragment input.
    /// </summary>
    public static IReadOnlyCollection<uint> GetPixelInterpolantLocations(
        Gen5ShaderState pixelState) =>
        pixelState.Program.Instructions
            .Select(instruction => instruction.Control)
            .OfType<Gen5InterpolationControl>()
            .Select(control => control.Attribute)
            .Distinct()
            .ToArray();

    public static bool TryCompileComputeShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        out Gen5SpirvShader shader,
        out string error)
        => TryCompileComputeShader(
            state,
            evaluation,
            localSizeX,
            localSizeY,
            localSizeZ,
            null,
            out shader,
            out error);

    public static bool TryCompileComputeShader(
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        NggComputeCapture? capture,
        out Gen5SpirvShader shader,
        out string error)
    {
        var context = new CompilationContext(
            Gen5SpirvStage.Compute,
            state,
            evaluation,
            [],
            Math.Max(localSizeX, 1),
            Math.Max(localSizeY, 1),
            Math.Max(localSizeZ, 1),
            0,
            -1,
            0,
            -1,
            false,
            capture);
        return context.TryCompile(out shader, out error);
    }

    private sealed partial class CompilationContext
    {
        private readonly SpirvModuleBuilder _module = new();
        private readonly Gen5SpirvStage _stage;
        private readonly Gen5ShaderState _state;
        private readonly Gen5ShaderEvaluation _evaluation;
        private readonly IReadOnlyList<Gen5PixelOutputBinding> _pixelOutputBindings;
        private readonly uint _localSizeX;
        private readonly uint _localSizeY;
        private readonly uint _localSizeZ;
        private readonly int _globalBufferBase;
        private readonly int _totalGlobalBufferCount;
        private readonly int _imageBindingBase;
        private readonly int _scalarRegisterBufferIndex;
        private readonly bool _instanceIdFromVertexIndex;
        private readonly NggComputeCapture? _computeCapture;
        private readonly IReadOnlyList<uint> _pixelInputControls;
        // SPI_PS_INPUT_ENA / SPI_PS_INPUT_ADDR from the draw's context registers.
        // ADDR selects which pixel-input VGPR slots the hardware allocates (in a
        // fixed order); ENA selects which of those slots actually receive data.
        private readonly uint _pixelInputEnable;
        private readonly uint _pixelInputAddress;
        private readonly ulong _pixelShaderAddress;
        private readonly List<uint> _interfaces = [];
        private readonly Dictionary<uint, uint> _pixelInputs = [];
        private readonly Dictionary<uint, SpirvPixelOutput> _pixelOutputs = [];
        // Fragment locations are keyed by pixel-attribute index so they remain
        // unique when multiple SPI_PS_INPUT_CNTL entries alias one guest
        // parameter. Vertex exports are fanned out through the reverse map.
        private readonly Dictionary<uint, uint> _vertexOutputs = [];
        // Vertex Output variables declared only to satisfy the paired pixel
        // shader's input interface (Locations it interpolates that this vertex
        // program never exports); zero-initialised at entry so the fragment
        // varyings are defined rather than undefined.
        private readonly List<uint> _paddedVertexOutputs = [];
        private readonly IReadOnlyCollection<uint> _requiredVertexOutputLocations;
        private readonly Dictionary<uint, List<uint>>
            _vertexOutputsByGuestLocation = [];
        private readonly Dictionary<uint, SpirvVertexInput> _vertexInputsByPc = [];
        private readonly List<SpirvImageResource> _imageResources = [];
        private readonly Dictionary<uint, int> _imageBindingByPc = [];
        private readonly Dictionary<uint, int> _bufferBindingByPc = [];

        // SHARPEMU_KEEP_UNBOUND_SMEM=1: don't zero the destinations of a scalar
        // load with no buffer binding; leave whatever an earlier bound load wrote.
        private static readonly bool _keepUnboundSmem = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_KEEP_UNBOUND_SMEM"),
            "1",
            StringComparison.Ordinal);
        // Attributes touched by V_INTERP_MOV_F32: their P0/P10/P20 hardware
        // interpolation coefficients are reconstructed in the entry block and
        // the guest's manual interpolation sequence replays them.
        private readonly HashSet<uint> _interpolationMoveAttributes = [];
        private readonly Dictionary<
            (uint Attribute, uint Channel),
            SpirvInterpolationCoefficients> _interpolationCoefficients = [];
        // Translation-time diagnostics for the black-title investigation
        // (ported from the ASTRO journal probes E15/E16): force the packed
        // half export components to one to separate export routing from value
        // computation, and replace pack results with an EXEC indicator (1.0
        // active / 0.5 inactive) to expose pack-time EXEC suppression.
        private static readonly bool _tracePackedExport =
            Environment.GetEnvironmentVariable(
                "SHARPEMU_TRACE_PACKED_EXPORT") == "1";
        private static readonly bool _forcePackedExportOne =
            Environment.GetEnvironmentVariable(
                "SHARPEMU_FORCE_PACKED_EXPORT_ONE") == "1";
        private static readonly bool _forcePackedStoreExecValues =
            Environment.GetEnvironmentVariable(
                "SHARPEMU_FORCE_PACKED_STORE_EXEC_VALUES") == "1";
        // Emits the raw HDR sample from the Astro Bot tonemap shader directly
        // to MRT0. This separates image-coordinate/binding failures from the
        // shader's downstream colour-grade and transfer-function ALU.
        private static readonly bool _debugTonemapSampleOutput =
            Environment.GetEnvironmentVariable(
                "SHARPEMU_PS_DEBUG_TONEMAP_SAMPLE") == "1";
        private static readonly bool _debugTonemapGradeOutput =
            Environment.GetEnvironmentVariable(
                "SHARPEMU_PS_DEBUG_TONEMAP_GRADE") == "1";
        private static readonly bool _debugTonemapCoreOutput =
            Environment.GetEnvironmentVariable(
                "SHARPEMU_PS_DEBUG_TONEMAP_CORE") == "1";
        private static readonly bool _debugTonemapCurveOutput =
            Environment.GetEnvironmentVariable(
                "SHARPEMU_PS_DEBUG_TONEMAP_CURVE") == "1";
        private static readonly bool _debugTonemapGamutOutput =
            Environment.GetEnvironmentVariable(
                "SHARPEMU_PS_DEBUG_TONEMAP_GAMUT") == "1";
        private static readonly bool _debugTonemapGamutBaseOutput =
            Environment.GetEnvironmentVariable(
                "SHARPEMU_PS_DEBUG_TONEMAP_GAMUT_BASE") == "1";
        private static readonly bool _debugTonemapGamutDenominatorOutput =
            Environment.GetEnvironmentVariable(
                "SHARPEMU_PS_DEBUG_TONEMAP_GAMUT_DENOM") == "1";
        private static readonly bool _debugTonemapGamutScalarOutput =
            Environment.GetEnvironmentVariable(
                "SHARPEMU_PS_DEBUG_TONEMAP_GAMUT_SCALAR") == "1";
        private static readonly bool _debugTonemapTransferOutput =
            Environment.GetEnvironmentVariable(
                "SHARPEMU_PS_DEBUG_TONEMAP_TRANSFER") == "1";
        private static readonly bool _debugTonemapAbsoluteOutput =
            Environment.GetEnvironmentVariable(
                "SHARPEMU_PS_DEBUG_TONEMAP_ABS") == "1";
        private static readonly bool _debugTonemapNanOutput =
            Environment.GetEnvironmentVariable(
                "SHARPEMU_PS_DEBUG_TONEMAP_NAN") == "1";
        private static readonly bool _debugTonemapExecOutput =
            Environment.GetEnvironmentVariable(
                "SHARPEMU_PS_DEBUG_TONEMAP_EXEC") == "1";
        // SHARPEMU_PS_FORCE_EXPOSURE_SCALAR=1 (high-probability toggle): for
        // the tonemap/composite pixel shader at PixelShaderAddress
        // 0x0000000500640200 only, force the exposure-scale S_BUFFER_LOAD_DWORD
        // (descriptor in s28 and NULL SOFFSET, immediate byte offset 116) to
        // return float 1.0 instead of the runtime cbuffer value. That scalar
        // feeds the first tonemap FMul against the in0 sample; when it reads 0
        // every channel multiplies to 0 and the menu presents black.
        private static readonly bool _forceExposureScalar =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(
                "SHARPEMU_PS_FORCE_EXPOSURE_SCALAR"));
        // The float value the toggle pins the exposure scalar to. "1" (or any
        // non-numeric) => 1.0; a positive float (e.g. "0.1") => that value, so
        // the exposure can be dialed in without over/under-saturating the HDR
        // scene. Stored as its 32-bit float bit pattern for the UInt() constant.
        private static readonly uint _forceExposureScalarBits =
            ParseForceExposureScalarBits();

        private static uint ParseForceExposureScalarBits()
        {
            var raw = Environment.GetEnvironmentVariable(
                "SHARPEMU_PS_FORCE_EXPOSURE_SCALAR");
            if (string.IsNullOrEmpty(raw))
            {
                return 0x3F800000u;
            }

            return float.TryParse(
                raw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) && value > 0f
                ? BitConverter.SingleToUInt32Bits(value)
                : 0x3F800000u;
        }
        // Known placements of the same tonemap/composite pixel shader. The game
        // upload address moves when preceding shader allocations change.
        private const ulong ExposureScalarShaderAddress = 0x0000000500640200UL;
        private const ulong RelocatedExposureScalarShaderAddress = 0x0000000500641900UL;
        // The exposure-scale S_BUFFER_LOAD_DWORD in that shader uses
        // architectural NULL for SOFFSET and immediate byte offset 0x74 (116).
        private const int ExposureScalarOffsetBytes = 116;
        private uint _voidType;
        private uint _boolType;
        private uint _uintType;
        private uint _intType;
        private uint _longType;
        private uint _ulongType;
        private uint _floatType;
        private uint _vec2Type;
        private uint _vec3Type;
        private uint _vec4Type;
        private uint _uvec2Type;
        private uint _uvec3Type;
        private uint _uvec4Type;
        private uint _privateUintPointer;
        private uint _privateBoolPointer;
        private uint _scalarRegisters;
        private uint _vectorRegisters;
        private uint _scc;
        private uint _vcc;
        private uint _exec;
        private uint _programCounter;
        private uint _programActive;
        private uint _globalBuffers;
        private uint _storageUintPointer;
        private uint _lds;
        private uint _workgroupUintPointer;
        private uint _positionOutput;
        private uint _vertexIndexInput;
        private uint _instanceIndexInput;
        private uint _fragCoordInput;
        private uint _baryCoordInput;
        private uint _baryCoordNoPerspInput;
        private uint _localInvocationIdInput;
        private uint _workGroupIdInput;
        private uint _globalInvocationIdInput;
        private uint _subgroupInvocationIdInput;
        private uint _glsl;

        private enum ImageComponentKind
        {
            Float,
            Sint,
            Uint,
        }

        private readonly record struct SpirvImageResource(
            uint Variable,
            uint ImageType,
            uint ObjectType,
            uint ComponentType,
            uint VectorType,
            ImageComponentKind ComponentKind,
            bool IsStorage);

        private enum VertexInputComponentKind
        {
            Float,
            Sint,
            Uint,
        }

        private readonly record struct SpirvVertexInput(
            uint Variable,
            uint Type,
            uint ComponentType,
            uint ComponentCount,
            VertexInputComponentKind ComponentKind);

        private readonly record struct SpirvPixelOutput(
            uint Variable,
            uint Type,
            Gen5PixelOutputKind Kind);

        private readonly record struct SpirvInterpolationCoefficients(
            uint P0,
            uint P10,
            uint P20);

        public CompilationContext(
            Gen5SpirvStage stage,
            Gen5ShaderState state,
            Gen5ShaderEvaluation evaluation,
            IReadOnlyList<Gen5PixelOutputBinding> pixelOutputBindings,
            uint localSizeX,
            uint localSizeY,
            uint localSizeZ,
            int globalBufferBase,
            int totalGlobalBufferCount,
            int imageBindingBase,
            int scalarRegisterBufferIndex,
            bool instanceIdFromVertexIndex = false,
            NggComputeCapture? computeCapture = null,
            IReadOnlyCollection<uint>? requiredVertexOutputLocations = null,
            IReadOnlyList<uint>? pixelInputControls = null,
            uint pixelInputEnable = 0,
            uint pixelInputAddress = 0,
            ulong pixelShaderAddress = 0)
        {
            _stage = stage;
            _state = state;
            _requiredVertexOutputLocations = requiredVertexOutputLocations ?? [];
            _evaluation = evaluation;
            _pixelOutputBindings = pixelOutputBindings;
            _pixelInputControls = pixelInputControls ?? [];
            _pixelInputEnable = pixelInputEnable;
            _pixelInputAddress = pixelInputAddress;
            _pixelShaderAddress = pixelShaderAddress;
            _localSizeX = localSizeX;
            _localSizeY = localSizeY;
            _localSizeZ = localSizeZ;
            _globalBufferBase = globalBufferBase;
            _totalGlobalBufferCount = totalGlobalBufferCount < 0
                ? evaluation.GlobalMemoryBindings.Count
                : totalGlobalBufferCount;
            _computeCapture = computeCapture;
            // The capture output buffer is appended after the ES's own global
            // buffers, so the guestBuffers descriptor array needs one extra slot.
            if (_computeCapture is not null)
            {
                _totalGlobalBufferCount += 1;
            }
            _imageBindingBase = imageBindingBase;
            _scalarRegisterBufferIndex = scalarRegisterBufferIndex;
            _instanceIdFromVertexIndex = instanceIdFromVertexIndex;
            foreach (var instruction in state.Program.Instructions)
            {
                if (instruction.Opcode == "VInterpMovF32" &&
                    instruction.Control is Gen5InterpolationControl move)
                {
                    _interpolationMoveAttributes.Add(move.Attribute);
                }
            }
        }

        public bool TryCompile(out Gen5SpirvShader shader, out string error)
        {
            shader = default!;
            error = string.Empty;
            try
            {
                DeclareModule();
                var blocks = BuildBasicBlocks(_state.Program.Instructions);
                if (blocks.Count == 0)
                {
                    error = "shader contains no executable blocks";
                    return false;
                }

                var functionType = _module.TypeFunction(_voidType);
                var main = _module.BeginFunction(_voidType, functionType);
                _module.AddName(main, "main");
                _module.AddLabel();
                EmitInitialState();
                if (!TryEmitInterpolationCoefficientState(out error))
                {
                    return false;
                }
                EmitPaddedVertexOutputStores();

                var loopHeader = _module.AllocateId();
                var switchHeader = _module.AllocateId();
                var switchMerge = _module.AllocateId();
                var loopContinue = _module.AllocateId();
                var loopMerge = _module.AllocateId();
                var defaultLabel = _module.AllocateId();
                var caseLabels = new uint[blocks.Count];
                for (var index = 0; index < caseLabels.Length; index++)
                {
                    caseLabels[index] = _module.AllocateId();
                }

                _module.AddStatement(SpirvOp.Branch, loopHeader);
                _module.AddLabel(loopHeader);
                _module.AddStatement(SpirvOp.LoopMerge, loopMerge, loopContinue, 0);
                _module.AddStatement(SpirvOp.Branch, switchHeader);

                _module.AddLabel(switchHeader);
                var selector = Load(_uintType, _programCounter);
                _module.AddStatement(SpirvOp.SelectionMerge, switchMerge, 0);
                var switchOperands = new uint[2 + (blocks.Count * 2)];
                switchOperands[0] = selector;
                switchOperands[1] = defaultLabel;
                for (var index = 0; index < blocks.Count; index++)
                {
                    switchOperands[2 + (index * 2)] = (uint)index;
                    switchOperands[3 + (index * 2)] = caseLabels[index];
                }

                _module.AddStatement(SpirvOp.Switch, switchOperands);
                for (var index = 0; index < blocks.Count; index++)
                {
                    _module.AddLabel(caseLabels[index]);
                    if (!TryEmitBlock(blocks, index, out error))
                    {
                        error = $"block=0x{blocks[index].StartPc:X}: {error}";
                        return false;
                    }

                    _module.AddStatement(SpirvOp.Branch, switchMerge);
                }

                _module.AddLabel(defaultLabel);
                Store(_programActive, _module.ConstantBool(false));
                _module.AddStatement(SpirvOp.Branch, switchMerge);

                _module.AddLabel(switchMerge);
                _module.AddStatement(SpirvOp.Branch, loopContinue);
                _module.AddLabel(loopContinue);
                var active = Load(_boolType, _programActive);
                _module.AddStatement(
                    SpirvOp.BranchConditional,
                    active,
                    loopHeader,
                    loopMerge);
                _module.AddLabel(loopMerge);
                if (_stage == Gen5SpirvStage.Pixel)
                {
                    // A fragment lane removed from EXEC is not a request to
                    // write the output variable's zero initializer. It is a
                    // killed fragment and must not participate in color,
                    // depth, or blend operations. Keep EXEC masking during
                    // translation, then terminate lanes that remain inactive
                    // when the guest pixel shader exits.
                    var returnLabel = _module.AllocateId();
                    var killLabel = _module.AllocateId();
                    // Materialize the condition before SelectionMerge: SPIR-V
                    // requires the merge instruction to be immediately followed
                    // by its structured branch terminator.
                    var laneActive = Load(_boolType, _exec);
                    _module.AddStatement(SpirvOp.SelectionMerge, returnLabel, 0);
                    _module.AddStatement(
                        SpirvOp.BranchConditional,
                        laneActive,
                        returnLabel,
                        killLabel);
                    _module.AddLabel(killLabel);
                    _module.AddStatement(SpirvOp.Kill);
                    _module.AddLabel(returnLabel);
                }

                _module.AddStatement(SpirvOp.Return);
                _module.EndFunction();

                var model = _stage switch
                {
                    Gen5SpirvStage.Vertex => SpirvExecutionModel.Vertex,
                    Gen5SpirvStage.Pixel => SpirvExecutionModel.Fragment,
                    _ => SpirvExecutionModel.GLCompute,
                };
                _module.AddEntryPoint(model, main, "main", _interfaces);
                if (_stage == Gen5SpirvStage.Pixel)
                {
                    _module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
                }
                else if (_stage == Gen5SpirvStage.Compute)
                {
                    _module.AddExecutionMode(
                        main,
                        SpirvExecutionMode.LocalSize,
                        _localSizeX,
                        _localSizeY,
                        _localSizeZ);
                }

                var attributeCount = _stage == Gen5SpirvStage.Vertex
                    ? (uint)_vertexOutputs.Count
                    : (uint)_pixelInputs.Count;
                shader = new Gen5SpirvShader(
                    _module.Build(),
                    _evaluation.GlobalMemoryBindings,
                    _evaluation.ImageBindings,
                    attributeCount,
                    _stage == Gen5SpirvStage.Vertex
                        ? _evaluation.VertexInputs ?? []
                        : []);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private void DeclareModule()
        {
            _module.AddCapability(SpirvCapability.Shader);
            _module.AddCapability(SpirvCapability.Int64);
            _module.AddCapability(SpirvCapability.ImageQuery);
            if (ShouldDeclarePerspBarycentric() || ShouldDeclareNoPerspBarycentric())
            {
                _module.AddExtension("SPV_KHR_fragment_shader_barycentric");
                _module.AddCapability(SpirvCapability.FragmentBarycentricKhr);
            }
            if (_evaluation.ImageBindings.Any(
                    static binding =>
                        (binding.Opcode.StartsWith(
                             "ImageSample",
                             StringComparison.Ordinal) ||
                         binding.Opcode.StartsWith(
                             "ImageGather4",
                             StringComparison.Ordinal)) &&
                        binding.Opcode.EndsWith("O", StringComparison.Ordinal)))
            {
                _module.AddCapability(SpirvCapability.ImageGatherExtended);
            }

            if (UsesSubgroupOperations())
            {
                _module.AddCapability(SpirvCapability.GroupNonUniform);
                if (UsesSubgroupShuffle())
                {
                    _module.AddCapability(SpirvCapability.GroupNonUniformShuffle);
                }

                if (UsesWaveControl())
                {
                    _module.AddCapability(SpirvCapability.GroupNonUniformVote);
                }

                if (UsesSubgroupBroadcast() || UsesWholeQuadMode())
                {
                    _module.AddCapability(SpirvCapability.GroupNonUniformBallot);
                }
            }

            _glsl = _module.ImportExtInst("GLSL.std.450");
            _voidType = _module.TypeVoid();
            _boolType = _module.TypeBool();
            _uintType = _module.TypeInt(32, signed: false);
            _intType = _module.TypeInt(32, signed: true);
            _longType = _module.TypeInt(64, signed: true);
            _ulongType = _module.TypeInt(64, signed: false);
            _floatType = _module.TypeFloat(32);
            _vec2Type = _module.TypeVector(_floatType, 2);
            _vec3Type = _module.TypeVector(_floatType, 3);
            _vec4Type = _module.TypeVector(_floatType, 4);
            _uvec2Type = _module.TypeVector(_uintType, 2);
            _uvec3Type = _module.TypeVector(_uintType, 3);
            _uvec4Type = _module.TypeVector(_uintType, 4);
            _privateUintPointer =
                _module.TypePointer(SpirvStorageClass.Private, _uintType);
            _privateBoolPointer =
                _module.TypePointer(SpirvStorageClass.Private, _boolType);

            var scalarArrayType = _module.TypeArray(_uintType, ScalarRegisterCount);
            var vectorArrayType = _module.TypeArray(_uintType, VectorRegisterCount);
            var privateScalarArrayPointer =
                _module.TypePointer(SpirvStorageClass.Private, scalarArrayType);
            var privateVectorArrayPointer =
                _module.TypePointer(SpirvStorageClass.Private, vectorArrayType);
            _scalarRegisters = _module.AddGlobalVariable(
                privateScalarArrayPointer,
                SpirvStorageClass.Private,
                _module.ConstantNull(scalarArrayType));
            _vectorRegisters = _module.AddGlobalVariable(
                privateVectorArrayPointer,
                SpirvStorageClass.Private,
                _module.ConstantNull(vectorArrayType));
            _scc = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(false));
            _vcc = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(false));
            _exec = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(true));
            _programCounter = _module.AddGlobalVariable(
                _privateUintPointer,
                SpirvStorageClass.Private,
                _module.Constant(_uintType, 0));
            _programActive = _module.AddGlobalVariable(
                _privateBoolPointer,
                SpirvStorageClass.Private,
                _module.ConstantBool(true));
            _interfaces.Add(_scalarRegisters);
            _interfaces.Add(_vectorRegisters);
            _interfaces.Add(_scc);
            _interfaces.Add(_vcc);
            _interfaces.Add(_exec);
            _interfaces.Add(_programCounter);
            _interfaces.Add(_programActive);
            _module.AddName(_scalarRegisters, "sgpr");
            _module.AddName(_vectorRegisters, "vgpr");

            DeclareBuffers();
            DeclareImages();
            DeclareLds();
            DeclareStageInterface();
        }

        private void DeclareLds()
        {
            if (_stage != Gen5SpirvStage.Compute || !UsesLds())
            {
                return;
            }

            var ldsArrayType = _module.TypeArray(_uintType, LdsDwordCount);
            var ldsPointer =
                _module.TypePointer(SpirvStorageClass.Workgroup, ldsArrayType);
            _workgroupUintPointer =
                _module.TypePointer(SpirvStorageClass.Workgroup, _uintType);
            _lds = _module.AddGlobalVariable(
                ldsPointer,
                SpirvStorageClass.Workgroup);
            _module.AddName(_lds, "lds");
            _interfaces.Add(_lds);
        }

        private void DeclareBuffers()
        {
            for (var index = 0; index < _evaluation.GlobalMemoryBindings.Count; index++)
            {
                foreach (var pc in _evaluation.GlobalMemoryBindings[index].InstructionPcs)
                {
                    _bufferBindingByPc.TryAdd(pc, _globalBufferBase + index);
                }
            }

            if (_totalGlobalBufferCount == 0)
            {
                return;
            }

            var runtimeArray = _module.TypeRuntimeArray(_uintType);
            _module.AddDecoration(runtimeArray, SpirvDecoration.ArrayStride, sizeof(uint));
            var block = _module.TypeStruct(runtimeArray);
            _module.AddDecoration(block, SpirvDecoration.Block);
            _module.AddMemberDecoration(block, 0, SpirvDecoration.Offset, 0);
            var descriptors = _module.TypeArray(
                block,
                (uint)_totalGlobalBufferCount);
            var descriptorsPointer =
                _module.TypePointer(SpirvStorageClass.StorageBuffer, descriptors);
            _storageUintPointer =
                _module.TypePointer(SpirvStorageClass.StorageBuffer, _uintType);
            _globalBuffers = _module.AddGlobalVariable(
                descriptorsPointer,
                SpirvStorageClass.StorageBuffer);
            _module.AddName(_globalBuffers, "guestBuffers");
            _module.AddDecoration(_globalBuffers, SpirvDecoration.DescriptorSet, 0);
            _module.AddDecoration(_globalBuffers, SpirvDecoration.Binding, 0);
            _interfaces.Add(_globalBuffers);
        }

        private void DeclareImages()
        {
            for (var index = 0; index < _evaluation.ImageBindings.Count; index++)
            {
                var binding = _evaluation.ImageBindings[index];
                _imageBindingByPc.TryAdd(binding.Pc, index);
                var isStorage =
                    Gen5ShaderTranslator.IsStorageImageOperation(binding.Opcode);
                var (format, componentKind) =
                    DecodeImageFormat(binding.ResourceDescriptor);
                var componentType = componentKind switch
                {
                    ImageComponentKind.Sint => _intType,
                    ImageComponentKind.Uint => _uintType,
                    _ => _floatType,
                };
                if (isStorage && format == SpirvImageFormat.Unknown)
                {
                    _module.AddCapability(
                        SpirvCapability.StorageImageReadWithoutFormat);
                    _module.AddCapability(
                        SpirvCapability.StorageImageWriteWithoutFormat);
                }
                else if (isStorage && RequiresExtendedStorageImageFormat(format))
                {
                    _module.AddCapability(
                        SpirvCapability.StorageImageExtendedFormats);
                }

                var imageType = _module.TypeImage(
                    componentType,
                    SpirvImageDim.Dim2D,
                    depth: false,
                    arrayed: false,
                    multisampled: false,
                    sampled: isStorage ? 2u : 1u,
                    isStorage ? format : SpirvImageFormat.Unknown);
                var objectType = isStorage
                    ? imageType
                    : _module.TypeSampledImage(imageType);
                var pointer = _module.TypePointer(
                    SpirvStorageClass.UniformConstant,
                    objectType);
                var variable = _module.AddGlobalVariable(
                    pointer,
                    SpirvStorageClass.UniformConstant);
                _module.AddName(variable, isStorage ? $"image{index}" : $"tex{index}");
                _module.AddDecoration(variable, SpirvDecoration.DescriptorSet, 0);
                _module.AddDecoration(
                    variable,
                    SpirvDecoration.Binding,
                    (uint)(_imageBindingBase + index + 1));
                _imageResources.Add(
                    new SpirvImageResource(
                        variable,
                        imageType,
                        objectType,
                        componentType,
                        _module.TypeVector(componentType, 4),
                        componentKind,
                        isStorage));
                _interfaces.Add(variable);
            }
        }

        private static bool RequiresExtendedStorageImageFormat(
            SpirvImageFormat format) =>
            format is not SpirvImageFormat.Unknown and
                not SpirvImageFormat.Rgba32f and
                not SpirvImageFormat.Rgba32i and
                not SpirvImageFormat.Rgba32ui;

        private static (SpirvImageFormat Format, ImageComponentKind Kind)
            DecodeImageFormat(IReadOnlyList<uint> descriptor)
        {
            if (descriptor.Count < 2)
            {
                return (SpirvImageFormat.Unknown, ImageComponentKind.Float);
            }

            var unifiedFormat = (descriptor[1] >> 20) & 0x1FFu;
            if (!Gfx10UnifiedFormat.TryDecode(
                    unifiedFormat,
                    out var dataFormat,
                    out var numberType))
            {
                return (SpirvImageFormat.Unknown, ImageComponentKind.Float);
            }

            return (dataFormat, numberType) switch
            {
                (1, _) => (SpirvImageFormat.R8, ImageComponentKind.Float),
                (2, _) => (SpirvImageFormat.R16f, ImageComponentKind.Float),
                (3, _) => (SpirvImageFormat.Rg8, ImageComponentKind.Float),
                (4, 4) => (SpirvImageFormat.R32ui, ImageComponentKind.Uint),
                (4, 5) => (SpirvImageFormat.R32i, ImageComponentKind.Sint),
                (4, _) => (SpirvImageFormat.R32f, ImageComponentKind.Float),
                (5, 4) => (SpirvImageFormat.Rg16ui, ImageComponentKind.Uint),
                (5, 5) => (SpirvImageFormat.Rg16i, ImageComponentKind.Sint),
                (5, 0) => (SpirvImageFormat.Rg16, ImageComponentKind.Float),
                (5, _) => (SpirvImageFormat.Rg16f, ImageComponentKind.Float),
                (6 or 7, _) => (
                    SpirvImageFormat.R11fG11fB10f,
                    ImageComponentKind.Float),
                (9, 4) => (SpirvImageFormat.Rgb10A2ui, ImageComponentKind.Uint),
                (9, _) => (SpirvImageFormat.Rgb10A2, ImageComponentKind.Float),
                (10, 4) => (SpirvImageFormat.Rgba8ui, ImageComponentKind.Uint),
                (10, 5) => (SpirvImageFormat.Rgba8i, ImageComponentKind.Sint),
                (10, _) => (SpirvImageFormat.Rgba8, ImageComponentKind.Float),
                (11, 4) => (SpirvImageFormat.Rg32ui, ImageComponentKind.Uint),
                (11, 5) => (SpirvImageFormat.Rg32i, ImageComponentKind.Sint),
                (11, _) => (SpirvImageFormat.Rg32f, ImageComponentKind.Float),
                (12, 4) => (SpirvImageFormat.Rgba16ui, ImageComponentKind.Uint),
                (12, 5) => (SpirvImageFormat.Rgba16i, ImageComponentKind.Sint),
                (12, 0) => (SpirvImageFormat.Rgba16, ImageComponentKind.Float),
                (12, _) => (SpirvImageFormat.Rgba16f, ImageComponentKind.Float),
                (13 or 14, 4) => (
                    SpirvImageFormat.Rgba32ui,
                    ImageComponentKind.Uint),
                (13 or 14, 5) => (
                    SpirvImageFormat.Rgba32i,
                    ImageComponentKind.Sint),
                (13 or 14, _) => (
                    SpirvImageFormat.Rgba32f,
                    ImageComponentKind.Float),
                (20, _) => (SpirvImageFormat.R32ui, ImageComponentKind.Uint),
                (22, _) => (SpirvImageFormat.Rgba16f, ImageComponentKind.Float),
                (29, _) => (SpirvImageFormat.R32f, ImageComponentKind.Float),
                (36, _) => (SpirvImageFormat.R8, ImageComponentKind.Float),
                (49, _) => (SpirvImageFormat.R8ui, ImageComponentKind.Uint),
                (56 or 62 or 64, _) => (
                    SpirvImageFormat.Rgba8,
                    ImageComponentKind.Float),
                (71, _) => (SpirvImageFormat.Rgba16f, ImageComponentKind.Float),
                (75, _) => (SpirvImageFormat.Rg32f, ImageComponentKind.Float),
                (_, 4) => (SpirvImageFormat.Unknown, ImageComponentKind.Uint),
                (_, 5) => (SpirvImageFormat.Unknown, ImageComponentKind.Sint),
                _ => (SpirvImageFormat.Unknown, ImageComponentKind.Float),
            };
        }

        private void DeclareStageInterface()
        {
            if (UsesSubgroupOperations())
            {
                var subgroupPointer =
                    _module.TypePointer(SpirvStorageClass.Input, _uintType);
                _subgroupInvocationIdInput = _module.AddGlobalVariable(
                    subgroupPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _subgroupInvocationIdInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.SubgroupLocalInvocationId);
                if (_stage == Gen5SpirvStage.Pixel)
                {
                    // Fragment integer inputs must be Flat, built-ins included
                    // (VUID-StandaloneSpirv-Flat-04744).
                    _module.AddDecoration(
                        _subgroupInvocationIdInput,
                        SpirvDecoration.Flat);
                }

                _interfaces.Add(_subgroupInvocationIdInput);
            }

            if (_stage == Gen5SpirvStage.Vertex)
            {
                DeclareVertexInputs();

                var inputPointer =
                    _module.TypePointer(SpirvStorageClass.Input, _uintType);
                _vertexIndexInput = _module.AddGlobalVariable(
                    inputPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _vertexIndexInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.VertexIndex);
                _interfaces.Add(_vertexIndexInput);

                _instanceIndexInput = _module.AddGlobalVariable(
                    inputPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _instanceIndexInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.InstanceIndex);
                _interfaces.Add(_instanceIndexInput);

                var outputPointer =
                    _module.TypePointer(SpirvStorageClass.Output, _vec4Type);
                _positionOutput = _module.AddGlobalVariable(
                    outputPointer,
                    SpirvStorageClass.Output);
                _module.AddDecoration(
                    _positionOutput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.Position);
                _interfaces.Add(_positionOutput);

                if (_pixelInputControls.Count != 0)
                {
                    // One output per fragment attribute: the host location is
                    // the attribute index and the guest location is the vertex
                    // parameter the control selects. Aliased controls fan one
                    // guest export into several unique host locations.
                    for (var attribute = 0;
                         attribute < _pixelInputControls.Count;
                         attribute++)
                    {
                        DeclareVertexParameterOutput(
                            outputPointer,
                            checked((uint)attribute),
                            _pixelInputControls[attribute] & 0x1Fu);
                    }
                }
                else
                {
                    var parameters = _state.Program.Instructions
                        .Select(instruction => instruction.Control)
                        .OfType<Gen5ExportControl>()
                        .Where(export => export.Target is >= 32 and < 64)
                        .Select(export => export.Target - 32)
                        .Distinct()
                        .Order()
                        .ToArray();
                    foreach (var parameter in parameters)
                    {
                        DeclareVertexParameterOutput(
                            outputPointer,
                            parameter,
                            parameter);
                    }
                }

                // Pad the interface with any Location the paired pixel shader
                // interpolates but this vertex program does not export. Without
                // it vkCreateGraphicsPipelines rejects the pipeline (fragment
                // Input with no matching vertex Output) and the draw renders
                // nothing -- which black-holed the fullscreen composite pass.
                foreach (var location in _requiredVertexOutputLocations)
                {
                    if (_vertexOutputs.ContainsKey(location))
                    {
                        continue;
                    }

                    var variable = _module.AddGlobalVariable(
                        outputPointer,
                        SpirvStorageClass.Output);
                    _module.AddDecoration(variable, SpirvDecoration.Location, location);
                    _paddedVertexOutputs.Add(variable);
                    _interfaces.Add(variable);
                }
            }
            else if (_stage == Gen5SpirvStage.Pixel)
            {
                var inputVec4Pointer =
                    _module.TypePointer(SpirvStorageClass.Input, _vec4Type);
                var attributes = _state.Program.Instructions
                    .Select(instruction => instruction.Control)
                    .OfType<Gen5InterpolationControl>()
                    .Select(control => control.Attribute)
                    .Distinct()
                    .Order()
                    .ToArray();
                foreach (var attribute in attributes)
                {
                    var variable = _module.AddGlobalVariable(
                        inputVec4Pointer,
                        SpirvStorageClass.Input);
                    // The paired vertex module remaps the guest source selected
                    // by this control into the unique pixel-attribute location.
                    _module.AddDecoration(variable, SpirvDecoration.Location, attribute);
                    if ((GetPixelInputControl(attribute) & 0x400u) != 0)
                    {
                        _module.AddDecoration(variable, SpirvDecoration.Flat);
                    }

                    _pixelInputs.Add(attribute, variable);
                    _interfaces.Add(variable);
                }

                if (ShouldDeclarePerspBarycentric() || ShouldDeclareNoPerspBarycentric())
                {
                    var inputVec3Pointer =
                        _module.TypePointer(SpirvStorageClass.Input, _vec3Type);
                    if (ShouldDeclarePerspBarycentric())
                    {
                        _baryCoordInput = _module.AddGlobalVariable(
                            inputVec3Pointer,
                            SpirvStorageClass.Input);
                        _module.AddDecoration(
                            _baryCoordInput,
                            SpirvDecoration.BuiltIn,
                            (uint)SpirvBuiltIn.BaryCoordKhr);
                        _interfaces.Add(_baryCoordInput);
                    }

                    if (ShouldDeclareNoPerspBarycentric())
                    {
                        _baryCoordNoPerspInput = _module.AddGlobalVariable(
                            inputVec3Pointer,
                            SpirvStorageClass.Input);
                        _module.AddDecoration(
                            _baryCoordNoPerspInput,
                            SpirvDecoration.BuiltIn,
                            (uint)SpirvBuiltIn.BaryCoordNoPerspKhr);
                        _interfaces.Add(_baryCoordNoPerspInput);
                    }
                }

                _fragCoordInput = _module.AddGlobalVariable(
                    inputVec4Pointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _fragCoordInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.FragCoord);
                _interfaces.Add(_fragCoordInput);

                foreach (var binding in _pixelOutputBindings)
                {
                    var outputType = GetPixelOutputType(binding.Kind);
                    var outputPointer =
                        _module.TypePointer(SpirvStorageClass.Output, outputType);
                    var variable = _module.AddGlobalVariable(
                        outputPointer,
                        SpirvStorageClass.Output);
                    _module.AddName(variable, $"mrt{binding.GuestSlot}");
                    _module.AddDecoration(
                        variable,
                        SpirvDecoration.Location,
                        binding.HostLocation);
                    _pixelOutputs.Add(
                        binding.GuestSlot,
                        new SpirvPixelOutput(variable, outputType, binding.Kind));
                    _interfaces.Add(variable);
                }
            }
            else
            {
                var inputPointer =
                    _module.TypePointer(SpirvStorageClass.Input, _uvec3Type);
                _localInvocationIdInput = _module.AddGlobalVariable(
                    inputPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _localInvocationIdInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.LocalInvocationId);
                _workGroupIdInput = _module.AddGlobalVariable(
                    inputPointer,
                    SpirvStorageClass.Input);
                _module.AddDecoration(
                    _workGroupIdInput,
                    SpirvDecoration.BuiltIn,
                    (uint)SpirvBuiltIn.WorkgroupId);
                _interfaces.Add(_localInvocationIdInput);
                _interfaces.Add(_workGroupIdInput);

                // Position-capture route only: the flattened output vertex index
                // is gl_GlobalInvocationID.x (one invocation == one vertex).
                if (_computeCapture is not null)
                {
                    _globalInvocationIdInput = _module.AddGlobalVariable(
                        inputPointer,
                        SpirvStorageClass.Input);
                    _module.AddDecoration(
                        _globalInvocationIdInput,
                        SpirvDecoration.BuiltIn,
                        (uint)SpirvBuiltIn.GlobalInvocationId);
                    _interfaces.Add(_globalInvocationIdInput);
                }
            }
        }

        private void DeclareVertexParameterOutput(
            uint outputPointer,
            uint hostLocation,
            uint guestLocation)
        {
            var variable = _module.AddGlobalVariable(
                outputPointer,
                SpirvStorageClass.Output);
            _module.AddDecoration(variable, SpirvDecoration.Location, hostLocation);
            _vertexOutputs.Add(hostLocation, variable);
            if (!_vertexOutputsByGuestLocation.TryGetValue(
                    guestLocation,
                    out var consumers))
            {
                consumers = [];
                _vertexOutputsByGuestLocation.Add(guestLocation, consumers);
            }

            consumers.Add(variable);
            _interfaces.Add(variable);
        }

        private uint GetPixelInputControl(uint attribute) =>
            attribute < (uint)_pixelInputControls.Count
                ? _pixelInputControls[(int)attribute]
                : attribute;

        // SPI_PS_INPUT_CNTL: FLAT_SHADE (bit 10) together with a nonzero
        // PT_SPRITE/custom select (bit 5) marks a custom-interpolated
        // attribute whose provoking-vertex word carries a packed payload.
        private static bool IsCustomInterpolation(uint control) =>
            (control & 0x420u) == 0x420u;

        // Coefficient reconstruction (and therefore the barycentric builtin)
        // is only needed when an interpolation move touches an attribute that
        // is NOT custom/flat; custom attributes replay the exact
        // provoking-vertex word without any derivative math.
        private bool RequiresBarycentricCoefficients() =>
            _stage == Gen5SpirvStage.Pixel &&
            _interpolationMoveAttributes.Any(attribute =>
                !IsCustomInterpolation(GetPixelInputControl(attribute)));

        // A perspective barycentric input (BaryCoordKHR) is declared when the
        // guest reconstructs V_INTERP coefficients (which need I/J) or when any
        // perspective ADDR bit -- PERSP_SAMPLE/CENTER/CENTROID/PULL_MODEL
        // (bits 0..3) -- requests one, so its weights can be seeded into the
        // hardware-selected VGPR slot.
        private bool ShouldDeclarePerspBarycentric() =>
            _stage == Gen5SpirvStage.Pixel &&
            (RequiresBarycentricCoefficients() || (_pixelInputAddress & 0xFu) != 0);

        // A linear barycentric input (BaryCoordNoPerspKHR) is declared when any
        // linear ADDR bit -- LINEAR_SAMPLE/CENTER/CENTROID (bits 4..6) -- is set.
        private bool ShouldDeclareNoPerspBarycentric() =>
            _stage == Gen5SpirvStage.Pixel &&
            (_pixelInputAddress & 0x70u) != 0;

        private void DeclareVertexInputs()
        {
            foreach (var input in _evaluation.VertexInputs ?? [])
            {
                // Declare UINT/SINT attributes with integer component types so
                // the shader interface matches the Vulkan pipeline's vertex
                // format; normalized/scaled/float formats stay on float inputs.
                var componentKind = input.NumberFormat switch
                {
                    4 => VertexInputComponentKind.Uint,
                    5 => VertexInputComponentKind.Sint,
                    _ => VertexInputComponentKind.Float,
                };
                var componentType = componentKind switch
                {
                    VertexInputComponentKind.Uint => _uintType,
                    VertexInputComponentKind.Sint => _intType,
                    _ => _floatType,
                };
                var type = input.ComponentCount switch
                {
                    1u => componentType,
                    >= 2u and <= 4u =>
                        _module.TypeVector(componentType, input.ComponentCount),
                    _ => 0u,
                };
                if (type == 0)
                {
                    continue;
                }

                var pointer = _module.TypePointer(SpirvStorageClass.Input, type);
                var variable = _module.AddGlobalVariable(
                    pointer,
                    SpirvStorageClass.Input);
                _module.AddName(variable, $"attr{input.Location}");
                _module.AddDecoration(
                    variable,
                    SpirvDecoration.Location,
                    input.Location);
                _vertexInputsByPc.TryAdd(
                    input.Pc,
                    new SpirvVertexInput(
                        variable,
                        type,
                        componentType,
                        input.ComponentCount,
                        componentKind));
                _interfaces.Add(variable);
            }
        }

        private void EmitInitialState()
        {
            if (_scalarRegisterBufferIndex >= 0)
            {
                for (uint index = 0; index < ScalarRegisterCount; index++)
                {
                    StoreS(index, LoadBufferWord(_scalarRegisterBufferIndex, UInt(index)));
                }
            }
            else
            {
                for (uint index = 0;
                     index < _evaluation.InitialScalarRegisters.Count &&
                     index < ScalarRegisterCount;
                     index++)
                {
                    var value = _evaluation.InitialScalarRegisters[(int)index];
                    if (value != 0)
                    {
                        StoreS(index, UInt(value));
                    }
                }
            }

            Store(_scc, _module.ConstantBool(false));
            if (_subgroupInvocationIdInput != 0)
            {
                StoreWaveMask(106, _module.ConstantBool(false));
                StoreWaveMask(126, _module.ConstantBool(true));
            }
            else
            {
                Store(_vcc, _module.ConstantBool(false));
                Store(_exec, _module.ConstantBool(true));
            }
            Store(_programCounter, UInt(0));
            Store(_programActive, _module.ConstantBool(true));

            if (_stage == Gen5SpirvStage.Vertex)
            {
                StoreV(5, Load(_uintType, _vertexIndexInput), guardWithExec: false);
                StoreV(
                    8,
                    Load(
                        _uintType,
                        _instanceIdFromVertexIndex ? _vertexIndexInput : _instanceIndexInput),
                    guardWithExec: false);
                if (_pixelInputControls.Count != 0)
                {
                    // With input controls every fragment attribute owns an
                    // output variable, including ones whose guest parameter
                    // this program never exports. Zero-fill them all so the
                    // unexported extras read as zero instead of garbage.
                    foreach (var variable in _vertexOutputs.Values)
                    {
                        Store(variable, _module.ConstantNull(_vec4Type));
                    }
                }
            }
            else if (_stage == Gen5SpirvStage.Pixel)
            {
                var fragCoord = Load(_vec4Type, _fragCoordInput);
                if (_pixelInputAddress != 0)
                {
                    // Real draw: allocate the pixel-input VGPRs in the hardware
                    // order gated by SPI_PS_INPUT_ADDR so the barycentric weights
                    // and POS values land in the same VGPRs the guest reads.
                    EmitPixelInputState(fragCoord);
                }
                else
                {
                    // Legacy callers (unit tests) provide no ADDR/ENA. Preserve
                    // the historical fixed PERSP_CENTER + POS_X/Y layout: I/J in
                    // v0/v1, fragCoord X/Y in v2/v3.
                    EmitLegacyPixelInputState(fragCoord);
                }

                foreach (var output in _pixelOutputs.Values)
                {
                    Store(output.Variable, _module.ConstantNull(output.Type));
                }
            }
            else
            {
                var localId = Load(_uvec3Type, _localInvocationIdInput);
                for (uint component = 0; component < 3; component++)
                {
                    var value = _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _uintType,
                        localId,
                        component);
                    StoreV(component, value, guardWithExec: false);
                }

                if (_state.ComputeSystemRegisters is { } registers)
                {
                    var workGroupId = Load(_uvec3Type, _workGroupIdInput);
                    StoreComputeSystemRegister(
                        registers.WorkGroupXRegister,
                        workGroupId,
                        0);
                    StoreComputeSystemRegister(
                        registers.WorkGroupYRegister,
                        workGroupId,
                        1);
                    StoreComputeSystemRegister(
                        registers.WorkGroupZRegister,
                        workGroupId,
                        2);
                    if (registers.ThreadGroupSizeRegister is { } sizeRegister)
                    {
                        StoreS(
                            sizeRegister,
                            UInt(checked(_localSizeX * _localSizeY * _localSizeZ)));
                    }
                }

                // Position-capture route only: seed the vertex-id and instance-id
                // VGPRs (v5/v8) from gl_GlobalInvocationID.x, mirroring the
                // vertex-stage v5<-VertexIndex/v8<-InstanceIndex seed above. The
                // ES recomputes its fetch index from these (e.g. v_cndmask v0,
                // v8, v5), so seeding v0 directly is overwritten; seeding the id
                // registers the shader reads makes that computation yield the
                // invocation index. One invocation produces one output vertex.
                if (_computeCapture is not null)
                {
                    // Force the execution mask on. NGG shaders use subgroup ops,
                    // so the prologue above set the wave masks but left _exec
                    // unset; the position-capture export (and any exec-guarded
                    // body store) is gated on _exec, so without this every
                    // invocation writes nothing. One invocation = one live vertex.
                    Store(_exec, _module.ConstantBool(true));
                    var invocationIndex = LoadGlobalInvocationIdX();
                    StoreV(5, invocationIndex, guardWithExec: false);
                    StoreV(8, invocationIndex, guardWithExec: false);
                }
            }
        }

        private void EmitLegacyPixelInputState(uint fragCoord)
        {
            if (_baryCoordInput != 0)
            {
                // Under the fixed PERSP_CENTER + POS_X/Y input layout the
                // barycentric I/J weights occupy v0/v1. Seed them so the
                // guest's manual interpolation (v_interp_p1/p2 against the
                // reconstructed P0/P10/P20 coefficients) sees real weights.
                var barycentrics = Load(_vec3Type, _baryCoordInput);
                for (uint component = 0; component < 2; component++)
                {
                    var weight = _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _floatType,
                        barycentrics,
                        component + 1);
                    StoreV(component, Bitcast(_uintType, weight), guardWithExec: false);
                }
            }

            var x = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                fragCoord,
                0);
            var y = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                fragCoord,
                1);
            StoreV(2, Bitcast(_uintType, x), guardWithExec: false);
            StoreV(3, Bitcast(_uintType, y), guardWithExec: false);
        }

        private void EmitPixelInputState(uint fragCoord)
        {
            uint vgpr = 0;

            // Pixel input VGPRs are compacted in SPI_PS_INPUT_ADDR order. Each
            // ADDR bit that is set consumes its VGPR slot(s) whether or not the
            // matching ENA bit requests data, so inputs that follow (notably the
            // POS_* fragCoord values and, for centroid/sample sampling, the
            // barycentric weights) land in the hardware-selected registers.
            EmitPixelBarycentricInput(0, _baryCoordInput, ref vgpr); // PERSP_SAMPLE
            EmitPixelBarycentricInput(1, _baryCoordInput, ref vgpr); // PERSP_CENTER
            EmitPixelBarycentricInput(2, _baryCoordInput, ref vgpr); // PERSP_CENTROID
            AdvancePixelInput(3, 3, ref vgpr); // PERSP_PULL_MODEL
            EmitPixelBarycentricInput(4, _baryCoordNoPerspInput, ref vgpr); // LINEAR_SAMPLE
            EmitPixelBarycentricInput(5, _baryCoordNoPerspInput, ref vgpr); // LINEAR_CENTER
            EmitPixelBarycentricInput(6, _baryCoordNoPerspInput, ref vgpr); // LINEAR_CENTROID
            AdvancePixelInput(7, 1, ref vgpr); // LINE_STIPPLE_TEX

            EmitPixelPositionInput(8, 0, fragCoord, ref vgpr); // POS_X_FLOAT
            EmitPixelPositionInput(9, 1, fragCoord, ref vgpr); // POS_Y_FLOAT
            EmitPixelPositionInput(10, 2, fragCoord, ref vgpr); // POS_Z_FLOAT
            EmitPixelPositionInput(11, 3, fragCoord, ref vgpr); // POS_W_FLOAT

            // FRONT_FACE, ANCILLARY, SAMPLE_COVERAGE and POS_FIXED_PT follow the
            // position inputs. Reserve their compact slots so anything the guest
            // reads past them stays aligned; their builtins are not seeded yet.
            AdvancePixelInput(12, 1, ref vgpr); // FRONT_FACE
            AdvancePixelInput(13, 1, ref vgpr); // ANCILLARY
            AdvancePixelInput(14, 1, ref vgpr); // SAMPLE_COVERAGE
            AdvancePixelInput(15, 1, ref vgpr); // POS_FIXED_PT
        }

        private void EmitPixelBarycentricInput(int bit, uint input, ref uint vgpr)
        {
            var mask = 1u << bit;
            if ((_pixelInputAddress & mask) == 0)
            {
                return;
            }

            if ((_pixelInputEnable & mask) != 0 && input != 0)
            {
                // The barycentric I/J weights map to vertex-one/vertex-two
                // components of the fragment-shader-barycentric builtin, matching
                // the guest interpolation equation P0 + P10 * I + P20 * J.
                var barycentrics = Load(_vec3Type, input);
                for (uint component = 0; component < 2; component++)
                {
                    var weight = _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        _floatType,
                        barycentrics,
                        component + 1);
                    StoreV(vgpr + component, Bitcast(_uintType, weight), guardWithExec: false);
                }
            }

            vgpr += 2;
        }

        private void AdvancePixelInput(int bit, uint dwordCount, ref uint vgpr)
        {
            if ((_pixelInputAddress & (1u << bit)) != 0)
            {
                vgpr += dwordCount;
            }
        }

        private void EmitPixelPositionInput(
            int bit,
            uint component,
            uint fragCoord,
            ref uint vgpr)
        {
            var mask = 1u << bit;
            if ((_pixelInputAddress & mask) == 0)
            {
                return;
            }

            if ((_pixelInputEnable & mask) != 0)
            {
                var value = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    _floatType,
                    fragCoord,
                    component);
                StoreV(vgpr, Bitcast(_uintType, value), guardWithExec: false);
            }

            vgpr++;
        }

        private uint LoadGlobalInvocationIdX()
        {
            var globalId = Load(_uvec3Type, _globalInvocationIdInput);
            return _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _uintType,
                globalId,
                0);
        }

        private void StoreComputeSystemRegister(
            uint? register,
            uint workGroupId,
            uint component)
        {
            if (register is null)
            {
                return;
            }

            var value = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _uintType,
                workGroupId,
                component);
            StoreS(register.Value, value);
        }

        private bool TryEmitBlock(
            IReadOnlyList<ShaderBlock> blocks,
            int blockIndex,
            out string error)
        {
            error = string.Empty;
            var block = blocks[blockIndex];
            for (var index = block.StartIndex; index < block.EndIndex; index++)
            {
                var instruction = _state.Program.Instructions[index];
                if (IsBranch(instruction.Opcode) || instruction.Opcode == "SEndpgm")
                {
                    continue;
                }

                if (!TryEmitInstruction(instruction, out error))
                {
                    error = $"pc=0x{instruction.Pc:X} {instruction.Opcode}: {error}";
                    return false;
                }
            }

            var terminator = _state.Program.Instructions[block.EndIndex - 1];
            if (terminator.Opcode == "SEndpgm")
            {
                Store(_programActive, _module.ConstantBool(false));
                return true;
            }

            var fallthrough = blockIndex + 1 < blocks.Count
                ? (uint)(blockIndex + 1)
                : uint.MaxValue;
            if (terminator.Opcode == "SBranch")
            {
                if (!TryGetBranchTargetPc(terminator, out var targetPc))
                {
                    error = "invalid scalar branch target";
                    return false;
                }

                if (IsExitBranchTarget(_state.Program.Instructions, targetPc))
                {
                    Store(_programActive, _module.ConstantBool(false));
                    return true;
                }

                if (!TryFindBlock(blocks, targetPc, out var targetBlock))
                {
                    error = $"invalid scalar branch target pc=0x{terminator.Pc:X} target=0x{targetPc:X} blocks={FormatBlockStarts(blocks)}";
                    return false;
                }

                Store(_programCounter, UInt((uint)targetBlock));
                return true;
            }

            if (terminator.Opcode.StartsWith("SCbranch", StringComparison.Ordinal))
            {
                var hasTarget = TryGetBranchTargetPc(terminator, out var targetPc);
                var targetBlock = -1;
                var hasTargetBlock = hasTarget && TryFindBlock(blocks, targetPc, out targetBlock);
                var targetExits = hasTarget && IsExitBranchTarget(_state.Program.Instructions, targetPc);
                var hasCondition = TryGetBranchCondition(terminator.Opcode, out var condition);
                if (!hasTarget || (!hasTargetBlock && !targetExits) || !hasCondition)
                {
                    error =
                        $"invalid conditional scalar branch opcode={terminator.Opcode} " +
                        $"pc=0x{terminator.Pc:X} " +
                        $"target={(hasTarget ? $"0x{targetPc:X}" : "invalid")} " +
                        $"target_block={(hasTargetBlock ? targetBlock.ToString() : targetExits ? "exit" : "missing")} " +
                        $"fallthrough={(fallthrough == uint.MaxValue ? "end" : fallthrough.ToString())} " +
                        $"condition={hasCondition} " +
                        $"blocks={FormatBlockStarts(blocks)}";
                    return false;
                }

                var takenBlock = targetExits ? uint.MaxValue : (uint)targetBlock;
                var selected = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    condition,
                    UInt(takenBlock),
                    UInt(fallthrough));
                Store(_programCounter, selected);
                return true;
            }

            if (fallthrough == uint.MaxValue)
            {
                Store(_programActive, _module.ConstantBool(false));
            }
            else
            {
                Store(_programCounter, UInt(fallthrough));
            }

            return true;
        }

        private static string FormatBlockStarts(IReadOnlyList<ShaderBlock> blocks)
        {
            const int maxBlocks = 32;
            var count = Math.Min(blocks.Count, maxBlocks);
            var starts = new string[count];
            for (var index = 0; index < count; index++)
            {
                starts[index] = $"0x{blocks[index].StartPc:X}";
            }

            return blocks.Count <= maxBlocks
                ? string.Join(",", starts)
                : string.Join(",", starts) + $",...({blocks.Count})";
        }

        private static bool IsExitBranchTarget(
            IReadOnlyList<Gen5ShaderInstruction> instructions,
            uint targetPc)
        {
            if (instructions.Count == 0)
            {
                return false;
            }

            var last = instructions[^1];
            var lastEndPc = last.Pc + (uint)(last.Words.Count * sizeof(uint));
            return targetPc >= lastEndPc;
        }

        private bool TryGetBranchCondition(string opcode, out uint condition)
        {
            condition = opcode switch
            {
                "SCbranchScc0" => LogicalNot(Load(_boolType, _scc)),
                "SCbranchScc1" => Load(_boolType, _scc),
                "SCbranchVccz" => LogicalNot(SubgroupAny(Load(_boolType, _vcc))),
                "SCbranchVccnz" => SubgroupAny(Load(_boolType, _vcc)),
                "SCbranchExecz" => LogicalNot(SubgroupAny(Load(_boolType, _exec))),
                "SCbranchExecnz" => SubgroupAny(Load(_boolType, _exec)),
                // Conditional-debug branches test the COND_DBG_SYS/USER mode
                // flags a debugger sets; retail hardware never takes them.
                "SCbranchCdbgsys" or
                "SCbranchCdbguser" or
                "SCbranchCdbgsysOrUser" or
                "SCbranchCdbgsysAndUser" => _module.ConstantBool(false),
                _ => 0,
            };
            return condition != 0;
        }

        private bool TryEmitInstruction(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Opcode is
                "SNop" or
                "SWaitcnt" or
                "SInstPrefetch" or
                "STtraceData" or
                // GFX10 split waitcnt ops are SOPK-encoded scheduling hints;
                // they must not reach the scalar-ALU emitter.
                "SWaitcntVscnt" or
                "SWaitcntVmcnt" or
                "SWaitcntExpcnt" or
                "SWaitcntLgkmcnt")
            {
                return true;
            }

            if (instruction.Opcode == "SBarrier")
            {
                var workgroup = UInt(2);
                var semantics = UInt(0x108);
                _module.AddStatement(
                    SpirvOp.ControlBarrier,
                    workgroup,
                    workgroup,
                    semantics);
                return true;
            }

            if (instruction.Control is Gen5ScalarMemoryControl scalarMemory)
            {
                return TryEmitScalarMemory(instruction, scalarMemory, out error);
            }

            if (instruction.Control is Gen5InterpolationControl interpolation)
            {
                return TryEmitInterpolation(instruction, interpolation, out error);
            }

            if (instruction.Control is Gen5ImageControl image)
            {
                return TryEmitImage(instruction, image, out error);
            }

            if (instruction.Control is Gen5GlobalMemoryControl globalMemory)
            {
                return TryEmitGlobalMemory(instruction, globalMemory, out error);
            }

            if (instruction.Control is Gen5BufferMemoryControl bufferMemory)
            {
                return TryEmitBufferMemory(instruction, bufferMemory, out error);
            }

            if (instruction.Control is Gen5ExportControl export)
            {
                return TryEmitExport(instruction, export, out error);
            }

            if (instruction.Control is Gen5DataShareControl)
            {
                return TryEmitDataShare(instruction, out error);
            }

            if (instruction.Encoding is
                Gen5ShaderEncoding.Sop1 or
                Gen5ShaderEncoding.Sop2 or
                Gen5ShaderEncoding.Sopc or
                Gen5ShaderEncoding.Sopk)
            {
                return TryEmitScalarAlu(instruction, out error);
            }

            if (instruction.Opcode == "SRoundMode")
            {
                // FP_ROUND: [1:0] single / [3:2] double+half; 0 is round to
                // nearest even, which is what the emitted SPIR-V already runs
                // under in Vulkan. Anything else changes later arithmetic and
                // must stay a loud failure (see #108's review thread).
                var mode = instruction.Words[0] & 0xFFFF;
                if ((mode & 0xF) == 0)
                {
                    return true;
                }

                error = $"s_round_mode 0x{mode:X} requests non-default rounding";
                return false;
            }

            if (instruction.Opcode == "SDenormMode")
            {
                // The translator declares no SPIR-V denormal execution modes,
                // so denorm behavior is host-defined and no immediate provably
                // matches the request; keep the FP MODE write a loud failure.
                error =
                    $"s_denorm_mode 0x{instruction.Words[0] & 0xFFFF:X} " +
                    "FP MODE write is not modeled";
                return false;
            }

            if (instruction.Encoding is
                Gen5ShaderEncoding.Sopp or
                Gen5ShaderEncoding.Smrd or
                Gen5ShaderEncoding.Smem)
            {
                return true;
            }

            var emitted = TryEmitVectorAlu(instruction, out error);
            if (emitted && IsKnownTonemapShader())
            {
                if (_debugTonemapGamutScalarOutput)
                {
                    if (instruction.Pc == 0x538u)
                    {
                        StoreV(240, LoadS(0));
                        StoreV(241, LoadS(1));
                        StoreV(242, LoadS(2));
                    }
                }
                else if (_debugTonemapGamutDenominatorOutput)
                {
                    CaptureTonemapCheckpoint(
                        instruction.Pc,
                        0x538u, 10,
                        0x53Cu, 9,
                        0x540u, 7);
                }
                else if (_debugTonemapGamutBaseOutput)
                {
                    CaptureTonemapCheckpoint(
                        instruction.Pc,
                        0x564u, 10,
                        0x57Cu, 12,
                        0x598u, 14);
                }
                else if (_debugTonemapGamutOutput)
                {
                    if (instruction.Pc == 0x604u)
                    {
                        var active = _module.AddInstruction(
                            SpirvOp.Select,
                            _uintType,
                            Load(_boolType, _exec),
                            UInt(0x3F80_0000),
                            UInt(0));
                        StoreV(
                            240,
                            _debugTonemapExecOutput ? active : LoadV(10),
                            guardWithExec: false);
                        StoreV(
                            241,
                            _debugTonemapExecOutput ? active : LoadV(12),
                            guardWithExec: false);
                        StoreV(
                            242,
                            _debugTonemapExecOutput ? active : LoadV(9),
                            guardWithExec: false);
                    }
                }
                else if (_debugTonemapCurveOutput)
                {
                    // 0x538 is the first vector instruction after all three
                    // conditional curve branches restore EXEC at 0x52C. Take
                    // every component there so lanes that skipped a branch are
                    // represented by their pass-through value, not probe zero.
                    if (instruction.Pc == 0x538u)
                    {
                        StoreV(240, LoadV(0));
                        StoreV(241, LoadV(2));
                        StoreV(242, LoadV(1));
                    }
                }
                else if (_debugTonemapCoreOutput)
                {
                    CaptureTonemapCheckpoint(
                        instruction.Pc,
                        0x4ACu, 6,
                        0x4B0u, 5,
                        0x4B4u, 4);
                }
                else if (_debugTonemapGradeOutput)
                {
                    CaptureTonemapCheckpoint(
                        instruction.Pc,
                        0x634u, 10,
                        0x638u, 12,
                        0x63Cu, 9);
                }
                else if (_debugTonemapTransferOutput)
                {
                    CaptureTonemapCheckpoint(
                        instruction.Pc,
                        0x764u, 10,
                        0x768u, 12,
                        0x76Cu, 9);
                }
            }

            return emitted;
        }

        private void CaptureTonemapCheckpoint(
            uint pc,
            uint redPc,
            uint redRegister,
            uint greenPc,
            uint greenRegister,
            uint bluePc,
            uint blueRegister)
        {
            if (pc == redPc)
            {
                StoreV(240, LoadV(redRegister));
            }
            else if (pc == greenPc)
            {
                StoreV(241, LoadV(greenRegister));
            }
            else if (pc == bluePc)
            {
                StoreV(242, LoadV(blueRegister));
            }
        }

        private bool TryEmitDataShare(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Control is not Gen5DataShareControl control)
            {
                error = "invalid LDS instruction";
                return false;
            }

            if (control.Gds)
            {
                error = "GDS data share is not implemented";
                return false;
            }

            // The lane permutes are pure cross-lane moves: they never touch
            // LDS storage, so they are valid in every stage.
            if (instruction.Opcode is "DsPermuteB32" or "DsBpermuteB32")
            {
                return TryEmitDsLanePermute(instruction, control, out error);
            }

            if (_stage != Gen5SpirvStage.Compute ||
                _lds == 0 ||
                _workgroupUintPointer == 0)
            {
                error = "invalid LDS instruction";
                return false;
            }

            switch (instruction.Opcode)
            {
                case "DsWriteB32":
                {
                    if (instruction.Sources.Count < 2)
                    {
                        error = "missing LDS write source";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    StoreLds(
                        LdsPointer(address, DsByteOffset(control)),
                        GetRawSource(instruction, 1));
                    return true;
                }
                case "DsWriteB64":
                case "DsWriteB96":
                case "DsWriteB128":
                {
                    var dwords = instruction.Sources.Count - 1;
                    if (dwords < 2)
                    {
                        error = "missing LDS wide write source";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    var offset = DsByteOffset(control);
                    for (var index = 0; index < dwords; index++)
                    {
                        StoreLds(
                            LdsPointer(address, offset + (uint)index * sizeof(uint)),
                            GetRawSource(instruction, index + 1));
                    }

                    return true;
                }
                case "DsWriteAddtidB32":
                {
                    if (instruction.Sources.Count < 1)
                    {
                        error = "missing LDS addtid write source";
                        return false;
                    }

                    // ds_write_addtid_b32 carries no address operand: the LDS
                    // address is offset + 4 * flattened thread id.
                    StoreLds(
                        LdsPointer(
                            ShiftLeftLogical(FlattenedLocalInvocationIndex(), UInt(2)),
                            DsByteOffset(control)),
                        GetRawSource(instruction, 0));
                    return true;
                }
                case "DsWrite2B32":
                case "DsWrite2St64B32":
                {
                    if (instruction.Sources.Count < 3)
                    {
                        error = "missing LDS write2 source";
                        return false;
                    }

                    var st64 = instruction.Opcode == "DsWrite2St64B32";
                    var address = GetRawSource(instruction, 0);
                    StoreLds(
                        LdsPointer(
                            address,
                            EffectiveDsOffsetBytes(control.Offset0, st64)),
                        GetRawSource(instruction, 1));
                    StoreLds(
                        LdsPointer(
                            address,
                            EffectiveDsOffsetBytes(control.Offset1, st64)),
                        GetRawSource(instruction, 2));
                    return true;
                }
                case "DsReadB32":
                case "DsReadB64":
                case "DsReadB96":
                case "DsReadB128":
                {
                    if (instruction.Destinations.Count < 1 ||
                        instruction.Sources.Count < 1)
                    {
                        error = "missing LDS read operand";
                        return false;
                    }

                    var address = GetRawSource(instruction, 0);
                    var offset = DsByteOffset(control);
                    for (var index = 0; index < instruction.Destinations.Count; index++)
                    {
                        var value = Load(
                            _uintType,
                            LdsPointer(address, offset + (uint)index * sizeof(uint)));
                        StoreV(instruction.Destinations[index].Value, value);
                    }

                    return true;
                }
                case "DsRead2B32":
                case "DsRead2St64B32":
                {
                    if (instruction.Destinations.Count < 2 ||
                        instruction.Sources.Count < 1)
                    {
                        error = "missing LDS read2 operand";
                        return false;
                    }

                    var st64 = instruction.Opcode == "DsRead2St64B32";
                    var address = GetRawSource(instruction, 0);
                    var first = Load(
                        _uintType,
                        LdsPointer(
                            address,
                            EffectiveDsOffsetBytes(control.Offset0, st64)));
                    var second = Load(
                        _uintType,
                        LdsPointer(
                            address,
                            EffectiveDsOffsetBytes(control.Offset1, st64)));
                    StoreV(instruction.Destinations[0].Value, first);
                    StoreV(instruction.Destinations[1].Value, second);
                    return true;
                }
                default:
                    if (Gen5ShaderTranslator.IsDataShareAtomic(instruction.Opcode))
                    {
                        return TryEmitDataShareAtomic(instruction, control, out error);
                    }

                    error = $"unsupported LDS opcode {instruction.Opcode}";
                    return false;
            }
        }

        private bool TryEmitDataShareAtomic(
            Gen5ShaderInstruction instruction,
            Gen5DataShareControl control,
            out string error)
        {
            error = string.Empty;
            var atomicOp = instruction.Opcode switch
            {
                "DsAddU32" or "DsAddRtnU32" => SpirvOp.AtomicIAdd,
                "DsSubU32" or "DsSubRtnU32" => SpirvOp.AtomicISub,
                "DsIncU32" or "DsIncRtnU32" => SpirvOp.AtomicIIncrement,
                "DsDecU32" or "DsDecRtnU32" => SpirvOp.AtomicIDecrement,
                "DsMinI32" or "DsMinRtnI32" => SpirvOp.AtomicSMin,
                "DsMaxI32" or "DsMaxRtnI32" => SpirvOp.AtomicSMax,
                "DsMinU32" or "DsMinRtnU32" => SpirvOp.AtomicUMin,
                "DsMaxU32" or "DsMaxRtnU32" => SpirvOp.AtomicUMax,
                "DsAndB32" or "DsAndRtnB32" => SpirvOp.AtomicAnd,
                "DsOrB32" or "DsOrRtnB32" => SpirvOp.AtomicOr,
                "DsXorB32" or "DsXorRtnB32" => SpirvOp.AtomicXor,
                "DsWrxchgRtnB32" => SpirvOp.AtomicExchange,
                "DsCmpstB32" or "DsCmpstRtnB32" => SpirvOp.AtomicCompareExchange,
                _ => SpirvOp.Nop,
            };
            if (atomicOp == SpirvOp.Nop)
            {
                error = $"unsupported LDS opcode {instruction.Opcode}";
                return false;
            }

            var address = GetRawSource(instruction, 0);
            var pointer = LdsPointer(address, control.Offset0);
            EmitExecConditional(() =>
            {
                var original = EmitAtomic(
                    atomicOp,
                    _uintType,
                    pointer,
                    scope: 2,
                    semantics: 0x108,
                    value: () => GetRawSource(
                        instruction,
                        atomicOp == SpirvOp.AtomicCompareExchange ? 2 : 1),
                    comparator: () => GetRawSource(instruction, 1));
                if (instruction.Destinations.Count > 0)
                {
                    StoreV(instruction.Destinations[0].Value, original);
                }
            });

            return true;
        }

        private bool TryEmitDsLanePermute(
            Gen5ShaderInstruction instruction,
            Gen5DataShareControl control,
            out string error)
        {
            error = string.Empty;
            if (instruction.Destinations.Count < 1 || instruction.Sources.Count < 2)
            {
                error = $"missing lane-permute operand for {instruction.Opcode}";
                return false;
            }

            // ds_permute_b32 / ds_bpermute_b32 index lanes with a byte
            // address: lane = ((addr + offset) >> 2) & (wave - 1). The
            // 16-bit instruction offset spans both offset fields.
            var data = GetRawSource(instruction, 1);
            var destination = instruction.Destinations[0].Value;
            if (_subgroupInvocationIdInput == 0)
            {
                // Single-lane fallback: every permutation is the identity.
                StoreV(destination, data);
                return true;
            }

            var address = GetRawSource(instruction, 0);
            var offset = (control.Offset0 | (control.Offset1 << 8)) & 0xFFFFu;
            if (offset != 0)
            {
                address = IAdd(address, UInt(offset));
            }

            var targetLane = BitwiseAnd(
                ShiftRightLogical(address, UInt(2)),
                UInt(RdnaWaveLaneCount - 1));
            uint result;
            if (instruction.Opcode == "DsBpermuteB32")
            {
                // Backward permute is a pull: D[i] = data[lane(addr_i)].
                result = _module.AddInstruction(
                    SpirvOp.GroupNonUniformShuffle,
                    _uintType,
                    UInt(3),
                    data,
                    targetLane);
            }
            else
            {
                // Forward permute is a push: D[lane(addr_j)] = data[j]. Invert
                // it by scanning every wave lane with constant-id shuffles and
                // keeping the value whose EXEC-active writer targets this
                // lane. Lanes nobody targets read 0, matching hardware.
                var myLane = BitwiseAnd(
                    Load(_uintType, _subgroupInvocationIdInput),
                    UInt(RdnaWaveLaneCount - 1));
                var activeFlag = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    Load(_boolType, _exec),
                    UInt(1),
                    UInt(0));
                result = UInt(0);
                for (uint lane = 0; lane < RdnaWaveLaneCount; lane++)
                {
                    var writerTarget = _module.AddInstruction(
                        SpirvOp.GroupNonUniformShuffle,
                        _uintType,
                        UInt(3),
                        targetLane,
                        UInt(lane));
                    var writerActive = _module.AddInstruction(
                        SpirvOp.GroupNonUniformShuffle,
                        _uintType,
                        UInt(3),
                        activeFlag,
                        UInt(lane));
                    var writerData = _module.AddInstruction(
                        SpirvOp.GroupNonUniformShuffle,
                        _uintType,
                        UInt(3),
                        data,
                        UInt(lane));
                    var writesHere = _module.AddInstruction(
                        SpirvOp.LogicalAnd,
                        _boolType,
                        _module.AddInstruction(
                            SpirvOp.IEqual,
                            _boolType,
                            writerTarget,
                            myLane),
                        IsNotZero(writerActive));
                    result = _module.AddInstruction(
                        SpirvOp.Select,
                        _uintType,
                        writesHere,
                        writerData,
                        result);
                }
            }

            StoreV(destination, result);
            return true;
        }

        private static uint EffectiveDsOffsetBytes(uint offset, bool st64 = false) =>
            offset * (st64 ? 256u : sizeof(uint));

        // Single-address DS operations use {offset1, offset0} as one unsigned
        // 16-bit byte offset; only the two-address read2/write2 forms scale
        // their per-slot offsets by the element size.
        private static uint DsByteOffset(Gen5DataShareControl control) =>
            control.Offset0 | (control.Offset1 << 8);

        // The wave-relative thread id used by the addtid LDS addressing mode,
        // reconstructed as the flattened local invocation index.
        private uint FlattenedLocalInvocationIndex()
        {
            var localId = Load(_uvec3Type, _localInvocationIdInput);
            var x = _module.AddInstruction(
                SpirvOp.CompositeExtract, _uintType, localId, 0);
            var y = _module.AddInstruction(
                SpirvOp.CompositeExtract, _uintType, localId, 1);
            var z = _module.AddInstruction(
                SpirvOp.CompositeExtract, _uintType, localId, 2);
            var rows = _module.AddInstruction(
                SpirvOp.IMul,
                _uintType,
                IAdd(y, _module.AddInstruction(
                    SpirvOp.IMul, _uintType, z, UInt(_localSizeY))),
                UInt(_localSizeX));
            return IAdd(x, rows);
        }

        private uint LdsPointer(uint address, uint offsetBytes)
        {
            var addressWithOffset = offsetBytes == 0
                ? address
                : IAdd(address, UInt(offsetBytes));
            var index = ShiftRightLogical(addressWithOffset, UInt(2));
            return _module.AddInstruction(
                SpirvOp.AccessChain,
                _workgroupUintPointer,
                _lds,
                index);
        }

        private void StoreLds(uint pointer, uint value)
        {
            var active = Load(_boolType, _exec);
            var oldValue = Load(_uintType, pointer);
            var selected = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                active,
                value,
                oldValue);
            Store(pointer, selected);
        }

        private bool TryEmitInterpolation(
            Gen5ShaderInstruction instruction,
            Gen5InterpolationControl interpolation,
            out string error)
        {
            error = string.Empty;
            if (_stage != Gen5SpirvStage.Pixel ||
                !_pixelInputs.TryGetValue(interpolation.Attribute, out var input) ||
                !TryGetVectorDestination(instruction, out var destination))
            {
                error = "invalid interpolated attribute";
                return false;
            }

            var vector = Load(_vec4Type, input);
            var component = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                vector,
                interpolation.Channel);
            if (!_interpolationMoveAttributes.Contains(interpolation.Attribute))
            {
                StoreV(destination, Bitcast(_uintType, component));
                return true;
            }

            if (instruction.Opcode == "VInterpMovF32" &&
                IsCustomInterpolation(GetPixelInputControl(interpolation.Attribute)))
            {
                // Custom interpolation carries packed payloads whose bits must
                // not be reconstructed through floating-point derivatives.
                // Until the backend exposes all three per-vertex parameters,
                // use the exact provoking-vertex word for every selector. The
                // guest's manual interpolation then collapses to a stable,
                // faceted value instead of unpacking derivative noise.
                StoreV(destination, Bitcast(_uintType, component));
                return true;
            }

            if (!_interpolationCoefficients.TryGetValue(
                    (interpolation.Attribute, interpolation.Channel),
                    out var coefficients))
            {
                error =
                    $"missing interpolation coefficients for attribute " +
                    $"{interpolation.Attribute} channel {interpolation.Channel}";
                return false;
            }

            uint result;
            if (instruction.Opcode == "VInterpMovF32")
            {
                if (instruction.Sources.Count == 0)
                {
                    error = "interpolation move is missing its P10/P20/P0 selector";
                    return false;
                }

                result = instruction.Sources[0].Value switch
                {
                    0 => coefficients.P10,
                    1 => coefficients.P20,
                    2 => coefficients.P0,
                    _ => 0,
                };
                if (result == 0)
                {
                    error =
                        $"invalid interpolation move selector {instruction.Sources[0].Value}";
                    return false;
                }
            }
            else
            {
                if (instruction.Sources.Count == 0 ||
                    instruction.Sources[0].Kind != Gen5OperandKind.VectorRegister)
                {
                    error = "interpolation instruction is missing its barycentric source";
                    return false;
                }

                var barycentric = Bitcast(
                    _floatType,
                    LoadV(instruction.Sources[0].Value));
                if (instruction.Opcode == "VInterpP1F32")
                {
                    result = _module.AddInstruction(
                        SpirvOp.FAdd,
                        _floatType,
                        coefficients.P0,
                        _module.AddInstruction(
                            SpirvOp.FMul,
                            _floatType,
                            coefficients.P10,
                            barycentric));
                }
                else if (instruction.Opcode == "VInterpP2F32")
                {
                    result = _module.AddInstruction(
                        SpirvOp.FAdd,
                        _floatType,
                        Bitcast(_floatType, LoadV(destination)),
                        _module.AddInstruction(
                            SpirvOp.FMul,
                            _floatType,
                            coefficients.P20,
                            barycentric));
                }
                else
                {
                    error = $"unsupported interpolation opcode {instruction.Opcode}";
                    return false;
                }
            }

            StoreV(destination, Bitcast(_uintType, result));
            return true;
        }

        private bool TryEmitInterpolationCoefficientState(out string error)
        {
            error = string.Empty;
            if (_stage != Gen5SpirvStage.Pixel ||
                _interpolationMoveAttributes.Count == 0)
            {
                return true;
            }

            // Derivatives are only defined in uniform control flow. The guest
            // shader body runs inside a PC dispatcher whose cases can diverge,
            // so reconstruct every coefficient once in the entry block and
            // let V_INTERP instructions read the dominating SSA values.
            var controls = _state.Program.Instructions
                .Where(static instruction => instruction.Opcode == "VInterpMovF32")
                .Select(static instruction =>
                    (Gen5InterpolationControl)instruction.Control!)
                .Select(static control => (control.Attribute, control.Channel))
                .Distinct()
                .OrderBy(static control => control.Attribute)
                .ThenBy(static control => control.Channel);
            foreach (var (attribute, channel) in controls)
            {
                if (IsCustomInterpolation(GetPixelInputControl(attribute)))
                {
                    continue;
                }

                if (!TryCreateInterpolationCoefficients(
                        attribute,
                        channel,
                        out var coefficients,
                        out error))
                {
                    return false;
                }

                _interpolationCoefficients.Add((attribute, channel), coefficients);
            }

            return true;
        }

        private bool TryCreateInterpolationCoefficients(
            uint attribute,
            uint channel,
            out SpirvInterpolationCoefficients coefficients,
            out string error)
        {
            coefficients = default;
            error = string.Empty;
            if (!_pixelInputs.TryGetValue(attribute, out var input))
            {
                error = $"invalid interpolated attribute {attribute}";
                return false;
            }

            if (_baryCoordInput == 0)
            {
                error = "interpolation move requires a barycentric pixel input";
                return false;
            }

            // MoltenVK exposes VK_KHR_fragment_shader_barycentric but its
            // SPIR-V-to-MSL path cannot consume PerVertexKHR inputs. Rebuild
            // the hardware interpolation coefficients from the ordinary
            // interpolant and the derivatives of its barycentric weights:
            //
            //   F = P0 + P10*I + P20*J
            //
            // Differentiating in screen X/Y yields a 2x2 system for P10/P20;
            // P0 then follows from F. Native Vulkan drivers use the same
            // standard SPIR-V path.
            var vector = Load(_vec4Type, input);
            var component = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                vector,
                channel);
            var barycentrics = Load(_vec3Type, _baryCoordInput);
            var i = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                barycentrics,
                1);
            var j = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                barycentrics,
                2);
            uint Binary(SpirvOp op, uint left, uint right) =>
                _module.AddInstruction(op, _floatType, left, right);
            uint Derivative(SpirvOp op, uint value) =>
                _module.AddInstruction(op, _floatType, value);

            var dFdx = Derivative(SpirvOp.DPdx, component);
            var dFdy = Derivative(SpirvOp.DPdy, component);
            var dIdx = Derivative(SpirvOp.DPdx, i);
            var dIdy = Derivative(SpirvOp.DPdy, i);
            var dJdx = Derivative(SpirvOp.DPdx, j);
            var dJdy = Derivative(SpirvOp.DPdy, j);
            var determinant = Binary(
                SpirvOp.FSub,
                Binary(SpirvOp.FMul, dIdx, dJdy),
                Binary(SpirvOp.FMul, dIdy, dJdx));
            var p10 = Binary(
                SpirvOp.FDiv,
                Binary(
                    SpirvOp.FSub,
                    Binary(SpirvOp.FMul, dFdx, dJdy),
                    Binary(SpirvOp.FMul, dFdy, dJdx)),
                determinant);
            var p20 = Binary(
                SpirvOp.FDiv,
                Binary(
                    SpirvOp.FSub,
                    Binary(SpirvOp.FMul, dIdx, dFdy),
                    Binary(SpirvOp.FMul, dIdy, dFdx)),
                determinant);
            var p0 = Binary(
                SpirvOp.FSub,
                Binary(
                    SpirvOp.FSub,
                    component,
                    Binary(SpirvOp.FMul, p10, i)),
                Binary(SpirvOp.FMul, p20, j));
            coefficients = new SpirvInterpolationCoefficients(p0, p10, p20);
            return true;
        }

        private bool TryEmitScalarMemory(
            Gen5ShaderInstruction instruction,
            Gen5ScalarMemoryControl control,
            out string error)
        {
            error = string.Empty;
            if (!_bufferBindingByPc.TryGetValue(instruction.Pc, out var bindingIndex))
            {
                // SHARPEMU_KEEP_UNBOUND_SMEM=1: a later unbound duplicate scalar
                // load can clobber a valid earlier bound load into the same
                // registers (e.g. a shader reloading a constant/color-grade slot).
                // Leaving the registers untouched preserves the earlier value.
                if (_keepUnboundSmem)
                {
                    return true;
                }

                foreach (var destination in instruction.Destinations)
                {
                    if (destination.Kind == Gen5OperandKind.ScalarRegister)
                    {
                        StoreS(destination.Value, UInt(0));
                    }
                }

                return true;
            }

            // SHARPEMU_PS_FORCE_EXPOSURE_SCALAR: pin the exposure-scale scalar of
            // the ts/composite shader (its S_BUFFER_LOAD reads the cbuffer at
            // descriptor + NULL SOFFSET + 116 bytes) to float 1.0 so a zero exposure
            // value cannot multiply the tonemap output to black.
            var forceExposureScalar =
                _forceExposureScalar &&
                _stage == Gen5SpirvStage.Pixel &&
                (_pixelShaderAddress == ExposureScalarShaderAddress ||
                 _pixelShaderAddress == RelocatedExposureScalarShaderAddress) &&
                control.DynamicOffsetRegister is null &&
                control.ImmediateOffsetBytes == ExposureScalarOffsetBytes;

            var dynamicOffset = control.DynamicOffsetRegister is { } register
                ? LoadS(register)
                : UInt(0);
            var byteAddress = IAdd(
                dynamicOffset,
                UInt(unchecked((uint)control.ImmediateOffsetBytes)));
            var dwordAddress = ShiftRightLogical(byteAddress, UInt(2));
            for (var index = 0; index < instruction.Destinations.Count; index++)
            {
                var destination = instruction.Destinations[index];
                if (destination.Kind != Gen5OperandKind.ScalarRegister)
                {
                    error = "invalid scalar-memory destination";
                    return false;
                }

                var address = index == 0
                    ? dwordAddress
                    : IAdd(dwordAddress, UInt((uint)index));
                var value = forceExposureScalar && index == 0
                    ? UInt(_forceExposureScalarBits)
                    : LoadBufferWord(bindingIndex, address);
                StoreS(destination.Value, value);
            }

            return true;
        }

        private bool TryEmitGlobalMemory(
            Gen5ShaderInstruction instruction,
            Gen5GlobalMemoryControl control,
            out string error)
        {
            error = string.Empty;
            if (!_bufferBindingByPc.TryGetValue(instruction.Pc, out var bindingIndex))
            {
                error = "missing global-memory binding";
                return false;
            }

            var byteAddress = IAdd(
                LoadV(control.VectorAddress),
                UInt(unchecked((uint)control.OffsetBytes)));
            var dwordAddress = ShiftRightLogical(byteAddress, UInt(2));
            for (uint index = 0; index < control.DwordCount; index++)
            {
                var address = index == 0
                    ? dwordAddress
                    : IAdd(dwordAddress, UInt(index));
                StoreV(
                    control.VectorData + index,
                    LoadBufferWord(bindingIndex, address));
            }

            return true;
        }

        private bool TryEmitBufferMemory(
            Gen5ShaderInstruction instruction,
            Gen5BufferMemoryControl control,
            out string error)
        {
            error = string.Empty;
            if (_stage == Gen5SpirvStage.Vertex &&
                _vertexInputsByPc.TryGetValue(instruction.Pc, out var vertexInput))
            {
                return TryEmitVertexInputFetch(control, vertexInput, out error);
            }

            if (_stage == Gen5SpirvStage.Vertex &&
                IsFormatBufferLoad(instruction.Opcode))
            {
                error = $"missing vertex input for {instruction.Opcode} pc=0x{instruction.Pc:X}";
                return false;
            }

            if (!_bufferBindingByPc.TryGetValue(instruction.Pc, out var bindingIndex))
            {
                error = "missing buffer-memory binding";
                return false;
            }

            var scalarOffset = instruction.Sources.Count > 2
                ? GetRawSource(instruction, 2)
                : UInt(0);
            var stride = ShiftRightLogical(LoadS(control.ScalarResource + 1), UInt(16));
            stride = BitwiseAnd(stride, UInt(0x3FFF));
            var vectorIndex = control.IndexEnabled
                ? LoadV(control.VectorAddress)
                : UInt(0);
            var vectorOffset = control.OffsetEnabled
                ? LoadV(control.VectorAddress + (control.IndexEnabled ? 1u : 0u))
                : UInt(0);
            var byteAddress = IAdd(
                UInt(unchecked((uint)control.OffsetBytes)),
                scalarOffset);
            byteAddress = IAdd(byteAddress, vectorOffset);
            byteAddress = IAdd(
                byteAddress,
                _module.AddInstruction(SpirvOp.IMul, _uintType, vectorIndex, stride));
            var dwordAddress = ShiftRightLogical(byteAddress, UInt(2));

            if (instruction.Opcode is
                "BufferLoadUbyte" or
                "BufferLoadSbyte" or
                "BufferLoadUshort" or
                "BufferLoadSshort")
            {
                // Sub-dword loads read the containing word and extract the
                // naturally aligned byte/short lane by byte offset.
                var word = LoadBufferWord(bindingIndex, dwordAddress);
                var bitOffset = ShiftLeftLogical(
                    BitwiseAnd(byteAddress, UInt(3)),
                    UInt(3));
                var width = instruction.Opcode is "BufferLoadUbyte" or "BufferLoadSbyte"
                    ? 8u
                    : 16u;
                var signed = instruction.Opcode is "BufferLoadSbyte" or "BufferLoadSshort";
                var value = _module.AddInstruction(
                    signed ? SpirvOp.BitFieldSExtract : SpirvOp.BitFieldUExtract,
                    _uintType,
                    word,
                    bitOffset,
                    UInt(width));
                StoreV(control.VectorData, value);
                return true;
            }

            if (instruction.Opcode.StartsWith("BufferAtomic", StringComparison.Ordinal))
            {
                if (!TryGetAtomicOp(instruction.Opcode["BufferAtomic".Length..], out var atomicOp))
                {
                    error = $"unsupported buffer opcode {instruction.Opcode}";
                    return false;
                }

                EmitExecConditional(() =>
                {
                    var original = EmitAtomic(
                        atomicOp,
                        _uintType,
                        BufferWordPointer(bindingIndex, dwordAddress),
                        scope: 1,
                        semantics: 0x48,
                        value: () => LoadV(control.VectorData),
                        comparator: () => LoadV(control.VectorData + 1));
                    if (control.Glc)
                    {
                        StoreV(control.VectorData, original);
                    }
                });

                return true;
            }

            // BufferStoreDword* and BufferStoreFormat* both write control.DwordCount
            // raw words; for float32 formats (the vertex/args case) the format
            // store is byte-identical to a raw dword store.
            if (instruction.Opcode.StartsWith("BufferStoreDword", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("BufferStoreFormat", StringComparison.Ordinal))
            {
                EmitExecConditional(() =>
                {
                    for (uint index = 0; index < control.DwordCount; index++)
                    {
                        var address = index == 0
                            ? dwordAddress
                            : IAdd(dwordAddress, UInt(index));
                        StoreBufferWord(
                            bindingIndex,
                            address,
                            LoadV(control.VectorData + index));
                    }
                });

                return true;
            }

            if (!instruction.Opcode.StartsWith("BufferLoad", StringComparison.Ordinal) &&
                !instruction.Opcode.StartsWith("TBufferLoad", StringComparison.Ordinal))
            {
                error = $"unsupported buffer opcode {instruction.Opcode}";
                return false;
            }

            for (uint index = 0; index < control.DwordCount; index++)
            {
                var address = index == 0
                    ? dwordAddress
                    : IAdd(dwordAddress, UInt(index));
                StoreV(
                    control.VectorData + index,
                    LoadBufferWord(bindingIndex, address));
            }

            return true;
        }

        private static bool IsFormatBufferLoad(string opcode) =>
            opcode.StartsWith("BufferLoadFormat", StringComparison.Ordinal) ||
            opcode.StartsWith("TBufferLoadFormat", StringComparison.Ordinal);

        private bool TryEmitVertexInputFetch(
            Gen5BufferMemoryControl control,
            SpirvVertexInput input,
            out string error)
        {
            error = string.Empty;
            if (control.DwordCount == 0 ||
                control.DwordCount > input.ComponentCount)
            {
                error =
                    $"invalid vertex input fetch components={control.DwordCount} " +
                    $"input={input.ComponentCount}";
                return false;
            }

            var loaded = Load(input.Type, input.Variable);
            for (uint component = 0; component < control.DwordCount; component++)
            {
                var value = input.ComponentCount == 1
                    ? loaded
                    : _module.AddInstruction(
                        SpirvOp.CompositeExtract,
                        input.ComponentType,
                        loaded,
                        component);
                var raw = input.ComponentKind == VertexInputComponentKind.Uint
                    ? value
                    : Bitcast(_uintType, value);
                StoreV(control.VectorData + component, raw);
            }

            return true;
        }

        private bool TryEmitImage(
            Gen5ShaderInstruction instruction,
            Gen5ImageControl image,
            out string error)
        {
            error = string.Empty;
            if (!_imageBindingByPc.TryGetValue(instruction.Pc, out var bindingIndex) ||
                bindingIndex >= _imageResources.Count)
            {
                // Last resort: sample a type-compatible bound image instead of
                // failing the translation and dropping the whole draw.
                var wantsStorage =
                    Gen5ShaderTranslator.IsStorageImageOperation(instruction.Opcode);
                bindingIndex = _imageResources.FindIndex(
                    resource => resource.IsStorage == wantsStorage);
                if (bindingIndex < 0)
                {
                    error = "unresolved image binding";
                    return false;
                }
            }

            var resource = _imageResources[bindingIndex];
            var imageObject = Load(resource.ObjectType, resource.Variable);
            if (instruction.Opcode == "ImageGetResinfo")
            {
                var queryImage = resource.IsStorage
                    ? imageObject
                    : _module.AddInstruction(
                        SpirvOp.Image,
                        resource.ImageType,
                        imageObject);
                var size = _module.AddInstruction(
                    resource.IsStorage
                        ? SpirvOp.ImageQuerySize
                        : SpirvOp.ImageQuerySizeLod,
                    _module.TypeVector(_intType, 2),
                    resource.IsStorage
                        ? [queryImage]
                        : [queryImage, UInt(0)]);
                uint outputIndex = 0;
                for (uint component = 0; component < 4; component++)
                {
                    if ((image.Dmask & (1u << (int)component)) == 0)
                    {
                        continue;
                    }

                    uint value;
                    if (component < 2)
                    {
                        var signedValue = _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            _intType,
                            size,
                            component);
                        value = Bitcast(_uintType, signedValue);
                    }
                    else
                    {
                        value = UInt(1);
                    }

                    StoreV(image.VectorData + outputIndex++, value);
                }

                return true;
            }

            if (instruction.Opcode is "ImageStore" or "ImageStoreMip")
            {
                if (!resource.IsStorage)
                {
                    error = "image store is not bound as storage";
                    return false;
                }

                var coordinates = BuildIntegerCoordinates(image, 0);
                var components = new uint[4];
                uint sourceIndex = 0;
                for (var component = 0; component < components.Length; component++)
                {
                    if ((image.Dmask & (1u << component)) != 0)
                    {
                        var raw = LoadV(image.VectorData + sourceIndex++);
                        components[component] = resource.ComponentKind switch
                        {
                            ImageComponentKind.Sint => Bitcast(_intType, raw),
                            ImageComponentKind.Uint => raw,
                            _ => Bitcast(_floatType, raw),
                        };
                    }
                    else
                    {
                        components[component] = resource.ComponentKind switch
                        {
                            ImageComponentKind.Sint =>
                                _module.Constant(_intType, 0),
                            ImageComponentKind.Uint => UInt(0),
                            _ => Float(0),
                        };
                    }
                }

                var texel = _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    resource.VectorType,
                    components);
                if (TryGetImageBounds(
                        _evaluation.ImageBindings[bindingIndex].ResourceDescriptor,
                        out var width,
                        out var height))
                {
                    EmitBoundsCheckedImageWrite(
                        coordinates,
                        width,
                        height,
                        imageObject,
                        texel);
                }
                else
                {
                    EmitExecConditional(
                        () => _module.AddStatement(
                            SpirvOp.ImageWrite,
                            imageObject,
                            coordinates,
                            texel));
                }

                return true;
            }

            if (instruction.Opcode.StartsWith("ImageAtomic", StringComparison.Ordinal))
            {
                if (!resource.IsStorage)
                {
                    error = "image atomic is not bound as storage";
                    return false;
                }

                if (resource.ComponentKind == ImageComponentKind.Float ||
                    !TryGetAtomicOp(instruction.Opcode["ImageAtomic".Length..], out var atomicOp))
                {
                    error = $"unsupported image atomic opcode {instruction.Opcode}";
                    return false;
                }

                var signed = resource.ComponentKind == ImageComponentKind.Sint;
                var atomicCoordinates = TryGetImageBounds(
                        _evaluation.ImageBindings[bindingIndex].ResourceDescriptor,
                        out var atomicWidth,
                        out var atomicHeight)
                    ? BuildClampedIntegerCoordinates(image, 0, atomicWidth, atomicHeight)
                    : BuildIntegerCoordinates(image, 0);
                EmitExecConditional(() =>
                {
                    var pointer = _module.AddInstruction(
                        SpirvOp.ImageTexelPointer,
                        _module.TypePointer(SpirvStorageClass.Image, resource.ComponentType),
                        resource.Variable,
                        atomicCoordinates,
                        UInt(0));
                    uint LoadData(uint register) => signed
                        ? Bitcast(_intType, LoadV(register))
                        : LoadV(register);
                    var original = EmitAtomic(
                        atomicOp,
                        resource.ComponentType,
                        pointer,
                        scope: 1,
                        semantics: 0x808,
                        value: () => LoadData(image.VectorData),
                        // image_atomic_cmpswap packs src into vdata[0] and the
                        // comparand into vdata[1].
                        comparator: () => LoadData(image.VectorData + 1));
                    if (image.Glc)
                    {
                        StoreV(
                            image.VectorData,
                            signed ? Bitcast(_uintType, original) : original);
                    }
                });

                return true;
            }

            if (resource.IsStorage &&
                instruction.Opcode is not ("ImageLoad" or "ImageLoadMip"))
            {
                error = $"unsupported storage image opcode {instruction.Opcode}";
                return false;
            }

            uint sampled;
            var writeAllComponents = false;
            if (instruction.Opcode is "ImageLoad" or "ImageLoadMip")
            {
                var coordinates = TryGetImageBounds(
                        _evaluation.ImageBindings[bindingIndex].ResourceDescriptor,
                        out var width,
                        out var height)
                    ? BuildClampedIntegerCoordinates(image, 0, width, height)
                    : BuildIntegerCoordinates(image, 0);
                if (resource.IsStorage)
                {
                    // ImageLoad binds as a storage image (the same descriptor
                    // can also be stored to); read the texels directly.
                    sampled = _module.AddInstruction(
                        SpirvOp.ImageRead,
                        resource.VectorType,
                        imageObject,
                        coordinates);
                }
                else
                {
                    var mipLevel = _evaluation.ImageBindings[bindingIndex].MipLevel ?? 0;
                    var fetchedImage = _module.AddInstruction(
                        SpirvOp.Image,
                        resource.ImageType,
                        imageObject);
                    sampled = _module.AddInstruction(
                        SpirvOp.ImageFetch,
                        resource.VectorType,
                        fetchedImage,
                        coordinates,
                        2,
                        UInt(mipLevel));
                }
            }
            else if (instruction.Opcode.StartsWith(
                         "ImageSample",
                         StringComparison.Ordinal))
            {
                var hasOffset =
                    instruction.Opcode.EndsWith("O", StringComparison.Ordinal);
                var hasCompare =
                    instruction.Opcode.Contains("SampleC", StringComparison.Ordinal);
                var start = (hasOffset ? 1 : 0) + (hasCompare ? 1 : 0);
                var coordinates = BuildFloatCoordinates(image, start);
                var explicitLod =
                    instruction.Opcode.Contains("Lz", StringComparison.Ordinal) ||
                    instruction.Opcode.Contains("SampleL", StringComparison.Ordinal);
                var lod = instruction.Opcode.Contains("Lz", StringComparison.Ordinal)
                    ? Float(0)
                    : Bitcast(
                        _floatType,
                        LoadV(image.GetAddressRegister(start + 2)));
                var offset = hasOffset ? BuildImageOffset(image, 0) : 0u;
                var imageOperands =
                    (explicitLod ? 2u : 0u) | (hasOffset ? 0x10u : 0u);
                var reference = hasCompare
                    ? Bitcast(_floatType, LoadV(image.GetAddressRegister(hasOffset ? 1 : 0)))
                    : 0u;
                var operands = new List<uint>
                {
                    imageObject,
                    coordinates,
                };

                if (imageOperands != 0)
                {
                    operands.Add(imageOperands);
                    if (explicitLod)
                    {
                        operands.Add(lod);
                    }

                    if (hasOffset)
                    {
                        operands.Add(offset);
                    }
                }

                sampled = _module.AddInstruction(
                    explicitLod
                        ? SpirvOp.ImageSampleExplicitLod
                        : SpirvOp.ImageSampleImplicitLod,
                    resource.VectorType,
                    [.. operands]);
                if (hasCompare)
                {
                    sampled = EmitManualDepthCompare(resource, sampled, reference);
                }
            }
            else if (instruction.Opcode.StartsWith(
                         "ImageGather4",
                         StringComparison.Ordinal))
            {
                var hasOffset =
                    instruction.Opcode.EndsWith("O", StringComparison.Ordinal);
                var hasCompare =
                    instruction.Opcode.Contains("Gather4C", StringComparison.Ordinal);
                var start = (hasOffset ? 1 : 0) + (hasCompare ? 1 : 0);
                var coordinates = BuildFloatCoordinates(image, start);
                var offset = hasOffset ? BuildImageOffset(image, 0) : 0u;
                var reference = hasCompare
                    ? Bitcast(_floatType, LoadV(image.GetAddressRegister(hasOffset ? 1 : 0)))
                    : 0u;
                var operands = new List<uint>
                {
                    imageObject,
                    coordinates,
                };
                if (hasCompare)
                {
                    operands.Add(UInt(0));
                }
                else
                {
                    uint component = 0;
                    while (component < 3 &&
                           (image.Dmask & (1u << (int)component)) == 0)
                    {
                        component++;
                    }

                    operands.Add(UInt(component));
                }

                if (hasOffset)
                {
                    operands.Add(0x10u);
                    operands.Add(offset);
                }

                sampled = _module.AddInstruction(
                    SpirvOp.ImageGather,
                    resource.VectorType,
                    [.. operands]);
                if (hasCompare)
                {
                    var compared = new uint[4];
                    for (var component = 0u; component < 4; component++)
                    {
                        var texel = _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            resource.ComponentType,
                            sampled,
                            component);
                        compared[component] = EmitDepthCompareScalar(resource, texel, reference);
                    }

                    sampled = _module.AddInstruction(
                        SpirvOp.CompositeConstruct,
                        resource.VectorType,
                        compared);
                }

                writeAllComponents = true;
            }
            else
            {
                error = $"unsupported image opcode {instruction.Opcode}";
                return false;
            }

            uint output = 0;
            for (uint component = 0; component < 4; component++)
            {
                if (!writeAllComponents &&
                    (image.Dmask & (1u << (int)component)) == 0)
                {
                    continue;
                }

                var value = _module.AddInstruction(
                    SpirvOp.CompositeExtract,
                    resource.ComponentType,
                    sampled,
                    component);
                var raw = resource.ComponentKind switch
                {
                    ImageComponentKind.Uint => value,
                    _ => Bitcast(_uintType, value),
                };
                StoreV(image.VectorData + output++, raw);
                if (_debugTonemapSampleOutput &&
                    IsKnownTonemapShader() &&
                    instruction.Pc == 0x2Cu &&
                    output <= 3)
                {
                    StoreV(240u + output - 1u, raw);
                }
            }

            return true;
        }

        private uint EmitDepthCompareScalar(
            SpirvImageResource resource,
            uint texel,
            uint reference)
        {
            var texelAsFloat = resource.ComponentKind switch
            {
                ImageComponentKind.Uint => _module.AddInstruction(
                    SpirvOp.ConvertUToF, _floatType, texel),
                ImageComponentKind.Sint => _module.AddInstruction(
                    SpirvOp.ConvertSToF, _floatType, texel),
                _ => texel,
            };
            var passes = _module.AddInstruction(
                SpirvOp.FOrdLessThanEqual,
                _boolType,
                reference,
                texelAsFloat);
            return _module.AddInstruction(
                SpirvOp.Select,
                resource.ComponentType,
                passes,
                resource.ComponentKind switch
                {
                    ImageComponentKind.Uint => UInt(1),
                    ImageComponentKind.Sint => _module.Constant(_intType, 1),
                    _ => Float(1),
                },
                resource.ComponentKind switch
                {
                    ImageComponentKind.Uint => UInt(0),
                    ImageComponentKind.Sint => _module.Constant(_intType, 0),
                    _ => Float(0),
                });
        }

        private uint EmitManualDepthCompare(
            SpirvImageResource resource,
            uint sampledVector,
            uint reference)
        {
            var texel = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                resource.ComponentType,
                sampledVector,
                0u);
            var scalar = EmitDepthCompareScalar(resource, texel, reference);
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                resource.VectorType,
                scalar,
                scalar,
                scalar,
                resource.ComponentKind switch
                {
                    ImageComponentKind.Uint => UInt(1),
                    ImageComponentKind.Sint => _module.Constant(_intType, 1),
                    _ => Float(1),
                });
        }

        private uint BuildFloatCoordinates(Gen5ImageControl image, int start)
        {
            var x = Bitcast(
                _floatType,
                LoadV(image.GetAddressRegister(start)));
            var y = Bitcast(
                _floatType,
                LoadV(image.GetAddressRegister(start + 1)));
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                _vec2Type,
                x,
                y);
        }

        private static bool TryGetAtomicOp(string name, out SpirvOp op)
        {
            op = name switch
            {
                "Swap" => SpirvOp.AtomicExchange,
                "Cmpswap" => SpirvOp.AtomicCompareExchange,
                "Add" => SpirvOp.AtomicIAdd,
                "Sub" => SpirvOp.AtomicISub,
                "Smin" => SpirvOp.AtomicSMin,
                "Umin" => SpirvOp.AtomicUMin,
                "Smax" => SpirvOp.AtomicSMax,
                "Umax" => SpirvOp.AtomicUMax,
                "And" => SpirvOp.AtomicAnd,
                "Or" => SpirvOp.AtomicOr,
                "Xor" => SpirvOp.AtomicXor,
                "Inc" => SpirvOp.AtomicIIncrement,
                "Dec" => SpirvOp.AtomicIDecrement,
                _ => SpirvOp.Nop,
            };
            return op != SpirvOp.Nop;
        }

        private uint EmitAtomic(
            SpirvOp op,
            uint type,
            uint pointer,
            uint scope,
            uint semantics,
            Func<uint> value,
            Func<uint> comparator)
        {
            if (op is SpirvOp.AtomicIIncrement or SpirvOp.AtomicIDecrement)
            {
                return _module.AddInstruction(
                    op,
                    type,
                    pointer,
                    UInt(scope),
                    UInt(semantics));
            }

            if (op == SpirvOp.AtomicCompareExchange)
            {
                return _module.AddInstruction(
                    op,
                    type,
                    pointer,
                    UInt(scope),
                    UInt(semantics),
                    UInt((semantics & ~0x8u) | 0x2u),
                    value(),
                    comparator());
            }

            return _module.AddInstruction(
                op,
                type,
                pointer,
                UInt(scope),
                UInt(semantics),
                value());
        }

        private uint BuildIntegerCoordinates(Gen5ImageControl image, int start)
        {
            var ivec2 = _module.TypeVector(_intType, 2);
            var x = Bitcast(
                _intType,
                LoadV(image.GetAddressRegister(start)));
            var y = Bitcast(
                _intType,
                LoadV(image.GetAddressRegister(start + 1)));
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                ivec2,
                x,
                y);
        }

        private uint BuildClampedIntegerCoordinates(
            Gen5ImageControl image,
            int start,
            uint width,
            uint height)
        {
            var ivec2 = _module.TypeVector(_intType, 2);
            var x = ClampSignedCoordinate(
                Bitcast(
                    _intType,
                    LoadV(image.GetAddressRegister(start))),
                width);
            var y = ClampSignedCoordinate(
                Bitcast(
                    _intType,
                    LoadV(image.GetAddressRegister(start + 1))),
                height);
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                ivec2,
                x,
                y);
        }

        private uint ClampSignedCoordinate(uint value, uint extent)
        {
            var zero = _module.Constant(_intType, 0);
            var max = _module.Constant(_intType, Math.Max(extent, 1) - 1);
            var belowZero = _module.AddInstruction(
                SpirvOp.SLessThan,
                _boolType,
                value,
                zero);
            var atLeastZero = _module.AddInstruction(
                SpirvOp.Select,
                _intType,
                belowZero,
                zero,
                value);
            var aboveMax = _module.AddInstruction(
                SpirvOp.SGreaterThan,
                _boolType,
                atLeastZero,
                max);
            return _module.AddInstruction(
                SpirvOp.Select,
                _intType,
                aboveMax,
                max,
                atLeastZero);
        }

        private void EmitBoundsCheckedImageWrite(
            uint coordinates,
            uint width,
            uint height,
            uint imageObject,
            uint texel)
        {
            var x = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _intType,
                coordinates,
                0);
            var y = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _intType,
                coordinates,
                1);
            var zero = _module.Constant(_intType, 0);
            var xNonNegative = _module.AddInstruction(
                SpirvOp.SGreaterThanEqual,
                _boolType,
                x,
                zero);
            var yNonNegative = _module.AddInstruction(
                SpirvOp.SGreaterThanEqual,
                _boolType,
                y,
                zero);
            var xInRange = _module.AddInstruction(
                SpirvOp.SLessThan,
                _boolType,
                x,
                _module.Constant(_intType, width));
            var yInRange = _module.AddInstruction(
                SpirvOp.SLessThan,
                _boolType,
                y,
                _module.Constant(_intType, height));
            var lowerInRange = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                xNonNegative,
                yNonNegative);
            var upperInRange = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                xInRange,
                yInRange);
            var inRange = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                lowerInRange,
                upperInRange);
            inRange = _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                Load(_boolType, _exec),
                inRange);
            var writeLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            _module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
            _module.AddStatement(
                SpirvOp.BranchConditional,
                inRange,
                writeLabel,
                mergeLabel);
            _module.AddLabel(writeLabel);
            _module.AddStatement(
                SpirvOp.ImageWrite,
                imageObject,
                coordinates,
                texel);
            _module.AddStatement(SpirvOp.Branch, mergeLabel);
            _module.AddLabel(mergeLabel);
        }

        private static bool TryGetImageBounds(
            IReadOnlyList<uint> descriptor,
            out uint width,
            out uint height)
        {
            width = 0;
            height = 0;
            if (descriptor.Count < 3)
            {
                return false;
            }

            width = (((descriptor[1] >> 30) & 0x3u) |
                     ((descriptor[2] & 0xFFFu) << 2)) + 1;
            height = ((descriptor[2] >> 14) & 0x3FFFu) + 1;
            return width != 0 && height != 0 && width <= 16384 && height <= 16384;
        }

        private uint BuildImageOffset(Gen5ImageControl image, int component)
        {
            var ivec2 = _module.TypeVector(_intType, 2);
            var packed = Bitcast(
                _intType,
                LoadV(image.GetAddressRegister(component)));
            var x = _module.AddInstruction(
                SpirvOp.BitFieldSExtract,
                _intType,
                packed,
                UInt(0),
                UInt(6));
            var y = _module.AddInstruction(
                SpirvOp.BitFieldSExtract,
                _intType,
                packed,
                UInt(8),
                UInt(6));
            return _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                ivec2,
                x,
                y);
        }

        private bool TryEmitExport(
            Gen5ShaderInstruction instruction,
            Gen5ExportControl export,
            out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count < 4)
            {
                error = "missing export sources";
                return false;
            }

            if (_stage == Gen5SpirvStage.Pixel)
            {
                if (!_pixelOutputs.TryGetValue(export.Target, out var output))
                {
                    return true;
                }

                var values = new uint[4];
                for (var component = 0; component < 4; component++)
                {
                    var enabled = (export.EnableMask & (1u << component)) != 0;
                    if (!enabled)
                    {
                        values[component] = _module.AddInstruction(
                            SpirvOp.CompositeExtract,
                            output.Kind switch
                            {
                                Gen5PixelOutputKind.Uint => _uintType,
                                Gen5PixelOutputKind.Sint => _intType,
                                _ => _floatType,
                            },
                            Load(output.Type, output.Variable),
                            (uint)component);
                        continue;
                    }

                    if (export.Compressed)
                    {
                        var value = LoadCompressedExportComponent(
                            instruction,
                            component);
                        values[component] = output.Kind switch
                        {
                            Gen5PixelOutputKind.Uint => _module.AddInstruction(
                                SpirvOp.ConvertFToU,
                                _uintType,
                                value),
                            Gen5PixelOutputKind.Sint => _module.AddInstruction(
                                SpirvOp.ConvertFToS,
                                _intType,
                                value),
                            _ => value,
                        };
                        continue;
                    }

                    var raw = LoadV(instruction.Sources[component].Value);
                    values[component] = output.Kind switch
                    {
                        Gen5PixelOutputKind.Uint => raw,
                        Gen5PixelOutputKind.Sint => Bitcast(_intType, raw),
                        _ => Bitcast(_floatType, raw),
                    };
                }

                var vector = _module.AddInstruction(
                    SpirvOp.CompositeConstruct,
                    output.Type,
                    values);
                vector = _module.AddInstruction(
                    SpirvOp.Select,
                    output.Type,
                    Load(_boolType, _exec),
                    vector,
                    Load(output.Type, output.Variable));
                Store(output.Variable, vector);
                return true;
            }

            if (_stage != Gen5SpirvStage.Vertex)
            {
                if (_stage == Gen5SpirvStage.Compute && _computeCapture is { } capture)
                {
                    return TryEmitComputeCaptureExport(instruction, export, capture);
                }

                return true;
            }

            IReadOnlyList<uint> outputVariables;
            if (export.Target is >= 12 and < 16)
            {
                if (export.Target != 12)
                {
                    return true;
                }

                outputVariables = [_positionOutput];
            }
            else if (export.Target is >= 32 and < 64 &&
                     _vertexOutputsByGuestLocation.TryGetValue(
                         export.Target - 32,
                         out var parameters))
            {
                outputVariables = parameters;
            }
            else
            {
                return true;
            }

            var components = new uint[4];
            for (var component = 0; component < 4; component++)
            {
                components[component] = (export.EnableMask & (1u << component)) != 0
                    ? export.Compressed
                        ? LoadCompressedExportComponent(instruction, component)
                        : Bitcast(
                            _floatType,
                            LoadV(instruction.Sources[component].Value))
                    : Float(component == 3 ? 1f : 0f);
            }

            var outputValue = _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                _vec4Type,
                components);
            var exportActive = Load(_boolType, _exec);
            foreach (var outputVariable in outputVariables)
            {
                var selectedValue = _module.AddInstruction(
                    SpirvOp.Select,
                    _vec4Type,
                    exportActive,
                    outputValue,
                    Load(_vec4Type, outputVariable));
                Store(outputVariable, selectedValue);
            }

            return true;
        }

        // Position-capture route: redirect the POS0 (exp target 12) export and
        // the parameter exports (targets 32..) to a device-local storage buffer
        // indexed by gl_GlobalInvocationID.x, so a pass-through NGG export shader
        // run as compute writes one clip-space vec4 plus each captured varyings
        // vec4 per invocation. PRIM target 20 and POS0 targets 13-15 are dropped.
        private bool TryEmitComputeCaptureExport(
            Gen5ShaderInstruction instruction,
            Gen5ExportControl export,
            NggComputeCapture capture)
        {
            // POS0 (target 12) lands in the first vec4 slot; parameter exports
            // (targets 32..) follow, one vec4 slot each, so the raster VS can
            // forward them to the game pixel shader as varyings. Every other
            // export target (PRIM target 20, POS0 targets 13-15) is dropped.
            uint slotDword;
            if (export.Target == 12)
            {
                slotDword = 0;
            }
            else if (export.Target >= 32 && export.Target - 32u < capture.ParamCount)
            {
                slotDword = 4u * (export.Target - 32u + 1u);
            }
            else
            {
                return true;
            }

            var components = new uint[4];
            for (var component = 0; component < 4; component++)
            {
                components[component] = (export.EnableMask & (1u << component)) != 0
                    ? export.Compressed
                        ? LoadCompressedExportComponent(instruction, component)
                        : Bitcast(
                            _floatType,
                            LoadV(instruction.Sources[component].Value))
                    : Float(component == 3 ? 1f : 0f);
            }

            var baseDword = _module.AddInstruction(
                SpirvOp.IMul,
                _uintType,
                LoadGlobalInvocationIdX(),
                UInt(capture.PositionDwordStride));

            EmitExecConditional(() =>
            {
                for (uint component = 0; component < 4; component++)
                {
                    var offset = slotDword + component;
                    var address = offset == 0
                        ? baseDword
                        : IAdd(baseDword, UInt(offset));
                    StoreBufferWord(
                        capture.PositionBufferBindingIndex,
                        address,
                        Bitcast(_uintType, components[component]));
                }
            });

            return true;
        }

        private uint LoadCompressedExportComponent(
            Gen5ShaderInstruction instruction,
            int component)
        {
            if (_tracePackedExport)
            {
                var source = instruction.Sources[component >> 1];
                Console.Error.WriteLine(
                    $"[AGC][PACKED-EXPORT] shader=0x{_state.Program.Address:X16} " +
                    $"exp_pc=0x{instruction.Pc:X} component={component} " +
                    $"source={source.Kind}:{source.Value}");
            }

            if (_forcePackedExportOne)
            {
                return Float(1f);
            }

            if ((_debugTonemapSampleOutput ||
                 _debugTonemapGamutOutput ||
                 _debugTonemapGamutBaseOutput ||
                 _debugTonemapGamutDenominatorOutput ||
                 _debugTonemapGamutScalarOutput ||
                 _debugTonemapCurveOutput ||
                 _debugTonemapCoreOutput ||
                 _debugTonemapGradeOutput ||
                 _debugTonemapTransferOutput) &&
                IsKnownTonemapShader())
            {
                if (component >= 3)
                {
                    return Float(1f);
                }

                var value = Bitcast(
                    _floatType,
                    LoadV(240u + (uint)component));
                if (_debugTonemapNanOutput)
                {
                    return _module.AddInstruction(
                        SpirvOp.Select,
                        _floatType,
                        _module.AddInstruction(
                            SpirvOp.IsNan,
                            _boolType,
                            value),
                        Float(1f),
                        Float(0f));
                }

                return _debugTonemapAbsoluteOutput
                    ? Ext(4, _floatType, value)
                    : value;
            }

            var packed = LoadV(instruction.Sources[component >> 1].Value);
            var unpacked = Ext(62, _vec2Type, packed);
            return _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                unpacked,
                (uint)(component & 1));
        }

        private bool IsKnownTonemapShader() =>
            _pixelShaderAddress == ExposureScalarShaderAddress ||
            _pixelShaderAddress == RelocatedExposureScalarShaderAddress;

        private uint GetPixelOutputType(Gen5PixelOutputKind kind) =>
            kind switch
            {
                Gen5PixelOutputKind.Uint => _uvec4Type,
                Gen5PixelOutputKind.Sint => _module.TypeVector(_intType, 4),
                _ => _vec4Type,
            };

        private uint LoadBufferWord(int binding, uint dwordAddress)
        {
            var pointer = BufferWordPointer(binding, dwordAddress);
            return Load(_uintType, pointer);
        }

        private void StoreBufferWord(int binding, uint dwordAddress, uint value)
        {
            var pointer = BufferWordPointer(binding, dwordAddress);
            Store(pointer, value);
        }

        private uint BufferWordPointer(int binding, uint dwordAddress) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _storageUintPointer,
                _globalBuffers,
                UInt((uint)binding),
                UInt(0),
                dwordAddress);

        private uint ScalarPointer(uint register) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _privateUintPointer,
                _scalarRegisters,
                UInt(register));

        private uint VectorPointer(uint register) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _privateUintPointer,
                _vectorRegisters,
                UInt(register));

        // Runtime-indexed VGPR access for the v_movrel* family. The index is
        // clamped to the 256-register guest file so a bad M0 cannot leave the
        // register array.
        private uint VectorPointerAt(uint index) =>
            _module.AddInstruction(
                SpirvOp.AccessChain,
                _privateUintPointer,
                _vectorRegisters,
                BitwiseAnd(index, UInt(0xFF)));

        private uint LoadS(uint register) => Load(_uintType, ScalarPointer(register));

        private uint LoadV(uint register) => Load(_uintType, VectorPointer(register));

        private void StoreS(uint register, uint value)
        {
            Store(ScalarPointer(register), value);
            if (register is 106 or 107)
            {
                Store(_vcc, IsWaveMaskActive(LoadS64(106)));
            }
            else if (register is 126 or 127)
            {
                Store(_exec, IsWaveMaskActive(LoadS64(126)));
            }
        }

        private void StoreV(uint register, uint value, bool guardWithExec = true)
        {
            if (guardWithExec)
            {
                var active = Load(_boolType, _exec);
                var oldValue = LoadV(register);
                value = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    active,
                    value,
                    oldValue);
            }

            Store(VectorPointer(register), value);
        }

        private uint Load(uint type, uint pointer) =>
            _module.AddInstruction(SpirvOp.Load, type, pointer);

        private void Store(uint pointer, uint value) =>
            _module.AddStatement(SpirvOp.Store, pointer, value);

        // Zero-initialise the interface-padding vertex outputs (Locations the
        // paired pixel shader reads but this program never exports) so the
        // fragment stage sees defined varyings. No-op for non-vertex stages.
        private void EmitPaddedVertexOutputStores()
        {
            if (_paddedVertexOutputs.Count == 0)
            {
                return;
            }

            var zero = Float(0f);
            var zeroVec = _module.AddInstruction(
                SpirvOp.CompositeConstruct,
                _vec4Type,
                zero,
                zero,
                zero,
                zero);
            foreach (var output in _paddedVertexOutputs)
            {
                Store(output, zeroVec);
            }
        }

        private uint UInt(uint value) => _module.Constant(_uintType, value);

        private uint Float(float value) => _module.ConstantFloat(_floatType, value);

        private uint Bitcast(uint type, uint value) =>
            _module.AddInstruction(SpirvOp.Bitcast, type, value);

        private uint IAdd(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.IAdd, _uintType, left, right);

        private uint ShiftLeftLogical(uint left, uint right) =>
            _module.AddInstruction(
                SpirvOp.ShiftLeftLogical,
                _uintType,
                left,
                BitwiseAnd(right, UInt(31)));

        private uint ShiftRightLogical(uint left, uint right) =>
            _module.AddInstruction(
                SpirvOp.ShiftRightLogical,
                _uintType,
                left,
                BitwiseAnd(right, UInt(31)));

        private uint ShiftRightArithmetic(uint left, uint right) =>
            Bitcast(
                _uintType,
                _module.AddInstruction(
                    SpirvOp.ShiftRightArithmetic,
                    _intType,
                    Bitcast(_intType, left),
                    BitwiseAnd(right, UInt(31))));

        private uint ShiftLeftLogical64(uint left, uint right) =>
            _module.AddInstruction(
                SpirvOp.ShiftLeftLogical,
                _ulongType,
                left,
                BitwiseAnd64(right, _module.Constant64(_ulongType, 63)));

        private uint ShiftRightLogical64(uint left, uint right) =>
            _module.AddInstruction(
                SpirvOp.ShiftRightLogical,
                _ulongType,
                left,
                BitwiseAnd64(right, _module.Constant64(_ulongType, 63)));

        private uint BitwiseAnd(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseAnd, _uintType, left, right);

        private uint BitwiseAnd64(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseAnd, _ulongType, left, right);

        private uint BitwiseOr(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseOr, _uintType, left, right);

        private uint BitwiseOr64(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseOr, _ulongType, left, right);

        private uint BitwiseXor(uint left, uint right) =>
            _module.AddInstruction(SpirvOp.BitwiseXor, _uintType, left, right);

        private uint LogicalNot(uint value) =>
            _module.AddInstruction(SpirvOp.LogicalNot, _boolType, value);

        private uint SubgroupAny(uint condition) =>
            _subgroupInvocationIdInput == 0
                ? condition
                : _module.AddInstruction(
                    SpirvOp.GroupNonUniformAny,
                    _boolType,
                    UInt(3),
                    condition);

        private uint CurrentLaneBit()
        {
            if (_subgroupInvocationIdInput == 0)
            {
                return _module.Constant64(_ulongType, 1);
            }

            var lane = Load(_uintType, _subgroupInvocationIdInput);
            var maskedLane = BitwiseAnd(lane, UInt(RdnaWaveLaneCount - 1));
            var shifted = ShiftLeftLogical64(
                _module.Constant64(_ulongType, 1),
                _module.AddInstruction(
                    SpirvOp.UConvert,
                    _ulongType,
                    maskedLane));
            return _module.AddInstruction(
                SpirvOp.Select,
                _ulongType,
                IsCurrentLaneInRdnaWave(),
                shifted,
                _module.Constant64(_ulongType, 0));
        }

        private uint IsCurrentLaneInRdnaWave() =>
            _module.AddInstruction(
                SpirvOp.ULessThan,
                _boolType,
                Load(_uintType, _subgroupInvocationIdInput),
                UInt(RdnaWaveLaneCount));

        private uint BooleanToLaneMask(uint condition) =>
            _module.AddInstruction(
                SpirvOp.Select,
                _ulongType,
                condition,
                CurrentLaneBit(),
                _module.Constant64(_ulongType, 0));

        // A wave-mask SGPR (VCC/EXEC) consumed as a per-lane predicate must be
        // tested at the current lane's bit, exactly as the hardware does, not as
        // "the 64-bit value is non-zero". They coincide for comparison results
        // (only the lane's own bit is set), but bitwise-complement idioms
        // (S_NOT/S_ORN2/S_ANDN2/S_NAND/S_NOR) set the unused upper 63 bits, so a
        // whole-word test reports the lane active even when its bit is clear.
        // A NaN killer like `anyNaN | ~allFinite` then reads every valid pixel as
        // NaN and replaces it with 0, zeroing the scene. Always extract the bit.
        private uint IsWaveMaskActive(uint mask) =>
            IsCurrentLaneSet(mask);

        private uint IsCurrentLaneSet(uint mask) =>
            IsNotZero64(
                _module.AddInstruction(
                    SpirvOp.BitwiseAnd,
                    _ulongType,
                    mask,
                    CurrentLaneBit()));

        private void StoreWaveMask(uint register, uint condition) =>
            StoreS64(register, BooleanToLaneMask(condition));

        // s_wqm widens the active mask to whole quads: any quad (4 consecutive
        // lanes) with at least one set lane becomes fully set. Our wave masks
        // keep only each lane's own bit, so a plain move can never see a
        // sibling lane -- reconstruct the full 32-lane mask with a ballot,
        // expand it, then hand back the current lane's expanded bit. Callers
        // also receive the full expanded mask for the s_wqm SCC result.
        private uint WholeQuadModeExpand(uint sourceLaneSet, out uint expandedFullMask)
        {
            var fullMask = WaveBallotMask(sourceLaneSet);
            expandedFullMask = ExpandWholeQuads(fullMask);
            return BooleanToLaneMask(IsCurrentLaneSet(expandedFullMask));
        }

        // (m | m>>1 | m>>2 | m>>3) & 0x1111... isolates the low bit of every
        // 4-lane quad that has any lane set; multiplying by 0xF fills the quad.
        private uint ExpandWholeQuads(uint mask)
        {
            var quadAny = BitwiseAnd64(
                BitwiseOr64(
                    mask,
                    BitwiseOr64(
                        ShiftRightLogical64(mask, _module.Constant64(_ulongType, 1)),
                        BitwiseOr64(
                            ShiftRightLogical64(mask, _module.Constant64(_ulongType, 2)),
                            ShiftRightLogical64(mask, _module.Constant64(_ulongType, 3))))),
                _module.Constant64(_ulongType, 0x1111_1111_1111_1111UL));
            return _module.AddInstruction(
                SpirvOp.IMul,
                _ulongType,
                quadAny,
                _module.Constant64(_ulongType, 0xFUL));
        }

        // Ballot the per-lane condition into a full 32-lane wave mask that every
        // participating invocation (covered and helper lanes alike) can read.
        private uint WaveBallotMask(uint condition)
        {
            var ballot = _module.AddInstruction(
                SpirvOp.GroupNonUniformBallot,
                _uvec4Type,
                UInt(3),
                condition);
            var low = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _uintType,
                ballot,
                0);
            return _module.AddInstruction(SpirvOp.UConvert, _ulongType, low);
        }

        // Only lanes enabled by EXEC can contribute to a balloted mask;
        // letting disabled lanes through leaks stale results into later
        // saveexec/branch sequences.
        private uint MaskWithExec(uint condition) =>
            _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                Load(_boolType, _exec),
                condition);

        private void EmitExecConditional(Action emit)
        {
            var activeLabel = _module.AllocateId();
            var mergeLabel = _module.AllocateId();
            var active = Load(_boolType, _exec);
            _module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
            _module.AddStatement(
                SpirvOp.BranchConditional,
                active,
                activeLabel,
                mergeLabel);
            _module.AddLabel(activeLabel);
            emit();
            _module.AddStatement(SpirvOp.Branch, mergeLabel);
            _module.AddLabel(mergeLabel);
        }

        private bool UsesLds() =>
            _state.Program.Instructions.Any(instruction =>
                instruction.Control is Gen5DataShareControl);

        private bool UsesSubgroupShuffle() =>
            _state.Program.Instructions.Any(instruction =>
                // VReadfirstlaneB32 and VReadlaneB32 read from a computed lane
                // via GroupNonUniformShuffle (Broadcast requires a constant
                // lane id before SPIR-V 1.5); the DS lane permutes shuffle
                // wave-wide.
                instruction.Opcode is "VPermlane16B32" or "VPermlanex16B32"
                    or "VReadfirstlaneB32" or "VReadlaneB32"
                    or "DsPermuteB32" or "DsBpermuteB32");

        private bool UsesWaveControl() =>
            _state.Program.Instructions.Any(instruction =>
                instruction.Opcode.Contains("Saveexec", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("SCbranchExec", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("SCbranchVcc", StringComparison.Ordinal) ||
                instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal) ||
                instruction.Sources.Any(IsWaveMaskOperand) ||
                instruction.Destinations.Any(IsWaveMaskOperand));

        private bool UsesSubgroupBroadcast() =>
            _state.Program.Instructions.Any(static instruction =>
                instruction.Opcode == "VReadfirstlaneB32");

        // s_wqm has to look across the quad, so it needs the subgroup lane id
        // (and a ballot) even when the shader uses no other subgroup op.
        private bool UsesWholeQuadMode() =>
            _state.Program.Instructions.Any(static instruction =>
                instruction.Opcode is "SWqmB32" or "SWqmB64");

        private bool UsesSubgroupOperations() =>
            UsesSubgroupBroadcast() ||
            // Lane shuffles (readlane, ds permutes) need the subgroup lane id
            // in every stage; wave control alone only opts in for compute.
            UsesSubgroupShuffle() ||
            UsesWholeQuadMode() ||
            (_stage == Gen5SpirvStage.Compute && UsesWaveControl());

        private static bool IsWaveMaskOperand(Gen5Operand operand) =>
            operand.Kind == Gen5OperandKind.ScalarRegister &&
            operand.Value is 106 or 107 or 126 or 127;

        private static bool TryGetVectorDestination(
            Gen5ShaderInstruction instruction,
            out uint destination)
        {
            if (instruction.Destinations.Count != 0 &&
                instruction.Destinations[0].Kind == Gen5OperandKind.VectorRegister)
            {
                destination = instruction.Destinations[0].Value;
                return true;
            }

            destination = 0;
            return false;
        }

        private static bool IsBranch(string opcode) =>
            opcode == "SBranch" ||
            opcode.StartsWith("SCbranch", StringComparison.Ordinal);

        private static bool TryGetBranchTargetPc(
            Gen5ShaderInstruction instruction,
            out uint targetPc)
        {
            targetPc = 0;
            if (instruction.Encoding != Gen5ShaderEncoding.Sopp ||
                instruction.Words.Count == 0)
            {
                return false;
            }

            var offset = unchecked((short)(instruction.Words[0] & 0xFFFF));
            var nextPc = (long)instruction.Pc +
                (instruction.Words.Count * sizeof(uint));
            var target = nextPc + (offset * sizeof(uint));
            if (target < 0 || target > uint.MaxValue)
            {
                return false;
            }

            targetPc = (uint)target;
            return true;
        }

        private static IReadOnlyList<ShaderBlock> BuildBasicBlocks(
            IReadOnlyList<Gen5ShaderInstruction> instructions)
        {
            if (instructions.Count == 0)
            {
                return [];
            }

            var leaders = new SortedSet<uint> { instructions[0].Pc };
            for (var index = 0; index < instructions.Count; index++)
            {
                var instruction = instructions[index];
                if (IsBranch(instruction.Opcode) &&
                    TryGetBranchTargetPc(instruction, out var targetPc))
                {
                    leaders.Add(targetPc);
                }

                if ((IsBranch(instruction.Opcode) || instruction.Opcode == "SEndpgm") &&
                    index + 1 < instructions.Count)
                {
                    leaders.Add(instructions[index + 1].Pc);
                }
            }

            var starts = leaders
                .Where(pc => instructions.Any(instruction => instruction.Pc == pc))
                .ToArray();
            var blocks = new List<ShaderBlock>(starts.Length);
            for (var index = 0; index < starts.Length; index++)
            {
                var startIndex = FindInstructionIndex(instructions, starts[index]);
                var endIndex = index + 1 < starts.Length
                    ? FindInstructionIndex(instructions, starts[index + 1])
                    : instructions.Count;
                if (startIndex >= 0 && endIndex > startIndex)
                {
                    blocks.Add(new ShaderBlock(starts[index], startIndex, endIndex));
                }
            }

            return blocks;
        }

        private static int FindInstructionIndex(
            IReadOnlyList<Gen5ShaderInstruction> instructions,
            uint pc)
        {
            for (var index = 0; index < instructions.Count; index++)
            {
                if (instructions[index].Pc == pc)
                {
                    return index;
                }
            }

            return -1;
        }

        private static bool TryFindBlock(
            IReadOnlyList<ShaderBlock> blocks,
            uint pc,
            out int block)
        {
            for (var index = 0; index < blocks.Count; index++)
            {
                if (blocks[index].StartPc == pc)
                {
                    block = index;
                    return true;
                }
            }

            block = -1;
            return false;
        }

        private readonly record struct ShaderBlock(
            uint StartPc,
            int StartIndex,
            int EndIndex);
    }
}
