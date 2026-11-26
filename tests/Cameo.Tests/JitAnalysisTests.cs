using Cameo.Jit;
using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests;

/// <summary>Checks numeric storage decisions against bytecode semantics. These complement
/// differential execution: a safe deoptimization preserves values but loses the feature.</summary>
public class JitAnalysisTests
{
    private static Proto Prototype(params Instruction[] instructions) => new()
    {
        MaxStack = 5, NumParams = 2,
        NumericParameters = [ValueKind.Integer, ValueKind.Float],
        K = [Value.FromInt(3), Value.FromFloat(0.5), Value.FromString("text")],
        Code = instructions,
    };

    [Theory]
    [InlineData(OpCode.Add, 0, ValueKind.Integer)]
    [InlineData(OpCode.Sub, 0, ValueKind.Integer)]
    [InlineData(OpCode.Mul, 0, ValueKind.Integer)]
    [InlineData(OpCode.IDiv, 0, ValueKind.Integer)]
    [InlineData(OpCode.Mod, 0, ValueKind.Integer)]
    [InlineData(OpCode.Div, 0, ValueKind.Float)]
    [InlineData(OpCode.Pow, 0, ValueKind.Float)]
    [InlineData(OpCode.BAnd, 0, ValueKind.Integer)]
    [InlineData(OpCode.BOr, 0, ValueKind.Integer)]
    [InlineData(OpCode.BXor, 0, ValueKind.Integer)]
    [InlineData(OpCode.Shl, 0, ValueKind.Integer)]
    [InlineData(OpCode.Shr, 0, ValueKind.Integer)]
    [InlineData(OpCode.Add, 1, ValueKind.Float)]
    [InlineData(OpCode.Sub, 1, ValueKind.Float)]
    [InlineData(OpCode.Mul, 1, ValueKind.Float)]
    [InlineData(OpCode.IDiv, 1, ValueKind.Float)]
    [InlineData(OpCode.Mod, 1, ValueKind.Float)]
    [InlineData(OpCode.Add, 2, null)]
    [InlineData(OpCode.Mul, 2, null)]
    [InlineData(OpCode.BAnd, 2, null)]
    public void Arithmetic_storage_follows_numeric_result_kind(OpCode op, int constant, ValueKind? expected)
    {
        Proto proto = Prototype(new(op, 2, 0, ~constant), new(OpCode.Return, 2, 2));
        ValueKind?[] result = NumericAnalysis.Analyze(proto);
        Assert.Equal(ValueKind.Integer, result[0]);
        Assert.Equal(ValueKind.Float, result[1]);
        Assert.Equal(expected, result[2]);
        Assert.Null(result[3]);
    }

    [Theory]
    [InlineData(OpCode.Move, ValueKind.Integer)]
    [InlineData(OpCode.LoadInt, ValueKind.Integer)]
    [InlineData(OpCode.LoadK, ValueKind.Integer)]
    [InlineData(OpCode.Unm, ValueKind.Integer)]
    [InlineData(OpCode.MathFn, ValueKind.Float)]
    [InlineData(OpCode.Not, null)]
    [InlineData(OpCode.IsKind, null)]
    [InlineData(OpCode.Len, null)]
    [InlineData(OpCode.BNot, null)]
    [InlineData(OpCode.LoadNil, null)]
    [InlineData(OpCode.LoadBool, null)]
    [InlineData(OpCode.NewTable, null)]
    [InlineData(OpCode.GetGlobal, null)]
    [InlineData(OpCode.GetUpval, null)]
    [InlineData(OpCode.GetTable, null)]
    [InlineData(OpCode.GetField, null)]
    public void Load_and_unary_storage_respects_dynamic_results(OpCode op, ValueKind? expected)
    {
        Proto proto = Prototype(new(op, 2, 0, 0), new(OpCode.Return, 2, 2));
        Assert.Equal(expected, NumericAnalysis.Analyze(proto)[2]);
    }

    [Fact]
    public void A_read_before_assignment_prevents_numeric_zero_substitution()
    {
        Proto proto = Prototype(new(OpCode.Move, 3, 2), new(OpCode.LoadInt, 2, 4), new(OpCode.Return, 2, 3));
        ValueKind?[] result = NumericAnalysis.Analyze(proto);
        Assert.Null(result[2]);
        Assert.Null(result[3]);
    }

    [Fact]
    public void Both_branch_paths_contribute_to_storage_kinds()
    {
        Proto proto = Prototype(new(OpCode.JmpIf, 0, 2), new(OpCode.LoadInt, 2, 4),
            new(OpCode.Jmp, 0, 1), new(OpCode.LoadK, 2, 1), new(OpCode.Return, 2, 2));
        Assert.Null(NumericAnalysis.Analyze(proto)[2]);
        // The other branch now produces the same exact kind, so primitive storage is safe.
        proto.Code[3] = new(OpCode.LoadInt, 2, 5);
        Assert.Equal(ValueKind.Integer, NumericAnalysis.Analyze(proto)[2]);
    }

    [Fact]
    public void Skipping_a_loop_does_not_initialize_its_index()
    {
        Proto proto = Prototype(new(OpCode.LoadInt, 0, 1), new(OpCode.LoadInt, 1, 0), new(OpCode.LoadInt, 2, 1),
            new(OpCode.ForPrep, 0, 1), new(OpCode.ForLoop, 0, -1), new(OpCode.Return, 3, 2));
        // The skipped path returns nil; the taken path returns an integer. Both matter.
        Assert.Null(NumericAnalysis.Analyze(proto)[3]);
    }

    [Fact]
    public void Debugger_traps_use_original_instructions_for_analysis()
    {
        Proto proto = Prototype(new Instruction(OpCode.Trap));
        proto.OriginalCode = [new(OpCode.LoadInt, 2, 9), new(OpCode.Return, 2, 2)];
        Assert.Equal(ValueKind.Integer, NumericAnalysis.Analyze(proto)[2]);
    }

    [Fact]
    public void Oversized_and_future_bytecode_deoptimize_safely()
    {
        var proto = new Proto { MaxStack = 999, Code = new Instruction[1000] };
        proto.Code[0] = new(OpCode.LoadInt, 0, 7);
        proto.Code[1] = new(OpCode.Return, 0, 2);
        Assert.Equal(ValueKind.Integer, NumericAnalysis.Analyze(proto)[0]);
        proto.Code = new Instruction[1001];
        proto.Code[0] = new(OpCode.LoadInt, 0, 7);
        proto.Code[1] = new(OpCode.Return, 0, 2);
        Assert.All(NumericAnalysis.Analyze(proto), Assert.Null);
        proto = Prototype(new(OpCode.LoadInt, 2, 7), new((OpCode)255));
        Assert.All(NumericAnalysis.Analyze(proto), Assert.Null);
        proto.Code = [];
        Assert.All(NumericAnalysis.Analyze(proto), Assert.Null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Fixed_and_open_calls_invalidate_their_result_registers(int results)
    {
        Proto proto = Prototype(new(OpCode.LoadInt, 2, 7), new(OpCode.Call, 2, 1, results), new(OpCode.Return, 2, 2));
        Assert.Equal(results == 1 ? ValueKind.Integer : null, NumericAnalysis.Analyze(proto)[2]);
    }
}
