using CsCheck;
using Hedgehog;
using Hedgehog.Linq;

namespace Cameo.Tests.Properties;

/// <summary>
/// Shared configuration for the property tests: fixed seeds and bounded iteration counts, so
/// that runs are reproducible in CI and the whole project stays fast.
/// </summary>
internal static class Prop
{
    /// <summary>Seed for CsCheck samples (set the CsCheck_Seed environment variable to explore other seeds).</summary>
    public const string CsSeed = "0000KOIPe0C6";

    /// <summary>Campaign seed shared across both frameworks, with independent replay overrides.</summary>
    internal static PropertySeedSettings Seeds => PropertySeedSettings.FromEnvironment();

    /// <summary>Runs a CsCheck property deterministically on one thread.</summary>
    public static void Cs<T>(CsCheck.Gen<T> gen, Action<T> assert, long iter = 400, Func<T, string>? print = null) =>
        gen.Sample(assert, seed: Seeds.CsCheckSeed, iter: iter, threads: 1, print: print);

    private static IPropertyConfig Config(int tests) =>
        PropertyConfig.withSeed(SeedModule.from(Seeds.HedgehogSeed), PropertyConfig.Default).WithTests(tests);

    /// <summary>Runs a Hedgehog property with a fixed seed; an exception thrown by <paramref name="assert"/> is a failure.</summary>
    public static void Hh<T>(Hedgehog.Gen<T> gen, Action<T> assert, int tests = 200)
    {
        var property = from x in gen.ForAll() select Run(assert, x);
        property.Check(Config(tests));
    }

    private static bool Run<T>(Action<T> assert, T x)
    {
        assert(x);
        return true;
    }
}
