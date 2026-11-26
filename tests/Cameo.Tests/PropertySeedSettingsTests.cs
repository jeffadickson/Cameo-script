using CsCheck;
using Cameo.Tests.Properties;
using Xunit;

namespace Cameo.Tests;

public class PropertySeedSettingsTests
{
    [Fact]
    public void Absent_campaign_preserves_existing_replay_defaults()
    {
        var settings = PropertySeedSettings.FromValues(null, null, null);
        Assert.Equal("0000KOIPe0C6", settings.CsCheckSeed);
        Assert.Equal(20261114UL, settings.HedgehogSeed);
    }

    [Theory]
    [InlineData("0", 0UL)]
    [InlineData("42", 42UL)]
    [InlineData("18446744073709551615", ulong.MaxValue)]
    public void Campaign_seed_is_replayable_by_both_frameworks(string text, ulong expected)
    {
        var settings = PropertySeedSettings.FromValues(text, null, null);
        Assert.Equal(expected, settings.HedgehogSeed);
        Assert.Equal(new PCG(0, expected).ToString(), settings.CsCheckSeed);
        Assert.Equal(settings.CsCheckSeed, PCG.Parse(settings.CsCheckSeed).ToString());
        Assert.Equal(settings, PropertySeedSettings.FromValues(text, null, null));
    }

    [Fact]
    public void Explicit_failure_replay_overrides_campaign_seed()
    {
        var settings = PropertySeedSettings.FromValues("42", "0000KOIPe0C6", "99");
        Assert.Equal("0000KOIPe0C6", settings.CsCheckSeed);
        Assert.Equal(99UL, settings.HedgehogSeed);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("18446744073709551616")]
    [InlineData("not-a-seed")]
    public void Invalid_campaign_seed_is_rejected_before_sampling(string invalid)
    {
        Assert.Throws<FormatException>(() => PropertySeedSettings.FromValues(invalid, null, null));
    }
}
