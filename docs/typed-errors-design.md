# Typed errors and catch clauses in Cameo

Status: tagged-result constructors (`ok`/`err`, `Result.ok`/`Result.err`), inspection
(`isOk`/`isErr`/`value`/`issue`/`unwrapOr`/`map`/`mapErr`, usable with `.` or `:`),
parallel/sandbox worker transfer, and `let`/`var` `try` propagation with named, bound,
exhaustive, and catch-all (`catch (e)` / `catch _`) expression handlers are implemented.
See REQ-CAM-010 for the correlated return contract it builds on.

Cameo functions can declare `Result<T, E>`. A result carries exactly one tag
and one payload. The tag distinguishes a success containing nil from an error
containing false. Values retain their identity through propagation and transfer across
`Future.parallel` and `sandbox.run` VMs (recursively copying payloads). Construction
uses `ok(value)` and `err(issue)` in a context with an expected result type, or explicit
`Result.ok(value)` / `Result.err(issue)`.
Read the tag with `result.isOk()` / `result.isErr()`, map with `result.map(fn)` /
`result.mapErr(fn)`, recover inline with `result.unwrapOr(fallback)`, and select the
matching payload with `result.value()` or `result.issue()`. Reading the wrong branch
raises an argument error.
See [the runnable port validation example](../examples/11-tagged-results.cameo)
and [pipelines and results](../examples/14-pipelines-and-results.cameo).

```text
fn finishReport(job: Future<Report>, cached: Report) -> Result<Report, FutureError> {
    let report = try Result.fromPair(job.await(timeout: 2, cancelOnTimeout: true))
        catch timeout { cached }

    return ok(report)
}
```

Use C#-style braced catch clauses alongside the expression that can fail. The
operation evaluates once. Success produces T. On failure, a matching clause
evaluates in its own lexical scope and supplies T. Named catches (`catch timeout { ... }`
or `catch timeout(e) { ... }`) resolve against the declared error discriminator enum
and reject unknown or duplicate cases. Catch-all clauses (`catch (e) { ... }` or
`catch _ { ... }`) handle any remaining error, including non-record error types such as
`Result<T, string>`. When a catch-all is present or named cases exhaustively cover every
discriminator variant, the enclosing function does not need to return `Result<T, E>`.
Otherwise an unhandled case propagates the original error through the enclosing result
type. Nested functions and tasks own their propagation separately.

The supported grammar is `let|var name = try operation()` (or `try (expr)`) with optional
`catch kind [(e)] { fallback }` and trailing `catch (e) { fallback }` / `catch _ { fallback }`
clauses. The operand must start with a name or `(` on the same line as `try` and
produce a tagged Result. Use `Result.fromPair(job.await(...))` to explicitly
adapt a value/error API such as Future.await. The adapter takes the two result
slots and treats nil as success. Raised Lua errors and host aborts continue on their
existing paths.

## Compiler and execution

The front end lowers this binding form to a private result capture and
ordinary conditional branches. The checker verifies the result identity,
handler payload and return error type before bytecode generation. An unmatched
error returns the original tagged object from the current function. The compiler
uses its existing branch, return, and close operations,
including close-on-return for active resource locals. The first slice is
expected to need no opcode or bytecode-format change. Verify the interpreter
and supported compiled paths before claiming execution parity.

The Result module and instance method tables are separate. Ordinary module
assignment cannot change the instance tag check. Raw debug access can bypass
metatable protection, as it can for other VM userdata.

Verification should cover single evaluation and evaluation order, named-case
checking, success and both cancellation outcomes, preservation of false and
nil payloads, nested functions, scheduler suspension, cleanup order, and
host-abort bypass. Compare Teal and Cameo on existing calls. A bounded Dafny
model could check the pure tag dispatch and first matching case rule. Disposal,
abort handling and scheduler suspension need execution tests at their actual
boundaries.

## Simulated panel priorities

1. Typed results, explicit try and braced catch clauses.
2. Tagged unions with exhaustive matching.
3. Formatter and language server with diagnostics, completion and rename.
4. Typed JSON and CSV decoding with field paths in errors.
5. Generated and checked host bindings.
6. Structured task scopes that cancel and join owned work.
7. Resource scopes with predictable cleanup and error handling.
8. Module contracts and reproducible dependency locking.
9. Consistent instance calls and explicit bound methods.
10. A script test runner with isolated fixtures and controllable clocks.
