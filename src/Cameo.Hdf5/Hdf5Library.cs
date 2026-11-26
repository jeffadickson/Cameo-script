using Cameo.Runtime;
using PureHDF;
using PureHDF.VOL.Native;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;

namespace Cameo.Hdf5;

/// <summary>
/// <c>hdf5</c>: reading and writing HDF5 files, with numeric datasets as <c>FloatArray</c>.
/// Kept out of the core library, like the SQLite provider; it is pure .NET (the PureHDF
/// package), so it needs no native HDF5 installation.
///
/// Every function returns its result, or nil and an <c>IoError</c> — none raises on a bad or
/// missing file. A file is written in one go from a description of its contents; an existing
/// file cannot be modified in place.
/// </summary>
public static class Hdf5Library
{
    /// <summary>Adds the <c>hdf5</c> library to an engine.</summary>
    public static void Enable(ScriptEngine engine)
    {
        LuaState L = engine.State;
        var lib = new Table();
        L.Register(lib, "read", static (ref CallContext c) =>
        {
            string path = c.CheckText(0), name = c.CheckText(1);
            return Try(ref c, path, L =>
            {
                using Hdf5Input input = Hdf5Input.OpenRead(path);
                NativeFile file = input.File;
                IH5Dataset ds = Find<IH5Dataset>(file, name) ?? throw new FileNotFoundException($"no dataset '{name}' in the file");
                return (ReadData(L, ds.Type, ds.Space, new DatasetReader(ds), wholeArray: true), ShapeOf(ds.Space));
            });
        });
        L.Register(lib, "info", static (ref CallContext c) =>
        {
            string path = c.CheckText(0), name = c.CheckText(1);
            return Try(ref c, path, L =>
            {
                using Hdf5Input input = Hdf5Input.OpenRead(path);
                NativeFile file = input.File;
                IH5Object obj = Find<IH5Object>(file, name) ?? throw new FileNotFoundException($"no object '{name}' in the file");
                return (Describe(L, obj, name), Value.Nil);
            });
        });
        // list(path): every group and dataset in the file, by full path, in the file's order
        L.Register(lib, "list", static (ref CallContext c) =>
        {
            string path = c.CheckText(0);
            return Try(ref c, path, L =>
            {
                using Hdf5Input input = Hdf5Input.OpenRead(path);
                NativeFile file = input.File;
                var list = new Table();
                Walk(L, file, "", list, depth: 0);
                return (Value.FromTable(list), Value.Nil);
            });
        });
        L.Register(lib, "write", Write);
        L.Globals.SetStr("hdf5", lib);
        L.LoadedModules.SetStr("hdf5", lib);
    }

    private delegate (Value First, Value Second) Operation(LuaState L);

    private static int Try(ref CallContext c, string target, Operation op)
    {
        try
        {
            var (first, second) = op(c.L);
            return second.IsNil ? c.Return(first, Value.Nil) : c.Return(first, second);
        }
        catch (Exception e) when (e is not (LuaException or LuaAbortException or LuaExitException or OutOfMemoryException))
        {
            // PureHDF reports a file it cannot read with plain exceptions of several kinds
            IoErrorKind kind = e switch
            {
                FileNotFoundException or DirectoryNotFoundException => IoErrorKind.NotFound,
                UnauthorizedAccessException => IoErrorKind.PermissionDenied,
                IOException => IoErrorKind.Failed,
                _ => IoErrorKind.Invalid, // not an HDF5 file, or a feature of the format this library does not read
            };
            return c.Return(Value.Nil, new IoError(kind, e.Message, target).ToValue(c.L));
        }
    }

    private static T? Find<T>(NativeFile file, string name) where T : class, IH5Object
    {
        string path = name.StartsWith('/') ? name : "/" + name;
        if (path == "/") return file as T;
        return file.LinkExists(path) ? file.Get(path) as T : null;
    }

    private static Value ShapeOf(IH5Dataspace space)
    {
        var shape = new Table(space.Dimensions.Length, 0);
        foreach (ulong d in space.Dimensions) shape.Append((long)d);
        return Value.FromTable(shape);
    }

