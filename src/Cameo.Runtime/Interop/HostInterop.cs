using System.Collections;
using System.Reflection;
using System.Text;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;

namespace Cameo.Runtime.Interop;

/// <summary>
/// Userdata wrapper for a host .NET object exposed via <see cref="ScriptEngine.Expose"/>.
/// Supports live property reads/writes and bound method dispatch without passing <c>self</c>.
/// </summary>
internal sealed class HostObject : IUserdata
{
    internal readonly object Target;
    internal readonly string HostName;
    internal readonly Dictionary<string, NativeFunction> Methods;
    internal readonly Dictionary<string, Func<LuaState, Value>> Getters;
    internal readonly Dictionary<string, Action<LuaState, Value>> Setters;

    public HostObject(
        object target,
        string hostName,
        Table metatable,
        Dictionary<string, NativeFunction> methods,
        Dictionary<string, Func<LuaState, Value>> getters,
        Dictionary<string, Action<LuaState, Value>> setters)
    {
        Target = target;
        HostName = hostName;
        Metatable = metatable;
        Methods = methods;
        Getters = getters;
        Setters = setters;
    }

    public string TypeName => HostName;
    public Table? Metatable { get; set; }
}

/// <summary>
/// Typed .NET host interop (inspired by Starlark-cs): marshals C# delegates, objects, and values
/// into Cameo.Runtime/Cameo globals while automatically generating matching static type declarations for
/// the type checker.
/// </summary>
internal static class HostInterop
{
    private static int _recordCounter;

    internal static void ValidateIdentifier(string name, string paramName)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Identifier must be non-empty", paramName);
        if (!(char.IsAsciiLetter(name[0]) || name[0] == '_'))
            throw new ArgumentException($"Invalid script identifier '{name}'", paramName);
        for (int i = 1; i < name.Length; i++)
        {
            char c = name[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                throw new ArgumentException($"Invalid script identifier '{name}'", paramName);
        }
    }

    internal static string DefaultScriptMemberName(string clrName)
    {
        if (string.IsNullOrEmpty(clrName)) return clrName;
        if (clrName.Length == 1) return clrName.ToLowerInvariant();
        if (char.IsUpper(clrName[0]) && !char.IsUpper(clrName[1]))
            return char.ToLowerInvariant(clrName[0]) + clrName[1..];
        return char.ToLowerInvariant(clrName[0]) + clrName[1..];
    }

    // ------------------------------------------------------------------ Register (delegate)

    internal static (NativeFunction Fn, string Declaration) BindDelegate(
        LuaState state,
        string name,
        Delegate fn,
        bool asFuture,
        Types.IUnitDimensionResolver? unitResolver)
    {
        ValidateIdentifier(name, nameof(name));
        ArgumentNullException.ThrowIfNull(fn);

        MethodInfo method = fn.Method;
        ValidateMethodSignature(state, name, method, asFuture, unitResolver);
        NativeFunction native = BuildNative(name, fn.Target, method, asFuture);
        string sig = BuildFunctionTypeSignature(state, method, asFuture);
        return (native, $"global {name}: {sig}\n");
    }

    // ------------------------------------------------------------------ Expose (object)

