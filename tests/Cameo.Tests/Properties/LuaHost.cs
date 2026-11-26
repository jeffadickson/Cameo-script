using Cameo.Runtime.Vm;

namespace Cameo.Tests.Properties;

/// <summary>
/// A VM instance for property tests: calls library functions directly and runs snippets,
/// capturing output. One instance per test class (a LuaState is single-threaded).
/// </summary>
internal sealed class LuaHost
{
    public readonly ScriptEngine Engine;
    public readonly StringWriter Out = new() { NewLine = "\n" };

    public LuaHost(bool typeCheck = false)
    {
        Engine = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = typeCheck });
        Engine.State.Stdout = Out;
    }

    public LuaState L => Engine.State;

    /// <summary>Looks up a library function such as "string.rep" or "tostring".</summary>
    public Value Fn(string path)
    {
        Value v = default;
        Table t = L.Globals;
        foreach (string part in path.Split('.'))
        {
            v = t.GetStr(part);
            if (v.IsTable) t = v.AsTable;
        }
        if (!v.IsFunction) throw new InvalidOperationException("no such function: " + path);
        return v;
    }

    public Value[] Call(string path, params Value[] args) => L.Call(Fn(path), args);

    /// <summary>First result of a library call (nil if none).</summary>
    public Value Call1(string path, params Value[] args)
    {
        Value[] r = L.Call(Fn(path), args);
        return r.Length > 0 ? r[0] : Value.Nil;
    }

    /// <summary>Runs a chunk given in the VM's byte-string form or as ASCII text and returns its results.</summary>
    public Value[] Run(string source, string chunkName = "prop") => Engine.RunString(source, chunkName);

    /// <summary>Runs a chunk and returns what it printed.</summary>
    public string Output(string source)
    {
        Out.GetStringBuilder().Clear();
        Engine.RunString(source, "prop");
        return Out.ToString();
    }

    /// <summary>Runs code expected to raise an error; returns the error message, or null if none was raised.</summary>
    public string? ErrorOf(Func<object?> action)
    {
        try
        {
            action();
            return null;
        }
        catch (LuaException e)
        {
            return e.Message;
        }
    }
}
