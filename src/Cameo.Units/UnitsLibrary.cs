using Cameo.Runtime;
using Cameo.Runtime.Interop;
using Cameo.Runtime.Types;
using SharpUnits;
using SharpUnits.Interop.Lua;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;

namespace Cameo.Units;

/// <summary>
/// <c>units</c> / <c>SharpUnits</c>: deterministic dimensional-analysis and ISO 80000
/// affine-quantity library backed by <see cref="LuaUnitsBridge"/>.
/// Safe in default, Sandboxed, and Hermetic modes; all values are immutable and transferable across workers.
/// </summary>
public static class UnitsLibrary
{
    private static readonly LuaUnitsBridge Bridge = new();
    private static readonly SharpUnitsDimensionResolver DimensionResolver = new();
    private static readonly SharpUnitsHostTypeMarshaller HostMarshaller = new();

    private const string MetaUnit = "SharpUnits.SymbolicUnit";
    private const string MetaQuantity = "SharpUnits.Quantity";
    private const string MetaPoint = "SharpUnits.QuantityPoint";
    private const string MetaSeries = "SharpUnits.QuantitySeries";

    /// <summary>Enables <c>units</c> and <c>SharpUnits</c> on <paramref name="engine"/> and all child workers.</summary>
    public static void Enable(ScriptEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.SetUnitDimensionResolver(DimensionResolver);
        engine.State.RegisterExtension(Open);
    }