    private static string TypeName(IH5DataType type) => type.Class switch
    {
        H5DataTypeClass.FloatingPoint => "float" + type.Size * 8,
        H5DataTypeClass.FixedPoint => (type.FixedPoint.IsSigned ? "int" : "uint") + type.Size * 8,
        H5DataTypeClass.String or H5DataTypeClass.VariableLength => "string",
        _ => type.Class.ToString().ToLowerInvariant(),
    };

    // Datasets and attributes are read the same way; this hides which of the two it is.
    private interface IReader { T Read<T>(); }
    private sealed class DatasetReader(IH5Dataset ds) : IReader { public T Read<T>() => ds.Read<T>(); }
    private sealed class AttributeReader(IH5Attribute a) : IReader { public T Read<T>() => a.Read<T>(); }

    // Numbers become a FloatArray (the values in row-major order; the shape is reported
    // separately) or, for a single value where the caller prefers it, a plain number;
    // strings become a string or a list of strings.
    private static Value ReadData(LuaState L, IH5DataType type, IH5Dataspace space, IReader reader, bool wholeArray)
    {
        long count = 1;
        foreach (ulong d in space.Dimensions) count = checked(count * (long)d);
        L.CheckArrayLength(count);
        bool single = space.Dimensions.Length == 0 || (!wholeArray && count == 1);
        switch (type.Class)
        {
            case H5DataTypeClass.FloatingPoint:
            case H5DataTypeClass.FixedPoint:
                {
                    double[] data = Numbers(type, reader);
                    if (single && data.Length == 1)
                    {
                        bool whole = type.Class == H5DataTypeClass.FixedPoint;
                        return whole ? Value.FromInt((long)data[0]) : Value.FromFloat(data[0]);
                    }
                    return Value.FromUserdata(new FloatArray(L, data));
                }
            case H5DataTypeClass.String:
            case H5DataTypeClass.VariableLength:
                {
                    string[] strings = reader.Read<string[]>();
                    if (single && strings.Length == 1) return Value.FromText(strings[0] ?? "");
                    var list = new Table(strings.Length, 0);
                    foreach (string s in strings) list.Append(Value.FromText(s ?? ""));
                    return Value.FromTable(list);
                }
            default:
                throw new NotSupportedException($"datasets of type '{TypeName(type)}' are not supported (numbers and strings are)");
        }
    }

    private static double[] Numbers(IH5DataType type, IReader r)
    {
        if (type.Class == H5DataTypeClass.FloatingPoint)
            return type.Size switch
            {
                8 => r.Read<double[]>(),
                4 => Array.ConvertAll(r.Read<float[]>(), x => (double)x),
                2 => Array.ConvertAll(r.Read<Half[]>(), x => (double)x),
                _ => throw new NotSupportedException($"{type.Size * 8}-bit floating point is not supported"),
            };
        bool signed = type.FixedPoint.IsSigned;
        return (type.Size, signed) switch
        {
            (1, true) => Array.ConvertAll(r.Read<sbyte[]>(), x => (double)x),
            (1, false) => Array.ConvertAll(r.Read<byte[]>(), x => (double)x),
            (2, true) => Array.ConvertAll(r.Read<short[]>(), x => (double)x),
            (2, false) => Array.ConvertAll(r.Read<ushort[]>(), x => (double)x),
            (4, true) => Array.ConvertAll(r.Read<int[]>(), x => (double)x),
            (4, false) => Array.ConvertAll(r.Read<uint[]>(), x => (double)x),
            (8, true) => Array.ConvertAll(r.Read<long[]>(), x => (double)x),
            (8, false) => Array.ConvertAll(r.Read<ulong[]>(), x => (double)x),
            _ => throw new NotSupportedException($"{type.Size * 8}-bit integers are not supported"),
        };
    }

