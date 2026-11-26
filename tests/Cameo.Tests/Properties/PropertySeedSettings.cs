using System.Globalization;
using CsCheck;

namespace Cameo.Tests.Properties;

/// <summary>Replayable framework seeds for ordinary runs and externally bounded campaigns.</summary>
internal readonly record struct PropertySeedSettings(string CsCheckSeed, ulong HedgehogSeed)
{
    internal static PropertySeedSettings FromValues(string? campaign, string? csCheck, string? hedgehog)
    {
        ulong? seed = Parse(campaign, "TEALVM_TEST_SEED");
        // Let the framework serialize PCG replay data; do not invent a seed encoding.
        string cs = string.IsNullOrEmpty(csCheck)
            ? seed.HasValue ? new PCG(0, seed.Value).ToString() : Prop.CsSeed
            : csCheck;
        _ = PCG.Parse(cs); // reject malformed explicit replay data before sampling
        return new(cs, Parse(hedgehog, "Hedgehog_Seed") ?? seed ?? 20261114UL);
    }

    internal static PropertySeedSettings FromEnvironment() => FromValues(
        Environment.GetEnvironmentVariable("TEALVM_TEST_SEED"),
        Environment.GetEnvironmentVariable("CsCheck_Seed"),
        Environment.GetEnvironmentVariable("Hedgehog_Seed"));

    private static ulong? Parse(string? value, string name)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong seed)) return seed;
        throw new FormatException($"{name} must be an unsigned 64-bit decimal replay seed");
    }
}
