# Cameo: readable tools on a shared engine

Cameo is an experimental scripting language alongside Teal. Both front ends use the
same checked AST, bytecode compiler, interpreter, libraries and optional IL target.
Teal keeps its `.tl` syntax and `tlvm` entry point. Cameo uses `.cameo`, `cameo`, and
the managed `Cameo.CameoEngine` API in assembly `Cameo.Language`.

The name is provisional. The existing Cameo Systems Modeler product means this is
not a claim of public name clearance. No repository or existing package is renamed.

## First working slice

The [example gallery](proposed-gallery.md) distinguishes the brace-style slice from
remaining proposals. See the [authoring guide](authoring.md) for declaration scope,
newline rules, error handling and method extraction.

```text
fn square(value: integer) -> integer {
    return value * value
}

let job = Future.parallel(() => square(21))
print(job.get())
```

Named `fn` functions are local and recursive, using the existing function checker.
Initialized `var` bindings permit reassignment; `let` prevents it. Both `let` and `var`
support table destructuring (`let { a, b = alias } = t`) and array destructuring
(`var [x, _, z] = xs`). Control flow (`if`/`else if`/`else`, `while`, `for`, `do`) and
declarations (`fn`, `record`, `interface`, `enum`) use pure brace syntax (`{ ... }`), while
Teal (`.tl`) retains full Lua/Teal syntax. Return annotations use adjacent `->`, and function
types accept both `fn(T) -> R` and `function(T): R`.

Cameo uses C/Rust/SystemVerilog-coherent comments and operators:
- Comments: `//` line comments and nestable `/* ... */` block comments (`//#pragma` for pragmas).
- Logical & comparison: `!`, `&&`, `||`, `!=`, `==`, `<`, `<=`, `>`, `>=`, `=~`, `!~`.
- Bitwise & math: unary `~` (bitwise NOT), `^` / `^=` (bitwise XOR), `&` / `&=`, `|` / `|=`,
  `<<` / `>>`, `**` / `**=` (exponentiation, right-associative, tighter than unary `-`),
  `~/` / `~/=` (floor/integer division, backing `__idiv`), and `/` (floating-point division).
  Bitwise operators bind tighter than comparisons (`flags & MASK == 0`).
- Multi-line raw strings use `"""..."""` and interpolated `$"""..."""`, freeing `[[1, 2], [3, 4]]`
  for nested list literals without extra spaces.
- Type aliases can be declared directly with `type Name = ...` and `export type Name = ...`.

Cameo adds:
- **Interfaces and inline record methods**: braced `interface Name [extends|is Base] { ... }`
  with `fn method(...) -> T` signatures and required data fields; braced `record Name
  [implements I1, I2] { ... }` with inline `fn` instance methods (implicit `self`) and
  `static fn` functions. Explicit `implements` verifies at compile time that every required
  field and method matches; records without `implements` and plain table literals also
  satisfy interfaces structurally (with contextual parameter inference on arrows). Records
  with inline methods construct via typed table literals (`let b: Bot = { ... }`) or
  `Bot { ... }`, support runtime `x is Bot` / `instanceof(x, Bot)`, and dispatch methods
  via unified `.` calls (`g.greet("world")`) with zero `PrepareDotCall` overhead when
  checked (keeping `:` working for compatibility). See `examples/15-interfaces.cameo`.
- Perl-style regex literals `/pattern/flags` with match operators `=~` and `!~`.
- Null-coalescing `a ?? b` (right-associative, falls back only on `nil`, preserving `false`)
  and optional chaining `x?.field`, `x?.[k]`, `x?.method(args)`, and `x?.(args)`.
- Block-body arrow functions `(x) => { ... }` and `(x) -> T => { ... }` (while keeping
  single-expression table-literal arrows `(x) => { a = x }`).
- Expression-position `try`/`catch` (`return try expr`, `f(try expr)`) and multi-statement
  `catch` blocks alongside `catch kind`, `catch kind(e)`, `catch (e)`, and `catch _`.

