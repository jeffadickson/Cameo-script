-- Declarations specific to TealVM (not part of the Teal standard library).

-- A UTF-16 string backed by a native .NET string. The ordinary `string` type is a byte
-- string holding UTF-8 text; convert with utf16.new(s) and tostring(u).
global record utf16
   userdata

   new: function(? string | utf16): utf16
   char: function(integer...): utf16
   len: function(utf16): integer
   sub: function(utf16, integer, ? integer): utf16
   upper: function(utf16): utf16
   lower: function(utf16): utf16
   rep: function(utf16, integer): utf16
   reverse: function(utf16): utf16
   unit: function(utf16, ? integer, ? integer): integer...
   codepoint: function(utf16, ? integer, ? integer): integer...
   find: function(utf16, utf16 | string, ? integer): integer, integer
   tostring: function(utf16): string

   metamethod __len: function(utf16): integer
   metamethod __concat: function(utf16 | string, utf16 | string): utf16
   metamethod __eq: function(utf16, utf16): boolean
   metamethod __lt: function(utf16, utf16): boolean
   metamethod __le: function(utf16, utf16): boolean
   metamethod __tostring: function(utf16): string
end

-- Math built-ins. The functions of the `math` library that take and return a float are also
-- global constants, so `sin(x)` needs no `math.` prefix; in type-checked code such calls
-- compile to a single VM instruction rather than a function call.
local record MathBuiltins
   unary: function(number): number
   abs: function<N is number>(N): N
   floor: function(number): integer
   ceil: function(number): integer
   round: function(number): integer
   trunc: function(number): integer
   sign: function(number): integer
   pow: function(number, number): number
   fmod: function(number, number): number
   atan2: function(number, number): number
   hypot: function(number, number): number
   log: function(number, ? number): number
   atan: function(number, ? number): number
   min: function(integer...): integer
   min: function((number | integer)...): number
   max: function(integer...): integer
   max: function((number | integer)...): number
end

global sin <const> = MathBuiltins.unary
global cos <const> = MathBuiltins.unary
global tan <const> = MathBuiltins.unary
global asin <const> = MathBuiltins.unary
global acos <const> = MathBuiltins.unary
global atan <const> = MathBuiltins.atan
global sinh <const> = MathBuiltins.unary
global cosh <const> = MathBuiltins.unary
global tanh <const> = MathBuiltins.unary
global asinh <const> = MathBuiltins.unary
global acosh <const> = MathBuiltins.unary
global atanh <const> = MathBuiltins.unary
global exp <const> = MathBuiltins.unary
global exp2 <const> = MathBuiltins.unary
global expm1 <const> = MathBuiltins.unary
global log <const> = MathBuiltins.log
global log2 <const> = MathBuiltins.unary
global log10 <const> = MathBuiltins.unary
global log1p <const> = MathBuiltins.unary
global sqrt <const> = MathBuiltins.unary
global cbrt <const> = MathBuiltins.unary
global deg <const> = MathBuiltins.unary
global rad <const> = MathBuiltins.unary
global abs <const> = MathBuiltins.abs
global floor <const> = MathBuiltins.floor
global ceil <const> = MathBuiltins.ceil
global round <const> = MathBuiltins.round
global trunc <const> = MathBuiltins.trunc
global sign <const> = MathBuiltins.sign
global pow <const> = MathBuiltins.pow
global fmod <const> = MathBuiltins.fmod
global atan2 <const> = MathBuiltins.atan2
global hypot <const> = MathBuiltins.hypot
global min <const> = MathBuiltins.min
global max <const> = MathBuiltins.max

