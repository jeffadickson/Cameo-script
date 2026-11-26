using System.Data;
using System.Data.Common;
using System.Text;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>An open database connection (userdata). One statement runs at a time, in the order asked.</summary>
public sealed class LuaDbConnection : IUserdata
{
    private readonly LuaState _state;
    internal readonly DbConnection Connection;
    internal readonly string Name;
    internal readonly SemaphoreSlim Gate = new(1, 1);
    internal DbTransaction? Transaction;
    private int _closed;
    internal LuaDbConnection(LuaState state, DbConnection connection, string name) { _state = state; Connection = connection; Name = name; }
    public string TypeName => "Connection";
    public Table? Metatable { get => _state.MetaFor("Connection"); set { } }
    internal bool Closed => Volatile.Read(ref _closed) != 0;
    internal bool MarkClosed() => Interlocked.Exchange(ref _closed, 1) == 0;
}

/// <summary>
/// <c>db</c>: SQL databases through any ADO.NET provider the host plugs in
/// (<see cref="LuaState.DatabaseFactory"/>); the VM itself contains no database driver.
///
/// <c>db.open(name)</c> gives a connection; <c>query</c>, <c>queryOne</c>, <c>scalar</c> and
/// <c>exec</c> run SQL with parameters — <c>?</c> placeholders filled from a list, or
/// <c>@name</c> placeholders filled from a table — never by pasting values into the SQL text.
/// SQL NULL is written <c>db.null</c> in a parameter list, and a NULL column is absent from a row.
/// Calls wait for the database without blocking other tasks. Each returns its result, or nil
/// and a FutureError whose <c>cause</c> is a typed <c>DbError</c>. Database access is a
/// permission (<see cref="LuaState.AllowDatabase"/>), off unless the host turns it on.
/// </summary>
public static class DbLib
{
    private sealed class DbFailure(string kind, string message) : Exception(message)
    {
        public string Kind => kind;
    }

    public static void Open(LuaState L)
    {
        var db = new Table();
        // db.null stands for SQL NULL in a parameter list (a Lua list cannot hold nil); it is json.null
        db.SetStr("null", L.Globals.GetStr("json").AsTable.GetStr("null"));
        var errorMeta = L.MetaFor("DbError");
        L.Register(errorMeta, "__tostring", static (ref CallContext c) =>
        {
            Table t = c.CheckTable(0);
            return c.Return($"DbError({t.GetStr("kind")}): {t.GetStr("message")}");
        });

        L.Register(db, "open", static (ref CallContext c) =>
        {
            string name = c.CheckText(0);
            LuaState state = c.L;
            Func<string, DbConnection>? factory = state.DatabaseFactory;
            bool allowed = state.AllowDatabase;
            return Wait(ref c, name, async ct =>
            {
                if (!allowed) throw new DbFailure("permission_denied", "database access is not enabled for this VM");
                if (factory == null) throw new DbFailure("unavailable", "no database provider is installed in this VM");
                DbConnection conn = factory(name) ?? throw new DbFailure("unavailable", $"there is no database named '{name}'");
                try
                {
                    await conn.OpenAsync(ct).ConfigureAwait(false);
                }
                catch (Exception openingError)
                {
                    // Retain acquisition diagnostics even when releasing the failed handle fails.
                    await PendingResource.FinallyAsync(() => Task.FromException(openingError),
                        () => conn.DisposeAsync()).ConfigureAwait(false);
                    throw; // FinallyAsync always rethrows the opening failure.
                }
                return PendingResource.Transfer(conn, ct, (s, owned) =>
                {
                    var handle = new LuaDbConnection(s, owned, name);
                    s.Scheduler.Lifetime.Register(() => Close(handle)); // a stopped run does not keep connections
                    return Value.FromUserdata(handle);
                }, owned => owned.DisposeAsync());
            });
        });

        var m = new Table();
        // query(sql [, params]): a list of rows; each row is a table from column name to value (a NULL column is absent)
        L.RegisterInstance<LuaDbConnection>(m, "query", static (ref CallContext c) => Statement(ref c, Shape.Rows));
        // queryOne(sql [, params]): the first row, or nil
        L.RegisterInstance<LuaDbConnection>(m, "queryOne", static (ref CallContext c) => Statement(ref c, Shape.Row));
        // scalar(sql [, params]): the first column of the first row, or nil
        L.RegisterInstance<LuaDbConnection>(m, "scalar", static (ref CallContext c) => Statement(ref c, Shape.Scalar));
        // exec(sql [, params]): the number of rows changed
        L.RegisterInstance<LuaDbConnection>(m, "exec", static (ref CallContext c) => Statement(ref c, Shape.Affected));
        L.RegisterInstance<LuaDbConnection>(m, "begin", static (ref CallContext c) =>
        {
            LuaDbConnection conn = Check(ref c);
            return Wait(ref c, conn.Name, ct => Locked(conn, async () =>
            {
                if (conn.Transaction != null) throw new DbFailure("invalid", "a transaction is already open on this connection");
                conn.Transaction = await conn.Connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                return _ => Value.True;
            }));
        });
        L.RegisterInstance<LuaDbConnection>(m, "commit", static (ref CallContext c) => End(ref c, commit: true));
        L.RegisterInstance<LuaDbConnection>(m, "rollback", static (ref CallContext c) => End(ref c, commit: false));
        // closing twice is fine
        L.RegisterInstance<LuaDbConnection>(m, "close", static (ref CallContext c) => { Close(c[0].O as LuaDbConnection ?? throw c.TypeError(0, "Connection")); return c.Return(); });
        Table mt = L.MetaFor("Connection");
        mt.SetStr("__index", m);
        mt.SetStr("__close", m.GetStr("close"));
        mt.SetStr("__gc", m.GetStr("close"));

        L.Globals.SetStr("db", db);
        L.LoadedModules.SetStr("db", db);
    }