Use the [authoring guide](authoring.md) for canonical complete-script style, callback
repairs and error handling. Prefer named business functions and one pipeline stage per
line. Shared `unwrap(value, issue)` raises the original companion error when non-nil,
preserving false/nil successes; `Future.get()` supplies the future equivalent.
Use these where failure should abort, and explicit railway handling where recovery occurs.
Initialized `let` bindings and direct future calls such as `job.get()` form part of
Cameo's source surface. `let` prevents reassignment, including in no-check compilation;
objects it refers to can still change. Use `var` for initialized mutable bindings.

```cameo
let job = Future.parallel(() => 21 * 2)
let answer = job.get()
print(answer) // 42
```

Direct dot calls automatically supply their receiver across built-in instance types:
`Future<T>`, `Result<T, E>`, `Query<T>`, `string`, `FILE`, `FloatArray`, `utf16`, `Regex`,
`Stream`, `Sink`, `Process`, `Watcher`, `HttpSession`, and `Connection`.
Static/module calls remain ordinary (`Future.parallel(...)`, `Query.from(...)`,
`Result.fromPair(...)`, `string.upper(...)`, `json.decode(...)`), and in type-checked Cameo
both module calls and known built-in instance calls compile directly with no `PrepareDotCall`
instruction. Existing colon calls remain valid. Extracted/parenthesized/bracket methods remain
unbound (`let getter = job.get` is called as `getter(job)`).

Cameo supports the left-associative pipeline operator `|>`:

```cameo
let doubled = Query.from({1, 2, 3})
    .where((n) => n > 1)
    .select((n) => n * 2)
    .toList()
print(table.concat(doubled, ", ")) // 4, 6

let banner = "  cameo  " |> string.trim |> string.upper
```

An expression function has parenthesized parameters, optional `-> ReturnType`, an adjacent
`=>`, and either one expression or a statement block `{ ... }`. Parameters use explicit
concrete annotations or an expected function type, as Query supplies above.
`(n: integer) => n * 2` works without context. `() => 42`, captures, nested arrows and
immediate calls also work. The return type is inferred when omitted, including literal
precision needed by enum callbacks. Named `fn` declarations use explicit return rules.

The first version deliberately rejects:

| Code | Boundary |
| --- | --- |
| CAMEO001 | A parameter has no concrete annotation/context, or is `any`. |
| CAMEO002 | The body result is unknown/`any`, zero, multiple, or variadic results. |
| CAMEO003 | A file combines arrows with macros, macro expressions or record `where` reparsing. |
| CAMEO004 | The body has an outer result cast. Use a full function. |
| CAMEO005 | Duplicate parameters or varargs. |

Parentheses do not hide a multi-result body from this check. The check concerns the
body's root return shape; it does not prohibit every multi-result call nested inside
another expression. This is not correlated success/error-pack analysis or a soundness
proof.

Cameo modules resolve `<name>.cameo` or `<name>/init.cameo`. There is no implicit
`.tl` fallback. Existing `.d.tl` declaration plus `.lua` implementation modules retain
the Teal/Lua parser. `load` and parallel worker engines retain their owning language;
dynamic `load` retains the existing unchecked-load behavior. Paths, byte strings,
permissions and VM thread affinity follow the shared engine's contracts.

## Run and embed

From the repository root with its pinned .NET SDK:

```sh
dotnet build Cameo.slnx -c Release
dotnet src/Cameo.Cli/bin/Release/net11.0/cameo.dll check --json examples/02-csv-report.cameo
dotnet src/Cameo.Cli/bin/Release/net11.0/cameo.dll run examples/02-csv-report.cameo examples/fixtures
dotnet src/Cameo.Cli/bin/Release/net11.0/cameo.dll dump examples/01-file-organizer.cameo
dotnet src/Cameo.Cli/bin/Release/net11.0/cameo.dll repl --sandboxed
dotnet src/Cameo.Cli/bin/Release/net11.0/cameo.dll commands
dotnet examples/host/bin/Release/net11.0/CameoHost.dll examples/05-embedded-events.cameo
```

