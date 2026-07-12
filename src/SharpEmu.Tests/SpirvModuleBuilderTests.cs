// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Unit tests for the SPIR-V emitter itself: ID allocation, type/constant
/// deduplication (SPIR-V forbids duplicate type declarations), section
/// ordering, and binary encoding.
/// </summary>
public sealed class SpirvModuleBuilderTests
{
    private const ushort OpTypeVoid = 19;
    private const ushort OpTypeInt = 21;
    private const ushort OpTypeFloat = 22;
    private const ushort OpTypeVector = 23;
    private const ushort OpConstant = 43;
    private const ushort OpVariable = 59;
    private const ushort OpCapability = 17;
    private const ushort OpEntryPoint = 15;
    private const ushort OpName = 5;
    private const ushort OpDecorate = 71;
    private const ushort OpFunction = 54;
    private const ushort OpFunctionEnd = 56;

    private static SpirvModuleBuilder MinimalModule(out uint main)
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var voidType = module.TypeVoid();
        var functionType = module.TypeFunction(voidType);
        main = module.BeginFunction(voidType, functionType);
        module.AddLabel();
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        module.AddEntryPoint(SpirvExecutionModel.Fragment, main, "main", []);
        module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        return module;
    }

    [Fact]
    public void Build_EmitsValidHeaderAndBound()
    {
        var module = MinimalModule(out _);

        var parsed = SpirvModuleAssert.Parse(module.Build()); // asserts magic, schema word, instruction integrity

        // The bound must exceed every ID actually used.
        var maxId = parsed.Instructions
            .Where(i => i.Opcode == OpFunction)
            .Select(i => i.Words[2])
            .Max();
        Assert.True(parsed.Bound > maxId, $"bound {parsed.Bound} must exceed max id {maxId}.");
    }

    [Fact]
    public void TypeDeclarations_AreDeduplicated()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidA = module.TypeVoid();
        var voidB = module.TypeVoid();
        var intA = module.TypeInt(32, signed: false);
        var intB = module.TypeInt(32, signed: false);
        var signedInt = module.TypeInt(32, signed: true);
        var floatA = module.TypeFloat(32);
        var floatB = module.TypeFloat(32);
        var vecA = module.TypeVector(floatA, 4);
        var vecB = module.TypeVector(floatA, 4);
        var vec2 = module.TypeVector(floatA, 2);

        // Identical requests must return the same ID...
        Assert.Equal(voidA, voidB);
        Assert.Equal(intA, intB);
        Assert.Equal(floatA, floatB);
        Assert.Equal(vecA, vecB);

        // ...and differing ones must not.
        Assert.NotEqual(intA, signedInt);
        Assert.NotEqual(vecA, vec2);

        var voidType = module.TypeVoid();
        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddLabel();
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        module.AddEntryPoint(SpirvExecutionModel.Fragment, main, "main", []);

        var parsed = SpirvModuleAssert.Parse(module.Build());

        // SPIR-V forbids duplicate type declarations — each must appear exactly once.
        Assert.Single(parsed.Instructions, i => i.Opcode == OpTypeVoid);
        Assert.Single(parsed.Instructions, i => i.Opcode == OpTypeFloat);
        Assert.Equal(2, parsed.Instructions.Count(i => i.Opcode == OpTypeInt));     // signed + unsigned
        Assert.Equal(2, parsed.Instructions.Count(i => i.Opcode == OpTypeVector));  // vec4 + vec2
    }

    [Fact]
    public void Constants_AreDeduplicatedPerTypeAndValue()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var uintType = module.TypeInt(32, signed: false);
        var floatType = module.TypeFloat(32);

        var oneA = module.Constant(uintType, 1);
        var oneB = module.Constant(uintType, 1);
        var two = module.Constant(uintType, 2);
        var floatOneA = module.ConstantFloat(floatType, 1f);
        var floatOneB = module.ConstantFloat(floatType, 1f);

        Assert.Equal(oneA, oneB);
        Assert.Equal(floatOneA, floatOneB);
        Assert.NotEqual(oneA, two);
        // Same bit pattern, different type — must be distinct constants.
        Assert.NotEqual(oneA, floatOneA);

        var voidType = module.TypeVoid();
        var main = module.BeginFunction(voidType, module.TypeFunction(voidType));
        module.AddLabel();
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        module.AddEntryPoint(SpirvExecutionModel.Fragment, main, "main", []);

        var parsed = SpirvModuleAssert.Parse(module.Build());

        // uint 1, uint 2, float 1.0 => exactly three OpConstant instructions.
        Assert.Equal(3, parsed.Instructions.Count(i => i.Opcode == OpConstant));
    }

    [Fact]
    public void AllocateId_ReturnsIncreasingUniqueIds()
    {
        var module = new SpirvModuleBuilder();

        var ids = Enumerable.Range(0, 8).Select(_ => module.AllocateId()).ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal(ids.OrderBy(i => i), ids);
        Assert.DoesNotContain(0u, ids); // 0 is reserved in SPIR-V
    }

    [Fact]
    public void Build_OrdersSectionsPerSpirvSpec()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var floatType = module.TypeFloat(32);
        var vec4 = module.TypeVector(floatType, 4);
        var outputPointer = module.TypePointer(SpirvStorageClass.Output, vec4);
        var output = module.AddGlobalVariable(outputPointer, SpirvStorageClass.Output);
        module.AddName(output, "outColor");
        module.AddDecoration(output, SpirvDecoration.Location, 0);

        var voidType = module.TypeVoid();
        var main = module.BeginFunction(voidType, module.TypeFunction(voidType));
        module.AddLabel();
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        module.AddEntryPoint(SpirvExecutionModel.Fragment, main, "main", [output]);

        var parsed = SpirvModuleAssert.Parse(module.Build());
        var order = parsed.Instructions.Select(i => i.Opcode).ToList();

        int First(ushort opcode) => order.IndexOf(opcode);

        // Spec-mandated order: capabilities < entry points < names < decorations
        // < types/constants/globals < function bodies.
        Assert.True(First(OpCapability) < First(OpEntryPoint));
        Assert.True(First(OpEntryPoint) < First(OpName));
        Assert.True(First(OpName) < First(OpDecorate));
        Assert.True(First(OpDecorate) < First(OpTypeFloat));
        Assert.True(First(OpVariable) < First(OpFunction));   // globals precede functions
        Assert.True(First(OpFunction) < First(OpFunctionEnd));
    }

    [Fact]
    public void Build_IsRepeatableAndByteIdentical()
    {
        var first = MinimalModule(out _).Build();
        var second = MinimalModule(out _).Build();

        Assert.Equal(first, second);
    }

    [Fact]
    public void AddInstruction_ResultIdIsUsableAsOperand()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var floatType = module.TypeFloat(32);
        var voidType = module.TypeVoid();
        var main = module.BeginFunction(voidType, module.TypeFunction(voidType));
        module.AddLabel();

        var two = module.ConstantFloat(floatType, 2f);
        var product = module.AddInstruction(SpirvOp.FMul, floatType, two, two);
        var sum = module.AddInstruction(SpirvOp.FAdd, floatType, product, two);

        Assert.NotEqual(0u, product);
        Assert.NotEqual(product, sum);

        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        module.AddEntryPoint(SpirvExecutionModel.Fragment, main, "main", []);

        var parsed = SpirvModuleAssert.Parse(module.Build());
        Assert.True(parsed.Bound > sum);
    }
}
