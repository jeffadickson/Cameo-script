# Cameo by example: current surface and remaining proposals

This gallery separates the brace-style implementation slice from remaining design
proposals. `fn`, brace-delimited blocks/records, and initialized `var` bindings
belong to that slice. General instance dot calls remain proposed: only future
instance methods currently receive implicit receivers. See [the language guide](language.md)
and [complete working examples](../examples/) for the executable contract.

The snippets explain intended usage; the executable files and their tests supply
qualification evidence. Section 6 remains a design fixture because Query still
uses colon calls. Documentation alone closes no requirement.

The [independent simulated design review](../reviews/2026-11-17-cameo-language-surface.md)
records decisions to settle before implementation. Proposed brace-style examples
use four spaces; existing keyword-style examples retain three.

## 1. A small script

```text
let name = "Morgan"
print($"Hello, {name}")
```

Keep small scripts small. No mandatory class, namespace, or entry-point ceremony.

## 2. Named functions

```text
fn square(value: integer) -> integer {
    return value * value
}

print(square(7))
```

Expected output: `49`. Public boundaries carry types; obvious local values infer them.

## 3. Deliberate mutation

```text
var total = 0
total += 12
total += 8
print(total)
```

Expected output: `20`. `let` prevents rebinding; `var` permits it. Neither spelling
implies deep immutability of referenced objects.

## 4. Decisions

```text
fn deliveryLabel(days: integer) -> string {
    if days <= 1 {
        return "express"
    }
    return "standard"
}

print(deliveryLabel(3))
```

Expected output: `standard`. Functions and control flow use the same block convention.

## 5. Records with readable construction

```text
record Reading {
    sensor: string
    value: number
}

let reading: Reading = {sensor = "inlet", value = 23.5}
print($"{reading.sensor}: {reading.value}")
```

Record construction retains named fields. Its braces denote an expression;
declaration and control-flow braces denote blocks. Parsing uses context rather
than textual rewriting of `end`.

## 6. A transformation pipeline

```text
let selected = Query.from({1, 2, 3, 4})
    .where((value) => value > 2)
    .select((value) => value * 10)
    .toList()

print(table.concat(selected, ", "))
```

Expected output: `30, 40`. One stage per line makes the operation readable.
Query supplies callback types. General Query dot calls are proposed; current
Query instance calls use colons. This gallery keeps parenthesized arrows rather
than introducing another shorthand before its parsing rules are settled.

## 7. A future you can wait on

```text
let job = Future.parallel(() => 21 * 2)
let answer = job.get()
print(answer)
```

Expected output: `42`. This particular snippet already uses implemented syntax.
Waiting follows the existing future contract; method spelling does not itself
create a thread or make a shared VM safe for concurrent access.

## 8. Compose work before waiting

```text
let answer = Future.parallel(() => 20)
    .thenApply((value) => value + 1)
    .thenApply((value) => value * 2)

print(answer.get())
```

Expected output: `42`. Composition expresses dependencies without making every
intermediate step block. This callback chain is checked and executed in the
gallery smoke qualification; complete file/future composition is also covered
by `08-file-futures.cameo`.

## 9. Read a file without hiding the wait

```text
fn readSource(path: string) -> string {
    return aio.readFile(path).get()
}

let source = readSource("settings.json")
print(source)
```

The file must exist; output is its contents. `get()` is the fail-fast convenience.
Recoverable failures need a separate example using the existing typed error track,
not a made-up `try` syntax or a promise that cancellation is an ordinary error.

## 10. Keep decoding and validation distinct

```text
let source = aio.readFile("settings.json").get()
let decoded = unwrap(json.decode(source))
```

This decodes JSON; it does not validate a domain record. `unwrap` raises a non-nil
companion error and otherwise returns the first value. A cast cannot substitute
for checking field types, missing values, and JSON null. The working JSON client
shows those checks explicitly.

## Complete scripts and remaining gallery ideas

| Script | Current coverage and remaining work | Acceptance evidence |
| --- | --- | --- |
| CSV report | [02-csv-report](../examples/02-csv-report.cameo): braced records, typed business steps, pipeline | Fixed fixture and exact output; malformed row |
| JSON settings | [03-json-client](../examples/03-json-client.cameo): transport and domain validation | Valid, missing, null, and wrong-type fields |
| HTTP client | 03 has the optional permission-controlled route; local-server success coverage remains future work | Local server; no public-network dependency |
| Process runner | Arguments, output, exit status, cancellation | Local child fixture; no shell interpolation |
| Concurrent report | [04-async-workflow](../examples/04-async-workflow.cameo) covers composition/worker/cancellation; owned scopes remain proposed | Deterministic fixtures; cancellation and cleanup |
| SHA-256 manifest | [08-file-futures](../examples/08-file-futures.cameo) checks hashing; a full manifest is a future example | Published known-answer vectors and missing file |
| Managed host | [05-embedded-events](../examples/05-embedded-events.cameo): local fn plus explicit global host exports | Buildable C# host and observed completion |
| Sandbox | Capability restrictions and finite budgets | Allowed operation plus denied-operation checks |

These are gallery goals, not new APIs or release commitments. Bind each example
to the real library declarations before publishing code. Host integrations and
library semantics remain shared with Teal; the surface design belongs to Cameo.

## Implemented checks and remaining design work

- Make static calls, instance calls, extracted methods, and callbacks unambiguous.
  Preserve single evaluation and explicit binding rules; do not guess by names.
- Specify braces in records, tables, callbacks, nested blocks, and multiline calls.
- Preserve recursion, multiple returns, captures, source locations, and diagnostics.
- Show recovery, cancellation, cleanup, and resource ownership in complete scripts.
- Keep Teal syntax and semantics compatible and test both execution targets.
- Trace accepted grammar changes to StrictDoc, executable examples, parser negatives,
  property tests, coverage, and mutation evidence. Documentation is not proof.

## Cancellation and deadlines

[10-cancellation-deadlines.cameo](../examples/10-cancellation-deadlines.cameo)
is a complete offline example with deterministic expected output. It shows success,
explicit `cancel()`, and the typed `cancelled`/`timeout` errors returned by `await()`.
`job.await(timeout: 2, cancelOnTimeout: true)` sets a two-second scheduler deadline and
returns the usual value/error pair. If the deadline wins, the wait returns
`timeout` and the owned source settles as `cancelled`. Omit the second option
or pass `false` when the source should continue. The labels are supported on
direct Cameo `.await(...)` calls, in the order shown. In that position, `timeout:`
starts a named option. Parenthesize a positional colon call as
`job.await((timeout:read()))` to disambiguate it. Teal uses positional
`job:await(2, true)`. Timeouts accept finite seconds from 0 through 2147483.647.

Cancellation uses the source future's ordinary cancellation behavior.
External operations still need to cooperate with cancellation, and host
watchdog aborts remain a separate execution boundary.