The executable also accepts `cameo file.cameo [args...]`, Perl-style `-e`/`-n`/`-p`/`-a`/
`-F`/`-i[.bak]`/`--begin`/`--end` one-liners, `cameo dump [--no-check] <file.cameo>`, and
`cameo repl [--sandboxed]`. `run` and `eval` accept `--allow-net`, `--allow-run`,
`--allow-db`, `--sandboxed`, `--timeout <seconds>`, `--max-memory <MiB>`,
`--max-steps <N>` (deterministic VM step budget), and `--jit` (automatic IL tiering).
Non-sandboxed `CameoEngine` enables the shared `jit` library (`jit.compile`, `jit.auto`,
`jit.status`). The inherited memory watchdog samples `GC.GetTotalMemory(false)` process-wide
every 10ms and aborts the script when observed above the threshold. This includes other
managed host allocations, excludes native memory, permits sampling overshoot, and is not an
isolated per-script allocation budget. Short scripts can finish before a sample. No
threshold is imposed by default. This opt-in product capability is unrelated to
development-job memory policy. Network, process and database permissions default off.
`--sandboxed` runs under `CameoLibraries.Sandboxed` without `io`/`fs`/`aio`/`db`/`cli`/
`debug`; `--hermetic` runs under `CameoLibraries.Hermetic` (and `CameoEngine.CreateHermetic`),
additionally removing wall-clock (`os`/`time`), randomness (`math.random`/`math.randomseed`,
`crypto.randomBytes`/`crypto.randomInt`/`crypto.uuid`), `dofile`, `Future`, and `jit`,
while auto-freezing `require` module exports so runs are strictly deterministic. Hosts
needing OS confinement should still pair VM policies with OS isolation.

### Collections, comprehensions, freezing, and deterministic parallelism

Cameo provides C#-backed `List<T>` (`[1, 2, 3]`, `[]`, `List.new()`, `List.of(...)`,
`List.from(...)`) and insertion-ordered `Dict<K, V>` (`Dict.new()`, `Dict.from(...)`).
Both preserve nil elements / insertion order, encode unambiguously as JSON `[]` and `{}`,
dispatch instance methods via unified `.` calls with zero `PrepareDotCall` in checked mode,
and transfer cleanly across workers. List comprehensions `[expr for x in xs [if cond]]`
and `[expr for k, v in dict [if cond]]` construct typed `List<T>` values; direct
`for x in collection` and `for k, v in dict` iterate `List`, `Dict`, arrays, maps,
`FloatArray`, and `Query` without `ipairs`/`pairs` (which continue to work unchanged).
Because multi-line strings use `"""..."""`, nested list literals such as `[[1, 2], [3, 4]]`
parse directly with no spaces required.

`freeze(v)` deeply and cycle-safely freezes `Table`, `List`, and `Dict` graphs, rejecting
subsequent mutations (`set`, `push`, `sort`, `setmetatable`, etc.), and `isFrozen(v)`
queries immutability. `parallel.map(items, fn)` and `list.parallelMap(fn)` partition work
into deterministic chunks across at most `VmLimits.MaxParallelWorkers` isolated worker VMs
inheriting all parent safety policies and step budgets, returning a `List` in input order.

### Pattern matching, required/optional record fields, and `import`/`export` modules

Cameo supports `match expr { ... }` in both expression and statement position, with
literal arms, OR-patterns (`"a" | "b"`), type-test narrowing (`is Type`, `is Type(name)`),
wildcard (`_`), expression or multi-statement block arms, and compile-time exhaustiveness
checking for `enum` scrutinees.

Braced Cameo `record` declarations enforce that non-nullable data fields are provided in
record literals; mark optional fields with `field?: T` (which becomes `T | nil` and may
be omitted).

Modules can declare top-level `export fn`, `export let`, `export var`, `export record`,
`export interface`, `export enum`, and `export type`, and consumers can import them with
`import { a, b as c } from "mod"` or `import mod from "mod"`. Pure type exports
(`interface`, `enum`) bind types without runtime lookups, while `record` exports bind
both the runtime constructor/methods and the static type. In `--hermetic` mode, imported
module exports are automatically deeply frozen.

### Units (`SharpUnits`) and safe numerics (`SharpSafeNumerics`)

Cameo (including `--hermetic` and `--sandboxed`) and `tlvm` integrate
`Cameo.Units` (`units` / `SharpUnits` / `require("sharpunits")`, wrapping
`SharpUnits.Interop.Lua.LuaUnitsBridge`) and `Cameo.SafeNumerics` (`safenum`, wrapping
`SharpSafeNumerics.Core` and `SharpSafeNumerics.Decimal`):