    private static Value Describe(LuaState L, IH5Object obj, string path)
    {
        var t = new Table(0, 5);
        t.SetStr("path", Value.FromText(path));
        if (obj is IH5Dataset ds)
        {
            t.SetStr("kind", "dataset");
            t.SetStr("type", TypeName(ds.Type));
            t.SetStr("shape", ShapeOf(ds.Space));
        }
        else t.SetStr("kind", "group");
        var attributes = new Table();
        foreach (IH5Attribute a in obj.Attributes())
        {
            Value v;
            try { v = ReadData(L, a.Type, a.Space, new AttributeReader(a), wholeArray: false); }
            catch (NotSupportedException) { continue; } // an attribute of a type we do not read is left out
            attributes.Set(Value.FromText(a.Name), v);
        }
        t.SetStr("attributes", attributes);
        return Value.FromTable(t);
    }

    private static void Walk(LuaState L, IH5Group group, string prefix, Table into, int depth)
    {
        if (depth > 64) throw new InvalidDataException("groups are nested too deeply (a cycle of links?)");
        foreach (IH5Object child in group.Children())
        {
            string path = prefix + "/" + child.Name;
            var entry = new Table(0, 4);
            entry.SetStr("path", Value.FromText(path));
            if (child is IH5Dataset ds)
            {
                entry.SetStr("kind", "dataset");
                entry.SetStr("type", TypeName(ds.Type));
                entry.SetStr("shape", ShapeOf(ds.Space));
            }
            else entry.SetStr("kind", "group");
            into.Append(Value.FromTable(entry));
            L.CheckArrayLength(into.Length);
            if (child is IH5Group sub) Walk(L, sub, path, into, depth + 1);
        }
    }

    // ------------------------------------------------------------------ writing

