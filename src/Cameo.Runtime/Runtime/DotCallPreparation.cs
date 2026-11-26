using Cameo.Runtime.Stdlib;

namespace Cameo.Runtime.Vm;

/// <summary>Preserves direct Cameo dot-call identity while selecting its argument layout.</summary>
public static class DotCallPreparation
{
    private static bool Binds(Value function, Value receiver)
    {
        if (function.Kind != ValueKind.Function) return false;
        if (function.O is NativeFunction { InstanceReceiverType: { } expected })
        {
            if (expected == typeof(string)) return receiver.Kind == ValueKind.String;
            return receiver.Kind == ValueKind.Userdata && expected.IsInstanceOfType(receiver.O);
        }
        return function.O is Closure { Proto.IsCameoMethod: true }
            && receiver.O is Table { Metatable.IsCameoInstanceMeta: true };
    }

    /// <summary>Prepares valid occupied registers [start,end), returning the exclusive occupied end.</summary>
    /// <remarks>The first two slots contain the function and reserved receiver. Copying overlaps intentionally.</remarks>
    /// <param name="registers">Register storage containing the function, receiver and explicit arguments.</param>
    /// <param name="start">Function register index.</param>
    /// <param name="end">Exclusive occupied end, independent of any stale thread top.</param>
    /// <returns>The exclusive occupied end after retaining or removing the reserved receiver.</returns>
    /// <exception cref="ArgumentNullException">Storage is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The occupied range is outside storage or lacks a receiver.</exception>
    public static int PrepareRegisters(Value[] registers, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(registers);
        if ((uint)start > (uint)registers.Length || (uint)end > (uint)registers.Length || end < start || end - start < 2)
            throw new ArgumentOutOfRangeException(nameof(end));
        if (Binds(registers[start], registers[start + 1])) return end;
        Array.Copy(registers, start + 2, registers, start + 1, end - start - 2);
        registers[end - 1] = default;
        return end - 1;
    }

    /// <summary>Prepares the JIT's receiver-plus-explicit-arguments array without changing function identity.</summary>
    /// <param name="function">The function returned by ordinary receiver lookup.</param>
    /// <param name="receiverAndArguments">Reserved receiver followed by explicit argument values.</param>
    /// <returns>The original array when binding, otherwise a new array without its first value.</returns>
    /// <exception cref="ArgumentNullException">The array is null.</exception>
    /// <exception cref="ArgumentException">The array lacks a receiver.</exception>
    public static Value[] PrepareArguments(Value function, Value[] receiverAndArguments)
    {
        ArgumentNullException.ThrowIfNull(receiverAndArguments);
        if (receiverAndArguments.Length == 0) throw new ArgumentException("Missing reserved receiver.", nameof(receiverAndArguments));
        if (Binds(function, receiverAndArguments[0])) return receiverAndArguments;
        var result = new Value[receiverAndArguments.Length - 1];
        Array.Copy(receiverAndArguments, 1, result, 0, result.Length);
        return result;
    }
}
