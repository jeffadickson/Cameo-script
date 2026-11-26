# The IL target

`jit.compile(f)` compiles one Teal function to .NET IL; the .NET runtime then turns that into
machine code. The result is a function that is called like the original and does the same
thing, faster where the time goes into arithmetic and loops.

It is **opt-in and per function**: nothing is compiled unless the program asks, the
interpreter stays the way everything else runs, and a function the compiler cannot translate
is refused with a reason rather than compiled approximately. It is an add-on (`Cameo.Jit`,
`JitLibrary.Enable(engine)`; on in `tlvm`).

```lua
local function fir(x: FloatArray, y: FloatArray, taps: FloatArray): number
   local peak = 0.0
   for i = #taps, #x do
      local acc = 0.0
      for k = 1, #taps do acc = acc + taps[k] * x[i - k + 1] end
      y[i] = acc
      if acc > peak then peak = acc end
   end
   return peak
end

local fast, err = jit.compile(fir)
if not fast then
   print(err.kind, err.message)        -- unsupported   function 'fir' was not compiled: ...
   fast = fir                          -- the interpreted one still works
end
print(fast(x, y, taps))
```

| | |
| --- | --- |
| `jit.compile(f)` | The compiled function, or `nil` and a `JitError` (`kind`: `unsupported`, `unavailable`, `not_a_function`; `message`) |
| `jit.supports(f)` | `true`, or `false` and the reason, without compiling |
| `jit.available` | Whether this runtime can generate code at all |

To make a recursive or global function use its compiled form everywhere, assign it back:
`fib = assert(jit.compile(fib))`.

## What it is worth

`bench/jit.tl`, on the machine this was developed on (4 cores, other work running; treat the
ratios as rough):

| Kernel | Interpreted | Compiled | |
| --- | --- | --- | --- |
| `sumsq`: float arithmetic in a `for` loop, 20 million iterations | 480–640 ms | 92–98 ms | 5–6.5× |
| `collatz`: integer `%`, `//`, `while`, `if`, 300,000 starts | 1,110–1,450 ms | 380–530 ms | 2.3–3.2× |
| `fir`: a 32-tap filter over a 400,000-sample `FloatArray` | 390–400 ms | 147–156 ms | 2.5–2.7× |
| `sieve`: reads and writes of a 5-million-entry table | 670–790 ms | 550–700 ms | 1.0–1.4× |

So: worth it for numeric kernels (signal generation, filters, checksums), not for code whose
time is in tables, strings or calls — those spend their time in the same runtime routines
either way.

## How it works

`tlvm dump` shows the bytecode a function compiles to. The IL compiler
(`src/Cameo.Jit/IlCompiler.cs`) walks that bytecode once:

* checker-resolved numeric parameter hints pass into the bytecode prototype. Exact-kind
  entry guards select a specialized body; any mismatch selects generic IL before executing
  the function. A Teal `number` passed an integer stays an integer, including above 2^53;
* conservative control-flow analysis keeps registers in plain `long` or `double` locals
  only when every observed read/write has that exact kind. Nil, dynamic calls, mixed-kind
  joins and register reuse prevent unsafe specialization. Analysis above one million
  instruction/register pairs is skipped. Add/subtract/multiply/divide, unary minus and
  same-kind comparisons use primitive IL; integer overflow wraps and NaN stays unordered;
* each **instruction** becomes a call to a small C# method in `JitOps` that has the
  interpreter's fast path (two integers, two floats, a plain table, a `FloatArray`) and
  otherwise calls the same `LuaState` routine a library function would — so metamethods,
  string coercion and error messages come from the one implementation. The .NET JIT inlines
  the fast paths;
* **jumps** become IL branches, so there is no instruction decoding or dispatch left;
* a **numeric `for`** keeps its counter in a plain `long` or `double` local;
* backward jumps and calls **poll for an abort**, so the time limit and `cancel` stop
  compiled loops too;
