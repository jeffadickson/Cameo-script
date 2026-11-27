# Embedding in .NET, and how the VM works

TealVM is a library (`src/Cameo.Runtime`, no dependencies outside the base class library) with a
small embedding API in `ScriptEngine`. This page covers the API, the permissions and library
levels a host controls, and the design of the virtual machine.

* [The embedding API](#the-embedding-api)
* [Strings at the boundary](#strings-at-the-boundary)
* [Permissions](#permissions)
* [Library levels](#library-levels)
* [Futures from the host](#futures-from-the-host)
* [Databases from the host](#databases-from-the-host)
* [Stopping a run](#stopping-a-run)
* [The pipeline](#the-pipeline)
* [The VM](#the-vm)

## The embedding API

```csharp
using Cameo.Runtime;
using Cameo.Runtime.Vm;

var engine = new ScriptEngine();

// Expose a host function
engine.State.SetGlobal("add", Value.FromFunction((ref CallContext c) =>
    c.Return(Value.FromInt(c.CheckInt(0) + c.CheckInt(1))), "add"));

try
{
    Value[] results = engine.RunString("""
        local record Point
           x: number
           y: number
        end
        local p: Point = { x = 1, y = 2 }
        return p.x + p.y
        """);
    Console.WriteLine(results[0]);          // 3
}
catch (TealCompileException e)              // syntax and type errors, with positions
{
    foreach (var d in e.Diagnostics) Console.WriteLine(d);
}
catch (LuaException e)                      // run-time errors (carry the error value + traceback)
{
    Console.WriteLine(e.Message);
}
```

Host functions receive their arguments through `CallContext` (`c[0]`, `c.CheckInt(i)`,
`c.CheckString(i)`, …) and return with `c.Return(...)`. `engine.State.Call(fn, args)` calls
back into Teal code. Output of `print`/`io.write` goes to `engine.State.Stdout`.

### Typed host interop (`Register`, `Expose`, `DefineValue`)

For strongly typed .NET hosts (and `CameoEngine`), `Register`, `Expose`, and `DefineValue`
marshal .NET values automatically **and** register the generated signatures with the static
type checker so scripts need no manual `global` declarations:

```csharp
using Cameo.Runtime.Interop;

public sealed class Inventory
{
    private readonly Dictionary<string, int> _stock = new() { ["apple"] = 3 };
    [Expose] public string Owner { get; set; } = "shop";
    [Expose] public int Count(string item) => _stock.GetValueOrDefault(item);
    public void Wipe() => _stock.Clear(); // hidden because [Expose] opts in
}

engine.Register("add", (int a, int b) => a + b, threadSafe: true);
engine.Register("fetch", async (string k) => await LookupAsync(k), asFuture: true);
engine.Expose("inventory", new Inventory());
engine.DefineValue("weights", new[] { 1.5, 2.5, 3.0 });
```

- `Register(name, delegate, asFuture: false, threadSafe: false)` binds .NET primitives,
  strings, `FloatArray`, `List`/`Dict`, arrays/dictionaries, and C# default parameters;
  `asFuture: true` bridges `Task`/`Task<T>` to `Future<T>`, and `threadSafe: true`
  propagates the function to `parallel.map` / `Future.parallel` workers.
- `Expose(name, target)` exposes public properties as live fields and public methods as
  bound functions (camelCase by default; override with `[Expose("...")]` or exclude with
  `[Hide]`; overloads, generics, and `ref`/`Span` parameters throw `ArgumentException`).
- `DefineValue(name, value)` converts a .NET value into a typed global (including
  `SharpUnits.Quantity<double>`, preserving its compile-time physical dimension); returned
  sequences poll the VM step/abort budget per element so unbounded `IEnumerable<T>` stops
  cleanly.
- `[Unit("Ohm")]` (`Cameo.Runtime.Interop.UnitAttribute`) annotates host `double`/`float`/`decimal`
  parameters (or sequences like `double[]`, or direct `SharpUnits.Quantity<double>` parameters and
  return values) with a physical unit expression. On numeric parameters (`AllowNumber = true` by
  default), Cameo scripts may pass either a plain `number` (interpreted in the target unit) or a
  dimensionally compatible `SharpUnits.Quantity` (automatically converted into the target unit at
  runtime), while incompatible dimensions (such as Volts passed to an `"Ohm"` parameter) are
  rejected at compile time by the Cameo type checker. Set `AllowNumber = false` to require a
  `SharpUnits.Quantity` exclusively.
- `DeclareGlobal(tealDeclaration)` adds custom static declarations directly to the checker.

For low-level `State.SetGlobal` bindings that bypass `Register`/`Expose`, declare globals
in the script (`global add: function(integer, integer): integer`) or in a `.d.tl` file.

`engine.TryRunFile` / `TryRunString` return a `RunResult` union — `RunCompleted`,
`CompileFailed`, `RuntimeFailed`, `RunAborted(reason)`, `RunExited(code)` — so a host
handles every outcome without catching exceptions. `os.exit` raises `LuaExitException` to
the host instead of terminating the process (the CLI turns it into the exit code).

`engine.TryCall(function, args...)` calls a function of a loaded program (for example
`engine.State.GetGlobal("acquire")`) under the same limits and returns the same union; it
waits for the tasks the call started. `examples/host/csharp` has five worked hosts, and
[native.md](native.md) covers hosting from C and Rust.

`ScriptEngine.Close()` / `Dispose()` closes the state and runs the finalizers of objects still
alive (see [differences.md](differences.md#garbage-collection)).

`CameoRuntimeOptions` (passed to the engine) holds: `TypeCheck` (default true), `ModulePaths`
(extra directories for `require`), `Timeout`, `MaxMemoryBytes`, `Libraries`, `AllowNetwork`,
`AllowProcesses`, `AllowDatabase`.

## Strings at the boundary

VM strings are byte strings holding UTF-8 (see [language.md](language.md#strings)).
`Value.FromText(text)` / `value.ToText()` convert between .NET text and VM strings (UTF-8),
and `Value.FromUtf16(state, text)` hands a .NET string over as `utf16` without copying. In
native functions use `c.CheckText(i)` and `c.ReturnText(s)`. `Value.FromString` and the
implicit conversion from `string` expect a string that is already in the VM's internal form
(one byte per `char`); for ASCII the two are the same. Internally a byte string is held in a
.NET string with one byte per char, so it uses two bytes of memory per byte of data.

## Permissions

Three kinds of access are off by default. Without them the functions fail with a typed
error of kind `permission_denied` (they do not raise).

| Access | Host | CLI | Functions |
| --- | --- | --- | --- |
| Network | `CameoRuntimeOptions.AllowNetwork` | `--allow-net` | `aio.httpGet`, `httpRequest`, `getJson`, `postJson`, `download`, `httpStream`, `httpSession` |
| Starting programs | `CameoRuntimeOptions.AllowProcesses` | `--allow-run` | `aio.exec`, `aio.spawn` |
| Databases | `CameoRuntimeOptions.AllowDatabase` | `--allow-db` | `db.open` |

File access is not restricted (as with `io.open`). A program can run code with fewer rights
than it has itself with [`sandbox.run`](library.md#child-sandboxes): a sandbox can narrow
rights, never widen them.

`AllowProcesses` is a coarse permission. Managed hosts can additionally configure an immutable [process allowlist](embedding-process-policy.md) for exact executable/argument/PTY requests. Neither
`Standard` nor `Extended` confines files: both include `io`. Restricting hostile scripts
requires controlling every exposed file/loader/host API or an operating-system boundary.

## Library levels

A host chooses how much of the library a program gets, with `CameoRuntimeOptions.Libraries`:

| Level | Contents |
| --- | --- |
| `Standard` | The Lua 5.4 standard library only |
| `Extended` | plus what only computes: string/table/utf8 helpers, `json`, `csv`, `regex`, `base64`/`hex`/`crypto`, `Query`, `FloatArray`, `time` |
| `All` (default) | plus what reaches outside the VM: futures and workers, `aio`, `fs`, `db`, `cli`, `sandbox` |

The type checker knows every library at every level; using one that is not there fails at
run time. Network, process and database access need their permissions at any level.

## Futures from the host

A host adds asynchronous operations of its own: `state.FutureFromTask(Task<Value>)` wraps a
.NET task, and `future.CompleteFromAnyThread(value)` / `FailFromAnyThread(error)` settle a
future from any thread. `future.Result` is a C# union, `Completed | FutureError` (null while
pending). A `LuaState` is single-threaded: never share one across threads.

## Databases from the host

`db` runs SQL through whatever ADO.NET provider the host plugs in; the VM contains no
driver. The host sets `state.DatabaseFactory = name => connection` to decide what a name
means, so scripts never see connection strings. `TealVM.Sqlite` (`src/Cameo.Runtime.Sqlite`, the
only part with an external dependency) is a ready-made provider:
`SqliteDatabases.Enable(engine)`. The CLI includes it.

## Stopping a run

`CameoRuntimeOptions.Timeout`, `CameoRuntimeOptions.MaxMemoryBytes` and `engine.Cancel()` stop a running
program from outside; the engine can run code again afterwards. Details, and what cannot be
interrupted, are in [limits.md](limits.md#the-watchdog). `VmLimits` holds the adjustable
bounds (`MaxStackSlots`, `MaxNativeDepth`, `MaxParallelWorkers`, `MaxArrayLength`,
`MaxStringLength`). Hosts can call `LuaState.GcSafePoint()` to offer more points at which
weak tables are swept and finalizers run.

## The pipeline

Teal source is **compiled directly to bytecode for a virtual machine written in C#**. There
is no Lua source in the pipeline at any point:

```
 .tl source ──► Lexer ──► Parser ──► Type checker ──► Resolver ──► Bytecode compiler ──► VM
                (Syntax/)            (Types/)        (Compiler/)                      (Runtime/)
```

The reference `tl` compiler type-checks Teal and then prints Lua, which a separate Lua
interpreter runs. Here the typed syntax tree goes straight to register-based bytecode, and
Teal-only constructs (`record`, `enum`, `interface`, `is`, `as`, type declarations,
`global`) are lowered in the code generator; the table of what each becomes is in
[language.md](language.md#how-teal-constructs-are-compiled).

## The VM

* **Register-based bytecode** (`Runtime/Function.cs` lists the instruction set), one
  `Instruction` = opcode + three operands; operands marked RK address either a register or
  a constant. `tlvm dump` prints it with notes ([cli.md](cli.md#tlvm-dump)).
* **Values** are a three-word struct (payload, object reference, kind). Integers and floats
  are distinct, with Lua 5.4 semantics: 64-bit wrap-around integers, floor division and
  modulo, exact integer/float comparison, integer `for` loops that cannot overflow.
* **Tables** have an array part and an insertion-ordered hash part; removing entries while
  traversing with `next`/`pairs` is safe.
* **Closures** capture variables through heap boxes created where a captured local is
  declared, so there is no open-upvalue list to maintain; each loop iteration gets a fresh
  variable, as in Lua 5.4.
* **Calls between Teal functions do not use the native stack**: frames live in a per-thread
  array, so deep recursion is limited by memory (a `stack overflow` error at 1,000,000
  stack slots), and proper tail calls run in constant space.
* **Coroutines** each have their own value stack and frame array; `coroutine.yield` suspends
  the interpreter loop and `resume` re-enters it. `pcall` of a Teal function is handled
  inside the loop, so yielding across `pcall` works.
* **Errors** are .NET exceptions carrying a Lua value. Messages follow PUC Lua's wording,
  including the `(local 'x')` / `(field 'y')` / `(global 'f')` hints, and carry a
  traceback.
* **Metatables**: `__index`, `__newindex`, `__call`, arithmetic and bitwise operators,
  `__concat`, `__len`, `__unm`, `__eq`, `__lt`, `__le`, `__tostring`, `__name`, `__pairs`,
  `__close`, `__gc`, `__mode`, `__metatable`.

Managed GUI/TUI hosts: [event-loop wake and lifetime contracts](embedding-events.md).