-- Futures (Java's CompletableFuture). Asynchronous functions run as cooperative tasks on the
-- VM's scheduler. Failures are typed: a failed future carries a FutureError.
global record FutureError
   enum Kind
      "failed" "cancelled" "timeout" "deadlock"
   end
   kind: Kind
   message: string
   cause: any
end

global record Future<T>
   userdata

   new: function<U>(): Future<U>
   completedFuture: function<U>(U): Future<U>
   failedFuture: function<U>(any): Future<U>
   supplyAsync: function<U>(function(): U): Future<U>
   runAsync: function(function()): Future<any>
   delay: function(number): Future<boolean>
   delay: function<U>(number, U): Future<U>
   -- runs the function in a VM of its own on another thread (real parallelism); the function,
   -- what it captures and its arguments are copied there, and the result is copied back
   parallel: function<U>(function(...: any): (U), ...: any): Future<U>
   processors: function(): integer
   allOf: function<U>({Future<U>}): Future<{U}>
   anyOf: function<U>({Future<U>}): Future<U>

   -- railway style: returns the value, or nil and a typed error; never raises
   await: function(self, ? number, ? boolean): T, FutureError
   -- throwing variants: raise the FutureError
   get: function(self, ? number): T
   join: function(self): T
   getNow: function(self, T): T

   isDone: function(self): boolean
   isCompletedExceptionally: function(self): boolean
   isCancelled: function(self): boolean
   complete: function(self, T): boolean
   completeExceptionally: function(self, any): boolean
   cancel: function(self): boolean

   thenApply: function<U>(self, function(T): U): Future<U>
   thenAccept: function(self, function(T)): Future<any>
   thenRun: function(self, function()): Future<any>
   thenCompose: function<U>(self, function(T): Future<U>): Future<U>
   thenCombine: function<U, V>(self, Future<U>, function(T, U): V): Future<V>
   exceptionally: function(self, function(FutureError): T): Future<T>
   handle: function<U>(self, function(T, FutureError): U): Future<U>
   whenComplete: function(self, function(T, FutureError)): Future<T>
   orTimeout: function(self, number): Future<T>
   completeOnTimeout: function(self, T, number): Future<T>
end

-- Asynchronous I/O. Every function returns a Future at once; a failure is a FutureError whose
-- `cause` is an IoError.
global record FileStat
   size: integer
   modified: number
   isdir: boolean
end

global record IoError
   enum Kind
      "not_found" "permission_denied" "timeout" "network" "invalid" "cancelled" "failed" "http" "eof"
   end
   kind: Kind
   message: string
   target: string
end

global record HttpResponse
   status: integer
   headers: {string:string}
   body: string
end

global record HttpRequest
   url: string
   method: string
   headers: {string:string}
   timeout: number
   -- one kind of body: bytes, a value sent as JSON, a file, form fields, or files (multipart, with form)
   body: string
   json: any
   bodyFile: string
   form: {string:string}
   files: {string:string}
end

-- What stream:expect found: which pattern (1-based), the matched text, the output before it, and the captures.
global record ExpectMatch
   index: integer
   text: string
   before: string
   groups: {string}
end

-- A source read piece by piece: a file, an HTTP body or a program's output. Reads give nil at the end.
global record Stream
   userdata
   read: function(self, ? integer): Future<string>
   readLine: function(self): Future<string>
   readAll: function(self): Future<string>
   lines: function(self): (function(): string)
   -- waits for one of the regular expressions to match the incoming output
   expect: function(self, string | Regex | {string}, ? number): Future<ExpectMatch>
   close: function(self)
end

-- A destination written piece by piece; writes are applied in the order they were made.
global record Sink
   userdata
   write: function(self, string): Future<boolean>
   close: function(self): Future<boolean>
end

global record HttpStreamResponse
   status: integer
   headers: {string:string}
   body: Stream
end

-- Requests made through one session share cookies, default headers and a base URL.
global record HttpSessionOptions
   headers: {string:string}
   baseUrl: string
end

global record HttpSession
   userdata
   get: function(self, string, ? {string:string}): Future<HttpResponse>
   request: function(self, HttpRequest): Future<HttpResponse>
   getJson: function(self, string, ? {string:string}): Future<any>
   postJson: function(self, string, any, ? {string:string}): Future<any>
   download: function(self, string, string, ? {string:string}): Future<HttpResponse>
   stream: function(self, string | HttpRequest): Future<HttpStreamResponse>
   close: function(self)
end

-- A program started with aio.spawn.
global record Process
   userdata
   stdout: Stream
   stderr: Stream
   pid: integer
   write: function(self, string): Future<boolean>
   closeInput: function(self): Future<boolean>
   wait: function(self): Future<integer>
   kill: function(self)
   -- drive an interactive program: wait for output, then answer
   expect: function(self, string | Regex | {string}, ? number): Future<ExpectMatch>
   send: function(self, string): Future<boolean>
   sendLine: function(self, string): Future<boolean>
end

-- pty = true runs the program on a pseudo-terminal, so that it behaves as it does for a person
global record SpawnOptions
   pty: boolean
   cols: integer
   rows: integer
end

global record FileEvent
   enum Kind
      "created" "changed" "deleted" "renamed"
   end
   kind: Kind
   path: string
   oldPath: string
end

global record WatchOptions
   recursive: boolean
   filter: string
end

global record Watcher
   userdata
   next: function(self, ? number): Future<FileEvent>
   events: function(self): (function(): FileEvent)
   close: function(self)
end

global record ProcessResult
   code: integer
   stdout: string
   stderr: string
end

global record aio
   readFile: function(string): Future<string>
   writeFile: function(string, string): Future<boolean>
   appendFile: function(string, string): Future<boolean>
   listDir: function(string): Future<{string}>
   -- need the network permission (TealOptions.AllowNetwork, tlvm --allow-net)
   httpGet: function(string, ? {string:string}): Future<HttpResponse>
   httpRequest: function(HttpRequest): Future<HttpResponse>
   getJson: function(string, ? {string:string}): Future<any>
   postJson: function(string, any, ? {string:string}): Future<any>
   download: function(string, string, ? {string:string}): Future<HttpResponse>
   httpStream: function(string | HttpRequest): Future<HttpStreamResponse>
   httpSession: function(? HttpSessionOptions): HttpSession
   -- needs the process permission (TealOptions.AllowProcesses, tlvm --allow-run); no shell is involved
   exec: function(string, ? {string}, ? string): Future<ProcessResult>
   spawn: function(string, ? {string}, ? SpawnOptions): Future<Process>
   -- streaming files
   openRead: function(string): Future<Stream>
   openWrite: function(string, ? boolean): Future<Sink>
   -- file operations and watching
   exists: function(string): Future<boolean>
   stat: function(string): Future<FileStat>
   mkdir: function(string): Future<boolean>
   remove: function(string, ? boolean): Future<boolean>
   copy: function(string, string, ? boolean): Future<boolean>
   move: function(string, string): Future<boolean>
   watch: function(string, ? WatchOptions): Future<Watcher>
end

-- JSON. decode/encode return the result, or nil and a JsonError; they do not raise.
global record JsonError
   enum Kind
      "syntax" "too_deep" "unsupported" "cycle" "invalid_number"
   end
   kind: Kind
   message: string
   position: integer
end

global record JsonOptions
   indent: integer
   sortKeys: boolean
end

global record json
   null: any
   decode: function(string): any, JsonError
   encode: function(any, ? JsonOptions): string, JsonError
   array: function<T>(? {T}): {T}
   isArray: function(any): boolean
end

-- Files, directories and paths (blocking; aio has the asynchronous versions).

global record FileEntry
   path: string
   name: string
   isdir: boolean
   size: integer
   modified: number
end

global record fs
   exists: function(string): boolean
   isfile: function(string): boolean
   isdir: function(string): boolean
   stat: function(string): FileStat, IoError
   mkdir: function(string): boolean, IoError
   remove: function(string, ? boolean): boolean, IoError
   list: function(string): {string}, IoError
   copy: function(string, string, ? boolean): boolean, IoError
   move: function(string, string): boolean, IoError
   read: function(string): string, IoError
   write: function(string, string): boolean, IoError
   join: function(string...): string
   basename: function(string): string
   dirname: function(string): string
   extension: function(string): string
   stem: function(string): string
   absolute: function(string): string
   cwd: function(): string
   tempdir: function(): string
   separator: string
   -- paths matching a pattern with *, ?, **, [a-z] and {a,b}; sorted
   glob: function(string, ? string): {string}
   -- everything below a directory
   walk: function(string): {FileEntry}, IoError
end

-- Clocks and dates. A time is seconds since 1970-01-01 UTC.
global record TimeError
   enum Kind
      "invalid_format" "invalid_time" "unknown_zone"
   end
   kind: Kind
   message: string
end

global record time
   now: function(): number
   millis: function(): integer
   monotonic: function(): number
   sleep: function(number)
   format: function(? number, ? string, ? string): string, TimeError
   parse: function(string, ? string, ? string): number, TimeError
end

-- Regular expressions (.NET syntax). An invalid pattern given to regex.new is returned as a
-- RegexError; the other functions raise it.
global record RegexError
   enum Kind
      "invalid_pattern" "timeout"
   end
   kind: Kind
   message: string
end

global record RegexMatch
   text: string
   start: integer
   finish: integer
   groups: {string}
   named: {string:string}
end

global record Regex
   userdata
   test: function(self, string): boolean
   match: function(self, string, ? integer): RegexMatch
   matches: function(self, string): {RegexMatch}
   replace: function(self, string, string | function(RegexMatch): (string), ? integer): string, integer
   split: function(self, string, ? integer): {string}
end

global record regex
   new: function(string, ? string): Regex, RegexError
   escape: function(string): string
   test: function(string, string | Regex, ? string): boolean
   match: function(string, string | Regex, ? integer): RegexMatch
   matches: function(string, string): {RegexMatch}
   replace: function(string, string, string | function(RegexMatch): (string), ? integer): string, integer
   split: function(string, string, ? integer): {string}
end

-- Encodings, hashes and secure random data.
global record DecodeError
   enum Kind
      "invalid_base64" "invalid_hex"
   end
   kind: Kind
   message: string
end

global record base64
   encode: function(string, ? boolean): string
   decode: function(string): string, DecodeError
end

global record hex
   encode: function(string): string
   decode: function(string): string, DecodeError
end

global record crypto
   sha256: function(string, ? boolean): string
   sha512: function(string, ? boolean): string
   sha1: function(string, ? boolean): string
   md5: function(string, ? boolean): string
   hmac: function(string, string, string, ? boolean): string
   randomBytes: function(integer): string
   randomInt: function(integer, integer): integer
   uuid: function(): string
end

-- A fixed-length array of 64-bit floats in one block of memory, for signal data. Operators
-- work element by element and return a new array; the ...InPlace methods change the array.
global record FloatArray
   is {number}
   userdata

   new: function(integer, ? number): FloatArray
   from: function({number} | FloatArray): FloatArray
   from: function(List<number>): FloatArray
   linspace: function(number, number, integer): FloatArray
   frombytes: function(string, ? string, ? boolean): FloatArray
   interp: function(FloatArray, FloatArray, FloatArray): FloatArray
   interp: function(number, FloatArray, FloatArray): number

   get: function(self, integer): number
   set: function(self, integer, number)
   fill: function(self, number, ? integer, ? integer): FloatArray
   copy: function(self): FloatArray
   slice: function(self, ? integer, ? integer): FloatArray
   totable: function(self): {number}
   toList: function(self): List<number>
   tobytes: function(self, ? string, ? boolean): string

   abs: function(self): FloatArray
   sqrt: function(self): FloatArray
   sin: function(self): FloatArray
   cos: function(self): FloatArray
   exp: function(self): FloatArray
   log: function(self): FloatArray
   clamp: function(self, number, number): FloatArray
   map: function(self, function(number, integer): number): FloatArray
   cumsum: function(self): FloatArray
   diff: function(self): FloatArray

   sum: function(self): number
   mean: function(self): number
   min: function(self): number, integer
   max: function(self): number, integer
   rms: function(self): number
   variance: function(self): number
   std: function(self, ? boolean): number
   median: function(self): number
   quantile: function(self, number): number
   dot: function(self, FloatArray): number

   addInPlace: function(self, FloatArray | number): FloatArray
   subInPlace: function(self, FloatArray | number): FloatArray
   mulInPlace: function(self, FloatArray | number): FloatArray
   divInPlace: function(self, FloatArray | number): FloatArray

   metamethod __add: function(FloatArray | number, FloatArray | number): FloatArray
   metamethod __sub: function(FloatArray | number, FloatArray | number): FloatArray
   metamethod __mul: function(FloatArray | number, FloatArray | number): FloatArray
   metamethod __div: function(FloatArray | number, FloatArray | number): FloatArray
   metamethod __unm: function(FloatArray): FloatArray
   metamethod __len: function(FloatArray): integer
   metamethod __eq: function(FloatArray, FloatArray): boolean
end

-- Deterministic, explicitly seeded xoshiro256** PRNG (available even in Hermetic mode).
global record Rng
   userdata

   float: function(self): number
   int: function(self, integer, integer): integer
   normal: function(self, ? number, ? number): number
   floats: function(self, integer): FloatArray
   normals: function(self, integer, ? number, ? number): FloatArray
   clone: function(self): Rng
end

-- LINQ-style queries: Query.from(list):where(f):select(g):orderBy(k):toList(). Each step
-- builds its result at once and leaves its input unchanged.
global record Group<K, T>
   key: K
   items: {T}
end

global record Query<T>
   userdata

   from: function<U>({U} | List<U> | Query<U>): Query<U>
   range: function(integer, integer, ? integer): Query<integer>

   where: function(self, function(T): boolean): Query<T>
   select: function<U>(self, function(T): U): Query<U>
   flatMap: function<U>(self, function(T): {U}): Query<U>
   orderBy: function<K>(self, function(T): K): Query<T>
   orderByDescending: function<K>(self, function(T): K): Query<T>
   distinct: function(self, ? function(T): any): Query<T>
   groupBy: function<K>(self, function(T): K): Query<Group<K, T>>
   take: function(self, integer): Query<T>
   skip: function(self, integer): Query<T>
   reverse: function(self): Query<T>
   concat: function(self, {T} | Query<T>): Query<T>

   toList: function(self): {T}
   toMap: function<K>(self, function(T): K): {K:T}
   toMap: function<K, V>(self, function(T): (K), function(T): V): {K:V}
   first: function(self, ? function(T): boolean): T
   last: function(self, ? function(T): boolean): T
   any: function(self, ? function(T): boolean): boolean
   all: function(self, function(T): boolean): boolean
   count: function(self, ? function(T): boolean): integer
   sum: function(self, ? function(T): number): number
   min: function(self, ? function(T): any): T
   max: function(self, ? function(T): any): T
   contains: function(self, T): boolean
   aggregate: function<A>(self, function(A, T): (A), A): A
   forEach: function(self, function(T))

   metamethod __len: function(Query<T>): integer
end

-- SQL databases (the host supplies the provider; `tlvm --allow-db` has SQLite). Values go in as
-- parameters: `?` filled from a list, `@name` filled from a table. Failures are a FutureError
-- whose cause is a DbError.
global record DbError
   enum Kind
      "permission_denied" "unavailable" "invalid" "constraint" "closed" "cancelled" "failed"
   end
   kind: Kind
   message: string
   target: string
end

global record Connection
   userdata
   query: function(self, string, ? {any:any}): {{string:any}}, FutureError
   queryOne: function(self, string, ? {any:any}): {string:any}, FutureError
   scalar: function(self, string, ? {any:any}): any, FutureError
   exec: function(self, string, ? {any:any}): integer, FutureError
   begin: function(self): boolean, FutureError
   commit: function(self): boolean, FutureError
   rollback: function(self): boolean, FutureError
   close: function(self)
end

global record db
   open: function(string): Connection, FutureError
   -- SQL NULL in a parameter list (a list cannot hold nil)
   null: any
end

-- CSV. Fields are text: convert with tonumber where needed.
global record CsvError
   enum Kind
      "syntax"
   end
   kind: Kind
   message: string
   line: integer
end

global record CsvOptions
   header: boolean
   separator: string
   columns: {string}
end

global record csv
   decode: function(string, ? CsvOptions): {{string:string}}, CsvError
   encode: function({{any:any}}, ? CsvOptions): string
end

-- Running code with fewer rights than this program has.
global record SandboxOptions
   enum Permission
      "net" "run" "db"
   end
   allow: {Permission}
   timeout: number
   args: {any}
   quiet: boolean
   isolated: boolean
   maxSteps: integer
end

global record sandbox
   run: function(string, ? SandboxOptions): Future<any>
end

-- Declared command-line parameters.
global record CliError
   enum Kind
      "help" "unknown_option" "missing_value" "invalid_value" "missing_argument" "too_many_arguments" "missing_option"
   end
   kind: Kind
   message: string
end

global record CliParam
   enum Type
      "string" "integer" "number" "boolean"
   end
   name: string
   short: string
   type: Type
   default: any
   help: string
   required: boolean
   multiple: boolean
   rest: boolean
end

global record CliSpec
   name: string
   description: string
   options: {CliParam}
   positionals: {CliParam}
end

global record cli
   parse: function(CliSpec, ? {string}): {string:any}
   tryParse: function(CliSpec, ? {string}): {string:any}, CliError
   usage: function(CliSpec): string
end

-- HDF5 files (an add-on: present in tlvm, and in a host that calls Hdf5Library.Enable).
-- Numeric datasets are FloatArrays in row-major order; their shape is returned separately.
global record Hdf5Entry
   enum Kind
      "group" "dataset"
   end
   path: string
   kind: Kind
   type: string
   shape: {integer}
   attributes: {string:any}
end

global record Hdf5Dataset
   data: any
   shape: {integer}
   type: string
   attributes: {string:any}
end

global record hdf5
   -- the data and its shape ({integer}); on failure nil and an IoError in the shape's place
   read: function(string, string): any, any
   info: function(string, string): Hdf5Entry, IoError
   list: function(string): {Hdf5Entry}, IoError
   write: function(string, {string:any}, ? {string:any}): boolean, IoError
end

-- GTKWave's FST waveform files (add-on TealVM.Fst; in tlvm). Times are whole numbers of ticks
-- of the file's timescale. File problems come back as nil and an IoError.
global record FstSignal
   name: string           -- full name, scopes joined with "."
   scope: string
   handle: integer
   width: integer         -- bits; 64 for a real, 0 for a string
   type: string           -- "wire", "reg", "real", "string", "integer", ...
   direction: string
end

global record FstScope
   name: string
   type: string
end

global record FstInfo
   timescale: string      -- "1ns", "100ps", ...
   timescaleExponent: integer
   startTime: integer
   endTime: integer
   version: string
   date: string
   signals: {FstSignal}
   scopes: {FstScope}
end

-- One variable's changes. Bit variables and strings have `values`; reals have `reals`.
global record FstTrace
   name: string
   type: string
   width: integer
   times: {integer}
   values: {string}       -- "0", "1", "x", "z", "0101xxzz", ... most significant bit first
   reals: FloatArray
end

global record FstWriter
   userdata
   scope: function(self, string, ? string): FstWriter
   upscope: function(self): FstWriter
   var: function(self, string, ? integer, ? string): integer
   real: function(self, string): integer
   string: function(self, string): integer
   time: function(self, integer): FstWriter
   set: function(self, integer, number | string | boolean): FstWriter
   close: function(self): boolean, IoError
end

global record fst
   info: function(string): FstInfo, IoError
   read: function(string, string): FstTrace, IoError
   readAll: function(string, ? {string}): {string:FstTrace}, IoError
   write: function(string, {string:any}): boolean, IoError
   create: function(string, ? {string:any}): FstWriter, IoError
end

-- The IL target (add-on TealVM.Jit; in tlvm): compile one function to .NET IL on request.
global record JitError
   enum Kind
      "unsupported" "unavailable" "not_a_function"
   end
   kind: Kind
   message: string
end

global record jit
   available: boolean
   compile: function<F>(F): F, JitError
   supports: function(any): boolean, string
   auto: function(? integer): integer
end

-- First-class C#-backed collections, deep freezing, and chunked deterministic parallelism.
global record List<T>
   is {T}
   userdata

   new: function<U>(? integer): List<U>
   of: function<U>(U...): List<U>
   from: function<U>({U} | List<U>): List<U>

   push: function(self, T...): integer
   pop: function(self): T
   len: function(self): integer
   isEmpty: function(self): boolean
   get: function(self, integer): T
   set: function(self, integer, T)
   insert: function(self, integer, T)
   remove: function(self, ? integer): T
   clear: function(self)
   contains: function(self, T): boolean
   indexOf: function(self, T): integer
   slice: function(self, ? integer, ? integer): List<T>
   join: function(self, ? string): string
   reverse: function(self): List<T>
   sort: function(self, ? function(T, T): boolean): List<T>
   map: function<U>(self, function(T, integer): U): List<U>
   filter: function(self, function(T, integer): boolean): List<T>
   reduce: function<A>(self, A, function(A, T, integer): A): A
   any: function(self, function(T, integer): boolean): boolean
   all: function(self, function(T, integer): boolean): boolean
   extend: function(self, List<T> | {T}): List<T>
   clone: function(self): List<T>
   toTable: function(self): {T}
   freeze: function(self): List<T>
   isFrozen: function(self): boolean
   parallelMap: function<U>(self, function(T): U): List<U>

   metamethod __len: function(List<T>): integer
   metamethod __eq: function(List<T>, List<T>): boolean
end

global record Dict<K, V>
   userdata

   new: function<K2, V2>(? integer): Dict<K2, V2>
   from: function<K2, V2>({K2:V2} | Dict<K2, V2>): Dict<K2, V2>

   get: function(self, K): V
   getOr: function(self, K, V): V
   set: function(self, K, V): Dict<K, V>
   has: function(self, K): boolean
   delete: function(self, K): boolean
   clear: function(self)
   len: function(self): integer
   isEmpty: function(self): boolean
   keys: function(self): List<K>
   values: function(self): List<V>
   items: function(self): List<{K | V}>
   merge: function(self, Dict<K, V> | {K:V}): Dict<K, V>
   clone: function(self): Dict<K, V>
   toTable: function(self): {K:V}
   freeze: function(self): Dict<K, V>
   isFrozen: function(self): boolean

   metamethod __len: function(Dict<K, V>): integer
   metamethod __eq: function(Dict<K, V>, Dict<K, V>): boolean
end

global record parallel
   map: function<T, U>(List<T> | {T}, function(T): U): List<U>
end

global freeze: function<T>(T): T
global isFrozen: function(any): boolean
global __cameo_iter1: function(any): function(): any
global __cameo_iter2: function(any): function(): any, any
