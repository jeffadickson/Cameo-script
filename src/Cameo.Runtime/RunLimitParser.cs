using System.Globalization;

namespace Cameo.Runtime;

/// <summary>
/// Parses command-line watchdog thresholds consistently for Teal and Cameo. These
/// validate requested VM limits; they do not impose limits on development processes.
/// </summary>
public static class RunLimitParser
{
    /// <summary>Largest CLI timeout, in seconds (one day).</summary>
    public const int MaxTimeoutSeconds = 86400;

    /// <summary>Largest CLI managed-heap threshold, in binary mebibytes.</summary>
    public const long MaxMemoryMiB = int.MaxValue;

    /// <summary>Number of bytes in one mebibyte.</summary>
    public const long BytesPerMiB = 1048576;

    /// <summary>
    /// Parses invariant seconds into a positive timeout of at least one runtime tick,
    /// bounded by <see cref="MaxTimeoutSeconds"/>. Failure returns a zero timeout.
    /// </summary>
    /// <param name="text">Invariant decimal or exponent-form seconds.</param>
    /// <param name="timeout">The validated timeout, or zero on failure.</param>
    /// <returns>Whether the input describes a supported CLI timeout.</returns>
    public static bool TryTimeout(string? text, out TimeSpan timeout)
    {
        timeout = TimeSpan.Zero;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            || !double.IsFinite(seconds) || seconds <= 0 || seconds > MaxTimeoutSeconds)
            return false;
        // The finite one-day bound makes conversion representable. Very small positive
        // inputs can still truncate to zero ticks and must not become an ineffective limit.
        TimeSpan converted = TimeSpan.FromSeconds(seconds);
        if (converted <= TimeSpan.Zero) return false;
        timeout = converted;
        return true;
    }

    /// <summary>
    /// Parses unsigned invariant decimal MiB within <see cref="MaxMemoryMiB"/> and
    /// returns the exact byte threshold. Signs, separators and fractional values fail.
    /// </summary>
    /// <param name="text">Unsigned invariant decimal mebibytes.</param>
    /// <param name="bytes">The exact byte threshold, or zero on failure.</param>
    /// <returns>Whether the input is a supported positive MiB count.</returns>
    public static bool TryMemoryMiB(string? text, out long bytes)
    {
        bytes = 0;
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long mib)
            && TryScaleMemoryMiB(mib, out bytes);
    }

    /// <summary>
    /// Converts a positive bounded MiB count to bytes without Int64 overflow. Failure
    /// returns zero. This pure boundary is also checked against the verified Dafny model.
    /// </summary>
    /// <param name="mib">A requested MiB count.</param>
    /// <param name="bytes">The exact byte threshold, or zero on failure.</param>
    /// <returns>Whether scaling is supported and representable.</returns>
    public static bool TryScaleMemoryMiB(long mib, out long bytes)
    {
        bytes = 0;
        if (mib < 1 || mib > MaxMemoryMiB) return false;
        bytes = checked(mib * BytesPerMiB);
        return true;
    }
}