- **`units` (`SharpUnits`)**: `units.unit`, `units.qty`, `units.parse`, `units.point`,
  and SIMD-backed `units.series` (`min`, `max`, `mean`, `rms`, `peak_to_peak`, `in_unit`,
  `auto_scale`). Enforces ISO 80000 compile-time affine safety: `QuantityPoint + Quantity -> QuantityPoint`
  and `QuantityPoint - QuantityPoint -> Quantity`, while `QuantityPoint + QuantityPoint` is
  a compile-time error.
- **`safenum` (`SharpSafeNumerics`)**: overflow-checked 64-bit integer operations
  (`intAdd`, `intSub`, `intMul`, `intDiv`, `intNeg`, `intInRange`) with explicit policies
  (`"reject"`, `"saturate"`, `"wrap"`, `"throw"`), SIMD 32-bit `intAddBatch`, exact Q32.32
  `Fixed64` (`fixedFromInt`, `fixedFromRatio`, `fixedFromRaw`), and exact IEEE 754 base-10
  `Decimal64`/`Decimal128` (`dec64`, `dec128`, `tryAdd`/`tryDiv` with `"exact"` or `"to_even"`).

All unit and safe-numeric values are immutable (`isFrozen(v) == true`), dispatch methods
via unified `.` calls with zero `PrepareDotCall` in checked mode, and transfer cleanly
across `parallel.map` workers.

For scientific scripting, `FloatArray` supports `freeze`, `List<number>` conversion
(`FloatArray.from(list)` / `toList()`), cross-worker `parallel.map` transfer, `std`,
`median`, `quantile`, `cumsum`, `diff`, and 1D `FloatArray.interp(x, xp, fp)`. `math`
provides `isnan`, `isinf`, `isfinite`, `isclose`, and the explicitly seeded, zero-global-state
`math.rng(seed)` (`float`, `int`, `normal`, `floats`, `normals`, `clone`) available even
under `--hermetic`. Non-sandboxed/non-hermetic `CameoEngine` and `cameo` CLI also enable
`hdf5` (`Cameo.Hdf5`) and `fst` (`Cameo.Fst`, with unified `.` calls on `FstWriter`).

### Canonical formatter (`cameo fmt`) and VS Code syntax highlighting

`cameo fmt [--check] [--migrate] <file.cameo>...` formats Cameo source files into canonical
4-space indentation, LF line endings, trimmed trailing whitespace, and collapsed blank lines
while preserving comments, regex literals, interpolated strings, and multi-line strings
verbatim and validating syntax before writing. `--migrate` rewrites legacy Lua-surface syntax
(`--` / `--[[...]]`, `not` / `and` / `or` / `~=`, `//` / `//=`, `^` / `^=`, binary `~`,
`[[...]]` / `$[[...]]`, `elseif`) to clean Cameo syntax before formatting. `--check` leaves
files untouched and exits `1` if any file needs formatting. The VS Code extension
(`editors/vscode/`) ships a dedicated Cameo TextMate grammar (`syntaxes/cameo.tmLanguage.json`,
scope `source.cameo`) alongside save-time Cameo diagnostics.

Exit codes: 0 success, 1 compile/runtime/check/fmt-check failure, 2 usage or file-access
exception, 124 timeout, 130 cancellation, 125 memory/step/other runtime abort. A missing
run file returns 2; a missing check input is a collected check diagnostic and returns 1.
Explicit `os.exit` preserves the script's code. Check diagnostics can be JSON; script
stdout remains raw bytes. `CameoCommands.Parse` and `Execute` expose the same typed
operations to managed automation, with stable IDs `cameo.check`, `cameo.run`,
`cameo.eval`, `cameo.dump`, `cameo.repl`, `cameo.fmt`, `cameo.test`, `cameo.help` and
`cameo.describe`. Scripts own their side effects; there is no general undo. MCP transport
is still draft, not implemented by command discovery alone.

