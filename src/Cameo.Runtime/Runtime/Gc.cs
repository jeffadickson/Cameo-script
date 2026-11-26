using System.Collections.Concurrent;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace Cameo.Runtime.Vm;

// Garbage-collection features layered on top of the .NET collector: weak tables (__mode),
// finalizers (__gc) and the collectgarbage() controls.
//
// Memory itself is managed by the .NET GC. What Lua adds are two *observable* behaviours:
//
//  * Weak tables. A Table stores ordinary strong references, so weakness is applied at
//    collection points: the collectable keys/values of every registered weak table are moved
//    out into weak holders, the .NET GC runs, and the survivors are put back.
//
//  * Finalizers. An object marked for finalization gets a companion token that has a .NET
//    finalizer and is kept alive exactly as long as the object (ConditionalWeakTable). When the
//    object becomes unreachable the token's finalizer resurrects it into a queue, and the VM
//    thread calls __gc at its next safe point (never on the .NET finalizer thread).
public sealed partial class LuaState
{
    private GcManager? _gc;
    internal GcManager Gc => _gc ??= new GcManager(this);

    /// <summary>
    /// A point where pending finalizers may run and weak tables may be swept. Cheap when there is
    /// nothing to do. The standard library calls it from setmetatable and collectgarbage; hosts
    /// and other natives may call it too.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void GcSafePoint()
    {
        GcManager? gc = _gc;
        if (gc != null && gc.WorkPending) gc.RunPending();
    }

    /// <summary>Runs a full collection: sweeps weak tables and calls pending finalizers.</summary>
    public void CollectGarbage() => Gc.FullCollect();

    private bool _stateClosed;
    /// <summary>
    /// Closes the state: calls the finalizer of every object still marked for finalization,
    /// in reverse order of marking, then cancels pending work and releases run-owned
    /// resources. Safe to call more than once; the closed state must not be reused.
    /// </summary>
    public void Close()
    {
        if (_stateClosed) return;
        _stateClosed = true;
        try { _gc?.Close(); }
        finally
        {
            RequestAbort(AbortReason.Cancelled);
            Scheduler.Shutdown();
            DropPendingWorkers();
        }
    }
}

internal sealed class GcManager
{
    private sealed class GcToken
    {
        public readonly object Owner;
        public readonly long Seq;
        public readonly GcManager Manager;
        public bool Done;

        public GcToken(object owner, long seq, GcManager manager) { Owner = owner; Seq = seq; Manager = manager; }

        // Runs on the .NET finalizer thread once Owner is unreachable. Enqueueing the token makes
        // the owner reachable again (resurrection) until the VM thread has called __gc.
        ~GcToken()
        {
            if (Done || Manager._closed) return;
            Manager._queue.Enqueue(this);
            Manager.WorkPending = true;
        }
    }

    // Notices .NET gen-2 collections so weak tables get swept without an explicit collectgarbage().
    private sealed class Gen2Callback
    {
        // A raw weak handle rather than a WeakReference object: a WeakReference reachable only
        // from this finalizable object may itself be finalized (and cleared) first.
        private System.Runtime.InteropServices.GCHandle _manager;
        private int _lastGen2;

        public Gen2Callback(GcManager manager)
        {
            _manager = System.Runtime.InteropServices.GCHandle.Alloc(manager, System.Runtime.InteropServices.GCHandleType.Weak);
            _lastGen2 = GC.CollectionCount(2);
        }

        ~Gen2Callback()
        {
            if (Environment.HasShutdownStarted) return;
            if (_manager.Target is not GcManager m || m._closed)
            {
                _manager.Free();
                return;
            }
            int g2 = GC.CollectionCount(2);
            if (g2 != _lastGen2)
            {
                _lastGen2 = g2;
                m._gen2Seen = true;
                m.WorkPending = true;
            }
            GC.ReRegisterForFinalize(this);
        }
    }

    // One entry taken out of a weak table for the duration of a collection.
    private sealed class Item
    {
        public Value Key, Val;              // the strongly held side(s)
        public ValueKind KeyKind, ValKind;  // kinds of weakly held sides
        public WeakReference? WeakKey, WeakVal;
        public DependentHandle Ephemeron;   // weak key -> value (mode "k")
        public bool IsEphemeron;
    }

    private sealed class Holder
    {
        public WeakReference<Table> Table = null!;
        public List<Item> Items = new();
    }

    private readonly LuaState _L;

    private readonly ConditionalWeakTable<object, GcToken> _tokens = new();
    private readonly List<WeakReference<GcToken>> _allTokens = new();
    private int _pruneAt = 256;
    private long _seq;
    private readonly ConcurrentQueue<GcToken> _queue = new();

    private readonly List<WeakReference<Table>> _weakTables = new();
    private readonly ConditionalWeakTable<Table, object> _weakSet = new();
    private bool _gen2CallbackStarted;
    private volatile bool _gen2Seen;
    private long _lastAutoSweep;