    internal static (HostObject Obj, string Declaration) BindObject(
        LuaState state,
        string name,
        object target,
        Types.IUnitDimensionResolver? unitResolver)
    {
        ValidateIdentifier(name, nameof(name));
        ArgumentNullException.ThrowIfNull(target);

        Type type = target.GetType();
        BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        PropertyInfo[] allProps = type.GetProperties(flags)
            .Where(p => p.GetIndexParameters().Length == 0 && p.DeclaringType != typeof(object))
            .ToArray();
        MethodInfo[] allMethods = type.GetMethods(flags)
            .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
            .ToArray();

        bool optIn = allProps.Any(p => p.IsDefined(typeof(ExposeAttribute), true))
                  || allMethods.Any(m => m.IsDefined(typeof(ExposeAttribute), true));

        var methods = new Dictionary<string, NativeFunction>(StringComparer.Ordinal);
        var getters = new Dictionary<string, Func<LuaState, Value>>(StringComparer.Ordinal);
        var setters = new Dictionary<string, Action<LuaState, Value>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var fieldsDecl = new List<string>();

        foreach (PropertyInfo prop in allProps)
        {
            if (prop.IsDefined(typeof(HideAttribute), true)) continue;
            ExposeAttribute? attr = prop.GetCustomAttribute<ExposeAttribute>(true);
            if (optIn && attr == null) continue;

            string scriptName = attr?.Name ?? DefaultScriptMemberName(prop.Name);
            ValidateIdentifier(scriptName, prop.Name);
            if (!seen.Add(scriptName))
                throw new ArgumentException($"Duplicate exposed member name '{scriptName}' on {type.Name}; rename with [Expose(\"...\")] or mark [Hide]");
            if (prop.PropertyType.IsPointer || prop.PropertyType.IsByRefLike)
                throw new ArgumentException($"Unsupported property type '{prop.PropertyType.Name}' on {type.Name}.{prop.Name}");

            MethodInfo? getter = prop.GetGetMethod();
            if (getter == null)
                throw new ArgumentException($"Exposed property {type.Name}.{prop.Name} must have a public getter");

            UnitAttribute? propUnit = prop.GetCustomAttribute<UnitAttribute>(true);
            string memberLabel = $"{name}.{scriptName}";
            if (propUnit != null)
                ValidateUnitAttribute(state, memberLabel, prop.PropertyType, propUnit, unitResolver);

            getters[scriptName] = s =>
            {
                try
                {
                    object? raw = getter.Invoke(target, null);
                    return ToLuaValue(s, raw, prop.PropertyType);
                }
                catch (TargetInvocationException tie)
                {
                    throw WrapHostException(memberLabel, tie.InnerException ?? tie);
                }
                catch (Exception ex) when (ex is not LuaException and not LuaAbortException and not LuaExitException)
                {
                    throw WrapHostException(memberLabel, ex);
                }
            };

            MethodInfo? setter = prop.GetSetMethod();
            if (setter != null)
            {
                setters[scriptName] = (s, val) =>
                {
                    try
                    {
                        object? arg = FromLuaValue(s, val, prop.PropertyType, propUnit, memberLabel, 0);
                        setter.Invoke(target, [arg]);
                    }
                    catch (TargetInvocationException tie)
                    {
                        throw WrapHostException(memberLabel, tie.InnerException ?? tie);
                    }
                    catch (Exception ex) when (ex is not LuaException and not LuaAbortException and not LuaExitException)
                    {
                        throw WrapHostException(memberLabel, ex);
                    }
                };
            }

            fieldsDecl.Add($"    {scriptName}: {ToTealReturnType(state, prop.PropertyType, propUnit)}");
        }

        foreach (MethodInfo method in allMethods)
        {
            if (method.IsDefined(typeof(HideAttribute), true)) continue;
            ExposeAttribute? attr = method.GetCustomAttribute<ExposeAttribute>(true);
            if (optIn && attr == null) continue;

            string scriptName = attr?.Name ?? DefaultScriptMemberName(method.Name);
            ValidateIdentifier(scriptName, method.Name);
            if (!seen.Add(scriptName))
                throw new ArgumentException($"Duplicate exposed member name '{scriptName}' on {type.Name}; overloads are not supported — rename with [Expose(\"...\")] or mark [Hide]");

            bool asFuture = attr?.AsFuture == true;
            string memberLabel = $"{name}.{scriptName}";
            ValidateMethodSignature(state, memberLabel, method, asFuture, unitResolver);
            methods[scriptName] = BuildNative(memberLabel, target, method, asFuture);
            fieldsDecl.Add($"    {scriptName}: {BuildFunctionTypeSignature(state, method, asFuture)}");
        }

        Table meta = GetHostObjectMetatable(state);
        var hostObj = new HostObject(target, name, meta, methods, getters, setters);

        int id = Interlocked.Increment(ref _recordCounter);
        string recName = $"__Host_{name}_{id}";
        var sb = new StringBuilder();
        sb.Append("global record ").Append(recName).Append('\n');
        foreach (string line in fieldsDecl) sb.Append(line).Append('\n');
        sb.Append("end\n");
        sb.Append("global ").Append(name).Append(": ").Append(recName).Append('\n');
        return (hostObj, sb.ToString());
    }

