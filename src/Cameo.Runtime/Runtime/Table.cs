using System.Runtime.CompilerServices;

namespace Cameo.Runtime.Vm;

/// <summary>
/// The table type: an array part for keys 1..n plus an insertion-ordered hash part.
/// Deleting a key during traversal is safe (entries become tombstones until the next rehash).
/// </summary>
public sealed class Table
{
    private Value[] _arr = Array.Empty<Value>();
    private int _arrCount; // keys 1.._arrCount live in _arr; _arr[_arrCount-1] is non-nil when _arrCount > 0

    private struct Entry
    {
        public Value Key;
        public Value Val;   // nil = tombstone
        public int Next;    // next entry index in bucket chain, -1 = end
        public int Hash;
    }
    private Entry[]? _entries;
    private int[]? _buckets;     // index into _entries, -1 = empty
    private int _entryCount;     // used slots in _entries (including tombstones)
    private int _liveCount;      // non-tombstone entries

    public Table? Metatable;
    /// <summary>When this table is the metatable of a class's objects: the class's property
    /// getters and setters by name (see <c>get</c>/<c>set</c> in a class). Null for every other table.</summary>
    internal Table? Getters, Setters;
    /// <summary>On the metatable of a class's objects and on the class's own metatable.</summary>
    internal ClassData? Class;
    /// <summary>Set on an object (or class table) whose class has private or protected members:
    /// field access by name is checked against <see cref="GuardTag"/>, the class's identity.</summary>
    internal bool Guarded;
    internal ClassTag? GuardTag;
    /// <summary>True on the instance metatable of a Cameo record with inline methods.</summary>
    internal bool IsCameoInstanceMeta;
    /// <summary>True once frozen via <c>freeze(v)</c>; any subsequent mutation throws a Lua error.</summary>
    public bool Frozen { get; internal set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CheckMutable() { if (Frozen) ThrowFrozen(); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFrozen() => throw new LuaException("cannot mutate frozen table");

    public Table() { }
    public Table(int arrayCapacity, int hashCapacity)
    {
        if (arrayCapacity > 0) _arr = new Value[arrayCapacity];
        if (hashCapacity > 0) InitHash(hashCapacity);
    }

    /// <summary>The border used by the length operator.</summary>
    // Verified: verification/TableArray.dfy, method Length (the result is a border)
    public int Length
    {
        get
        {
            if (_arrCount > 0 || _liveCount == 0) return _arrCount;
            // Array part empty but integer keys may live in the hash (e.g. t[2] set before t[1]).
            if (GetInt(1).IsNil) return 0;
            int n = 1;
            while (!GetInt(n + 1).IsNil) n++;
            return n;
        }
    }

    public int ArrayCount => _arrCount;

    // ---------------------------------------------------------------- get

    // By-reference lookups: they return a reference to the stored value (or to a shared nil),
    // so the interpreter can copy straight from the table into a register.
    private static readonly Value[] s_nil = new Value[1];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly Value GetIntRef(long i)
    {
        if ((ulong)(i - 1) < (ulong)_arrCount) return ref _arr[i - 1];
        return ref GetHashRef(Value.FromInt(i), i.GetHashCode());
    }

    internal ref readonly Value GetStrRef(string key, int h)
    {
        if (_liveCount != 0)
        {
            var entries = _entries!;
            for (int e = _buckets![h & (_buckets.Length - 1)]; e >= 0; e = entries[e].Next)
            {
                ref Entry en = ref entries[e];
                if (en.Hash == h && (ReferenceEquals(en.Key.O, key) || (en.Key.Kind == ValueKind.String && string.Equals((string)en.Key.O!, key))))
                    return ref en.Val;
            }
        }
        return ref s_nil[0];
    }

    /// <summary>
    /// Field lookup that follows <c>__index</c> links which are tables (the usual class /
    /// inheritance chain) without leaving managed fast-path code. Sets <paramref name="slow"/>
    /// when the chain reaches something else (an <c>__index</c> function, a non-table), in
    /// which case the caller must take the general path. Profile: method lookups through a
    /// metatable were ~8% of an OOP-style workload when routed through the general path.
    /// </summary>
    internal ref readonly Value GetStrChainRef(string key, int hash, out bool slow)
    {
        Table t = this;
        for (int depth = 0; depth < 16; depth++)
        {
            ref readonly Value v = ref t.GetStrRef(key, hash);
            if (!v.IsNil) { slow = false; return ref v; }
            Table? mt = t.Metatable;
            if (mt == null) { slow = false; return ref v; }
            if (mt.Getters != null) break;      // a class with properties: the name may be one
            ref readonly Value h = ref mt.GetStrRef(Meta.Index.Name, Meta.Index.Hash);
            if (h.IsNil) { slow = false; return ref v; }
            if (h.Kind != ValueKind.Table) break;
            t = Unsafe.As<Table>(h.O!);
        }
        slow = true;
        return ref s_nil[0];
    }

    internal ref readonly Value GetRef(scoped in Value key)
    {
        switch (key.Kind)
        {
            case ValueKind.Integer: return ref GetIntRef(key.I);
            case ValueKind.String: return ref GetStrRef((string)key.O!, key.O!.GetHashCode());
            case ValueKind.Nil: return ref s_nil[0];
            case ValueKind.Float:
                if (LuaNumber.FloatToInteger(key.N, out long i)) return ref GetIntRef(i);
                break;
        }
        return ref GetHashRef(key, key.GetHashCode());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ref readonly Value GetHashRef(scoped in Value key, int h)
    {
        if (_liveCount != 0)
        {
            var entries = _entries!;
            for (int e = _buckets![h & (_buckets.Length - 1)]; e >= 0; e = entries[e].Next)
            {
                ref Entry en = ref entries[e];
                if (en.Hash == h && en.Key.RawEquals(key)) return ref en.Val;
            }
        }
        return ref s_nil[0];
    }

    public Value this[Value key]
    {
        get => Get(key);
        set => Set(key, value);
    }
    public Value this[string key]
    {
        get => GetStr(key);
        set => SetStr(key, value);
    }
    public Value this[long key]
    {
        get => GetInt(key);
        set => SetInt(key, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Value Get(in Value key)
    {
        switch (key.Kind)
        {
            case ValueKind.Integer: return GetInt(key.I);
            case ValueKind.String: return GetStr((string)key.O!);
            case ValueKind.Nil: return default;
            case ValueKind.Float:
                if (LuaNumber.FloatToInteger(key.N, out long i)) return GetInt(i);
                break;
        }
        return GetHash(key, key.GetHashCode());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Value GetInt(long i)
    {
        if ((ulong)(i - 1) < (ulong)_arrCount) return _arr[i - 1];
        if (_liveCount == 0) return default;
        return GetHash(Value.FromInt(i), i.GetHashCode());
    }

    public Value GetStr(string key) => _liveCount == 0 ? default : GetStr(key, key.GetHashCode());
    public Value GetStr(MetaKey key) => _liveCount == 0 ? default : GetStr(key.Name, key.Hash);

    /// <summary>String lookup with a precomputed <c>key.GetHashCode()</c>.</summary>
    public Value GetStr(string key, int h)
    {
        if (_liveCount == 0) return default;
        var entries = _entries!;
        for (int e = _buckets![h & (_buckets.Length - 1)]; e >= 0; e = entries[e].Next)
        {
            ref Entry en = ref entries[e];
            if (en.Hash == h && en.Key.Kind == ValueKind.String && (ReferenceEquals(en.Key.O, key) || string.Equals((string)en.Key.O!, key)))
                return en.Val;
        }
        return default;
    }

    private Value GetHash(in Value key, int h)
    {
        if (_liveCount == 0) return default;
        var entries = _entries!;
        for (int e = _buckets![h & (_buckets.Length - 1)]; e >= 0; e = entries[e].Next)
        {
            ref Entry en = ref entries[e];
            if (en.Hash == h && en.Key.RawEquals(key)) return en.Val;
        }
        return default;
    }

    // ---------------------------------------------------------------- set

    /// <summary>Raw set. Throws for nil and NaN keys.</summary>
    public void Set(in Value key, in Value value)
    {
        CheckMutable();
        switch (key.Kind)
        {
            case ValueKind.Integer: SetInt(key.I, value); return;
            case ValueKind.String: SetHash(key, key.GetHashCode(), value); return;
            case ValueKind.Nil: throw new LuaException("table index is nil");
            case ValueKind.Float:
                if (LuaNumber.FloatToInteger(key.N, out long i)) { SetInt(i, value); return; }
                if (double.IsNaN(key.N)) throw new LuaException("table index is NaN");
                break;
        }
        SetHash(key, key.GetHashCode(), value);
    }

    public void SetStr(string key, in Value value) { CheckMutable(); SetHash(Value.FromString(key), key.GetHashCode(), value); }
    public void SetStr(string key, int hash, in Value value)
    {
        CheckMutable();
        // Fast path: overwrite an existing field found by reference-equal key.
        if (_liveCount != 0)
        {
            var entries = _entries!;
            for (int e = _buckets![hash & (_buckets.Length - 1)]; e >= 0; e = entries[e].Next)
            {
                ref Entry en = ref entries[e];
                if (en.Hash == hash && ReferenceEquals(en.Key.O, key))
                {
                    if (en.Val.IsNil || value.IsNil) break; // live-count bookkeeping: general path
                    Value.Copy(ref en.Val, value);
                    return;
                }
            }
        }
        SetHash(Value.FromString(key), hash, value);
    }

    /// <summary>Stores values at consecutive integer keys start+1.. (table constructors).
    /// Nils inside the range are kept as holes so the length matches Lua's.</summary>
    public void SetRange(long start, ReadOnlySpan<Value> values)
    {
        CheckMutable();
        if (start == _arrCount && _liveCount == 0)
        {
            int n = _arrCount + values.Length;
            if (_arr.Length < n) Array.Resize(ref _arr, n);
            values.CopyTo(_arr.AsSpan(_arrCount));
            if (_entryCount != 0)
                for (long k = _arrCount + 1; k <= n; k++) ForgetTombstone(k);
            while (n > 0 && _arr[n - 1].IsNil) n--;
            _arrCount = n;
            return;
        }
        for (int i = 0; i < values.Length; i++) SetInt(start + 1 + i, values[i]);
    }

    // Verified: verification/TableArray.dfy, method SetInt (invariant kept; behaves as a map update)
    public void SetInt(long i, in Value value)
    {
        CheckMutable();
        if ((ulong)(i - 1) < (ulong)_arrCount)
        {
            _arr[i - 1] = value;
            if (value.IsNil && i == _arrCount)
            {
                // keep the invariant: last array slot is non-nil
                int n = _arrCount - 1;
                while (n > 0 && _arr[n - 1].IsNil) n--;
                _arrCount = n;
            }
            return;
        }
        if (i == _arrCount + 1)
        {
            if (value.IsNil)
            {
                if (_liveCount != 0) SetHash(Value.FromInt(i), i.GetHashCode(), value);
                return;
            }
            Append(value);
            return;
        }
        SetHash(Value.FromInt(i), i.GetHashCode(), value);
    }

    /// <summary>Appends at index Length+1 (array part).</summary>
    // Verified: verification/TableArray.dfy, method Append
    public void Append(in Value value)
    {
        CheckMutable();
        if (_arrCount == _arr.Length)
            Array.Resize(ref _arr, _arr.Length == 0 ? 4 : _arr.Length * 2);
        _arr[_arrCount++] = value;
        if (_entryCount != _liveCount) ForgetTombstone(_arrCount);
        if (_liveCount != 0) MigrateFromHash();
    }

    // A key that now lives in the array part must not also be findable as a deleted entry of the
    // hash part: next() would resume from that stale slot (skipping live hash entries) after the
    // key is removed from the array part again and the array shrinks.
    private void ForgetTombstone(long k)
    {
        int e = FindEntry(Value.FromInt(k), k.GetHashCode());
        if (e >= 0 && _entries![e].Val.IsNil) _entries[e].Key = default;
    }

    // Moves keys _arrCount+1, _arrCount+2, ... from the hash part into the array part.
    // Verified: verification/TableArray.dfy, method Migrate
    private void MigrateFromHash()
    {
        while (true)
        {
            long k = _arrCount + 1;
            int h = k.GetHashCode();
            int e = FindEntry(Value.FromInt(k), h);
            if (e < 0 || _entries![e].Val.IsNil) return;
            Value v = _entries[e].Val;
            _entries[e].Val = default;
            // Also forget the key: the entry is not a "deleted during traversal" tombstone.
            // Leaving the key would make next() resume from this stale slot (skipping live hash
            // entries) once the key has been removed from the array part and the array shrank.
            _entries[e].Key = default;
            _liveCount--;
            if (_arrCount == _arr.Length) Array.Resize(ref _arr, _arr.Length * 2);
            _arr[_arrCount++] = v;
            if (_liveCount == 0) return;
        }
    }

    private void InitHash(int capacity)
    {
        int size = 4;
        while (size < capacity) size <<= 1;
        _entries = new Entry[size];
        _buckets = new int[size];
        Array.Fill(_buckets, -1);
        _entryCount = 0;
        _liveCount = 0;
    }

    private int FindEntry(in Value key, int h)
    {
        if (_entries == null) return -1;
        var entries = _entries;
        for (int e = _buckets![h & (_buckets.Length - 1)]; e >= 0; e = entries[e].Next)
        {
            ref Entry en = ref entries[e];
            if (en.Hash == h && en.Key.RawEquals(key)) return e;
        }
        return -1;
    }

    private void SetHash(in Value key, int h, in Value value)
    {
        int e = FindEntry(key, h);
        if (e >= 0)
        {
            ref Entry en = ref _entries![e];
            if (en.Val.IsNil) { if (!value.IsNil) _liveCount++; }
            else if (value.IsNil) _liveCount--;
            en.Val = value;
            return;
        }
        if (value.IsNil) return;
        if (_entries == null) InitHash(4);
        else if (_entryCount == _entries.Length) Rehash();
        int idx = _entryCount++;
        int b = h & (_buckets!.Length - 1);
        ref Entry ne = ref _entries![idx];
        ne.Key = key.Kind == ValueKind.Float && LuaNumber.FloatToInteger(key.N, out long ik) ? Value.FromInt(ik) : key;
        ne.Val = value;
        ne.Hash = h;
        ne.Next = _buckets[b];
        _buckets[b] = idx;
        _liveCount++;
    }

    /// <summary>Drops the keys of removed entries (kept as tombstones for traversal), so that the
    /// table no longer references them. Used when sweeping weak tables.</summary>
    internal void PurgeRemovedKeys()
    {
        if (_entries != null && _entryCount != _liveCount) Rehash();
    }

    private void Rehash()
    {
        var old = _entries!;
        int oldCount = _entryCount;
        int size = 4;
        while (size < (_liveCount + 1) * 2) size <<= 1;
        _entries = new Entry[size];
        _buckets = new int[size];
        Array.Fill(_buckets, -1);
        _entryCount = 0;
        for (int i = 0; i < oldCount; i++)
        {
            if (old[i].Val.IsNil) continue;
            int idx = _entryCount++;
            int b = old[i].Hash & (size - 1);
            _entries[idx] = old[i];
            _entries[idx].Next = _buckets[b];
            _buckets[b] = idx;
        }
    }

    // ---------------------------------------------------------------- traversal

    /// <summary>
    /// Implements <c>next</c>. Pass nil to start. Returns false at the end.
    /// Throws if the key is not present in the table.
    /// </summary>
    public bool Next(in Value key, out Value nextKey, out Value nextValue)
    {
        int idx; // position in the virtual sequence: array slots then hash entries
        if (key.IsNil) idx = 0;
        else
        {
            long ik;
            bool isInt = key.Kind == ValueKind.Integer;
            ik = key.I;
            if (key.Kind == ValueKind.Float && LuaNumber.FloatToInteger(key.N, out long fi)) { isInt = true; ik = fi; }
            if (isInt && ik >= 1 && ik <= _arrCount) idx = (int)ik;
            else
            {
                int e = FindEntry(key, key.GetHashCode());
                if (e < 0)
                {
                    // The key may have been in the array part before it shrank.
                    if (isInt && ik >= 1 && ik <= _arr.Length) idx = _arrCount;
                    else throw new LuaException("invalid key to 'next'");
                }
                else idx = _arrCount + e + 1;
            }
        }
        for (; idx < _arrCount; idx++)
        {
            if (!_arr[idx].IsNil)
            {
                nextKey = Value.FromInt(idx + 1);
                nextValue = _arr[idx];
                return true;
            }
        }
        if (_entries != null)
        {
            for (int e = idx - _arrCount; e < _entryCount; e++)
            {
                if (!_entries[e].Val.IsNil)
                {
                    nextKey = _entries[e].Key;
                    nextValue = _entries[e].Val;
                    return true;
                }
            }
        }
        nextKey = default;
        nextValue = default;
        return false;
    }

    /// <summary>Enumerates all key/value pairs (array part first). Do not add keys while enumerating.</summary>
    public IEnumerable<KeyValuePair<Value, Value>> Pairs()
    {
        Value k = default;
        while (Next(k, out var nk, out var nv))
        {
            yield return new KeyValuePair<Value, Value>(nk, nv);
            k = nk;
        }
    }

    // ---------------------------------------------------------------- array helpers (for table.*)

    /// <summary>Inserts at 1-based position, shifting later elements up. pos must be in [1, Length+1].</summary>
    // Verified: verification/TableArray.dfy, method InsertFast (the array fast path)
    public void Insert(long pos, in Value value)
    {
        CheckMutable();
        int n = Length;
        if (n == _arrCount && pos >= 1 && pos <= n + 1 && !value.IsNil)
        {
            if (_arrCount == _arr.Length) Array.Resize(ref _arr, _arr.Length == 0 ? 4 : _arr.Length * 2);
            Array.Copy(_arr, pos - 1, _arr, pos, n - (pos - 1));
            _arr[pos - 1] = value;
            _arrCount++;
            if (_entryCount != _liveCount) ForgetTombstone(_arrCount);
            if (_liveCount != 0) MigrateFromHash();
            return;
        }
        for (long i = n; i >= pos; i--) SetInt(i + 1, GetInt(i));
        SetInt(pos, value);
    }

    /// <summary>Removes and returns the element at 1-based position, shifting later elements down.</summary>
    // Verified: verification/TableArray.dfy, method RemoveAtFast (the array fast path)
    public Value RemoveAt(long pos)
    {
        CheckMutable();
        int n = Length;
        if (n == _arrCount && pos >= 1 && pos <= n)
        {
            Value v = _arr[pos - 1];
            Array.Copy(_arr, pos, _arr, pos - 1, n - pos);
            _arr[n - 1] = default;
            int c = n - 1;
            while (c > 0 && _arr[c - 1].IsNil) c--;
            _arrCount = c;
            return v;
        }
        Value r = GetInt(pos);
        for (long i = pos; i < n; i++) SetInt(i, GetInt(i + 1));
        if (pos <= n) SetInt(n, default);
        return r;
    }

    /// <summary>Direct access to the array part (elements 1..ArrayCount) for fast paths such as sort.</summary>
    public Span<Value> ArraySpan => _arr.AsSpan(0, _arrCount);

    public void EnsureArrayCapacity(int n)
    {
        if (_arr.Length < n) Array.Resize(ref _arr, n);
    }
}
