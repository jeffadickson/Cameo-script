using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>Immutable tagged results used by the Cameo prelude.</summary>
internal static class ResultLib
{
    // The tag is independent of payload nil/false, and scripts cannot forge or mutate it.
    internal sealed class TaggedResult(Table meta, bool success, Value payload) : IUserdata
    {
        public string TypeName => "Result";
        public Table? Metatable { get => meta; set { } }
        public bool Success { get; } = success;
        public Value Payload { get; } = payload;
    }

    internal static void Open(LuaState state)
    {
        var methods = new Table();
        var meta = new Table();
        meta.SetStr("__index", Value.FromTable(methods));
        meta.SetStr("__metatable", Value.FromText("Result"));
        state.ResultMeta = meta;
        state.Register(methods, "fromPair", (ref CallContext c) =>
        {
            // The explicit bridge follows the existing value/error ABI: only
            // nil means success. A false companion is a genuine error value.
            Value issue = c[1];
            return c.Return(Value.FromUserdata(new TaggedResult(meta, issue.IsNil, issue.IsNil ? c[0] : issue)));
        });
        state.Register(methods, "ok", (ref CallContext c) => Create(ref c, meta, true));
        state.Register(methods, "err", (ref CallContext c) => Create(ref c, meta, false));
        state.RegisterInstance<TaggedResult>(methods, "isOk", static (ref CallContext c) => c.Return(Check(ref c).Success));
        state.RegisterInstance<TaggedResult>(methods, "isErr", static (ref CallContext c) => c.Return(!Check(ref c).Success));
        state.RegisterInstance<TaggedResult>(methods, "value", static (ref CallContext c) =>
        {
            var result = Check(ref c);
            return result.Success ? c.Return(result.Payload) : throw c.ArgError(0, "expected an ok Result");
        });
        state.RegisterInstance<TaggedResult>(methods, "issue", static (ref CallContext c) =>
        {
            var result = Check(ref c);
            return result.Success ? throw c.ArgError(0, "expected an err Result") : c.Return(result.Payload);
        });
        state.RegisterInstance<TaggedResult>(methods, "unwrapOr", static (ref CallContext c) =>
        {
            var result = Check(ref c);
            return c.Return(result.Success ? result.Payload : c[1]);
        });
        state.RegisterInstance<TaggedResult>(methods, "map", (ref CallContext c) =>
        {
            var result = Check(ref c);
            Value fn = c.CheckFunction(1);
            if (!result.Success) return c.Return(c[0]);
            return c.Return(Value.FromUserdata(new TaggedResult(meta, true, c.L.Call1(fn, result.Payload))));
        });
        state.RegisterInstance<TaggedResult>(methods, "mapErr", (ref CallContext c) =>
        {
            var result = Check(ref c);
            Value fn = c.CheckFunction(1);
            if (result.Success) return c.Return(c[0]);
            return c.Return(Value.FromUserdata(new TaggedResult(meta, false, c.L.Call1(fn, result.Payload))));
        });
        // Keep ordinary module edits away from instance dispatch. Lowered try
        // trusts the instance tag check before retyping an error-only result.
        var module = new Table();
        foreach (var entry in methods.Pairs()) module.Set(entry.Key, entry.Value);
        state.Globals.SetStr("Result", Value.FromTable(module));
        state.Register(state.Globals, "ok", (ref CallContext c) => Create(ref c, meta, true));
        state.Register(state.Globals, "err", (ref CallContext c) => Create(ref c, meta, false));
    }

    private static int Create(ref CallContext c, Table meta, bool success)
    {
        // Use the first slot, with nil padding for an empty pack. This matches the
        // checker's scalar interpretation of a call used as the payload.
        return c.Return(Value.FromUserdata(new TaggedResult(meta, success, c[0])));
    }

    private static TaggedResult Check(ref CallContext c) =>
        c[0].O as TaggedResult ?? throw c.TypeError(0, "Result");
}