    private static Table GetHostObjectMetatable(LuaState state)
    {
        const string metaKey = "__host_object_meta";
        Table meta = state.MetaFor(metaKey);
        if (!meta.GetStr("__index").IsNil) return meta;

        meta.SetStr("__index", Value.FromFunction((ref CallContext c) =>
        {
            if (c[0].O is not HostObject h) return c.Return(Value.Nil);
            if (c[1].Kind != ValueKind.String) return c.Return(Value.Nil);
            string key = c[1].ToText();
            if (h.Methods.TryGetValue(key, out NativeFunction? fn))
                return c.Return(Value.FromFunction(fn));
            if (h.Getters.TryGetValue(key, out Func<LuaState, Value>? getter))
                return c.Return(getter(c.L));
            return c.Return(Value.Nil);
        }, "host.__index"));
        meta.SetStr("__newindex", Value.FromFunction((ref CallContext c) =>
        {
            if (c[0].O is not HostObject h)
                throw new LuaException("invalid host object receiver");
            string key = c[1].Kind == ValueKind.String ? c[1].ToText() : c[1].ToString();
            if (h.Setters.TryGetValue(key, out Action<LuaState, Value>? setter))
            {
                setter(c.L, c[2]);
                return 0;
            }
            if (h.Getters.ContainsKey(key) || h.Methods.ContainsKey(key))
                throw new LuaException($"cannot assign to read-only host member '{h.HostName}.{key}'");
            throw new LuaException($"unknown host member '{h.HostName}.{key}'");
        }, "host.__newindex"));
        meta.SetStr("__tostring", Value.FromFunction((ref CallContext c) =>
        {
            if (c[0].O is HostObject h) return c.ReturnText($"{h.HostName}(host)");
            return c.ReturnText("host");
        }, "host.__tostring"));

        return meta;
    }

    // ------------------------------------------------------------------ DefineValue

    internal static (Value Val, string Declaration) BindValue(LuaState state, string name, object? value)
    {
        ValidateIdentifier(name, nameof(name));
        if (value == null)
            return (Value.Nil, $"global {name}: any\n");
        Type runtimeType = value.GetType();
        Value v = ToLuaValue(state, value, runtimeType);
        string tealType = ToTealValueType(state, value, runtimeType);
        return (v, $"global {name}: {tealType}\n");
    }

    // ------------------------------------------------------------------ Method binding

    private static IHostTypeMarshaller? FindMarshaller(LuaState state, Type coreType)
    {
        if (state.HostTypeMarshallers == null) return null;
        foreach (IHostTypeMarshaller m in state.HostTypeMarshallers)
            if (m.CanMarshal(coreType)) return m;
        return null;
    }

    private static void ValidateUnitAttribute(
        LuaState state,
        string label,
        Type clrType,
        UnitAttribute unitAttr,
        Types.IUnitDimensionResolver? unitResolver)
    {
        Type core = Nullable.GetUnderlyingType(clrType) ?? clrType;
        bool validTarget = IsFloatClrType(core)
            || FindMarshaller(state, core) != null
            || (TryGetSequenceElementType(core, out Type? elem) && IsFloatClrType(Nullable.GetUnderlyingType(elem) ?? elem));
        if (!validTarget)
            throw new ArgumentException($"[Unit(\"{unitAttr.Unit}\")] on '{label}' is only supported on floating-point numeric, numeric sequence, or quantity types (got '{clrType.Name}')");

        if (unitResolver != null && !unitResolver.TryResolveUnitDimension(unitAttr.Unit, out _, out string? err))
            throw new ArgumentException($"Invalid [Unit(\"{unitAttr.Unit}\")] on '{label}': {err}");
    }

    private static void ValidateMethodSignature(
        LuaState state,
        string label,
        MethodInfo method,
        bool asFuture,
        Types.IUnitDimensionResolver? unitResolver)
    {
        if (method.IsGenericMethodDefinition)
            throw new ArgumentException($"Generic method '{label}' cannot be exposed to scripts");

        foreach (ParameterInfo p in method.GetParameters())
        {
            Type pt = p.ParameterType;
            if (pt.IsByRef || pt.IsPointer || pt.IsByRefLike)
                throw new ArgumentException($"Unsupported parameter type '{pt.Name}' in '{label}' (parameter '{p.Name}')");
            ValidateSupportedParamType(state, label, pt);
            UnitAttribute? uattr = p.GetCustomAttribute<UnitAttribute>(true);
            if (uattr != null)
                ValidateUnitAttribute(state, $"{label}({p.Name})", pt, uattr, unitResolver);
        }

        Type ret = method.ReturnType;
        if (ret.IsByRef || ret.IsPointer || ret.IsByRefLike)
            throw new ArgumentException($"Unsupported return type '{ret.Name}' in '{label}'");

        bool returnsTask = typeof(Task).IsAssignableFrom(ret);
        if (asFuture && !returnsTask)
            throw new ArgumentException($"Host function '{label}' has asFuture=true but returns '{ret.Name}' instead of Task or Task<T>");
        if (!asFuture && returnsTask)
            throw new ArgumentException($"Host function '{label}' returns '{ret.Name}'; pass asFuture: true to expose it as a Future");

        UnitAttribute? retUnit = method.ReturnParameter.GetCustomAttribute<UnitAttribute>(true);
        if (retUnit != null)
        {
            Type effectiveRet = asFuture && ret.IsGenericType && ret.GetGenericTypeDefinition() == typeof(Task<>)
                ? ret.GetGenericArguments()[0]
                : ret;
            ValidateUnitAttribute(state, $"{label}:return", effectiveRet, retUnit, unitResolver);
        }
    }

