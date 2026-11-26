namespace Cameo.Runtime;

public sealed partial class ScriptEngine : IDisposable
{
    /// <summary>Closes the VM state: runs the __gc finalizers of all objects still marked for
    /// finalization (like lua_close). Call it when the program is done.</summary>
    public void Close() => State.Close();

    public void Dispose() => Close();
}