    private static LuaDbConnection Check(ref CallContext c)
    {
        // A closed connection is not an error here: the statement then fails on the error track
        // with a DbError of kind "closed", like every other database failure.
        return c[0].O as LuaDbConnection ?? throw c.TypeError(0, "Connection");
    }

    private static void Close(LuaDbConnection conn)
    {
        if (!conn.MarkClosed()) return;
        // after the statement that is running, if any; an open transaction is rolled back by the provider
        _ = Task.Run(async () =>
        {
            await conn.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await PendingResource.FinallyAsync(
                    () => conn.Transaction?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                    () => conn.Connection.DisposeAsync()).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("Cameo.Runtime database close failed: {0}", error);
            }
            finally { conn.Gate.Release(); }
        });
    }

    private static int End(ref CallContext c, bool commit)
    {
        LuaDbConnection conn = Check(ref c);
        return Wait(ref c, conn.Name, ct => Locked(conn, async () =>
        {
            DbTransaction tx = conn.Transaction ?? throw new DbFailure("invalid", "no transaction is open on this connection");
            conn.Transaction = null;
            try
            {
                if (commit) await tx.CommitAsync(ct).ConfigureAwait(false); else await tx.RollbackAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
            return _ => Value.True;
        }));
    }

    private static async Task<Func<LuaState, Value>> Locked(LuaDbConnection conn, Func<Task<Func<LuaState, Value>>> work)
    {
        await conn.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (conn.Closed) throw new DbFailure("closed", "the database connection is closed");
            return await work().ConfigureAwait(false);
        }
        finally
        {
            conn.Gate.Release();
        }
    }

    // ------------------------------------------------------------------ statements

    private enum Shape { Rows, Row, Scalar, Affected }

    private static int Statement(ref CallContext c, Shape shape)
    {
        LuaDbConnection conn = Check(ref c);
        string sql = LuaText.Decode(c.CheckString(1));
        var parameters = new List<(string Name, object Value)>();
        if (!c.IsNoneOrNil(2))
        {
            Table t = c.CheckTable(2);
            int positional = t.Length;
            for (int i = 1; i <= positional; i++) parameters.Add(("@p" + i, ToDb(ref c, t.GetInt(i), $"parameter {i}")));
            foreach (KeyValuePair<Value, Value> kv in t.Pairs())
            {
                if (kv.Key.IsString) parameters.Add(("@" + LuaText.Decode(kv.Key.AsString), ToDb(ref c, kv.Value, $"parameter '{kv.Key}'")));
            }
            sql = NumberPlaceholders(sql, out int used);
            if (used != positional)
                throw c.ArgError(2, $"the SQL has {used} '?' placeholder{(used == 1 ? "" : "s")} but {positional} value{(positional == 1 ? " was" : "s were")} given"
                    + (positional < used ? " (a list cannot hold nil: write db.null for NULL)" : ""));
        }
        else
        {
            sql = NumberPlaceholders(sql, out int used);
            if (used != 0) throw c.ArgError(2, $"the SQL has {used} '?' placeholder{(used == 1 ? "" : "s")} but no values were given");
        }
        long maxRows = c.L.Limits.MaxArrayLength;
        return Wait(ref c, conn.Name, ct => Locked(conn, async () =>
        {
            await using DbCommand cmd = conn.Connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = conn.Transaction;
            foreach (var (name, value) in parameters)
            {
                DbParameter p = cmd.CreateParameter();
                p.ParameterName = name;
                p.Value = value;
                cmd.Parameters.Add(p);
            }
            if (shape == Shape.Affected)
            {
                int n = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return _ => Value.FromInt(Math.Max(n, 0));
            }
            var rows = new List<object?[]>();
            string[] columns;
            await using (DbDataReader reader = await cmd.ExecuteReaderAsync(shape == Shape.Rows ? CommandBehavior.Default : CommandBehavior.SingleRow, ct).ConfigureAwait(false))
            {
                columns = new string[reader.FieldCount];
                for (int i = 0; i < columns.Length; i++) columns[i] = reader.GetName(i);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var row = new object?[columns.Length];
                    reader.GetValues(row!);
                    rows.Add(row);
                    if (shape != Shape.Rows) break;
                    if (rows.Count > maxRows) throw new DbFailure("failed", "the query returned too many rows");
                }
            }
            return _ =>
            {
                Value Row(object?[] r)
                {
                    var t = new Table(0, columns.Length);
                    for (int i = 0; i < columns.Length; i++) t.Set(Value.FromText(columns[i]), FromDb(r[i]));
                    return Value.FromTable(t);
                }
                switch (shape)
                {
                    case Shape.Scalar: return rows.Count == 0 || columns.Length == 0 ? Value.Nil : FromDb(rows[0][0]);
                    case Shape.Row: return rows.Count == 0 ? Value.Nil : Row(rows[0]);
                    default:
                        var list = new Table(rows.Count, 0);
                        foreach (object?[] r in rows) list.Append(Row(r));
                        return Value.FromTable(list);
                }
            };
        }));
    }

    /// <summary>Rewrites each <c>?</c> outside strings, quoted names and comments to <c>@p1</c>, <c>@p2</c>, ...
    /// (the one placeholder form every ADO.NET provider understands).</summary>
    internal static string NumberPlaceholders(string sql, out int count)
    {
        count = 0;
        var sb = new StringBuilder(sql.Length + 8);
        for (int i = 0; i < sql.Length; i++)
        {
            char ch = sql[i];
            if (ch == '\'' || ch == '"' || ch == '`')
            {
                // quoted text ends at the same quote; a doubled quote inside it is an escaped quote
                int j = i + 1;
                while (j < sql.Length)
                {
                    if (sql[j] == ch) { if (j + 1 < sql.Length && sql[j + 1] == ch) j++; else break; }
                    j++;
                }
                j = Math.Min(j, sql.Length - 1);
                sb.Append(sql, i, j - i + 1);
                i = j;
            }
            else if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                int j = sql.IndexOf('\n', i);
                if (j < 0) j = sql.Length - 1;
                sb.Append(sql, i, j - i + 1);
                i = j;
            }
            else if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int j = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                j = j < 0 ? sql.Length - 1 : j + 1;
                sb.Append(sql, i, j - i + 1);
                i = j;
            }
            else if (ch == '?') sb.Append("@p").Append(++count);
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    private static object ToDb(ref CallContext c, in Value v, string what)
    {
        switch (v.Kind)
        {
            case ValueKind.Nil: return DBNull.Value;
            case ValueKind.Boolean: return v.AsBool;
            case ValueKind.Integer: return v.I;
            case ValueKind.Float: return v.N;
            case ValueKind.String:
                {
                    // text when it is valid UTF-8, otherwise the bytes themselves (a blob)
                    byte[] bytes = LuaText.ToBytes(v.AsString);
                    try { return new UTF8Encoding(false, true).GetString(bytes); }
                    catch (DecoderFallbackException) { return bytes; }
                }
            default:
                if (v.O is Table t && t.Metatable?.GetStr("__name").ToString() == "json.null") return DBNull.Value;
                throw c.ArgError(2, $"{what} is a {LuaState.TypeNameOf(v)}; only nil, booleans, numbers and strings can be sent");
        }
    }

    private static Value FromDb(object? o) => o switch
    {
        null or DBNull => Value.Nil,
        bool b => Value.FromBool(b),
        long l => Value.FromInt(l),
        int i => Value.FromInt(i),
        short s => Value.FromInt(s),
        byte b => Value.FromInt(b),
        sbyte b => Value.FromInt(b),
        ushort u => Value.FromInt(u),
        uint u => Value.FromInt(u),
        double d => Value.FromFloat(d),
        float f => Value.FromFloat(f),
        decimal m => Value.FromFloat((double)m),
        string s => Value.FromText(s),
        byte[] bytes => Value.FromString(LuaText.FromBytes(bytes)),
        DateTime dt => Value.FromText(dt.ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
        DateTimeOffset dto => Value.FromText(dto.ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
        _ => Value.FromText(Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) ?? ""),
    };

    // ------------------------------------------------------------------ plumbing

    // Runs the work on the thread pool and waits for it on behalf of the calling function:
    // the task is suspended (other tasks run), or outside a task the scheduler runs until done.
    private static int Wait(ref CallContext c, string target, Func<CancellationToken, Task<Func<LuaState, Value>>> work)
    {
        LuaState L = c.L;
        var fut = new LuaFuture(L);
        Scheduler s = L.Scheduler;
        var operation = new OperationLifetime(s.Lifetime);
        fut.OnComplete(_ => operation.Settle());
        int epoch = s.BeginExternal();
        s.LiveTasks++;
        Task.Run(async () =>
        {
            Func<LuaState, Value>? build = null;
            (string Kind, string Message)? error = null;
            try
            {
                build = await work(operation.Token).ConfigureAwait(false);
            }
            catch (DbFailure f) { error = (f.Kind, f.Message); }
            catch (OperationCanceledException) { error = ("cancelled", "cancelled"); }
            catch (DbException e)
            {
                bool constraint = e.SqlState?.StartsWith("23", StringComparison.Ordinal) == true
                    || e.Message.Contains("constraint", StringComparison.OrdinalIgnoreCase);
                error = (constraint ? "constraint" : "failed", e.Message);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException or IOException)
            {
                error = ("invalid", e.Message);
            }
            catch (Exception e) { error = ("failed", e.ToString()); } // provider/aggregate diagnostics must settle the boundary
            finally { operation.ProducerStopped(); }
            s.PostExternal(() =>
            {
                if (!s.EndExternal(epoch)) return;
                s.LiveTasks--;
                if (error is not { } err) { fut.Complete(build!(L)); return; }
                var t = new Table(0, 3);
                t.SetStr("kind", err.Kind);
                t.SetStr("message", LuaText.Encode(err.Message));
                t.SetStr("target", LuaText.Encode(target));
                t.Metatable = L.MetaFor("DbError");
                fut.Fail(new FutureError(err.Kind == "cancelled" ? FutureErrorKind.Cancelled : FutureErrorKind.Failed,
                    LuaText.Encode($"{target}: {err.Message}"), Value.FromTable(t)));
            });
        });
        return FutureLib.WaitFor(ref c, fut, throws: false, timeout: -1);
    }
}