    // write(path, contents [, attributes]): contents maps dataset paths ("/wave/ch1") to
    //   a FloatArray, a list of numbers, a number, a string or a list of strings; or
    //   {data = ..., shape = {rows, cols}, type = "float32", attributes = {...}}.
    // Groups are created as the paths need them. `attributes` are attached to the file's root.
    private static int Write(ref CallContext c)
    {
        string path = c.CheckText(0);
        Table contents = c.CheckTable(1);
        Table? rootAttributes = c.IsNoneOrNil(2) ? null : c.CheckTable(2);
        var file = new H5File();
        if (rootAttributes != null) SetAttributes(ref c, file, rootAttributes);
        foreach (KeyValuePair<Value, Value> kv in contents.Pairs())
        {
            if (!kv.Key.IsString) throw c.ArgError(1, "dataset names must be strings");
            string[] parts = LuaText.Decode(kv.Key.AsString).Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) throw c.ArgError(1, "a dataset needs a name");
            H5Group group = file;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!group.TryGetValue(parts[i], out object? next)) group[parts[i]] = next = new H5Group();
                group = next as H5Group ?? throw c.ArgError(1, $"'{parts[i]}' is used both as a dataset and as a group");
            }
            if (group.ContainsKey(parts[^1])) throw c.ArgError(1, $"'{kv.Key}' is given twice");
            group[parts[^1]] = Dataset(ref c, kv.Value, LuaText.Decode(kv.Key.AsString));
        }
        LuaState state = c.L;
        return Try(ref c, path, _ =>
        {
            file.Write(path);
            return (Value.True, Value.Nil);
        });
    }

    private static H5Dataset Dataset(ref CallContext c, Value spec, string name)
    {
        Value data = spec;
        Table? attributes = null;
        ulong[]? shape = null;
        string type = "float64";
        if (spec.O is Table t && t.Metatable == null && !t.GetStr("data").IsNil)
        {
            data = t.GetStr("data");
            attributes = t.GetStr("attributes").O as Table;
            if (t.GetStr("type").IsString) type = t.GetStr("type").AsString;
            if (t.GetStr("shape").O is Table s)
            {
                shape = new ulong[s.Length];
                for (int i = 0; i < shape.Length; i++)
                {
                    if (!s.GetInt(i + 1).TryToInteger(out long d) || d < 0) throw c.ArgError(1, $"'{name}': shape entries must be non-negative integers");
                    shape[i] = (ulong)d;
                }
            }
        }
        object payload = Payload(ref c, data, type, name, out long count);
        if (shape != null)
        {
            long product = 1;
            foreach (ulong d in shape) product *= (long)d;
            if (product != count) throw c.ArgError(1, $"'{name}': shape {string.Join("x", shape)} does not hold {count} values");
        }
        var ds = shape == null ? new H5Dataset(payload) : new H5Dataset(payload, fileDims: shape);
        if (attributes != null) SetAttributes(ref c, ds, attributes);
        return ds;
    }

    private static void SetAttributes(ref CallContext c, H5Object target, Table attributes)
    {
        foreach (KeyValuePair<Value, Value> kv in attributes.Pairs())
        {
            if (!kv.Key.IsString) throw c.ArgError(1, "attribute names must be strings");
            string name = LuaText.Decode(kv.Key.AsString);
            target.Attributes[name] = Payload(ref c, kv.Value, "float64", "attribute " + name, out _);
        }
    }

    // A VM value as the .NET object PureHDF stores. Booleans are stored as the integers 0 and 1
    // (HDF5 has no boolean type), so they read back as numbers.
    private static object Payload(ref CallContext c, Value v, string type, string name, out long count)
    {
        count = 1;
        double[]? numbers = null;
        switch (v.Kind)
        {
            case ValueKind.Integer: return v.I;
            case ValueKind.Float: return v.N;
            case ValueKind.Boolean: return v.AsBool ? 1L : 0L;
            case ValueKind.String: return LuaText.Decode(v.AsString);
            case ValueKind.Userdata when v.O is FloatArray fa:
                numbers = fa.Data;
                break;
            case ValueKind.Table:
                {
                    Table t = v.AsTable;
                    int n = t.Length;
                    if (n > 0 && t.GetInt(1).IsString)
                    {
                        var strings = new string[n];
                        for (int i = 0; i < n; i++)
                        {
                            if (!t.GetInt(i + 1).IsString) throw c.ArgError(1, $"'{name}': a list must hold only strings or only numbers");
                            strings[i] = LuaText.Decode(t.GetInt(i + 1).AsString);
                        }
                        count = n;
                        return strings;
                    }
                    numbers = new double[n];
                    for (int i = 0; i < n; i++)
                        if (t.GetInt(i + 1).IsString || !t.GetInt(i + 1).TryToDouble(out numbers[i]))
                            throw c.ArgError(1, $"'{name}': a list must hold only strings or only numbers");
                    break;
                }
        }
        if (numbers == null) throw c.ArgError(1, $"'{name}': cannot store a {LuaState.TypeNameOf(v)}");
        count = numbers.Length;
        // integer types round to nearest and saturate, as FloatArray.tobytes does
        static double Sat(double d, double lo, double hi) => double.IsNaN(d) ? 0 : Math.Clamp(Math.Round(d, MidpointRounding.AwayFromZero), lo, hi);
        return type switch
        {
            "float64" => (double[])numbers.Clone(),
            "float32" => Array.ConvertAll(numbers, x => (float)x),
            "int64" => Array.ConvertAll(numbers, x => (long)Sat(x, long.MinValue, long.MaxValue)),
            "int32" => Array.ConvertAll(numbers, x => (int)Sat(x, int.MinValue, int.MaxValue)),
            "int16" => Array.ConvertAll(numbers, x => (short)Sat(x, short.MinValue, short.MaxValue)),
            "int8" => Array.ConvertAll(numbers, x => (sbyte)Sat(x, sbyte.MinValue, sbyte.MaxValue)),
            "uint32" => Array.ConvertAll(numbers, x => (uint)Sat(x, 0, uint.MaxValue)),
            "uint16" => Array.ConvertAll(numbers, x => (ushort)Sat(x, 0, ushort.MaxValue)),
            "uint8" => Array.ConvertAll(numbers, x => (byte)Sat(x, 0, 255)),
            _ => throw c.ArgError(1, $"'{name}': unknown type '{type}' (float64, float32, int64, int32, int16, int8, uint32, uint16, uint8)"),
        };
    }
}
