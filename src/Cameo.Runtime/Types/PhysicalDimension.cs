using System.Text;

namespace Cameo.Runtime.Types;

/// <summary>
/// Integer exponents of the seven SI base dimensions (L, M, T, I, Θ, N, J) used by Cameo's
/// static type checker to enforce physical dimension safety at compile time.
/// </summary>
public readonly record struct PhysicalDimension(
    int Length,
    int Mass,
    int Time,
    int Current,
    int Temperature,
    int Amount,
    int Luminosity)
{
    public static PhysicalDimension None => default;
    public static PhysicalDimension LengthDimension => new(1, 0, 0, 0, 0, 0, 0);
    public static PhysicalDimension MassDimension => new(0, 1, 0, 0, 0, 0, 0);
    public static PhysicalDimension TimeDimension => new(0, 0, 1, 0, 0, 0, 0);
    public static PhysicalDimension ElectricCurrent => new(0, 0, 0, 1, 0, 0, 0);
    public static PhysicalDimension ThermodynamicTemperature => new(0, 0, 0, 0, 1, 0, 0);
    public static PhysicalDimension AmountOfSubstance => new(0, 0, 0, 0, 0, 1, 0);
    public static PhysicalDimension LuminousIntensity => new(0, 0, 0, 0, 0, 0, 1);

    public static PhysicalDimension Area => new(2, 0, 0, 0, 0, 0, 0);
    public static PhysicalDimension Volume => new(3, 0, 0, 0, 0, 0, 0);
    public static PhysicalDimension Velocity => new(1, 0, -1, 0, 0, 0, 0);
    public static PhysicalDimension Acceleration => new(1, 0, -2, 0, 0, 0, 0);
    public static PhysicalDimension Frequency => new(0, 0, -1, 0, 0, 0, 0);
    public static PhysicalDimension Force => new(1, 1, -2, 0, 0, 0, 0);
    public static PhysicalDimension Pressure => new(-1, 1, -2, 0, 0, 0, 0);
    public static PhysicalDimension Energy => new(2, 1, -2, 0, 0, 0, 0);
    public static PhysicalDimension Power => new(2, 1, -3, 0, 0, 0, 0);
    public static PhysicalDimension ElectricCharge => new(0, 0, 1, 1, 0, 0, 0);
    public static PhysicalDimension Voltage => new(2, 1, -3, -1, 0, 0, 0);
    public static PhysicalDimension Capacitance => new(-2, -1, 4, 2, 0, 0, 0);
    public static PhysicalDimension Resistance => new(2, 1, -3, -2, 0, 0, 0);
    public static PhysicalDimension Conductance => new(-2, -1, 3, 2, 0, 0, 0);
    public static PhysicalDimension MagneticFlux => new(2, 1, -2, -1, 0, 0, 0);
    public static PhysicalDimension MagneticFluxDensity => new(0, 1, -2, -1, 0, 0, 0);
    public static PhysicalDimension Inductance => new(2, 1, -2, -2, 0, 0, 0);
    public static PhysicalDimension Density => new(-3, 1, 0, 0, 0, 0, 0);
    public static PhysicalDimension Momentum => new(1, 1, -1, 0, 0, 0, 0);

    public bool IsDimensionless =>
        Length == 0 && Mass == 0 && Time == 0 && Current == 0 &&
        Temperature == 0 && Amount == 0 && Luminosity == 0;

    public static PhysicalDimension operator *(PhysicalDimension a, PhysicalDimension b) => new(
        checked(a.Length + b.Length),
        checked(a.Mass + b.Mass),
        checked(a.Time + b.Time),
        checked(a.Current + b.Current),
        checked(a.Temperature + b.Temperature),
        checked(a.Amount + b.Amount),
        checked(a.Luminosity + b.Luminosity));

    public static PhysicalDimension operator /(PhysicalDimension a, PhysicalDimension b) => new(
        checked(a.Length - b.Length),
        checked(a.Mass - b.Mass),
        checked(a.Time - b.Time),
        checked(a.Current - b.Current),
        checked(a.Temperature - b.Temperature),
        checked(a.Amount - b.Amount),
        checked(a.Luminosity - b.Luminosity));

    public PhysicalDimension Pow(int power) => new(
        checked(Length * power),
        checked(Mass * power),
        checked(Time * power),
        checked(Current * power),
        checked(Temperature * power),
        checked(Amount * power),
        checked(Luminosity * power));

    public bool TryRoot(int root, out PhysicalDimension result)
    {
        if (root <= 0 ||
            Length % root != 0 || Mass % root != 0 || Time % root != 0 || Current % root != 0 ||
            Temperature % root != 0 || Amount % root != 0 || Luminosity % root != 0)
        {
            result = default;
            return false;
        }
        result = new PhysicalDimension(
            Length / root, Mass / root, Time / root, Current / root,
            Temperature / root, Amount / root, Luminosity / root);
        return true;
    }

    public string DisplayName
    {
        get
        {
            if (IsDimensionless) return "Dimensionless";
            if (this == LengthDimension) return "Length";
            if (this == MassDimension) return "Mass";
            if (this == TimeDimension) return "Time";
            if (this == ElectricCurrent) return "Current";
            if (this == ThermodynamicTemperature) return "Temperature";
            if (this == AmountOfSubstance) return "Amount";
            if (this == LuminousIntensity) return "LuminousIntensity";
            if (this == Area) return "Area";
            if (this == Volume) return "Volume";
            if (this == Velocity) return "Velocity";
            if (this == Acceleration) return "Acceleration";
            if (this == Frequency) return "Frequency";
            if (this == Force) return "Force";
            if (this == Pressure) return "Pressure";
            if (this == Energy) return "Energy";
            if (this == Power) return "Power";
            if (this == ElectricCharge) return "Charge";
            if (this == Voltage) return "Voltage";
            if (this == Capacitance) return "Capacitance";
            if (this == Resistance) return "Resistance";
            if (this == Conductance) return "Conductance";
            if (this == MagneticFlux) return "MagneticFlux";
            if (this == MagneticFluxDensity) return "MagneticFluxDensity";
            if (this == Inductance) return "Inductance";
            if (this == Density) return "Density";
            if (this == Momentum) return "Momentum";

            var sb = new StringBuilder();
            AppendTerm(sb, "L", Length);
            AppendTerm(sb, "M", Mass);
            AppendTerm(sb, "T", Time);
            AppendTerm(sb, "I", Current);
            AppendTerm(sb, "Θ", Temperature);
            AppendTerm(sb, "N", Amount);
            AppendTerm(sb, "J", Luminosity);
            return sb.ToString();
        }
    }

    private static void AppendTerm(StringBuilder sb, string sym, int exp)
    {
        if (exp == 0) return;
        if (sb.Length > 0) sb.Append('·');
        sb.Append(sym);
        if (exp != 1) sb.Append('^').Append(exp);
    }

    public override string ToString() => DisplayName;

    /// <summary>
    /// Minimal built-in fallback table for prelude declarations when a standalone
    /// <see cref="TypeChecker"/> is created without <c>Cameo.Units.UnitsLibrary.Enable</c>.
    /// </summary>
    internal static bool TryParseBuiltin(string unitExpression, out PhysicalDimension dimension)
    {
        switch (unitExpression.Trim())
        {
            case "1":
            case "dimensionless":
            case "%":
            case "ppm":
                dimension = None; return true;
            case "m": case "km": case "cm": case "mm": case "um": case "µm": case "nm": case "mil": case "in":
                dimension = LengthDimension; return true;
            case "m^2": case "mm^2": case "cm^2":
                dimension = Area; return true;
            case "m^3": case "L": case "mL":
                dimension = Volume; return true;
            case "kg": case "g": case "mg":
                dimension = MassDimension; return true;
            case "s": case "ms": case "us": case "µs": case "ns": case "ps": case "min": case "h":
                dimension = TimeDimension; return true;
            case "A": case "mA": case "uA": case "µA": case "nA": case "pA": case "kA":
                dimension = ElectricCurrent; return true;
            case "K": case "°C": case "degC": case "°F": case "degF":
                dimension = ThermodynamicTemperature; return true;
            case "mol":
                dimension = AmountOfSubstance; return true;
            case "cd":
                dimension = LuminousIntensity; return true;
            case "m/s":
                dimension = Velocity; return true;
            case "m/s^2":
                dimension = Acceleration; return true;
            case "Hz": case "kHz": case "MHz": case "GHz":
                dimension = Frequency; return true;
            case "N": case "kN":
                dimension = Force; return true;
            case "Pa": case "kPa": case "MPa": case "bar":
                dimension = Pressure; return true;
            case "J": case "kJ": case "MJ": case "mJ": case "eV":
                dimension = Energy; return true;
            case "W": case "mW": case "uW": case "µW": case "kW": case "MW":
                dimension = Power; return true;
            case "C": case "mC": case "uC": case "µC": case "nC": case "pC":
                dimension = ElectricCharge; return true;
            case "V": case "mV": case "uV": case "µV": case "nV": case "kV":
                dimension = Voltage; return true;
            case "F": case "mF": case "uF": case "µF": case "nF": case "pF": case "fF":
                dimension = Capacitance; return true;
            case "Ohm": case "ohm": case "Ω": case "mOhm": case "kOhm": case "MOhm": case "GOhm":
            case "mΩ": case "kΩ": case "MΩ": case "GΩ":
                dimension = Resistance; return true;
            case "S": case "mS": case "uS": case "µS":
                dimension = Conductance; return true;
            case "Wb":
                dimension = MagneticFlux; return true;
            case "T": case "mT":
                dimension = MagneticFluxDensity; return true;
            case "H": case "mH": case "uH": case "µH": case "nH": case "pH":
                dimension = Inductance; return true;
            default:
                dimension = default;
                return false;
        }
    }
}

/// <summary>
/// Resolves unit expressions (e.g. <c>"kOhm"</c>, <c>"m/s^2"</c>) and quantity literals
/// (e.g. <c>"2.2kOhm"</c>) into their SI <see cref="PhysicalDimension"/> at compile time.
/// Returns <see langword="true"/> with a <see langword="null"/> dimension for valid fractional-exponent
/// units (such as <c>nV/sqrt(Hz)</c>), and <see langword="false"/> for invalid unit syntax.
/// Implemented by <c>Cameo.Units</c> using <c>SharpUnits</c>.
/// </summary>
public interface IUnitDimensionResolver
{
    bool TryResolveUnitDimension(string unitExpression, out PhysicalDimension? dimension, out string? error);
    bool TryResolveQuantityLiteralDimension(string quantityLiteral, out PhysicalDimension? dimension, out string? error);
}
