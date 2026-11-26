# Writing clear Cameo

Cameo extends typed Lua/Teal with initialized `let` bindings, short expression functions
and direct future-object dot calls. The brace-style slice adds named `fn` functions,
initialized mutable `var` bindings, and brace-delimited records and control blocks. This guide
defines its preferred authoring style; it is not an automatic formatter or a claim that
the larger language proposal is implemented. Shared libraries, including `unwrap`, are
available to Teal too. Cameo owns its new source syntax; Teal keeps its calling rules.

## Make the useful work easy to find

Use named functions for business steps, short arrows for transformations, inferred locals
where the meaning is evident, and explicit types at data boundaries. Put one Query stage
on each line. Use four spaces inside brace blocks, blank lines between functions, descriptive
names, `let` for non-reassignable bindings, and `fn` for new brace-style named
functions (`local function` remains the compatible keyword form).
Use `var` for initialized mutable bindings, or `local` for legacy/uninitialized
bindings. `try` and `match` are not implemented.

`let` requires an initializer and prevents reassignment, including through a captured
name or a bare function declaration, even with type checking disabled. It does not freeze
tables or future state. Prefer `record Name { ... }` for records; legacy type declarations remain valid. Names and fields
called `let` still work where they are ordinary identifiers rather than declarations.
Use `local` with explicit `<close>`/other existing local attributes; `let` does not accept
those attributes. Recursive named functions use `fn` or `local function`; a let initializer does
not introduce a new self-recursive binding rule. Named `fn` declarations share the
recursive local-function rule.

```text
let job = Future.parallel(() => 21 * 2)
let answer = job.get()
print(answer)

var attempts = 0
attempts += 1
```

Built-in instance methods (`Future`, `Result`, `Query`, `string`, `FILE`, `FloatArray`,
`utf16`, `Regex`, `Stream`, `Sink`, `Process`, `Watcher`, `HttpSession`, and `Connection`)
support direct dot calls in Cameo: `job.get()`, `result.isOk()`, `text.trim().upper()`,
and `Query.from(items).where(...).toList()`. Static/module calls (`Future.parallel(...)`,
`Query.from(...)`, `Result.fromPair(...)`, `string.upper(...)`, `json.decode(...)`) keep
their ordinary meaning and compile with zero `PrepareDotCall` overhead in checked code.
Existing colon calls remain valid. Extracting `let getter = job.get` still leaves an unbound
function, called as `getter(job)`; similarly `(job.get)(job)` and `job["get"](job)` stay
explicit. User-defined record/class methods still use `:`. Cameo also supports `!=` as an
alias for `~=` and the `|>` pipeline operator (`raw |> string.trim |> string.upper`).

```text
record WorkItem {
    title: string
    owner: string
    hours: number
    done: boolean
}

fn report(items: {WorkItem}) -> {string} {
    return Query.from(items)
        .where((item) => not item.done)
        .orderByDescending((item) => item.hours)
        .select((item) => $"{item.owner}: {item.title} ({item.hours} h)")
        .toList()
}

let items: {WorkItem} = {
    {title = "Review", owner = "Ada", hours = 2, done = false}
}
for _, line in ipairs(report(items)) {
    print(line)
}
```

The executable CSV report in `examples/02-csv-report.cameo` follows this pattern,
with real decoding and domain checks. The JSON client separates `readSource`, `profileFrom`
and `main`, so transport, validation and useful output can be read independently.
The examples retain missing/null/type checks and exact 64-bit integer behavior.
All ten runnable samples now use the implemented style. The embedded example
keeps explicit typed global exports because `fn` is local: `runWork` is exported as
`work`, preserving the host's lookup contract without adding a new global-fn syntax.

## Brace-style scripts

Use four spaces in brace blocks. New code can use local named functions:

```text
fn square(value: integer) -> integer {
    return value * value
}

let job = Future.parallel(() => square(21))
print(job.get())
```

`fn` follows existing local-function scope: recursion is lexical, names are not
automatically exported or forward-hoisted. Parameters and returns use existing
types, with `->` introducing return annotations. Multi-step functions retain
ordinary checking rules; expression-function inference is not silently applied.
Annotate named functions that return values with `-> type` (or a return list);
omitting it retains the existing zero-result contract rather than inferring a result.
`var`/`let` initializers follow existing local visibility and shadowing.
The new `fn`/`var`/`record` forms document literal source names. Keep existing
legacy declaration forms for macro name-splicing; braced record Statement arguments
are separately covered by the integration tests.

Braces delimit `record Name`, `if`/`else`, `while`, numeric/generic `for`, and `do`
bodies. Table expressions keep existing braces and named-field syntax. In a
brace-style control header, write table arguments in parentheses to keep the body
unambiguous. Legacy keyword blocks and table-shorthand calls remain available.

