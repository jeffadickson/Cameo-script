using System.Runtime.CompilerServices;

namespace Cameo.Runtime.Vm;

/// <summary>
/// Copies values from one VM to another (a parallel worker and back). VMs share nothing
/// mutable, so everything mutable is copied:
/// <list type="bullet">
/// <item>nil, booleans, numbers and strings pass as they are (they are immutable);</item>
/// <item>tables are copied deeply, with their metatables; shared and cyclic structure is kept;</item>
/// <item>Teal functions are copied with the variables they capture (their compiled code is
/// immutable and shared);</item>
/// <item>standard-library functions and tables map to the other VM's own;</item>
/// <item>numeric arrays (FloatArray) are copied;</item>
/// <item>coroutines, futures, files and other host objects cannot be sent.</item>
/// </list>
/// </summary>
internal sealed class ValueTransfer
{
    private sealed class TransferException(string message) : Exception(message);

    private readonly LuaState _from, _to;
    private readonly Dictionary<object, object> _memo = new(ReferenceEqualityComparer.Instance);

    public ValueTransfer(LuaState from, LuaState to) { _from = from; _to = to; }

    /// <summary>Copies <paramref name="v"/>; on failure <paramref name="error"/> says what cannot be sent.</summary>
    public bool TryCopy(in Value v, out Value copy, out string? error)
    {
        try
        {
            copy = Copy(v);
            error = null;
            return true;
        }
        catch (TransferException e)
        {
            copy = default;
            error = e.Message;
            return false;
        }
        catch (InsufficientExecutionStackException)
        {
            copy = default;
            error = "value is too deeply nested to send to another VM";
            return false;
        }
    }

    private Value Copy(in Value v)
    {
        switch (v.Kind)
        {
            case ValueKind.Nil:
            case ValueKind.Boolean:
            case ValueKind.Integer:
            case ValueKind.Float:
            case ValueKind.String:
                return v;
            case ValueKind.Table:
                return Value.FromTable(CopyTable(v.AsTable));
            case ValueKind.Function:
                return Value.FromFunction(CopyFunction(v.AsFunction));
            case ValueKind.Userdata when v.O is Utf16String u:
                return Value.FromUtf16(_to, u.Text);
            case ValueKind.Userdata when v.O is Stdlib.FloatArray a:
                return Value.FromUserdata(CopyArray(a));
            case ValueKind.Userdata when v.O is Stdlib.ResultLib.TaggedResult r:
                return Value.FromUserdata(CopyResult(r));
            case ValueKind.Userdata when v.O is IValueTransferable tr:
                if (_memo.TryGetValue(tr, out object? cached)) return (Value)cached;
                RuntimeHelpers.EnsureSufficientExecutionStack();
                Value transferred = tr.TransferTo(_to, child => Copy(child));
                _memo[tr] = transferred;
                return transferred;
            default:
                throw new TransferException($"cannot send a {LuaState.TypeNameOf(v)} to another VM");
        }
    }

    private Stdlib.ResultLib.TaggedResult CopyResult(Stdlib.ResultLib.TaggedResult r)
    {
        if (_memo.TryGetValue(r, out object? done)) return (Stdlib.ResultLib.TaggedResult)done;
        if (_to.ResultMeta is not { } meta)
            throw new TransferException("cannot send a Result to a VM without the Result library");
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var copy = new Stdlib.ResultLib.TaggedResult(meta, r.Success, Copy(r.Payload));
        _memo[r] = copy;
        return copy;
    }

    private bool TryStandard(object o, out Value mapped)
    {
        if (_from.StdPaths.TryGetValue(o, out string? path) && _to.StdByPath.TryGetValue(path, out mapped)) return true;
        mapped = default;
        return false;
    }

    private Table CopyTable(Table t)
    {
        if (TryStandard(t, out Value std)) return std.AsTable;
        if (_memo.TryGetValue(t, out object? done)) return (Table)done;
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var copy = new Table(t.ArrayCount, 0);
        _memo[t] = copy;
        foreach (KeyValuePair<Value, Value> kv in t.Pairs()) copy.Set(Copy(kv.Key), Copy(kv.Value));
        if (t.Metatable is { } mt) copy.Metatable = CopyTable(mt);
        // what the VM keeps about classes travels with the tables that carry it
        if (t.Getters != null) copy.Getters = CopyTable(t.Getters);
        if (t.Setters != null) copy.Setters = CopyTable(t.Setters);
        if (t.Class is { } data)
        {
            if (!_memo.TryGetValue(data, out object? copied))
            {
                var d = new ClassData { Id = data.Id, Name = data.Name, OwnerTag = data.OwnerTag, FastTag = data.FastTag };
                d.Ids.UnionWith(data.Ids);
                if (data.Hidden != null)
                {
                    d.Hidden = new Table();
                    foreach (KeyValuePair<Value, Value> kv in data.Hidden.Pairs()) d.Hidden.Set(kv.Key, kv.Value);   // the members are immutable enough to share
                }
                _memo[data] = copied = d;
            }
            copy.Class = (ClassData)copied;
        }
        copy.Guarded = t.Guarded;
        copy.GuardTag = t.GuardTag;
        copy.IsCameoInstanceMeta = t.IsCameoInstanceMeta;
        copy.Frozen = t.Frozen;
        return copy;
    }

    // A numeric array is copied (the two VMs must not share memory they can both write).
    private Stdlib.FloatArray CopyArray(Stdlib.FloatArray a)
    {
        if (_memo.TryGetValue(a, out object? done)) return (Stdlib.FloatArray)done;
        var copy = new Stdlib.FloatArray(_to, (double[])a.Data.Clone(), a.Frozen);
        _memo[a] = copy;
        return copy;
    }

    private LuaFunction CopyFunction(LuaFunction f)
    {
        if (TryStandard(f, out Value std)) return std.AsFunction;
        if (_memo.TryGetValue(f, out object? done)) return (LuaFunction)done;
        if (f is not Closure cl)
            throw new TransferException($"cannot send the host function '{f.Name}' to another VM");
        RuntimeHelpers.EnsureSufficientExecutionStack();
        // Registered before its captured variables are copied: a function that refers to itself
        // (or to a table that holds it) finds the copy instead of recursing for ever.
        var boxes = new Box[cl.Upvals.Length];
        var copy = new Closure(cl.Proto, boxes);
        _memo[f] = copy;
        for (int i = 0; i < boxes.Length; i++) boxes[i] = CopyBox(cl.Upvals[i]);
        return copy;
    }

    // Two functions that share a captured variable still share one (their own) in the other VM.
    private Box CopyBox(Box b)
    {
        if (_memo.TryGetValue(b, out object? done)) return (Box)done;
        var copy = new Box();
        _memo[b] = copy;
        copy.Value = Copy(b.Value);
        return copy;
    }
}