    private static void ValidateSupportedParamType(LuaState state, string label, Type pt)
    {
        Type core = Nullable.GetUnderlyingType(pt) ?? pt;
        if (core == typeof(bool) || core == typeof(string) || core == typeof(Value) ||
            core == typeof(FloatArray) || core == typeof(LuaList) || core == typeof(LuaDict) ||
            IsIntegerClrType(core) || IsFloatClrType(core) ||
            FindMarshaller(state, core) != null)
            return;
        if (TryGetSequenceElementType(core, out Type? elem))
        {
            ValidateSupportedParamType(state, label, elem);
            return;
        }
        if (TryGetStringDictionaryValueType(core, out Type? valType))
        {
            ValidateSupportedParamType(state, label, valType);
            return;
        }
        throw new ArgumentException($"Unsupported host interop parameter type '{pt.Name}' in '{label}'");
    }

    private static NativeFunction BuildNative(string label, object? target, MethodInfo method, bool asFuture)
    {
        ParameterInfo[] parameters = method.GetParameters();
        var paramUnits = new UnitAttribute?[parameters.Length];
        int requiredCount = 0;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (!parameters[i].HasDefaultValue) requiredCount = i + 1;
            paramUnits[i] = parameters[i].GetCustomAttribute<UnitAttribute>(true);
        }

        Type returnType = method.ReturnType;
        Type? taskResultType = asFuture && returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? returnType.GetGenericArguments()[0]
            : null;
        PropertyInfo? taskResultProp = taskResultType != null ? returnType.GetProperty("Result") : null;