Newlines generally remain whitespace, including after `return`; no automatic
semicolon insertion occurs. Leading-dot chains continue over newlines/comments.
The existing exception remains: a call-opening `(` on a later line does not
continue the preceding expression. Keep parentheses beside the callee and use
semicolons where adjacent expressions would otherwise join. Teal keeps its grammar.

The object-method rules remain explicit. Only actual future instance methods get
an implicit receiver in a direct dot call. For a bound callback use `() => job.get()`;
extracting `job.get` still produces an unbound function. General dot calls, formatter
automation, correlated result checking, and new error-propagation syntax are not
part of this slice.

## Choose recovery or deliberate failure

Recover where you know what to do with a failure:

```text
let text, issue = aio.readFile("settings.json").await()
if issue ~= nil {
    print($"Settings unavailable: {issue.message}")
} else {
    print(text)
}
```

When an operation cannot usefully continue, existing `Future.get()` raises its typed
future error. `unwrap` does the same for a conventional `value, issue` result:

```text
let source = aio.readFile("settings.json").get()
let decoded = unwrap(json.decode(source))
```

`unwrap` returns exactly the first value when the second is absent or nil. Successful
false and nil values are preserved. Every non-nil error, even false, is raised unchanged;
it does not reduce a typed error to its message. Extra values are ignored. `assert` tests
the first value's truthiness instead, so it is not interchangeable for false/nil successes.
The helper does not validate external data or infer correlated success/error types.

Keep one presentation boundary for a command that deliberately aborts:

```text
fn main() {
    let source = aio.readFile("settings.json").get()
    let decoded = unwrap(json.decode(source))
    print(type(decoded))
}

let ok, issue = pcall(main)
if not ok {
    io.stderr:write(tostring(issue) .. "\n")
    os.exit(2)
}
```

This preserves the error object until presentation. It is suitable for a standalone
command, not a library that silently exits its host. `pcall` does not catch the VM's
uncatchable budget/host aborts. Use explicit railway results where recovery is intended.

## Multiple results are part of the call contract

```text
let decoded = unwrap(json.decode(source)) -- final call supplies value and error
```

Avoid these forms: parentheses truncate a call to one value, and a call in a non-final
argument position is also reduced before `unwrap` sees it:

```text
let decoded = unwrap((json.decode(source))) -- loses the error companion
let decoded = unwrap(json.decode(source), nil) -- also loses the companion
```

When naming intermediate values, retain both:

```text
let decoded, issue = json.decode(source)
let value = unwrap(decoded, issue)
```

`unwrap(pcall(work))` is wrong: `pcall` returns a success boolean first, not a value/error
pair. Arbitrary multiple-success results need explicit variables. An absent error companion
cannot be reconstructed. A cast such as `decoded as Profile` is not runtime validation.

## Move callbacks without losing their type

Query supplies a callback context:

```text
let doubled = Query.from({1, 2, 3})
    :select((value) => value * 2)
    :toList()
```

An extracted callback needs an annotation or a declared receiving function type:

```text
let twice = (value: integer) => value * 2
let alsoTwice: function(integer): integer = (value) => value * 2
```

`let twice = (value) => value * 2` has no receiving context and is rejected. A repair
must use the actual intended type, not an invented `any` or guessed integer annotation.
Arrows contain one expression with one known fixed result. Multi-step work, fallible
multi-result calls, varargs and outer result casts use ordinary full functions:

```text
fn decode(source: string) -> any {
    return unwrap(json.decode(source))
}
```

The current file-wide restriction also rejects arrows in a file containing macros,
macro expressions or record `where` reparsing, even in unrelated functions. Use full
functions throughout such a file. CAMEO001..005 diagnostics identify these restrictions
and a repair; they do not promise all inference or parser combinations are supported.

## Agent checklist

- Select `.cameo` and the Cameo entry; declarations/raw Lua keep their own parser.
- Keep Teal runtime/permission/byte-string rules. Unicode conversion happens at boundaries.
- Use arrows only with concrete parameter context and one known fixed result.
- Validate outside data before building typed records; retain missing versus null semantics.
- Preserve both values of a fallible pair until you handle or unwrap it.
- Use `await` for recovery, `get`/`unwrap` for deliberate abort, one presentation boundary.
- Check and run the complete script using the documented CLI. A clean fragment does not
  establish module, cancellation, permission or malformed-input behavior.

Formatter/LSP, correlated result packs, generated decoders, variants/matching, owned task
scopes and async JIT remain separately tracked proposals. This style guide does not claim
measured improvements in human comprehension or agent success rates.

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
