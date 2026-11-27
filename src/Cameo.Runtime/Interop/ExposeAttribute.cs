using Cameo.Runtime.Types;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Interop;

/// <summary>
/// Marks a public method or property on a host object as visible to scripts via
/// <see cref="ScriptEngine.Expose"/>. When any member on a type carries <see cref="ExposeAttribute"/>,
/// only explicitly marked members are exposed (opt-in mode).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = true)]
public sealed class ExposeAttribute : Attribute
{
    /// <summary>Optional script-visible name overriding the default camelCase member name.</summary>
    public string? Name { get; }

    /// <summary>When true on a <see cref="Task"/> or <see cref="Task{TResult}"/>-returning method,
    /// exposes the call as a script <c>Future</c>.</summary>
    public bool AsFuture { get; init; }

    /// <summary>Exposes the member using its default camelCase script name.</summary>
    public ExposeAttribute() { }

    /// <summary>Exposes the member under an explicit script name.</summary>
    public ExposeAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }
}

/// <summary>
/// Excludes a public method or property from being exposed to scripts via <see cref="ScriptEngine.Expose"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = true)]
public sealed class HideAttribute : Attribute;

/// <summary>
/// Annotates a host-exposed numeric (<c>double</c>/<c>float</c>/<c>decimal</c>), numeric sequence,
/// or <c>SharpUnits.Quantity</c> parameter/property/return value with a physical unit expression
/// (e.g. <c>"Ohm"</c>, <c>"F"</c>, <c>"H"</c>, <c>"V"</c>, <c>"A"</c>, <c>"mm"</c>, <c>"s"</c>, <c>"Hz"</c>).
/// <para>
/// On numeric parameters, Cameo scripts may pass either a plain <c>number</c> (when <see cref="AllowNumber"/>
/// is <see langword="true"/>) or a dimensionally compatible <c>SharpUnits.Quantity</c> (automatically converted
/// into <see cref="Unit"/> at runtime), while dimension mismatches (such as Volts passed to a Resistor) are
/// rejected at compile time by the Cameo type checker.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.Property, Inherited = true)]
public sealed class UnitAttribute : Attribute
{
    /// <summary>Target unit expression (e.g. <c>"Ohm"</c>, <c>"V"</c>, <c>"mm"</c>).</summary>
    public string Unit { get; }

    /// <summary>
    /// When <see langword="true"/> (default) on numeric parameters, plain numbers are accepted alongside
    /// dimension-checked <c>SharpUnits.Quantity</c> values. Set to <see langword="false"/> to require a
    /// <c>SharpUnits.Quantity</c> exclusively.
    /// </summary>
    public bool AllowNumber { get; init; } = true;

    public UnitAttribute(string unit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        Unit = unit;
    }
}

/// <summary>
/// Implemented by physical-quantity userdata (such as <c>SharpUnits.Quantity</c> in <c>Cameo.Units</c>)
/// so <see cref="HostInterop"/> can validate physical dimensions and convert values into a host's
/// target unit without coupling <c>Cameo.Runtime</c> to a specific units library.
/// </summary>
public interface IHostQuantity
{
    PhysicalDimension Dimension { get; }
    string UnitSymbol { get; }
    object UnderlyingQuantity { get; }
    bool TryConvertToUnit(string targetUnitExpression, out double value, out string? error);
}

/// <summary>
/// Extension hook allowing libraries such as <c>Cameo.Units</c> to marshal custom CLR types
/// (e.g. <c>SharpUnits.Quantity&lt;double&gt;</c>) across <see cref="ScriptEngine.Register"/>,
/// <see cref="ScriptEngine.Expose"/>, and <see cref="ScriptEngine.DefineValue"/>.
/// </summary>
public interface IHostTypeMarshaller
{
    bool CanMarshal(Type clrType);
    string GetTealType(Type clrType, UnitAttribute? unitAttr);
    string GetTealValueType(object value, Type runtimeType);
    object? FromLuaValue(LuaState state, Value value, Type targetType, UnitAttribute? unitAttr, string label, int argIndex);
    Value ToLuaValue(LuaState state, object value, Type declaredType);
}