        return new NativeFunction(label, (ref CallContext c) =>
        {
            if (c.Count < requiredCount)
                throw new LuaException($"{label}: expected at least {requiredCount} argument(s), got {c.Count}");
            if (c.Count > parameters.Length)
                throw new LuaException($"{label}: expected at most {parameters.Length} argument(s), got {c.Count}");

            object?[] args = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                ParameterInfo p = parameters[i];
                if (i >= c.Count || (c[i].IsNil && p.HasDefaultValue))
                {
                    args[i] = p.DefaultValue;
                }
                else
                {
                    args[i] = FromLuaValue(c.L, c[i], p.ParameterType, paramUnits[i], label, i);
                }
            }

            object? result;
            try
            {
                result = method.Invoke(target, args);
            }
            catch (TargetInvocationException tie)
            {
                throw WrapHostException(label, tie.InnerException ?? tie);
            }
            catch (Exception ex) when (ex is not LuaException and not LuaAbortException and not LuaExitException)
            {
                throw WrapHostException(label, ex);
            }

            if (asFuture)
            {
                if (result is not Task task)
                    throw new LuaException($"{label}: async host function returned null Task");
                LuaFuture fut = WrapHostTask(c.L, task, taskResultType, taskResultProp, label);
                return c.Return(Value.FromUserdata(fut));
            }

            if (returnType == typeof(void))
                return 0;

            try
            {
                return c.Return(ToLuaValue(c.L, result, returnType));
            }
            catch (Exception ex) when (ex is not LuaException and not LuaAbortException and not LuaExitException)
            {
                throw WrapHostException(label, ex);
            }
        });
    }

    private static LuaFuture WrapHostTask(
        LuaState state,
        Task task,
        Type? resultType,
        PropertyInfo? resultProp,
        string label)
    {
        var future = new LuaFuture(state);
        int epoch = state.Scheduler.BeginExternal();
        task.ContinueWith(t => state.Scheduler.PostExternal(() =>
        {
            if (!state.Scheduler.EndExternal(epoch)) return;
            if (t.IsCanceled)
            {
                future.Fail(new FutureError(FutureErrorKind.Cancelled, "cancelled"));
            }
            else if (t.IsFaulted)
            {
                Exception baseEx = t.Exception!.GetBaseException();
                future.Fail(new FutureError(FutureErrorKind.Failed, LuaText.Encode($"{label}: {baseEx.Message}")));
            }
            else if (resultType == null || resultProp == null)
            {
                future.Complete(Value.Nil);
            }
            else
            {
                try
                {
                    object? raw = resultProp.GetValue(t);
                    future.Complete(ToLuaValue(state, raw, resultType));
                }
                catch (Exception ex)
                {
                    future.Fail(new FutureError(FutureErrorKind.Failed, LuaText.Encode($"{label}: {ex.Message}")));
                }
            }
        }), TaskScheduler.Default);
        return future;
    }

    private static LuaException WrapHostException(string label, Exception ex)
    {
        if (ex is LuaException le) return le;
        return new LuaException($"{label}: {ex.Message}");
    }

    // ------------------------------------------------------------------ Value marshalling

    internal static object? FromLuaValue(LuaState state, Value v, Type targetType, UnitAttribute? unitAttr, string label, int argIndex)
    {
        Type? nullableInner = Nullable.GetUnderlyingType(targetType);
        if (v.IsNil)
        {
            if (nullableInner != null || !targetType.IsValueType) return null;
            throw new LuaException($"{label}: bad argument #{argIndex + 1} (expected {targetType.Name}, got nil)");
        }

        Type core = nullableInner ?? targetType;

        if (core == typeof(Value)) return v;
        if (core == typeof(bool))
        {
            if (v.Kind != ValueKind.Boolean)
                throw new LuaException($"{label}: bad argument #{argIndex + 1} (expected boolean, got {v.TypeName})");
            return v.I != 0;
        }
        if (core == typeof(string))
        {
            if (v.Kind != ValueKind.String)
                throw new LuaException($"{label}: bad argument #{argIndex + 1} (expected string, got {v.TypeName})");
            return v.ToText();
        }
        if (core == typeof(FloatArray))
        {
            if (v.O is FloatArray fa) return fa;
            throw new LuaException($"{label}: bad argument #{argIndex + 1} (expected FloatArray, got {v.TypeName})");
        }
        if (core == typeof(LuaList))
        {
            if (v.O is LuaList ll) return ll;
            throw new LuaException($"{label}: bad argument #{argIndex + 1} (expected List, got {v.TypeName})");
        }
        if (core == typeof(LuaDict))
        {
            if (v.O is LuaDict ld) return ld;
            throw new LuaException($"{label}: bad argument #{argIndex + 1} (expected Dict, got {v.TypeName})");
        }
        if (IsIntegerClrType(core))
        {
            if (!v.TryToInteger(out long iv))
                throw new LuaException($"{label}: bad argument #{argIndex + 1} (expected integer, got {v.TypeName})");
            return ConvertInteger(iv, core, label, argIndex);
        }
        if (IsFloatClrType(core))
        {
            if (unitAttr != null)
            {
                if (v.O is IHostQuantity hq)
                {
                    if (!hq.TryConvertToUnit(unitAttr.Unit, out double qv, out string? err))
                        throw new LuaException($"{label}: bad argument #{argIndex + 1} ({err})");
                    return ConvertFloat(qv, core);
                }
                if (!unitAttr.AllowNumber)
                    throw new LuaException($"{label}: bad argument #{argIndex + 1} (expected SharpUnits.Quantity<\"{unitAttr.Unit}\">, got {v.TypeName})");
            }
            if (!v.TryToDouble(out double dv))
                throw new LuaException($"{label}: bad argument #{argIndex + 1} (expected {(unitAttr != null ? $"number or SharpUnits.Quantity<\"{unitAttr.Unit}\">" : "number")}, got {v.TypeName})");
            return ConvertFloat(dv, core);
        }
        if (FindMarshaller(state, core) is { } marshaller)
        {
            return marshaller.FromLuaValue(state, v, core, unitAttr, label, argIndex);
        }
        if (TryGetSequenceElementType(core, out Type? elemType))
        {
            return ConvertSequenceFromLua(state, v, core, elemType, unitAttr, label, argIndex);
        }
        if (TryGetStringDictionaryValueType(core, out Type? valType))
        {
            return ConvertDictionaryFromLua(state, v, core, valType, label, argIndex);
        }

        throw new LuaException($"{label}: cannot convert argument #{argIndex + 1} to {targetType.Name}");
    }

    private static object ConvertFloat(double dv, Type core)
    {
        if (core == typeof(double)) return dv;
        if (core == typeof(float)) return (float)dv;
        return (decimal)dv;
    }

    private static object ConvertInteger(long iv, Type core, string label, int argIndex)
    {
        try
        {
            if (core == typeof(long)) return iv;
            if (core == typeof(int)) return checked((int)iv);
            if (core == typeof(short)) return checked((short)iv);
            if (core == typeof(sbyte)) return checked((sbyte)iv);
            if (core == typeof(byte)) return checked((byte)iv);
            if (core == typeof(ushort)) return checked((ushort)iv);
            if (core == typeof(uint)) return checked((uint)iv);
        }
        catch (OverflowException)
        {
            throw new LuaException($"{label}: argument #{argIndex + 1} ({iv}) is out of range for {core.Name}");
        }
        throw new LuaException($"{label}: unsupported integer type {core.Name}");
    }

    private static object ConvertSequenceFromLua(
        LuaState state,
        Value v,
        Type core,
        Type elemType,
        UnitAttribute? unitAttr,
        string label,
        int argIndex)
    {
        var items = new List<object?>();
        if (v.O is LuaList list)
        {
            for (int i = 0; i < list.Items.Count; i++)
            {
                if (state.PollStep()) state.CheckAbortOrStep();
                items.Add(FromLuaValue(state, list.Items[i], elemType, unitAttr, label, argIndex));
            }
        }
        else if (v.O is Table tbl)
        {
            int len = tbl.Length;
            for (int i = 1; i <= len; i++)
            {
                if (state.PollStep()) state.CheckAbortOrStep();
                items.Add(FromLuaValue(state, tbl.GetInt(i), elemType, unitAttr, label, argIndex));
            }
        }
        else if (v.O is FloatArray fa && (elemType == typeof(double) || elemType == typeof(float)) && (unitAttr == null || unitAttr.AllowNumber))
        {
            for (int i = 0; i < fa.Data.Length; i++)
            {
                if (state.PollStep()) state.CheckAbortOrStep();
                items.Add(elemType == typeof(double) ? fa.Data[i] : (float)fa.Data[i]);
            }
        }
        else
        {
            throw new LuaException($"{label}: bad argument #{argIndex + 1} (expected List or sequence table, got {v.TypeName})");
        }

        if (core.IsArray)
        {
            Array arr = Array.CreateInstance(elemType, items.Count);
            for (int i = 0; i < items.Count; i++) arr.SetValue(items[i], i);
            return arr;
        }

        IList typedList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elemType), items.Count)!;
        foreach (object? item in items) typedList.Add(item);
        return typedList;
    }

    private static object ConvertDictionaryFromLua(LuaState state, Value v, Type core, Type valType, string label, int argIndex)
    {
        IDictionary dict = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), valType))!;
        if (v.O is LuaDict luaDict)
        {
            foreach (KeyValuePair<Value, Value> kv in luaDict.Entries)
            {
                if (state.PollStep()) state.CheckAbortOrStep();
                if (kv.Key.Kind != ValueKind.String)
                    throw new LuaException($"{label}: bad argument #{argIndex + 1} (dictionary key must be string, got {kv.Key.TypeName})");
                dict[kv.Key.ToText()] = FromLuaValue(state, kv.Value, valType, null, label, argIndex);
            }
        }
        else if (v.O is Table tbl)
        {
            Value key = Value.Nil;
            while (tbl.Next(key, out Value nextKey, out Value nextVal))
            {
                if (state.PollStep()) state.CheckAbortOrStep();
                if (nextKey.Kind != ValueKind.String)
                    throw new LuaException($"{label}: bad argument #{argIndex + 1} (table key must be string, got {nextKey.TypeName})");
                dict[nextKey.ToText()] = FromLuaValue(state, nextVal, valType, null, label, argIndex);
                key = nextKey;
            }
        }
        else
        {
            throw new LuaException($"{label}: bad argument #{argIndex + 1} (expected Dict or table, got {v.TypeName})");
        }
        return dict;
    }

    internal static Value ToLuaValue(LuaState state, object? value, Type declaredType)
    {
        if (value == null) return Value.Nil;
        if (value is Value v) return v;
        if (value is bool b) return Value.FromBool(b);
        if (value is string s) return Value.FromText(s);
        if (value is FloatArray fa) return Value.FromUserdata(fa);
        if (value is LuaList ll) return Value.FromUserdata(ll);
        if (value is LuaDict ld) return Value.FromUserdata(ld);
        if (value is IUserdata ud) return Value.FromUserdata(ud);

        switch (value)
        {
            case sbyte x: return Value.FromInt(x);
            case byte x: return Value.FromInt(x);
            case short x: return Value.FromInt(x);
            case ushort x: return Value.FromInt(x);
            case int x: return Value.FromInt(x);
            case uint x: return Value.FromInt(x);
            case long x: return Value.FromInt(x);
            case float x: return Value.FromFloat(x);
            case double x: return Value.FromFloat(x);
            case decimal x: return Value.FromFloat((double)x);
        }

        Type actualType = value.GetType();
        if (FindMarshaller(state, actualType) is { } marshaller)
        {
            return marshaller.ToLuaValue(state, value, declaredType);
        }

        if (value is IDictionary nonGenDict)
        {
            var dict = new LuaDict(state);
            foreach (DictionaryEntry de in nonGenDict)
            {
                if (state.PollStep()) state.CheckAbortOrStep();
                Value k = LuaDict.NormalizeKey(ToLuaValue(state, de.Key, de.Key?.GetType() ?? typeof(object)));
                Value val = ToLuaValue(state, de.Value, de.Value?.GetType() ?? typeof(object));
                dict.Entries[k] = val;
            }
            return Value.FromUserdata(dict);
        }

        if (TryGetGenericDictionaryTypes(actualType, out Type? kType, out Type? vType))
        {
            var dict = new LuaDict(state);
            foreach (object? entry in (IEnumerable)value)
            {
                if (state.PollStep()) state.CheckAbortOrStep();
                PropertyInfo kp = entry!.GetType().GetProperty("Key")!;
                PropertyInfo vp = entry.GetType().GetProperty("Value")!;
                Value k = LuaDict.NormalizeKey(ToLuaValue(state, kp.GetValue(entry), kType));
                Value val = ToLuaValue(state, vp.GetValue(entry), vType);
                dict.Entries[k] = val;
            }
            return Value.FromUserdata(dict);
        }

        if (value is IEnumerable seq)
        {
            var list = new LuaList(state);
            foreach (object? elem in seq)
            {
                if (state.PollStep()) state.CheckAbortOrStep();
                list.Items.Add(ToLuaValue(state, elem, elem?.GetType() ?? typeof(object)));
            }
            return Value.FromUserdata(list);
        }

        throw new LuaException($"cannot convert host value of type '{declaredType.Name}' to script value");
    }

    // ------------------------------------------------------------------ Static Teal/Cameo type declarations

    internal static string EscapeTealString(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string BuildFunctionTypeSignature(LuaState state, MethodInfo method, bool asFuture)
    {
        ParameterInfo[] ps = method.GetParameters();
        var parts = new string[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            UnitAttribute? uattr = ps[i].GetCustomAttribute<UnitAttribute>(true);
            string t = ToTealParamType(state, ps[i].ParameterType, uattr);
            parts[i] = ps[i].HasDefaultValue ? $"? {t}" : t;
        }
        string args = string.Join(", ", parts);
        UnitAttribute? retUnit = method.ReturnParameter.GetCustomAttribute<UnitAttribute>(true);

        if (asFuture)
        {
            Type ret = method.ReturnType;
            if (ret.IsGenericType && ret.GetGenericTypeDefinition() == typeof(Task<>))
            {
                string inner = ToTealReturnType(state, ret.GetGenericArguments()[0], retUnit);
                return $"function({args}): Future<{inner}>";
            }
            return $"function({args}): Future<any>";
        }

        if (method.ReturnType == typeof(void))
            return $"function({args})";
        return $"function({args}): {ToTealReturnType(state, method.ReturnType, retUnit)}";
    }

    private static string FormatFloatWithUnit(UnitAttribute unitAttr)
    {
        string qty = $"SharpUnits.Quantity<\"{EscapeTealString(unitAttr.Unit)}\">";
        return unitAttr.AllowNumber ? $"number | {qty}" : qty;
    }

    private static string ToTealParamType(LuaState state, Type pt, UnitAttribute? unitAttr)
    {
        Type core = Nullable.GetUnderlyingType(pt) ?? pt;
        if (core == typeof(bool)) return "boolean";
        if (IsIntegerClrType(core)) return "integer";
        if (IsFloatClrType(core))
            return unitAttr != null ? FormatFloatWithUnit(unitAttr) : "number";
        if (core == typeof(string)) return "string";
        if (core == typeof(FloatArray)) return "FloatArray";
        if (core == typeof(LuaList)) return "List<any>";
        if (core == typeof(LuaDict)) return "Dict<any, any>";
        if (core == typeof(Value)) return "any";
        if (FindMarshaller(state, core) is { } marshaller)
            return marshaller.GetTealType(core, unitAttr);
        if (TryGetSequenceElementType(core, out Type? elem))
        {
            Type elemCore = Nullable.GetUnderlyingType(elem) ?? elem;
            string e = unitAttr != null && IsFloatClrType(elemCore)
                ? FormatFloatWithUnit(unitAttr)
                : ToTealReturnType(state, elem, null);
            return $"List<{e}> | {{{e}}}";
        }
        if (TryGetStringDictionaryValueType(core, out Type? val))
        {
            string v = ToTealReturnType(state, val, null);
            return $"Dict<string, {v}> | {{string: {v}}}";
        }
        return "any";
    }

    private static string ToTealReturnType(LuaState state, Type rt, UnitAttribute? unitAttr)
    {
        Type? nullableInner = Nullable.GetUnderlyingType(rt);
        Type core = nullableInner ?? rt;
        string baseType = CoreToTealType(state, core, unitAttr);
        return nullableInner != null ? $"{baseType} | nil" : baseType;
    }

    private static string ToTealValueType(LuaState state, object value, Type runtimeType)
    {
        if (value is FloatArray) return "FloatArray";
        if (value is LuaList) return "List<any>";
        if (value is LuaDict) return "Dict<any, any>";
        if (FindMarshaller(state, runtimeType) is { } marshaller)
            return marshaller.GetTealValueType(value, runtimeType);
        return CoreToTealType(state, runtimeType, null);
    }

    private static string CoreToTealType(LuaState state, Type core, UnitAttribute? unitAttr)
    {
        if (core == typeof(bool)) return "boolean";
        if (IsIntegerClrType(core)) return "integer";
        if (IsFloatClrType(core)) return "number";
        if (core == typeof(string)) return "string";
        if (core == typeof(FloatArray)) return "FloatArray";
        if (core == typeof(LuaList)) return "List<any>";
        if (core == typeof(LuaDict)) return "Dict<any, any>";
        if (core == typeof(Value)) return "any";
        if (FindMarshaller(state, core) is { } marshaller)
            return marshaller.GetTealType(core, unitAttr);
        if (TryGetGenericDictionaryTypes(core, out Type? k, out Type? v))
            return $"Dict<{CoreToTealType(state, k, null)}, {CoreToTealType(state, v, null)}>";
        if (typeof(IDictionary).IsAssignableFrom(core))
            return "Dict<any, any>";
        if (TryGetSequenceElementType(core, out Type? elem))
            return $"List<{CoreToTealType(state, elem, null)}>";
        return "any";
    }

    private static bool IsIntegerClrType(Type t) =>
        t == typeof(sbyte) || t == typeof(byte) || t == typeof(short) || t == typeof(ushort) ||
        t == typeof(int) || t == typeof(uint) || t == typeof(long);

    private static bool IsFloatClrType(Type t) =>
        t == typeof(float) || t == typeof(double) || t == typeof(decimal);

    private static bool TryGetSequenceElementType(Type t, out Type elemType)
    {
        if (t == typeof(string) || t == typeof(FloatArray) || typeof(IDictionary).IsAssignableFrom(t))
        {
            elemType = typeof(object);
            return false;
        }
        if (t.IsArray && t.GetArrayRank() == 1)
        {
            elemType = t.GetElementType()!;
            return true;
        }
        if (t.IsGenericType)
        {
            Type def = t.GetGenericTypeDefinition();
            if (def == typeof(IEnumerable<>) || def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyCollection<>) ||
                def == typeof(IList<>) || def == typeof(ICollection<>) || def == typeof(List<>))
            {
                elemType = t.GetGenericArguments()[0];
                return true;
            }
        }
        foreach (Type iface in t.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                // Avoid matching dictionaries as sequences of KeyValuePair
                if (TryGetGenericDictionaryTypes(t, out _, out _))
                {
                    elemType = typeof(object);
                    return false;
                }
                elemType = iface.GetGenericArguments()[0];
                return true;
            }
        }
        elemType = typeof(object);
        return false;
    }

    private static bool TryGetStringDictionaryValueType(Type t, out Type valType)
    {
        if (TryGetGenericDictionaryTypes(t, out Type? k, out Type? v) && k == typeof(string))
        {
            valType = v;
            return true;
        }
        valType = typeof(object);
        return false;
    }

    private static bool TryGetGenericDictionaryTypes(Type t, out Type keyType, out Type valType)
    {
        if (t.IsGenericType)
        {
            Type def = t.GetGenericTypeDefinition();
            if (def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>) || def == typeof(Dictionary<,>))
            {
                Type[] args = t.GetGenericArguments();
                keyType = args[0];
                valType = args[1];
                return true;
            }
        }
        foreach (Type iface in t.GetInterfaces())
        {
            if (iface.IsGenericType)
            {
                Type def = iface.GetGenericTypeDefinition();
                if (def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
                {
                    Type[] args = iface.GetGenericArguments();
                    keyType = args[0];
                    valType = args[1];
                    return true;
                }
            }
        }
        keyType = typeof(object);
        valType = typeof(object);
        return false;
    }
}
