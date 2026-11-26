using Cameo;
using CameoHost;
using Cameo.Runtime;
using Cameo.Runtime.Vm;

// The outer host remains asynchronous. A dedicated owner thread preserves the VM's
// affinity; a GUI/TUI could instead marshal every call and pump to its own dispatcher.
if (args.Length != 1) { Console.Error.WriteLine("Usage: CameoHost <05-embedded-events.cameo>"); return 2; }
Task<string> completion = OwnerThread.Run(() => new CameoEngine(), engine =>
{
    engine.State.SetGlobal("fetch_data", Value.FromFunction((ref CallContext context) =>
        context.Return(Value.FromUserdata(context.L.FutureFromTask(Fetch(context.L.Scheduler.Lifetime)))), "fetch_data"));
    engine.RunFile(args[0]);
    LuaFuture job = engine.Runtime.Start(engine.State.GetGlobal("work"));
    while (!job.IsDone)
    {
        RunResult pump = engine.Runtime.TryPump(TimeSpan.Zero, out _);
        if (pump.Value is not RunCompleted) throw new InvalidOperationException(pump.Value?.ToString() ?? "Missing pump outcome");
        // This thread owns the VM. The task pool is never blocked waiting for VM work.
        if (!job.IsDone) engine.Runtime.WaitForWorkAsync().GetAwaiter().GetResult();
    }
    if (job.Error is { } error) throw new InvalidOperationException(error.Message);
    return engine.State.Call(engine.State.GetGlobal("formatResult"), job.Value)[0].ToText();
});
Console.WriteLine("host: event loop remains available");
Console.WriteLine(await completion);
return 0;

static async Task<Value> Fetch(CancellationToken cancellation)
{
    // Transfer only an immutable scalar. Construct VM tables/userdata on the owner.
    await Task.Delay(25, cancellation).ConfigureAwait(false);
    return Value.FromInt(21);
}