    private bool _collecting;     // inside a collection or a finalizer call
    private volatile bool _closed;

    /// <summary>Set (possibly from the finalizer thread) when the VM thread has something to do.</summary>
    internal volatile bool WorkPending;
    /// <summary>collectgarbage("stop") / ("restart"): whether automatic work is enabled.</summary>
    internal bool Running = true;
    /// <summary>The mode last selected with collectgarbage("incremental"/"generational").</summary>
    internal string Mode = "incremental";

    public GcManager(LuaState state) { _L = state; }

    private static bool IsCollectable(in Value v) =>
        v.Kind is ValueKind.Table or ValueKind.Function or ValueKind.Userdata or ValueKind.Thread;

    private static Value Rebuild(ValueKind kind, object o) => kind switch
    {
        ValueKind.Table => Value.FromTable((Table)o),
        ValueKind.Function => Value.FromFunction((LuaFunction)o),
        ValueKind.Thread => Value.FromThread((LuaThread)o),
        _ => Value.FromUserdata(o),
    };

    private static string? WeakMode(Table? mt)
    {
        if (mt == null) return null;
        Value m = mt.GetStr(Meta.Mode);
        if (!m.IsString) return null;
        string s = m.AsString;
        return s.Contains('k') || s.Contains('v') ? s : null;
    }

    // ------------------------------------------------------------------ registration

    /// <summary>Called whenever a metatable is assigned to a table or userdata.</summary>
    public void OnSetMetatable(object obj, Table? mt)
    {
        if (mt == null || _closed) return;
        if (obj is Table t && WeakMode(mt) != null && !_weakSet.TryGetValue(t, out _))
        {
            _weakSet.Add(t, this);
            _weakTables.Add(new WeakReference<Table>(t));
            if (!_gen2CallbackStarted)
            {
                _gen2CallbackStarted = true;
                _ = new Gen2Callback(this);
            }
        }
        // As in Lua, an object is marked for finalization only if __gc is present at this moment.
        if (!mt.GetStr(Meta.Gc).IsNil) Mark(obj);
    }

    private void Mark(object obj)
    {
        if (_tokens.TryGetValue(obj, out GcToken? existing) && !existing.Done) return;
        var token = new GcToken(obj, ++_seq, this);
        _tokens.AddOrUpdate(obj, token);
        _allTokens.Add(new WeakReference<GcToken>(token, trackResurrection: true));
        if (_allTokens.Count > _pruneAt)
        {
            _allTokens.RemoveAll(static w => !w.TryGetTarget(out GcToken? tk) || tk.Done);
            _pruneAt = Math.Max(256, _allTokens.Count * 2);
        }
    }

    // ------------------------------------------------------------------ collection

    /// <summary>collectgarbage("collect"): sweep weak tables, then run the finalizers of dead objects.</summary>
    public void FullCollect()
    {
        if (_collecting || _closed) return;
        _collecting = true;
        try
        {
            ClearDeadStack();
            List<Holder> holders = Detach();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Reattach(holders);
            _gen2Seen = false;
            _lastAutoSweep = Environment.TickCount64;
        }
        finally
        {
            _collecting = false;
        }
        RunFinalizers();
    }

    /// <summary>Work requested by the finalizer thread; called through LuaState.GcSafePoint.</summary>
    public void RunPending()
    {
        if (_collecting || _closed || !Running) return;
        if (_gen2Seen && _weakTables.Count > 0)
        {
            // The .NET GC did a full collection: objects only referenced from weak tables are
            // still there (our tables hold them strongly), so sweep now. Rate-limited because a
            // sweep forces another full collection.
            if (Environment.TickCount64 - _lastAutoSweep >= 1000) FullCollect();
            else if (!_queue.IsEmpty) RunFinalizers();
            return;
        }
        _gen2Seen = false;
        RunFinalizers();
    }

    // Registers above the live part of the running thread's stack are dead but may still hold
    // references; clear them so they do not keep garbage alive (Lua's collector ignores them too).
    private void ClearDeadStack()
    {
        LuaThread th = _L.CurrentThread;
        int top = th.CallTop;
        if (top >= 0 && top < th.Stack.Length) Array.Clear(th.Stack, top, th.Stack.Length - top);
    }