* calls suspend the generated body. A dispatcher calls the actual current function value,
  keeps compiled continuations on the managed heap, and resumes the caller with all results.
  Register and loop locals are saved at calls, so primitive locals may be boxed there;
* the body sits in one `try`: an error gets the `file:line:` of the statement that was
  running.

## What is different from interpreted code

* **Compiled-to-compiled calls use explicit frames.** Deep self and mutual recursion do
  not consume a native frame per call; compiled tail calls replace the active frame.
  `VmLimits.MaxStackSlots` bounds the logical storage of registers, saved loop state and
  auxiliary locals. Exceeding it raises `stack overflow (compiled frame budget)` and the
  engine remains usable. This counts storage slots, not exact managed bytes: use the memory
  watchdog for a heap limit. Calls through interpreted or host functions still use the
  native boundary and its depth limit. A native callback can re-enter compiled code, but
  repeated native transitions still consume native stack.
* **A compiled function cannot be yielded across.** `coroutine.yield` (or `await` on a
  pending future) inside something a compiled function called is the error "attempt to yield
  across a native call". Compiled functions can be *called from* coroutines and tasks freely.
* **Error messages do not name the operand.** The interpreter says
  `attempt to perform arithmetic on a nil value (local 'x')`; compiled code says
  `attempt to perform arithmetic on a nil value`, with the same `file:line:`.
* **The debugger does not see inside** a compiled function: no breakpoints or stepping in
  it, and it appears as one native frame in tracebacks.
* The compiled function is a new value: it is not `==` to the original, and
  `debug.getinfo` reports it as native.

## What is translated

All VM bytecode instructions are now translated, including:

* varargs (`...`), closures, Cameo arrows (`=>`), and captured upvalue boxes;
* generic `for` loops (`for k, v in ipairs(t)` / `pairs(t)`);
* to-be-closed variables (`<close>` and Cameo `using`), including reverse-order unwinding on errors;
* all operators, `if`/`while`/`repeat`/numeric `for`/`goto`/`break`/`continue`, tables and fields,
  method calls, globals and upvalues, calls with any number of arguments and results, `math` built-ins.

`BytecodeValidator.Validate` verifies prototype structure before emission; malformed prototypes
are rejected with `JitErrorKind.Unsupported`. Compiled loops and calls poll both host aborts and
deterministic `MaxSteps` budgets.

Automatic hot-function tiering is available via `jit.auto(threshold)`, `CameoRuntimeOptions.JitTierThreshold`,
or the `--jit` flag on `tlvm` and `cameo`. Both `tlvm` and non-sandboxed `CameoEngine` instances
enable `Cameo.Jit`.

## What has and has not been tested

`tests/Cameo.Tests/JitTests.cs` verifies interpreter-vs-compiled equivalence across arithmetic,
loops, varargs, closures, generic `for`, `<close>` error unwinding, auto-tiering, Cameo functions,
`MaxSteps` aborts, and `BytecodeValidator` rejection, alongside 600 CsCheck arithmetic programs,
`JitSpecializationTests.cs`, and `JitAnalysisTests.cs`.

Still not done or not tried:

* Under an ahead-of-time compiled host (`libteal`) there is no code generation: the add-on
  is not part of that library, and `jit.compile` reporting `unavailable` there has not been
  exercised.
* Specialization is deliberately conservative: dynamic numeric results, mixed-kind joins,
  many reused temporaries, table access and arithmetic with possible metamethods stay on
  the `Value` path. Direct compiled calls still allocate continuation snapshots and
  argument/result arrays; call-heavy code may pay more overhead than a leaf kernel.
* No yielding (`coroutine.yield` / `Future.await`) across compiled frames, and no compiling a
  whole program to an assembly on disk.
* The benchmark numbers above were taken on a busy machine and not repeated on a quiet one.

Stage 2 verification and measurements are recorded in
[`evidence/2026-11-16-jit-stage2.md`](evidence/2026-11-16-jit-stage2.md).
