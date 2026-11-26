using System.Runtime.CompilerServices;

namespace Cameo.Runtime.Vm;

/// <summary>Unary float functions available as global built-ins and, when the compiler can
/// prove the global is the built-in, as the <see cref="OpCode.MathFn"/> instruction.</summary>
public enum MathFn
{
    Sin, Cos, Tan, Asin, Acos, Atan,
    Sinh, Cosh, Tanh, Asinh, Acosh, Atanh,
    Exp, Exp2, Expm1,
    Log, Log2, Log10, Log1p,
    Sqrt, Cbrt,
    Deg, Rad,
}

public static class MathBuiltins
{
    /// <summary>Global names of the intrinsic functions, indexed by <see cref="MathFn"/>.</summary>
    public static readonly string[] Names =
    {
        "sin", "cos", "tan", "asin", "acos", "atan",
        "sinh", "cosh", "tanh", "asinh", "acosh", "atanh",
        "exp", "exp2", "expm1",
        "log", "log2", "log10", "log1p",
        "sqrt", "cbrt",
        "deg", "rad",
    };

    private static readonly Dictionary<string, MathFn> ByName = BuildIndex();

    private static Dictionary<string, MathFn> BuildIndex()
    {
        Dictionary<string, MathFn> d = [with(capacity: Names.Length)];
        for (int i = 0; i < Names.Length; i++) d[Names[i]] = (MathFn)i;
        return d;
    }

    public static bool TryGet(string name, out MathFn fn) => ByName.TryGetValue(name, out fn);

    /// <summary>Evaluates a built-in. Kept out of line: the interpreter loop only pays a direct
    /// call, against a full native-function call for <c>math.sin(x)</c>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static double Apply(MathFn fn, double x) => fn switch
    {
        MathFn.Sin => Math.Sin(x),
        MathFn.Cos => Math.Cos(x),
        MathFn.Tan => Math.Tan(x),
        MathFn.Asin => Math.Asin(x),
        MathFn.Acos => Math.Acos(x),
        // Match the Lua/global optional-second-argument path bit for bit. Math.Atan
        // is mathematically equivalent here but can round differently from Atan2.
        MathFn.Atan => Math.Atan2(x, 1.0),
        MathFn.Sinh => Math.Sinh(x),
        MathFn.Cosh => Math.Cosh(x),
        MathFn.Tanh => Math.Tanh(x),
        MathFn.Asinh => Math.Asinh(x),
        MathFn.Acosh => Math.Acosh(x),
        MathFn.Atanh => Math.Atanh(x),
        MathFn.Exp => Math.Exp(x),
        MathFn.Exp2 => Math.Pow(2, x),
        MathFn.Expm1 => Expm1(x),
        MathFn.Log => Math.Log(x),
        MathFn.Log2 => Math.Log2(x),
        MathFn.Log10 => Math.Log10(x),
        MathFn.Log1p => Log1p(x),
        MathFn.Sqrt => Math.Sqrt(x),
        MathFn.Cbrt => Math.Cbrt(x),
        MathFn.Deg => x * (180.0 / Math.PI),
        MathFn.Rad => x * (Math.PI / 180.0),
        _ => throw new ArgumentOutOfRangeException(nameof(fn)),
    };

    // e^x - 1 without cancellation for small x (Kahan's method).
    private static double Expm1(double x)
    {
        if (Math.Abs(x) >= 1e-5) return Math.Exp(x) - 1.0;
        return x + 0.5 * x * x + x * x * x / 6.0;
    }

    // ln(1 + x) without cancellation for small x.
    private static double Log1p(double x)
    {
        double u = 1.0 + x;
        if (u == 1.0) return x;
        return Math.Log(u) * x / (u - 1.0);
    }
}