    // Moves the weakly held entries of all weak tables into holders. Kept out of line so no
    // strong reference to a table or its contents stays in a caller's frame during the GC.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<Holder> Detach()
    {
        var holders = new List<Holder>();
        var entries = new List<KeyValuePair<Value, Value>>();
        for (int i = _weakTables.Count - 1; i >= 0; i--)
        {
            WeakReference<Table> wr = _weakTables[i];
            if (!wr.TryGetTarget(out Table? t))
            {
                _weakTables.RemoveAt(i);
                continue;
            }
            string? mode = WeakMode(t.Metatable);
            if (mode == null)
            {
                // no longer weak
                _weakTables.RemoveAt(i);
                _weakSet.Remove(t);
                continue;
            }
            bool weakK = mode.Contains('k'), weakV = mode.Contains('v');
            entries.Clear();
            foreach (var kv in t.Pairs())
            {
                if ((weakK && IsCollectable(kv.Key)) || (weakV && IsCollectable(kv.Value))) entries.Add(kv);
            }
            if (entries.Count == 0) continue;
            var holder = new Holder { Table = wr };
            foreach (var kv in entries)
            {
                t.Set(kv.Key, default);
                var item = new Item();
                bool wk = weakK && IsCollectable(kv.Key);
                bool wv = weakV && IsCollectable(kv.Value);
                if (wk && !wv && !weakV)
                {
                    // ephemeron: the value is kept alive only through its key
                    item.IsEphemeron = true;
                    item.KeyKind = kv.Key.Kind;
                    item.Ephemeron = new DependentHandle(kv.Key.O, new Box(kv.Value));
                }
                else
                {
                    if (wk) { item.KeyKind = kv.Key.Kind; item.WeakKey = new WeakReference(kv.Key.O); }
                    else item.Key = kv.Key;
                    if (wv) { item.ValKind = kv.Value.Kind; item.WeakVal = new WeakReference(kv.Value.O); }
                    else item.Val = kv.Value;
                }
                holder.Items.Add(item);
            }
            t.PurgeRemovedKeys(); // removed entries would otherwise still reference their keys
            holders.Add(holder);
        }
        return holders;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Reattach(List<Holder> holders)
    {
        foreach (Holder h in holders)
        {
            bool alive = h.Table.TryGetTarget(out Table? t);
            foreach (Item item in h.Items)
            {
                if (item.IsEphemeron)
                {
                    var (key, dep) = item.Ephemeron.TargetAndDependent;
                    item.Ephemeron.Dispose();
                    if (alive && key != null) t!.Set(Rebuild(item.KeyKind, key), ((Box)dep!).Value);
                    continue;
                }
                if (!alive) continue;
                Value k = item.Key, v = item.Val;
                if (item.WeakKey != null)
                {
                    object? o = item.WeakKey.Target;
                    if (o == null) continue;
                    k = Rebuild(item.KeyKind, o);
                }
                if (item.WeakVal != null)
                {
                    object? o = item.WeakVal.Target;
                    if (o == null) continue;
                    v = Rebuild(item.ValKind, o);
                }
                t!.Set(k, v);
            }
        }
    }

    // ------------------------------------------------------------------ finalizers

    private void RunFinalizers()
    {
        if (_collecting) return;
        WorkPending = _gen2Seen;
        if (_queue.IsEmpty) return;
        var batch = new List<GcToken>();
        while (_queue.TryDequeue(out GcToken? tk)) batch.Add(tk);
        // objects that died together are finalized in reverse order of marking
        batch.Sort(static (a, b) => b.Seq.CompareTo(a.Seq));
        foreach (GcToken tk in batch) Finalize(tk);
    }

    private void Finalize(GcToken token)
    {
        if (token.Done) return;
        token.Done = true;
        GC.SuppressFinalize(token);
        object owner = token.Owner;
        if (_tokens.TryGetValue(owner, out GcToken? cur) && ReferenceEquals(cur, token)) _tokens.Remove(owner);
        Value v = owner is Table t ? Value.FromTable(t) : Value.FromUserdata(owner);
        Value h = _L.GetMetamethod(v, Meta.Gc);
        if (!h.IsFunction) return;
        bool saved = _collecting;
        _collecting = true; // collectgarbage() inside a finalizer does nothing, as in Lua
        try
        {
            _L.Call(h, v);
        }
        catch (LuaException e)
        {
            Value ev = e.Value;
            string msg = ev.IsString ? ev.AsString : ev.ToString();
            _L.WriteErr("Lua warning: error in __gc (" + msg + ")\n");
        }
        finally
        {
            _collecting = saved;
        }
    }

    /// <summary>Finalizes everything that is still marked, newest first, then shuts down.</summary>
    public void Close()
    {
        if (_closed) return;
        for (int pass = 0; pass < 10; pass++)
        {
            var set = new HashSet<GcToken>();
            while (_queue.TryDequeue(out GcToken? q)) set.Add(q);
            foreach (WeakReference<GcToken> w in _allTokens)
                if (w.TryGetTarget(out GcToken? tk) && !tk.Done) set.Add(tk);
            _allTokens.Clear();
            if (set.Count == 0) break;
            var batch = new List<GcToken>(set);
            batch.Sort(static (a, b) => b.Seq.CompareTo(a.Seq));
            foreach (GcToken tk in batch) Finalize(tk);
        }
        _closed = true;
        WorkPending = false;
    }
}