```csharp
using var engine = Cameo.CameoEngine.CreateHermetic(maxSteps: 1_000_000);
engine.Register("scale", (double x, double k = 2.0) => x * k, threadSafe: true);
var values = engine.RunString("let xs = [scale(x) for x in [1.0, 2.0, 3.0]] return json.encode(xs)");
Console.WriteLine(values[0].ToText()); // [2.0,4.0,6.0]
```

`CameoEngine` and `ScriptEngine` support typed .NET host binding via `Register` (including
`asFuture: true` for `Task`/`Task<T>` and `threadSafe: true` for `parallel.map` workers),
`Expose` (opt-in `[Expose]` / opt-out `[Hide]` properties and methods), `DefineValue`, and
`DeclareGlobal`, automatically registering signatures with the static type checker (see
[embedding.md](../embedding.md)). Use a VM owner thread with an adequate stack (tests/hosts
use 8 MiB). `Runtime` exposes existing host registration, JIT and Start/TryPump/WaitForWorkAsync
APIs; awaiting host work never authorizes another thread to touch VM state.

## Reference programs

`examples` contains 17 executable scripts, expected output and synthetic fixtures:

1. File organizer: asynchronous directory listing and a sorted, reviewable plan;
   it does not move files.
2. CSV report: explicit boundary validation constructs typed work items before a
   Query pipeline. Bad hours, missing fields and invalid booleans stop the report.
3. JSON client: offline fixture by default; optional HTTP uses explicit permission.
   Missing and null names are distinct, and a 64-bit integer survives decoding.
4. Async workflow: existing futures, a parallel worker and cancellation. This does
   not introduce structured concurrency or an asynchronous join-all shutdown.
5. Embedded events: a managed host transfers immutable results and pumps only on
   the VM owner thread while the outer host remains available.

Examples 06–17 cover stock records/control flow, recursion and mutable captures,
file/future composition with SHA-256, explicit typed recovery, cancellation/deadlines
(`cancelOnTimeout: true`), tagged `Result<T, E>` (`11`), deadline recovery (`12`),
`using` resource cleanup (`13`), pipelines and parallel `Result` transfer (`14`),
interfaces and structural conformance (`15`), the flagship strictly `--hermetic`
engineering & financial BOM showcase (`16-hermetic-engineering.cameo`) exercising
`List`/`Dict`, comprehensions, `match`, deep `freeze`, `parallel.map`, `SharpUnits`,
`SharpSafeNumerics`, and `cameo fmt --check`, and the embedded AXI/pointer hardware debugging
script (`17-embed-axi-debug.cameo`) targeting `Cameo.Embed` and the zero-heap static C++20 VM
(see [embed.md](embed.md)).

`bash examples/run.sh` verifies all 17 through real CLI/Embed-compiler/host entries
after building. The automated suite also checks malformed fixture and denied-network paths.
No live internet service or GUI toolkit is required.

## Direction and deliberate gaps

The positive-minded panel was five simulated design perspectives (C#, F#, Go, Lua
and script authors), reviewed over five rounds; no real language architect participated.
Most core ergonomic pieces from that roadmap are now implemented (`fn`/braces/`let`/`var`,
unified `.` calls, `Result<T, E>`, `try`/`catch`, `using`, `interface`, `match`,
`import`/`export`, `List`/`Dict`/comprehensions, deep `freeze`, `--hermetic`,
`parallel.map`, `units`, `safenum`, `cameo fmt`, and VS Code TextMate highlighting +
diagnostics on save).

Schema-generated decoders, correlated return packs, owned task scopes, async JIT
compilation, full LSP completion/rename, MCP transport, and C/Rust Cameo entry points
remain unimplemented. The JSON/CSV examples use handwritten runtime validation; casts
alone are not validation.

Reuse decision: keep the established parser/checker/compiler and libraries, with an
explicit source-language strategy and gated syntax, rather than duplicate the VM or
rewrite source text. No dependency is added. The versioned SDK and existing libraries
remain the compatibility baseline. The bounded executable evidence is the paired Teal/
Cameo tests and reference programs, not a claim that the broader design is qualified.

A useful future Dafny experiment is the parenthesis-pairing helper: prove cursor bounds,
progress and matching-index validity, emit C#, and test through the parser. No such
proof/generated integration is included here; parser tests are finite evidence only.