    /// <summary>Opens <c>units</c> and <c>SharpUnits</c> on <paramref name="L"/>.</summary>
    public static void Open(LuaState L)
    {
        ArgumentNullException.ThrowIfNull(L);
        L.RegisterHostTypeMarshaller(HostMarshaller);
        CopyMeta(BuildUnitMeta(L), L.MetaFor(MetaUnit));
        CopyMeta(BuildQuantityMeta(L), L.MetaFor(MetaQuantity));
        CopyMeta(BuildPointMeta(L), L.MetaFor(MetaPoint));
        CopyMeta(BuildSeriesMeta(L), L.MetaFor(MetaSeries));

        var lib = new Table();
        L.Register(lib, "unit", static (ref CallContext c) =>
        {
            string expr = c.CheckText(0);
            try { return c.Return(Wrap(c.L, Bridge.Unit(expr))); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.Register(lib, "qty", static (ref CallContext c) =>
        {
            double val = c.CheckNumber(0);
            try
            {
                if (c[1].O is LuaUnit lu)
                    return c.Return(Wrap(c.L, Quantity<double>.Of(val, lu.Unit)));
                string expr = c.CheckText(1);
                return c.Return(Wrap(c.L, Bridge.Qty(val, expr)));
            }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.Register(lib, "parse", static (ref CallContext c) =>
        {
            string lit = c.CheckText(0);
            try { return c.Return(Wrap(c.L, Bridge.Parse(lit))); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.Register(lib, "point", static (ref CallContext c) =>
        {
            double val = c.CheckNumber(0);
            string expr = c.CheckText(1);
            string? origin = c.IsNoneOrNil(2) ? null : c.CheckText(2);
            try { return c.Return(Wrap(c.L, Bridge.Point(val, expr, origin))); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.Register(lib, "series", static (ref CallContext c) =>
        {
            double[] values = ExtractDoubles(ref c, 0);
            string expr = c.CheckText(1);
            string? origin = c.IsNoneOrNil(2) ? null : c.CheckText(2);
            try { return c.Return(Wrap(c.L, Bridge.Series(values, expr, origin))); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });

        L.Globals.SetStr("units", lib);
        L.Globals.SetStr("SharpUnits", lib);
        L.LoadedModules.SetStr("SharpUnits", lib);
        L.LoadedModules.SetStr("sharpunits", lib);
    }

    private static bool TryToPhysicalDimension(RationalDimension d, out PhysicalDimension result)
    {
        if (d.Length.IsInteger && d.Mass.IsInteger && d.Time.IsInteger && d.Current.IsInteger &&
            d.Temperature.IsInteger && d.Amount.IsInteger && d.Luminosity.IsInteger)
        {
            result = new PhysicalDimension(
                checked((int)d.Length.Numerator),
                checked((int)d.Mass.Numerator),
                checked((int)d.Time.Numerator),
                checked((int)d.Current.Numerator),
                checked((int)d.Temperature.Numerator),
                checked((int)d.Amount.Numerator),
                checked((int)d.Luminosity.Numerator));
            return true;
        }
        result = default;
        return false;
    }

    private static string FormatDimensionName(RationalDimension d) =>
        TryToPhysicalDimension(d, out PhysicalDimension pd) ? pd.DisplayName : d.ToCoherentSiSymbol();

    private sealed class SharpUnitsDimensionResolver : IUnitDimensionResolver
    {
        public bool TryResolveUnitDimension(string unitExpression, out PhysicalDimension? dimension, out string? error)
        {
            try
            {
                SymbolicUnit u = Bridge.Unit(unitExpression);
                dimension = TryToPhysicalDimension(u.Dimension, out PhysicalDimension pd) ? pd : null;
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                dimension = null;
                error = ex.Message;
                return false;
            }
        }

        public bool TryResolveQuantityLiteralDimension(string quantityLiteral, out PhysicalDimension? dimension, out string? error)
        {
            try
            {
                Quantity<double> q = Bridge.Parse(quantityLiteral);
                dimension = TryToPhysicalDimension(q.Unit.Dimension, out PhysicalDimension pd) ? pd : null;
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                dimension = null;
                error = ex.Message;
                return false;
            }
        }
    }

    private sealed class SharpUnitsHostTypeMarshaller : IHostTypeMarshaller
    {
        public bool CanMarshal(Type clrType) =>
            clrType == typeof(Quantity<double>) ||
            clrType == typeof(Quantity) ||
            clrType == typeof(SymbolicUnit) ||
            clrType == typeof(QuantityPoint<double>) ||
            clrType == typeof(QuantitySeries<double>);

        public string GetTealType(Type clrType, UnitAttribute? unitAttr)
        {
            if (clrType == typeof(Quantity<double>) || clrType == typeof(Quantity))
                return unitAttr != null
                    ? $"SharpUnits.Quantity<\"{Escape(unitAttr.Unit)}\">"
                    : "SharpUnits.Quantity";
            if (clrType == typeof(SymbolicUnit))
                return unitAttr != null
                    ? $"SharpUnits.SymbolicUnit<\"{Escape(unitAttr.Unit)}\">"
                    : "SharpUnits.SymbolicUnit";
            if (clrType == typeof(QuantityPoint<double>))
                return "SharpUnits.QuantityPoint";
            if (clrType == typeof(QuantitySeries<double>))
                return unitAttr != null
                    ? $"SharpUnits.QuantitySeries<\"{Escape(unitAttr.Unit)}\">"
                    : "SharpUnits.QuantitySeries";
            return "any";
        }

        public string GetTealValueType(object value, Type runtimeType)
        {
            if (value is Quantity<double> q)
                return FormatQuantityTealType("SharpUnits.Quantity", q.Unit);
            if (value is Quantity nq)
                return FormatQuantityTealType("SharpUnits.Quantity", nq.AsGeneric<double>().Unit);
            if (value is SymbolicUnit su)
                return FormatQuantityTealType("SharpUnits.SymbolicUnit", su);
            if (value is QuantityPoint<double>)
                return "SharpUnits.QuantityPoint";
            if (value is QuantitySeries<double> qs)
                return FormatQuantityTealType("SharpUnits.QuantitySeries", qs.Unit);
            return GetTealType(runtimeType, null);
        }

        public object? FromLuaValue(LuaState state, Value value, Type targetType, UnitAttribute? unitAttr, string label, int argIndex)
        {
            if (targetType == typeof(Quantity<double>) || targetType == typeof(Quantity))
            {
                if (value.O is not LuaQuantity lq)
                    throw new LuaException($"{label}: bad argument #{argIndex + 1} (expected SharpUnits.Quantity, got {value.TypeName})");
                if (unitAttr != null)
                {
                    SymbolicUnit target = Bridge.Unit(unitAttr.Unit);
                    if (lq.Qty.Unit.Dimension != target.Dimension)
                    {
                        throw new LuaException(
                            $"{label}: bad argument #{argIndex + 1} (incompatible physical dimensions: got {FormatDimensionName(lq.Qty.Unit.Dimension)} ({lq.Qty.Unit.Symbol}), expected {FormatDimensionName(target.Dimension)} ({target.Symbol}))");
                    }
                }
                return targetType == typeof(Quantity) ? lq.Qty.ToQuantity() : lq.Qty;
            }
            if (targetType == typeof(SymbolicUnit))
            {
                if (value.O is not LuaUnit lu)
                    throw new LuaException($"{label}: bad argument #{argIndex + 1} (expected SharpUnits.SymbolicUnit, got {value.TypeName})");
                if (unitAttr != null)
                {
                    SymbolicUnit target = Bridge.Unit(unitAttr.Unit);
                    if (lu.Unit.Dimension != target.Dimension)
                    {
                        throw new LuaException(
                            $"{label}: bad argument #{argIndex + 1} (incompatible physical dimensions: got {FormatDimensionName(lu.Unit.Dimension)} ({lu.Unit.Symbol}), expected {FormatDimensionName(target.Dimension)} ({target.Symbol}))");
                    }
                }
                return lu.Unit;
            }
            if (targetType == typeof(QuantityPoint<double>))
            {
                if (value.O is not LuaPoint lp)
                    throw new LuaException($"{label}: bad argument #{argIndex + 1} (expected SharpUnits.QuantityPoint, got {value.TypeName})");
                return lp.Point;
            }
            if (targetType == typeof(QuantitySeries<double>))
            {
                if (value.O is not LuaSeries ls)
                    throw new LuaException($"{label}: bad argument #{argIndex + 1} (expected SharpUnits.QuantitySeries, got {value.TypeName})");
                if (unitAttr != null)
                {
                    SymbolicUnit target = Bridge.Unit(unitAttr.Unit);
                    if (ls.Series.Unit.Dimension != target.Dimension)
                    {
                        throw new LuaException(
                            $"{label}: bad argument #{argIndex + 1} (incompatible physical dimensions: got {FormatDimensionName(ls.Series.Unit.Dimension)} ({ls.Series.Unit.Symbol}), expected {FormatDimensionName(target.Dimension)} ({target.Symbol}))");
                    }
                }
                return ls.Series;
            }
            throw new LuaException($"{label}: cannot convert argument #{argIndex + 1} to {targetType.Name}");
        }

        public Value ToLuaValue(LuaState state, object value, Type declaredType) => value switch
        {
            Quantity<double> q => Wrap(state, q),
            Quantity nq => Wrap(state, nq.AsGeneric<double>()),
            SymbolicUnit u => Wrap(state, u),
            QuantityPoint<double> p => Wrap(state, p),
            QuantitySeries<double> s => Wrap(state, s),
            _ => throw new LuaException($"cannot convert host value of type '{declaredType.Name}' to SharpUnits value"),
        };

        private static string Escape(string s) =>
            s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

        private static string FormatQuantityTealType(string baseName, SymbolicUnit u)
        {
            string? sym = !string.IsNullOrWhiteSpace(u.AsciiSymbol) ? u.AsciiSymbol : u.Symbol;
            if (!string.IsNullOrWhiteSpace(sym) &&
                TryToPhysicalDimension(u.Dimension, out var udim) &&
                DimensionResolver.TryResolveUnitDimension(sym, out var dim, out _) &&
                dim == udim)
                return $"{baseName}<\"{Escape(sym)}\">";
            return baseName;
        }
    }

    // ------------------------------------------------------------------ userdata wrappers

    private sealed class LuaUnit : IUserdata, IFreezable, IValueTransferable
    {
        public readonly SymbolicUnit Unit;
        public Table? Metatable { get; set; }
        public string TypeName => MetaUnit;
        public LuaUnit(SymbolicUnit unit, Table mt) { Unit = unit; Metatable = mt; }
        public bool IsFrozen => true;
        public void Freeze(HashSet<object> visited) { }
        public Value TransferTo(LuaState target, Func<Value, Value> copyChild) => Wrap(target, Unit);
        public override string ToString() => Unit.Symbol;
    }

    private sealed class LuaQuantity : IUserdata, IFreezable, IValueTransferable, IHostQuantity
    {
        public readonly Quantity<double> Qty;
        public Table? Metatable { get; set; }
        public string TypeName => MetaQuantity;
        public PhysicalDimension Dimension => TryToPhysicalDimension(Qty.Unit.Dimension, out var pd) ? pd : default;
        public string UnitSymbol => Qty.Unit.Symbol;
        public object UnderlyingQuantity => Qty;
        public LuaQuantity(Quantity<double> qty, Table mt) { Qty = qty; Metatable = mt; }
        public bool IsFrozen => true;
        public void Freeze(HashSet<object> visited) { }
        public Value TransferTo(LuaState target, Func<Value, Value> copyChild) => Wrap(target, Qty);
        public bool TryConvertToUnit(string targetUnitExpression, out double value, out string? error)
        {
            try
            {
                SymbolicUnit target = Bridge.Unit(targetUnitExpression);
                if (Qty.Unit.Dimension != target.Dimension)
                {
                    value = 0;
                    error = $"incompatible physical dimensions: got {FormatDimensionName(Qty.Unit.Dimension)} ({Qty.Unit.Symbol}), expected {FormatDimensionName(target.Dimension)} ({target.Symbol})";
                    return false;
                }
                value = Qty.In(target).Value;
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                value = 0;
                error = ex.Message;
                return false;
            }
        }
        public override string ToString() => Bridge.Format(Qty);
    }

    private sealed class LuaPoint : IUserdata, IFreezable, IValueTransferable
    {
        public readonly QuantityPoint<double> Point;
        public Table? Metatable { get; set; }
        public string TypeName => MetaPoint;
        public LuaPoint(QuantityPoint<double> point, Table mt) { Point = point; Metatable = mt; }
        public bool IsFrozen => true;
        public void Freeze(HashSet<object> visited) { }
        public Value TransferTo(LuaState target, Func<Value, Value> copyChild) => Wrap(target, Point);
        public override string ToString() => Point.ToString();
    }

    private sealed class LuaSeries : IUserdata, IFreezable, IValueTransferable
    {
        public readonly QuantitySeries<double> Series;
        public Table? Metatable { get; set; }
        public string TypeName => MetaSeries;
        public LuaSeries(QuantitySeries<double> series, Table mt) { Series = series; Metatable = mt; }
        public bool IsFrozen => true;
        public void Freeze(HashSet<object> visited) { }
        public Value TransferTo(LuaState target, Func<Value, Value> copyChild) => Wrap(target, Series);
        public override string ToString() => $"QuantitySeries({Series.Count}, {Series.Unit.Symbol})";
    }

    // ------------------------------------------------------------------ metatables

    private static Table BuildUnitMeta(LuaState L)
    {
        var methods = new Table();
        L.RegisterInstance<LuaUnit>(methods, "pow", static (ref CallContext c) =>
        {
            SymbolicUnit u = CheckUnit(ref c, 0).Unit;
            int exp = checked((int)c.CheckInt(1));
            try { return c.Return(Wrap(c.L, u.Pow(exp))); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.RegisterInstance<LuaUnit>(methods, "root", static (ref CallContext c) =>
        {
            SymbolicUnit u = CheckUnit(ref c, 0).Unit;
            int deg = checked((int)c.CheckInt(1));
            try { return c.Return(Wrap(c.L, u.Root(deg))); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.RegisterInstance<LuaUnit>(methods, "to_si", static (ref CallContext c) =>
        {
            SymbolicUnit u = CheckUnit(ref c, 0).Unit;
            try { return c.Return(Wrap(c.L, u.ToCoherentSi())); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });

        var mt = new Table();
        mt.SetStr("__name", MetaUnit);
        L.Register(mt, "__index", (ref CallContext c) =>
        {
            LuaUnit u = CheckUnit(ref c, 0);
            if (c[1].Kind == ValueKind.String)
            {
                switch (c[1].AsString)
                {
                    case "symbol": return c.Return(Value.FromText(u.Unit.Symbol));
                    case "ascii_symbol": return c.Return(Value.FromText(u.Unit.AsciiSymbol));
                    case "scale_to_si": return c.Return(Value.FromFloat(u.Unit.ScaleToSi));
                }
                Value m = methods.Get(c[1]);
                if (!m.IsNil) return c.Return(m);
            }
            return c.Return(Value.Nil);
        });
        L.Register(mt, "__mul", static (ref CallContext c) =>
        {
            SymbolicUnit a = CheckUnit(ref c, 0).Unit, b = CheckUnit(ref c, 1).Unit;
            try { return c.Return(Wrap(c.L, a * b)); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.Register(mt, "__div", static (ref CallContext c) =>
        {
            SymbolicUnit a = CheckUnit(ref c, 0).Unit, b = CheckUnit(ref c, 1).Unit;
            try { return c.Return(Wrap(c.L, a / b)); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.Register(mt, "__eq", static (ref CallContext c) =>
            c.Return(c[0].O is LuaUnit a && c[1].O is LuaUnit b && a.Unit.Equals(b.Unit)));
        L.Register(mt, "__tostring", static (ref CallContext c) =>
            c.Return(Value.FromText(CheckUnit(ref c, 0).Unit.Symbol)));
        return mt;
    }

    private static Table BuildQuantityMeta(LuaState L)
    {
        var methods = new Table();
        L.RegisterInstance<LuaQuantity>(methods, "in_unit", static (ref CallContext c) =>
        {
            Quantity<double> q = CheckQty(ref c, 0).Qty;
            object target = c[1].O is LuaUnit lu ? lu.Unit : c.CheckText(1);
            try { return c.Return(Wrap(c.L, Bridge.InUnit(q, target))); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.RegisterInstance<LuaQuantity>(methods, "to_si", static (ref CallContext c) =>
        {
            Quantity<double> q = CheckQty(ref c, 0).Qty;
            try { return c.Return(Wrap(c.L, q.ToCoherentSi())); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.RegisterInstance<LuaQuantity>(methods, "format", static (ref CallContext c) =>
        {
            Quantity<double> q = CheckQty(ref c, 0).Qty;
            string? fmt = c.IsNoneOrNil(1) ? null : c.CheckText(1);
            try { return c.Return(Value.FromText(Bridge.Format(q, fmt))); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.RegisterInstance<LuaQuantity>(methods, "is_equivalent", static (ref CallContext c) =>
        {
            Quantity<double> a = CheckQty(ref c, 0).Qty, b = CheckQty(ref c, 1).Qty;
            double tol = c.IsNoneOrNil(2) ? 0.0 : c.CheckNumber(2);
            try { return c.Return(a.IsEquivalentTo(b, tol)); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });

        var mt = new Table();
        mt.SetStr("__name", MetaQuantity);
        L.Register(mt, "__index", (ref CallContext c) =>
        {
            LuaQuantity q = CheckQty(ref c, 0);
            if (c[1].Kind == ValueKind.String)
            {
                switch (c[1].AsString)
                {
                    case "value": return c.Return(Value.FromFloat(q.Qty.Value));
                    case "si_value": return c.Return(Value.FromFloat(q.Qty.SiValue));
                    case "unit": return c.Return(Wrap(c.L, q.Qty.Unit));
                    case "spec": return c.Return(q.Qty.Spec is { } sp ? Value.FromText(sp.Name) : Value.Nil);
                }
                Value m = methods.Get(c[1]);
                if (!m.IsNil) return c.Return(m);
            }
            return c.Return(Value.Nil);
        });
        RegisterArithmeticMeta(L, mt, includeMulDivUnm: true);
        L.Register(mt, "__tostring", static (ref CallContext c) =>
            c.Return(Value.FromText(Bridge.Format(CheckQty(ref c, 0).Qty))));
        return mt;
    }

    private static Table BuildPointMeta(LuaState L)
    {
        var methods = new Table();
        L.RegisterInstance<LuaPoint>(methods, "in_unit", static (ref CallContext c) =>
        {
            QuantityPoint<double> p = CheckPoint(ref c, 0).Point;
            SymbolicUnit? u = c[1].O is LuaUnit lu ? lu.Unit : null;
            string? expr = u is null ? c.CheckText(1) : null;
            try { return c.Return(Wrap(c.L, p.In(u ?? Bridge.Unit(expr!)))); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.RegisterInstance<LuaPoint>(methods, "relative_to", static (ref CallContext c) =>
        {
            QuantityPoint<double> p = CheckPoint(ref c, 0).Point;
            string origin = c.CheckText(1);
            try { return c.Return(Wrap(c.L, Bridge.PointRelativeTo(p, origin))); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.RegisterInstance<LuaPoint>(methods, "shift", static (ref CallContext c) =>
        {
            QuantityPoint<double> p = CheckPoint(ref c, 0).Point;
            Quantity<double> d = CheckQty(ref c, 1).Qty;
            try { return c.Return(Wrap(c.L, Bridge.PointShift(p, d))); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.RegisterInstance<LuaPoint>(methods, "to_celsius", static (ref CallContext c) =>
        {
            QuantityPoint<double> p = CheckPoint(ref c, 0).Point;
            try { return c.Return(Wrap(c.L, p.ToCelsius())); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.RegisterInstance<LuaPoint>(methods, "to_fahrenheit", static (ref CallContext c) =>
        {
            QuantityPoint<double> p = CheckPoint(ref c, 0).Point;
            try { return c.Return(Wrap(c.L, p.ToFahrenheit())); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.RegisterInstance<LuaPoint>(methods, "to_kelvin", static (ref CallContext c) =>
        {
            QuantityPoint<double> p = CheckPoint(ref c, 0).Point;
            try { return c.Return(Wrap(c.L, p.ToKelvin())); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });

        var mt = new Table();
        mt.SetStr("__name", MetaPoint);
        L.Register(mt, "__index", (ref CallContext c) =>
        {
            LuaPoint p = CheckPoint(ref c, 0);
            if (c[1].Kind == ValueKind.String)
            {
                switch (c[1].AsString)
                {
                    case "value": return c.Return(Value.FromFloat(p.Point.Displacement.Value));
                    case "unit": return c.Return(Wrap(c.L, p.Point.Displacement.Unit));
                    case "origin": return c.Return(Value.FromText(p.Point.Origin.Name));
                }
                Value m = methods.Get(c[1]);
                if (!m.IsNil) return c.Return(m);
            }
            return c.Return(Value.Nil);
        });
        RegisterArithmeticMeta(L, mt, includeMulDivUnm: false);
        L.Register(mt, "__tostring", static (ref CallContext c) =>
            c.Return(Value.FromText(CheckPoint(ref c, 0).Point.ToString())));
        return mt;
    }

    private static void RegisterArithmeticMeta(LuaState L, Table mt, bool includeMulDivUnm)
    {
        L.Register(mt, "__add", static (ref CallContext c) =>
        {
            object a = UnwrapOperand(c[0]), b = UnwrapOperand(c[1]);
            try { return c.Return(WrapObject(c.L, Bridge.Add(a, b))); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.Register(mt, "__sub", static (ref CallContext c) =>
        {
            object a = UnwrapOperand(c[0]), b = UnwrapOperand(c[1]);
            try { return c.Return(WrapObject(c.L, Bridge.Sub(a, b))); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        if (includeMulDivUnm)
        {
            L.Register(mt, "__mul", static (ref CallContext c) =>
            {
                object a = UnwrapOperand(c[0]), b = UnwrapOperand(c[1]);
                try { return c.Return(WrapObject(c.L, Bridge.Mul(a, b))); }
                catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
            });
            L.Register(mt, "__div", static (ref CallContext c) =>
            {
                object a = UnwrapOperand(c[0]), b = UnwrapOperand(c[1]);
                try { return c.Return(WrapObject(c.L, Bridge.Div(a, b))); }
                catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
            });
            L.Register(mt, "__unm", static (ref CallContext c) =>
            {
                object a = UnwrapOperand(c[0]);
                try { return c.Return(WrapObject(c.L, Bridge.Unm(a))); }
                catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
            });
        }
        L.Register(mt, "__lt", static (ref CallContext c) =>
        {
            object a = UnwrapOperand(c[0]), b = UnwrapOperand(c[1]);
            try { return c.Return(Bridge.Lt(a, b)); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.Register(mt, "__le", static (ref CallContext c) =>
        {
            object a = UnwrapOperand(c[0]), b = UnwrapOperand(c[1]);
            try { return c.Return(Bridge.Le(a, b)); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.Register(mt, "__eq", static (ref CallContext c) =>
        {
            object a = UnwrapOperand(c[0]), b = UnwrapOperand(c[1]);
            try { return c.Return(Bridge.Eq(a, b)); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
    }

    private static Table BuildSeriesMeta(LuaState L)
    {
        var methods = new Table();
        L.RegisterInstance<LuaSeries>(methods, "in_unit", static (ref CallContext c) =>
        {
            QuantitySeries<double> s = CheckSeries(ref c, 0).Series;
            SymbolicUnit? u = c[1].O is LuaUnit lu ? lu.Unit : null;
            string? expr = u is null ? c.CheckText(1) : null;
            try { return c.Return(Wrap(c.L, u is not null ? s.In(u) : Bridge.SeriesInUnit(s, expr!))); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.RegisterInstance<LuaSeries>(methods, "auto_scale", static (ref CallContext c) =>
        {
            QuantitySeries<double> s = CheckSeries(ref c, 0).Series;
            try { return c.Return(Wrap(c.L, s.AutoScale())); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.RegisterInstance<LuaSeries>(methods, "relative_to", static (ref CallContext c) =>
        {
            QuantitySeries<double> s = CheckSeries(ref c, 0).Series;
            string origin = c.CheckText(1);
            try { return c.Return(Wrap(c.L, s.RelativeTo(Bridge.Environment.ResolveOrigin(origin)))); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.RegisterInstance<LuaSeries>(methods, "min", static (ref CallContext c) =>
        {
            QuantitySeries<double> s = CheckSeries(ref c, 0).Series;
            try { return c.Return(Wrap(c.L, s.MinMax().Min)); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.RegisterInstance<LuaSeries>(methods, "max", static (ref CallContext c) =>
        {
            QuantitySeries<double> s = CheckSeries(ref c, 0).Series;
            try { return c.Return(Wrap(c.L, s.MinMax().Max)); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.RegisterInstance<LuaSeries>(methods, "mean", static (ref CallContext c) =>
        {
            QuantitySeries<double> s = CheckSeries(ref c, 0).Series;
            try { return c.Return(Wrap(c.L, s.Mean())); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.RegisterInstance<LuaSeries>(methods, "rms", static (ref CallContext c) =>
        {
            QuantitySeries<double> s = CheckSeries(ref c, 0).Series;
            try { return c.Return(Wrap(c.L, s.Rms())); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.RegisterInstance<LuaSeries>(methods, "peak_to_peak", static (ref CallContext c) =>
        {
            QuantitySeries<double> s = CheckSeries(ref c, 0).Series;
            try { return c.Return(Wrap(c.L, s.PeakToPeak())); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.RegisterInstance<LuaSeries>(methods, "at", static (ref CallContext c) =>
        {
            QuantitySeries<double> s = CheckSeries(ref c, 0).Series;
            int idx = checked((int)c.CheckInt(1));
            try { return c.Return(Wrap(c.L, Bridge.SeriesAt(s, idx))); }
            catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
        });
        L.RegisterInstance<LuaSeries>(methods, "to_list", static (ref CallContext c) =>
        {
            QuantitySeries<double> s = CheckSeries(ref c, 0).Series;
            var list = new LuaList(c.L, s.Count);
            for (int i = 0; i < s.Count; i++) list.Items.Add(Value.FromFloat(s[i].Value));
            return c.Return(Value.FromUserdata(list));
        });

        var mt = new Table();
        mt.SetStr("__name", MetaSeries);
        L.Register(mt, "__index", (ref CallContext c) =>
        {
            LuaSeries s = CheckSeries(ref c, 0);
            if (c[1].Kind == ValueKind.String)
            {
                switch (c[1].AsString)
                {
                    case "count": return c.Return(Value.FromInt(s.Series.Count));
                    case "unit": return c.Return(Wrap(c.L, s.Series.Unit));
                }
                Value m = methods.Get(c[1]);
                if (!m.IsNil) return c.Return(m);
            }
            if (c[1].TryToInteger(out long idx))
            {
                try { return c.Return(Wrap(c.L, Bridge.SeriesAt(s.Series, checked((int)idx)))); }
                catch (Exception ex) when (ex is not LuaException) { throw new LuaException($"units: {ex.Message}"); }
            }
            return c.Return(Value.Nil);
        });
        L.Register(mt, "__len", static (ref CallContext c) =>
            c.Return(Value.FromInt(CheckSeries(ref c, 0).Series.Count)));
        L.Register(mt, "__tostring", static (ref CallContext c) =>
            c.Return(Value.FromText(CheckSeries(ref c, 0).ToString())));
        return mt;
    }

    // ------------------------------------------------------------------ helpers

    private static void CopyMeta(Table src, Table dst)
    {
        foreach (KeyValuePair<Value, Value> kv in src.Pairs()) dst.Set(kv.Key, kv.Value);
    }

    private static Value Wrap(LuaState L, SymbolicUnit u) =>
        Value.FromUserdata(new LuaUnit(u, GetMeta(L, MetaUnit)));
    private static Value Wrap(LuaState L, Quantity<double> q) =>
        Value.FromUserdata(new LuaQuantity(q, GetMeta(L, MetaQuantity)));
    private static Value Wrap(LuaState L, QuantityPoint<double> p) =>
        Value.FromUserdata(new LuaPoint(p, GetMeta(L, MetaPoint)));
    private static Value Wrap(LuaState L, QuantitySeries<double> s) =>
        Value.FromUserdata(new LuaSeries(s, GetMeta(L, MetaSeries)));

    private static Table GetMeta(LuaState L, string key)
    {
        Table mt = L.MetaFor(key);
        if (mt.GetStr("__index").IsNil) Open(L);
        return mt;
    }

    private static Value WrapObject(LuaState L, object obj) => obj switch
    {
        Quantity<double> q => Wrap(L, q),
        QuantityPoint<double> p => Wrap(L, p),
        SymbolicUnit u => Wrap(L, u),
        QuantitySeries<double> s => Wrap(L, s),
        double d => Value.FromFloat(d),
        long i => Value.FromInt(i),
        bool b => Value.FromBool(b),
        string str => Value.FromText(str),
        _ => throw new LuaException($"units: unexpected result type '{obj?.GetType().Name ?? "nil"}'"),
    };

    private static object UnwrapOperand(Value v) => v.O switch
    {
        LuaQuantity q => q.Qty,
        LuaPoint p => p.Point,
        LuaUnit u => u.Unit,
        LuaSeries s => s.Series,
        _ when v.TryToDouble(out double d) => d,
        _ => throw new LuaException($"units: unsupported operand type '{LuaState.TypeNameOf(v)}'"),
    };

    private static LuaUnit CheckUnit(ref CallContext c, int i) =>
        c[i].O as LuaUnit ?? throw c.ArgError(i, "expected SymbolicUnit");
    private static LuaQuantity CheckQty(ref CallContext c, int i) =>
        c[i].O as LuaQuantity ?? throw c.ArgError(i, "expected Quantity");
    private static LuaPoint CheckPoint(ref CallContext c, int i) =>
        c[i].O as LuaPoint ?? throw c.ArgError(i, "expected QuantityPoint");
    private static LuaSeries CheckSeries(ref CallContext c, int i) =>
        c[i].O as LuaSeries ?? throw c.ArgError(i, "expected QuantitySeries");

    private static double[] ExtractDoubles(ref CallContext c, int argIndex)
    {
        Value v = c[argIndex];
        if (v.O is LuaList list)
        {
            var arr = new double[list.Items.Count];
            for (int i = 0; i < arr.Length; i++)
            {
                if (!list.Items[i].TryToDouble(out double d))
                    throw c.ArgError(argIndex, $"element {i + 1} must be a number");
                arr[i] = d;
            }
            return arr;
        }
        if (v.O is FloatArray fa)
        {
            var arr = new double[fa.Data.Length];
            Array.Copy(fa.Data, arr, arr.Length);
            return arr;
        }
        Table t = c.CheckTable(argIndex);
        int n = t.Length;
        var res = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (!t.GetInt(i + 1).TryToDouble(out double d))
                throw c.ArgError(argIndex, $"element {i + 1} must be a number");
            res[i] = d;
        }
        return res;
    }
}
