using System.Buffers.Binary;
using System.Text;
using Cameo.Runtime;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;
using Cameo.Runtime.Syntax;

namespace Cameo.Embed;

/// <summary>
/// Compiles and lowers a Cameo source program (or compiled <see cref="Proto"/>) into a
/// flat, CRC-32C-verified, ROM/hot-loadable <c>CEMB</c> v1 image for the static C++20
/// <c>Cameo.Embed</c> VM (no heap, no units, no dynamic dictionaries, integer-only by default).
/// </summary>
public static class EmbedCompiler
{
    private static readonly HashSet<string> FloatOnlyMathFields = new(StringComparer.Ordinal)
    {
        "pi", "huge",
        "sin", "cos", "tan", "asin", "acos", "atan",
        "sinh", "cosh", "tanh", "asinh", "acosh", "atanh",
        "exp", "log", "log2", "log10", "sqrt", "cbrt", "deg", "rad",
        "isnan", "isinf", "isfinite", "isclose",
    };

    private static readonly HashSet<string> AxiWriteFields = new(StringComparer.Ordinal)
    {
        "write8", "write16", "write32", "write64", "rmw32", "burst_write32",
    };

    private static readonly HashSet<string> PtrWriteMethods = new(StringComparer.Ordinal)
    {
        "write8", "write16", "write32", "write64",
        "write8_at", "write16_at", "write32_at", "write64_at", "rmw_bits32_at",
    };

    private static readonly HashSet<string> PtrAllMethods = new(StringComparer.Ordinal)
    {
        "addr", "is_null", "is_aligned", "align_up", "add", "sub", "diff",
        "read8", "read16", "read32", "read64",
        "write8", "write16", "write32", "write64",
        "read8_at", "read16_at", "read32_at", "read64_at",
        "write8_at", "write16_at", "write32_at", "write64_at",
        "bits32_at", "rmw_bits32_at",
    };

    private static readonly HashSet<string> DisallowedEmbedGlobals = new(StringComparer.Ordinal)
    {
        "Dict", "List", "units", "SharpUnits", "safenum", "FloatArray",
        "io", "os", "fs", "aio", "db", "print", "require", "Future",
        "sandbox", "jit", "parallel", "json", "csv", "xml", "regex",
        "crypto", "utf16", "cli", "debug", "dofile", "loadfile", "load",
    };

    private sealed class CameoSourceLanguage : ISourceLanguage
    {
        public static CameoSourceLanguage Instance { get; } = new();
        public string Name => "Cameo";
        public string SourceExtension => ".cameo";
        public bool UsesTaggedResults => true;
        public Chunk Parse(string source, string fileName) => Parser.ParseCameo(source, fileName);
    }

    /// <summary>Parses, validates the embed profile, type-checks, compiles, and lowers Cameo source to a CEMB v1 image.</summary>
    public static EmbedCompileResult Compile(string source, string fileName = "program.cameo", EmbedCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new EmbedCompileOptions();

        var diagnostics = new List<EmbedDiagnostic>();

        // 1. Parse AST first to reject dynamic dictionaries/maps, List literals, and disallowed types early.
        try
        {
            Chunk parsed = Parser.ParseCameo(source, fileName);
            ValidateAstProfile(parsed, fileName, diagnostics);
        }
        catch (CameoCompileException ex)
        {
            foreach (Diagnostic d in ex.Diagnostics)
                diagnostics.Add(new("CAMEOSYNTAX", LuaText.Decode(d.Message), d.File, d.Line, d.Col));
            if (diagnostics.Count == 0)
                diagnostics.Add(new("CAMEOSYNTAX", ex.Message, fileName));
            return new(null, diagnostics);
        }

        if (diagnostics.Count > 0)
            return new(null, diagnostics);

        var engineOptions = new CameoRuntimeOptions
        {
            TypeCheck = options.TypeCheck,
            Libraries = CameoLibraries.Hermetic,
            AllowDebug = false,
            FilePolicy = FilePolicy.DenyAll,
        };

        using var engine = new ScriptEngine(engineOptions, CameoSourceLanguage.Instance);
        if (options.IncludePtrPrelude)
            engine.DeclareGlobal(EmbedPrelude.PtrDeclarationSource, "<embed-ptr-prelude>");
        if (options.IncludeAxiPrelude)
            engine.DeclareGlobal(EmbedPrelude.AxiDeclarationSource, "<embed-axi-prelude>");
        if (options.IncludeDbgPrelude)
            engine.DeclareGlobal(EmbedPrelude.DbgDeclarationSource, "<embed-dbg-prelude>");
        if (options.IncludeRtosPrelude)
            engine.DeclareGlobal(EmbedPrelude.RtosDeclarationSource, "<embed-rtos-prelude>");

        foreach (EmbedHostFunctionSpec host in options.HostFunctions)
        {
            if (string.IsNullOrWhiteSpace(host.Name))
            {
                diagnostics.Add(new("CAMEOEMB012", "Host function name must be non-empty", fileName));
                continue;
            }
            string sig = string.IsNullOrWhiteSpace(host.Signature)
                ? $"global {host.Name}: function(...: any): any"
                : host.Signature.TrimStart().StartsWith("global ", StringComparison.Ordinal)
                    ? host.Signature
                    : $"global {host.Name}: {host.Signature}";
            engine.DeclareGlobal(sig, $"<host:{host.Name}>");
        }

        if (diagnostics.Count > 0)
            return new(null, diagnostics);

        if (options.TypeCheck)
        {
            IReadOnlyList<Diagnostic> checkDiags = engine.Check(source, fileName);
            foreach (Diagnostic d in checkDiags)
            {
                if (!d.IsWarning)
                    diagnostics.Add(new("CAMEOTYPE", LuaText.Decode(d.Message), d.File, d.Line, d.Col));
            }
            if (diagnostics.Count > 0)
                return new(null, diagnostics);
        }

        Proto proto;
        try
        {
            proto = engine.Compile(source, fileName);
        }
        catch (CameoCompileException ex)
        {
            foreach (Diagnostic d in ex.Diagnostics)
                diagnostics.Add(new("CAMEOSYNTAX", LuaText.Decode(d.Message), d.File, d.Line, d.Col));
            if (diagnostics.Count == 0)
                diagnostics.Add(new("CAMEOSYNTAX", ex.Message, fileName));
            return new(null, diagnostics);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new("CAMEOEMB099", ex.Message, fileName));
            return new(null, diagnostics);
        }

        return LowerProto(proto, fileName, options);
    }

    /// <summary>Compiles a Cameo source file on disk to a CEMB v1 binary image.</summary>
    public static EmbedCompileResult CompileFile(string path, EmbedCompileOptions? options = null)
    {
        if (!File.Exists(path))
            return new(null, [new("CAMEOEMB013", $"Source file not found: {path}", path)]);
        string source = File.ReadAllText(path, Encoding.UTF8);
        return Compile(source, path, options);
    }

    /// <summary>Validates and lowers an already-compiled <see cref="Proto"/> tree to a CEMB v1 binary image.</summary>
    public static EmbedCompileResult LowerProto(Proto root, string sourceName = "program.cameo", EmbedCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        options ??= new EmbedCompileOptions();

        var diagnostics = new List<EmbedDiagnostic>();

        if (BytecodeValidator.Validate(root) is { } validationError)
        {
            diagnostics.Add(new("CAMEOEMB000", $"Bytecode structural validation failed: {validationError}", sourceName));
            return new(null, diagnostics);
        }

        // Build custom host binding table
        var customHosts = new Dictionary<string, (ushort Id, byte Arity)>(StringComparer.Ordinal);
        var usedIds = new HashSet<ushort>();
        foreach (EmbedHostImport std in EmbedPrelude.StandardSyscalls)
            usedIds.Add(std.HostId);

        ushort nextCustomId = (ushort)EmbedStandardHostId.CustomBase;
        foreach (EmbedHostFunctionSpec spec in options.HostFunctions)
        {
            if (string.IsNullOrWhiteSpace(spec.Name))
            {
                diagnostics.Add(new("CAMEOEMB012", "Host function name must be non-empty", sourceName));
                continue;
            }
            if (spec.Name is "math" or "rtos" or "axi" or "dbg" or "ptr" or "Ptr")
            {
                diagnostics.Add(new("CAMEOEMB012", $"Host function name '{spec.Name}' collides with a built-in Cameo.Embed namespace or constructor", sourceName));
                continue;
            }
            ushort id;
            if (spec.ExplicitId is { } explicitId)
            {
                if (!usedIds.Add(explicitId))
                {
                    diagnostics.Add(new("CAMEOEMB012", $"Duplicate host syscall ID {explicitId} for '{spec.Name}'", sourceName));
                    continue;
                }
                id = explicitId;
            }
            else
            {
                while (usedIds.Contains(nextCustomId))
                    nextCustomId++;
                id = nextCustomId++;
                usedIds.Add(id);
            }
            if (!customHosts.TryAdd(spec.Name, (id, spec.Arity)))
                diagnostics.Add(new("CAMEOEMB012", $"Duplicate host function declaration '{spec.Name}'", sourceName));
        }
        if (diagnostics.Count > 0)
            return new(null, diagnostics);

        // Flatten prototypes in deterministic pre-order (root is always proto 0)
        var protos = new List<Proto>();
        var protoIndex = new Dictionary<Proto, int>(ReferenceEqualityComparer.Instance);
        void CollectProtos(Proto p)
        {
            protoIndex[p] = protos.Count;
            protos.Add(p);
            foreach (Proto child in p.Protos)
                CollectProtos(child);
        }
        CollectProtos(root);

        if (protos.Count > ushort.MaxValue)
        {
            diagnostics.Add(new("CAMEOEMB011", $"Too many function prototypes ({protos.Count} > {ushort.MaxValue})", sourceName));
            return new(null, diagnostics);
        }

        // Collect all script-defined globals (any global written via SetGlobal)
        var scriptGlobals = new List<string>();
        var scriptGlobalSlot = new Dictionary<string, ushort>(StringComparer.Ordinal);
        foreach (Proto p in protos)
        {
            for (int pc = 0; pc < p.Code.Length; pc++)
            {
                Instruction ins = p.At(pc);
                if (ins.Op == OpCode.SetGlobal)
                {
                    string name = LuaText.Decode(p.K[ins.B].AsString);
                    if (name is "math" or "rtos" or "axi" or "dbg" or "ptr" or "Ptr" || customHosts.ContainsKey(name))
                    {
                        diagnostics.Add(new("CAMEOEMB008",
                            $"Cannot assign to reserved namespace or host binding '{name}'",
                            sourceName, p.LineOf(pc)));
                        continue;
                    }
                    if (!scriptGlobalSlot.ContainsKey(name))
                    {
                        if (scriptGlobals.Count >= short.MaxValue)
                        {
                            diagnostics.Add(new("CAMEOEMB011", "Too many script globals for static Cameo.Embed profile", sourceName, p.LineOf(pc)));
                            continue;
                        }
                        scriptGlobalSlot[name] = (ushort)scriptGlobals.Count;
                        scriptGlobals.Add(name);
                    }
                }
            }
        }

        // String pool (interned UTF-8 bytes)
        var stringList = new List<byte[]>();
        var stringIds = new Dictionary<string, ushort>(StringComparer.Ordinal);
        ushort InternString(string s)
        {
            if (stringIds.TryGetValue(s, out ushort existing))
                return existing;
            byte[] utf8 = Encoding.UTF8.GetBytes(s);
            ushort id = (ushort)stringList.Count;
            stringList.Add(utf8);
            stringIds[s] = id;
            return id;
        }
        ushort InternVmString(string vmRaw)
        {
            string decoded = LuaText.Decode(vmRaw);
            if (stringIds.TryGetValue(decoded, out ushort existing))
                return existing;
            byte[] raw = new byte[vmRaw.Length];
            for (int i = 0; i < vmRaw.Length; i++)
                raw[i] = (byte)vmRaw[i];
            ushort id = (ushort)stringList.Count;
            stringList.Add(raw);
            stringIds[decoded] = id;
            return id;
        }

        var flags = EmbedImageFlags.None;
        var flatProtos = new List<LoweredProto>(protos.Count);
        var protoDebugMaps = new List<EmbedProtoDebugMap>(protos.Count);
        var flatConsts = new List<LoweredConst>();
        var flatCode = new List<LoweredInstr>();
        var flatUpvals = new List<LoweredUpval>();
        var importedHosts = new SortedDictionary<ushort, EmbedHostImport>();

        int reqBoxes = 0;
        int reqClosures = 1; // root chunk closure
        int reqRecords = 0;
        int reqRecordSlots = 0;
        int maxProtoStack = 0;
        int secondMaxProtoStack = 0;

        for (int pid = 0; pid < protos.Count; pid++)
        {
            Proto p = protos[pid];
            if (p.NumParams > byte.MaxValue || p.MaxStack > byte.MaxValue)
            {
                diagnostics.Add(new("CAMEOEMB011",
                    $"Function '{p.Name}' exceeds static register/parameter limit (params={p.NumParams}, maxStack={p.MaxStack}, limit=255)",
                    sourceName, p.LineDefined));
                continue;
            }
            if (p.IsVarArg && pid != 0)
            {
                diagnostics.Add(new("CAMEOEMB003",
                    $"Variadic function '{p.Name}' is not allowed in Cameo.Embed",
                    sourceName, p.LineDefined));
                continue;
            }

            if (p.MaxStack >= maxProtoStack)
            {
                secondMaxProtoStack = maxProtoStack;
                maxProtoStack = p.MaxStack;
            }
            else if (p.MaxStack > secondMaxProtoStack)
            {
                secondMaxProtoStack = p.MaxStack;
            }

            int constOffset = flatConsts.Count;
            var localConsts = new List<LoweredConst>(p.K.Length);
            for (int ki = 0; ki < p.K.Length; ki++)
            {
                Value kv = p.K[ki];
                switch (kv.Kind)
                {
                    case ValueKind.Nil:
                        localConsts.Add(new(EmbedConstKind.Nil, 0));
                        break;
                    case ValueKind.Boolean:
                        localConsts.Add(new(EmbedConstKind.Bool, kv.I != 0 ? 1UL : 0UL));
                        break;
                    case ValueKind.Integer:
                        localConsts.Add(new(EmbedConstKind.Int, unchecked((ulong)kv.I)));
                        break;
                    case ValueKind.Float:
                        if (!options.AllowFloats)
                        {
                            diagnostics.Add(new("CAMEOEMB014",
                                $"Floating-point constant in '{p.Name}' rejected in integer-only mode (pass --floats / AllowFloats=true to enable)",
                                sourceName, p.LineDefined));
                        }
                        flags |= EmbedImageFlags.HasFloat;
                        localConsts.Add(new(EmbedConstKind.Float, unchecked((ulong)BitConverter.DoubleToInt64Bits(kv.N))));
                        break;
                    case ValueKind.String:
                        ushort sid = InternVmString(kv.AsString);
                        localConsts.Add(new(EmbedConstKind.String, sid));
                        break;
                    default:
                        diagnostics.Add(new("CAMEOEMB015",
                            $"Unsupported constant kind '{kv.Kind}' in '{p.Name}'",
                            sourceName, p.LineDefined));
                        break;
                }
            }

            int AddIntConst(long val)
            {
                int idx = localConsts.Count;
                localConsts.Add(new(EmbedConstKind.Int, unchecked((ulong)val)));
                return idx;
            }

            int upvalOffset = flatUpvals.Count;
            if (p.Upvals.Length > 0)
            {
                if (!options.AllowClosures)
                {
                    diagnostics.Add(new("CAMEOEMB009",
                        $"Closure captures in '{p.Name}' rejected because AllowClosures=false",
                        sourceName, p.LineDefined));
                }
                flags |= EmbedImageFlags.HasClosures;
                foreach (UpvalDesc u in p.Upvals)
                {
                    if (u.Index < 0 || u.Index > byte.MaxValue)
                        diagnostics.Add(new("CAMEOEMB011", $"Upvalue index {u.Index} out of static range", sourceName, p.LineDefined));
                    flatUpvals.Add(new(u.FromParentRegister ? (byte)1 : (byte)0, (byte)u.Index));
                }
            }

            int codeOffset = flatCode.Count;
            for (int pc = 0; pc < p.Code.Length; pc++)
            {
                Instruction ins = p.At(pc);
                int line = p.LineOf(pc);

                if (ins.Op != OpCode.LoadInt &&
                    (!FitsInt16(ins.A) || !FitsInt16(ins.B) || !FitsInt16(ins.C)))
                {
                    diagnostics.Add(new("CAMEOEMB011",
                        $"Instruction {ins} at pc {pc} exceeds 16-bit operand range",
                        sourceName, line));
                    continue;
                }

                switch (ins.Op)
                {
                    case OpCode.Move: flatCode.Add(new(EmbedOpCode.Move, ins.A, ins.B, ins.C)); break;
                    case OpCode.LoadK: flatCode.Add(new(EmbedOpCode.LoadK, ins.A, ins.B, 0)); break;
                    case OpCode.LoadNil: flatCode.Add(new(EmbedOpCode.LoadNil, ins.A, ins.B, 0)); break;
                    case OpCode.LoadBool: flatCode.Add(new(EmbedOpCode.LoadBool, ins.A, ins.B, ins.C)); break;
                    case OpCode.LoadInt:
                        if (FitsInt16(ins.B))
                        {
                            flatCode.Add(new(EmbedOpCode.LoadInt, ins.A, ins.B, 0));
                        }
                        else
                        {
                            int kIdx = AddIntConst(ins.B);
                            if (!FitsInt16(kIdx))
                                diagnostics.Add(new("CAMEOEMB011", "Constant pool overflow promoting LoadInt", sourceName, line));
                            else
                                flatCode.Add(new(EmbedOpCode.LoadK, ins.A, kIdx, 0));
                        }
                        break;

                    case OpCode.GetUpval:
                        if (!options.AllowClosures)
                            diagnostics.Add(new("CAMEOEMB009", "Upvalues are disabled in this Cameo.Embed profile", sourceName, line));
                        flags |= EmbedImageFlags.HasClosures;
                        flatCode.Add(new(EmbedOpCode.GetUpval, ins.A, ins.B, 0));
                        break;
                    case OpCode.SetUpval:
                        if (!options.AllowClosures)
                            diagnostics.Add(new("CAMEOEMB009", "Upvalues are disabled in this Cameo.Embed profile", sourceName, line));
                        flags |= EmbedImageFlags.HasClosures;
                        flatCode.Add(new(EmbedOpCode.SetUpval, ins.A, ins.B, 0));
                        break;
                    case OpCode.NewBox:
                        if (!options.AllowClosures)
                            diagnostics.Add(new("CAMEOEMB009", "Captured locals (NewBox) are disabled in this Cameo.Embed profile", sourceName, line));
                        else if (options.BootOnlyClosures && pid != 0)
                            diagnostics.Add(new("CAMEOEMB010", $"Function '{p.Name}' allocates a capture box outside boot initialization", sourceName, line));
                        flags |= EmbedImageFlags.HasClosures;
                        reqBoxes++;
                        flatCode.Add(new(EmbedOpCode.NewBox, ins.A, 0, 0));
                        break;
                    case OpCode.GetBox:
                        flatCode.Add(new(EmbedOpCode.GetBox, ins.A, ins.B, 0));
                        break;
                    case OpCode.SetBox:
                        flatCode.Add(new(EmbedOpCode.SetBox, ins.A, ins.B, 0));
                        break;

                    case OpCode.GetGlobal:
                    {
                        string gname = LuaText.Decode(p.K[ins.B].AsString);
                        string? nextField = PeekNextGetField(p, pc, ins.A);
                        if (gname == "math")
                        {
                            if (!options.AllowMath)
                                diagnostics.Add(new("CAMEOEMB016", "Global 'math' is disabled because AllowMath=false", sourceName, line));
                            if (nextField is not null && !options.AllowFloats && FloatOnlyMathFields.Contains(nextField))
                            {
                                diagnostics.Add(new("CAMEOEMB014",
                                    $"Floating-point math member 'math.{nextField}' rejected in integer-only mode (pass --floats to enable)",
                                    sourceName, line));
                            }
                            flags |= EmbedImageFlags.HasMath;
                            flatCode.Add(new(EmbedOpCode.LoadNamespace, ins.A, (int)EmbedNamespaceId.Math, 0));
                        }
                        else if (gname == "ptr")
                        {
                            if (!options.IncludePtrPrelude)
                                diagnostics.Add(new("CAMEOEMB001", "Global 'ptr' is not enabled in this Cameo.Embed profile", sourceName, line));
                            flags |= EmbedImageFlags.HasPointers;
                            flatCode.Add(new(EmbedOpCode.LoadBuiltin, ins.A, (int)EmbedBuiltinId.PtrOf, 0));
                        }
                        else if (gname == "Ptr")
                        {
                            if (!options.IncludePtrPrelude)
                                diagnostics.Add(new("CAMEOEMB001", "Global 'Ptr' is not enabled in this Cameo.Embed profile", sourceName, line));
                            flags |= EmbedImageFlags.HasPointers;
                            flatCode.Add(new(EmbedOpCode.LoadNamespace, ins.A, (int)EmbedNamespaceId.Ptr, 0));
                        }
                        else if (gname == "axi")
                        {
                            if (!options.IncludeAxiPrelude)
                                diagnostics.Add(new("CAMEOEMB001", "Global 'axi' is not enabled in this Cameo.Embed profile", sourceName, line));
                            flags |= EmbedImageFlags.HasAxi;
                            if (nextField is not null && AxiWriteFields.Contains(nextField))
                            {
                                flags |= EmbedImageFlags.HasMemWrite;
                                if (!options.AllowMemWrites)
                                {
                                    diagnostics.Add(new("CAMEOEMB019",
                                        $"AXI write operation 'axi.{nextField}' rejected because AllowMemWrites=false (--read-only)",
                                        sourceName, line));
                                }
                            }
                            flatCode.Add(new(EmbedOpCode.LoadNamespace, ins.A, (int)EmbedNamespaceId.Axi, 0));
                        }
                        else if (gname == "dbg")
                        {
                            if (!options.IncludeDbgPrelude)
                                diagnostics.Add(new("CAMEOEMB001", "Global 'dbg' is not enabled in this Cameo.Embed profile", sourceName, line));
                            flags |= EmbedImageFlags.HasDbg;
                            if (nextField is not null)
                                TrackStandardHost(importedHosts, "dbg." + nextField);
                            flatCode.Add(new(EmbedOpCode.LoadNamespace, ins.A, (int)EmbedNamespaceId.Dbg, 0));
                        }
                        else if (gname == "rtos")
                        {
                            if (!options.IncludeRtosPrelude)
                                diagnostics.Add(new("CAMEOEMB001", "Global 'rtos' is not enabled in this Cameo.Embed profile", sourceName, line));
                            flags |= EmbedImageFlags.HasRtosSyscalls;
                            if (nextField is not null)
                                TrackStandardHost(importedHosts, "rtos." + nextField);
                            flatCode.Add(new(EmbedOpCode.LoadNamespace, ins.A, (int)EmbedNamespaceId.Rtos, 0));
                        }
                        else if (customHosts.TryGetValue(gname, out var custom))
                        {
                            importedHosts[custom.Id] = new EmbedHostImport(custom.Id, gname, custom.Arity);
                            flatCode.Add(new(EmbedOpCode.LoadHostFn, ins.A, custom.Id, 0));
                        }
                        else if (scriptGlobalSlot.TryGetValue(gname, out ushort slot))
                        {
                            flatCode.Add(new(EmbedOpCode.GetGlobalSlot, ins.A, slot, 0));
                        }
                        else
                        {
                            string hint = gname switch
                            {
                                "units" or "SharpUnits" => $"Global '{gname}' (physical units) is excluded from Cameo.Embed; use integer/scaled values and dbg/axi helpers",
                                "Dict" => "Global 'Dict' (dynamic hash dictionary) is excluded from Cameo.Embed; use fixed braced records or integer-indexed arrays",
                                "List" => "Global 'List' requires dynamic heap allocation and is excluded from Cameo.Embed; use fixed array literals { ... }",
                                "safenum" or "FloatArray" => $"Global '{gname}' is excluded from Cameo.Embed; use native 64-bit integers and static scratch buffers",
                                _ => $"Global '{gname}' is not available in the Cameo.Embed static profile (declare it as a host function or avoid host-OS libraries)",
                            };
                            diagnostics.Add(new("CAMEOEMB001", hint, sourceName, line));
                        }
                        break;
                    }
                    case OpCode.SetGlobal:
                    {
                        string gname = LuaText.Decode(p.K[ins.B].AsString);
                        if (scriptGlobalSlot.TryGetValue(gname, out ushort slot))
                            flatCode.Add(new(EmbedOpCode.SetGlobalSlot, ins.A, slot, 0));
                        break;
                    }

                    case OpCode.NewTable:
                        if (!options.AllowRecordsAndArrays)
                            diagnostics.Add(new("CAMEOEMB017", $"Record/array allocation in '{p.Name}' rejected because AllowRecordsAndArrays=false", sourceName, line));
                        flags |= EmbedImageFlags.HasRecordsOrArrays;
                        reqRecords++;
                        reqRecordSlots += Math.Max(4, ins.B + ins.C);
                        flatCode.Add(new(EmbedOpCode.NewTable, ins.A, ins.B, ins.C));
                        break;
                    case OpCode.GetTable:
                        flatCode.Add(new(EmbedOpCode.GetTable, ins.A, ins.B, ins.C));
                        break;
                    case OpCode.GetField:
                        flatCode.Add(new(EmbedOpCode.GetField, ins.A, ins.B, ins.C));
                        break;
                    case OpCode.SetTable:
                        if (!options.AllowRecordsAndArrays)
                            diagnostics.Add(new("CAMEOEMB017", $"Array mutation in '{p.Name}' rejected because AllowRecordsAndArrays=false", sourceName, line));
                        if (ins.B < 0)
                        {
                            Value keyConst = p.K[~ins.B];
                            if (keyConst.Kind != ValueKind.Integer)
                            {
                                diagnostics.Add(new("CAMEOEMB018",
                                    "Dynamic non-integer dictionary key assignment is disallowed in Cameo.Embed; use fixed braced records or integer-indexed arrays",
                                    sourceName, line));
                            }
                        }
                        flags |= EmbedImageFlags.HasRecordsOrArrays;
                        flatCode.Add(new(EmbedOpCode.SetTable, ins.A, ins.B, ins.C));
                        break;
                    case OpCode.SetField:
                        if (!options.AllowRecordsAndArrays)
                            diagnostics.Add(new("CAMEOEMB017", $"Record field mutation in '{p.Name}' rejected because AllowRecordsAndArrays=false", sourceName, line));
                        flags |= EmbedImageFlags.HasRecordsOrArrays;
                        flatCode.Add(new(EmbedOpCode.SetField, ins.A, ins.B, ins.C));
                        break;
                    case OpCode.Self:
                    {
                        if (ins.C >= 0 && ins.C < p.K.Length && p.K[ins.C].Kind == ValueKind.String)
                        {
                            string mname = LuaText.Decode(p.K[ins.C].AsString);
                            if (PtrAllMethods.Contains(mname))
                                flags |= EmbedImageFlags.HasPointers;
                            if (PtrWriteMethods.Contains(mname))
                            {
                                flags |= EmbedImageFlags.HasMemWrite;
                                if (!options.AllowMemWrites)
                                {
                                    diagnostics.Add(new("CAMEOEMB019",
                                        $"Pointer write method '.{mname}()' rejected because AllowMemWrites=false (--read-only)",
                                        sourceName, line));
                                }
                            }
                        }
                        flatCode.Add(new(EmbedOpCode.Self, ins.A, ins.B, ins.C));
                        break;
                    }
                    case OpCode.SetList:
                        if (!options.AllowRecordsAndArrays)
                            diagnostics.Add(new("CAMEOEMB017", $"Array literal in '{p.Name}' rejected because AllowRecordsAndArrays=false", sourceName, line));
                        flags |= EmbedImageFlags.HasRecordsOrArrays;
                        reqRecordSlots += ins.B > 0 ? ins.B : 4;
                        flatCode.Add(new(EmbedOpCode.SetList, ins.A, ins.B, ins.C));
                        break;

                    case OpCode.Add: flatCode.Add(new(EmbedOpCode.Add, ins.A, ins.B, ins.C)); break;
                    case OpCode.Sub: flatCode.Add(new(EmbedOpCode.Sub, ins.A, ins.B, ins.C)); break;
                    case OpCode.Mul: flatCode.Add(new(EmbedOpCode.Mul, ins.A, ins.B, ins.C)); break;
                    case OpCode.Div:
                        if (!options.AllowFloats)
                            diagnostics.Add(new("CAMEOEMB014", "Floating-point division '/' rejected in integer-only mode (use '//' for integer floor division or pass --floats)", sourceName, line));
                        flags |= EmbedImageFlags.HasFloat;
                        flatCode.Add(new(EmbedOpCode.Div, ins.A, ins.B, ins.C));
                        break;
                    case OpCode.Mod: flatCode.Add(new(EmbedOpCode.Mod, ins.A, ins.B, ins.C)); break;
                    case OpCode.Pow:
                        if (!options.AllowFloats)
                            diagnostics.Add(new("CAMEOEMB014", "Power operator '^' rejected in integer-only mode (pass --floats to enable)", sourceName, line));
                        flags |= EmbedImageFlags.HasFloat | EmbedImageFlags.HasMath;
                        flatCode.Add(new(EmbedOpCode.Pow, ins.A, ins.B, ins.C));
                        break;
                    case OpCode.IDiv: flatCode.Add(new(EmbedOpCode.IDiv, ins.A, ins.B, ins.C)); break;
                    case OpCode.BAnd: flatCode.Add(new(EmbedOpCode.BAnd, ins.A, ins.B, ins.C)); break;
                    case OpCode.BOr: flatCode.Add(new(EmbedOpCode.BOr, ins.A, ins.B, ins.C)); break;
                    case OpCode.BXor: flatCode.Add(new(EmbedOpCode.BXor, ins.A, ins.B, ins.C)); break;
                    case OpCode.Shl: flatCode.Add(new(EmbedOpCode.Shl, ins.A, ins.B, ins.C)); break;
                    case OpCode.Shr: flatCode.Add(new(EmbedOpCode.Shr, ins.A, ins.B, ins.C)); break;
                    case OpCode.Unm: flatCode.Add(new(EmbedOpCode.Unm, ins.A, ins.B, 0)); break;
                    case OpCode.Not: flatCode.Add(new(EmbedOpCode.Not, ins.A, ins.B, 0)); break;
                    case OpCode.Len: flatCode.Add(new(EmbedOpCode.Len, ins.A, ins.B, 0)); break;
                    case OpCode.BNot: flatCode.Add(new(EmbedOpCode.BNot, ins.A, ins.B, 0)); break;

                    case OpCode.Jmp: flatCode.Add(new(EmbedOpCode.Jmp, 0, ins.B, 0)); break;
                    case OpCode.JmpIf: flatCode.Add(new(EmbedOpCode.JmpIf, ins.A, ins.B, 0)); break;
                    case OpCode.JmpIfNot: flatCode.Add(new(EmbedOpCode.JmpIfNot, ins.A, ins.B, 0)); break;
                    case OpCode.Eq: flatCode.Add(new(EmbedOpCode.Eq, ins.A, ins.B, ins.C)); break;
                    case OpCode.Lt: flatCode.Add(new(EmbedOpCode.Lt, ins.A, ins.B, ins.C)); break;
                    case OpCode.Le: flatCode.Add(new(EmbedOpCode.Le, ins.A, ins.B, ins.C)); break;

                    case OpCode.Call: flatCode.Add(new(EmbedOpCode.Call, ins.A, ins.B, ins.C)); break;
                    case OpCode.TailCall: flatCode.Add(new(EmbedOpCode.TailCall, ins.A, ins.B, 0)); break;
                    case OpCode.Return: flatCode.Add(new(EmbedOpCode.Return, ins.A, ins.B, 0)); break;
                    case OpCode.ForPrep: flatCode.Add(new(EmbedOpCode.ForPrep, ins.A, ins.B, 0)); break;
                    case OpCode.ForLoop: flatCode.Add(new(EmbedOpCode.ForLoop, ins.A, ins.B, 0)); break;

                    case OpCode.Closure:
                    {
                        Proto child = p.Protos[ins.B];
                        int childId = protoIndex[child];
                        if (child.Upvals.Length > 0 && !options.AllowClosures)
                            diagnostics.Add(new("CAMEOEMB009", $"Closure '{child.Name}' requires captures, which are disabled", sourceName, line));
                        if (options.BootOnlyClosures && pid != 0)
                            diagnostics.Add(new("CAMEOEMB010", $"Function '{p.Name}' creates a closure outside boot initialization", sourceName, line));
                        if (child.Upvals.Length > 0)
                            flags |= EmbedImageFlags.HasClosures;
                        reqClosures++;
                        flatCode.Add(new(EmbedOpCode.Closure, ins.A, childId, 0));
                        break;
                    }

                    case OpCode.IsKind:
                        flatCode.Add(new(EmbedOpCode.IsKind, ins.A, ins.B, ins.C));
                        break;

                    case OpCode.MathFn:
                        if (!options.AllowFloats || !options.AllowMath)
                            diagnostics.Add(new("CAMEOEMB016", "Transcendental math intrinsic rejected by Cameo.Embed profile (requires --floats and AllowMath=true)", sourceName, line));
                        flags |= EmbedImageFlags.HasFloat | EmbedImageFlags.HasMath;
                        flatCode.Add(new(EmbedOpCode.MathFn, ins.A, ins.B, ins.C));
                        break;

                    case OpCode.Concat:
                        diagnostics.Add(new("CAMEOEMB002",
                            "Dynamic string concatenation ('..' / interpolation) allocates heap memory and is disallowed in Cameo.Embed; use rtos.log / dbg.log_hex",
                            sourceName, line));
                        break;
                    case OpCode.VarArg:
                        diagnostics.Add(new("CAMEOEMB003",
                            "Dynamic varargs ('...') are disallowed in Cameo.Embed",
                            sourceName, line));
                        break;
                    case OpCode.TForCall:
                    case OpCode.TForLoop:
                        diagnostics.Add(new("CAMEOEMB004",
                            "Generic iterators ('for x in ...') are disallowed in Cameo.Embed; use numeric 'for i = 1, n' loops",
                            sourceName, line));
                        break;
                    case OpCode.Tbc:
                    case OpCode.Close:
                        diagnostics.Add(new("CAMEOEMB005",
                            "To-be-closed resources ('using' / '<close>') are disallowed in Cameo.Embed",
                            sourceName, line));
                        break;
                    case OpCode.PrepareDotCall:
                        diagnostics.Add(new("CAMEOEMB006",
                            "Unchecked dynamic dot-call dispatch is disallowed in Cameo.Embed; annotate receiver types",
                            sourceName, line));
                        break;
                    default:
                        diagnostics.Add(new("CAMEOEMB007",
                            $"Opcode '{ins.Op}' is not supported by the Cameo.Embed static VM",
                            sourceName, line));
                        break;
                }
            }

            flatConsts.AddRange(localConsts);
            ushort nameStrId = string.IsNullOrEmpty(p.Name) || p.Name == "?"
                ? (ushort)0xFFFF
                : InternString(p.Name);

            int emittedCount = flatCode.Count - codeOffset;
            var pcToLine = new int[emittedCount];
            for (int k = 0; k < emittedCount; k++)
                pcToLine[k] = k < p.Lines.Length ? p.Lines[k] : p.LineDefined;
            protoDebugMaps.Add(new EmbedProtoDebugMap(
                pid,
                string.IsNullOrEmpty(p.Name) || p.Name == "?" ? (pid == 0 ? "<main>" : $"proto_{pid}") : p.Name,
                pcToLine));

            flatProtos.Add(new LoweredProto(
                CodeOffset: (uint)codeOffset,
                CodeCount: (uint)emittedCount,
                ConstOffset: (uint)constOffset,
                ConstCount: (ushort)localConsts.Count,
                UpvalOffset: (ushort)upvalOffset,
                UpvalCount: (ushort)p.Upvals.Length,
                NameStringId: nameStrId,
                NumParams: (byte)p.NumParams,
                MaxStack: (byte)p.MaxStack,
                Flags: p.IsCameoMethod ? (byte)1 : (byte)0));
        }

        if (diagnostics.Count > 0)
            return new(null, diagnostics);

        // Intern global & host names
        var globalDescs = new List<(ushort NameStrId, ushort Slot)>(scriptGlobals.Count);
        for (int gi = 0; gi < scriptGlobals.Count; gi++)
            globalDescs.Add((InternString(scriptGlobals[gi]), (ushort)gi));

        var hostList = importedHosts.Values.ToList();
        var hostDescs = new List<(ushort HostId, ushort NameStrId, byte Arity)>(hostList.Count);
        foreach (EmbedHostImport hi in hostList)
            hostDescs.Add((hi.HostId, InternString(hi.Name), hi.Arity));

        // Build contiguous NUL-terminated string data blob
        var stringOffsets = new uint[stringList.Count];
        var stringHashes = new uint[stringList.Count];
        using var stringBlob = new MemoryStream();
        for (int si = 0; si < stringList.Count; si++)
        {
            stringOffsets[si] = (uint)stringBlob.Length;
            byte[] bytes = stringList[si];
            stringHashes[si] = EmbedBinaryFormat.Fnv1a32(bytes);
            stringBlob.Write(bytes);
            stringBlob.WriteByte(0); // NUL terminator for zero-copy C/NexRT Logger interop
        }
        byte[] stringData = stringBlob.ToArray();

        static int Align8(int v) => (v + 7) & ~7;

        int protosOffset = EmbedBinaryFormat.HeaderSize;
        int constsOffset = Align8(protosOffset + flatProtos.Count * EmbedBinaryFormat.ProtoDescSize);
        int codeOffsetBytes = Align8(constsOffset + flatConsts.Count * EmbedBinaryFormat.ConstDescSize);
        int upvalsOffset = Align8(codeOffsetBytes + flatCode.Count * EmbedBinaryFormat.InstructionSize);
        int globalsOffset = Align8(upvalsOffset + flatUpvals.Count * EmbedBinaryFormat.UpvalDescSize);
        int hostsOffset = Align8(globalsOffset + globalDescs.Count * EmbedBinaryFormat.GlobalDescSize);
        int stringsOffset = Align8(hostsOffset + hostDescs.Count * EmbedBinaryFormat.HostImportDescSize);
        int stringDataOffset = Align8(stringsOffset + stringList.Count * EmbedBinaryFormat.StringDescSize);
        int totalSize = Align8(stringDataOffset + stringData.Length);

        int reqStackSlots = Math.Max(32, maxProtoStack + secondMaxProtoStack + 16);
        int reqFrames = Math.Clamp(flatProtos.Count + 2, 4, 64);

        byte[] image = new byte[totalSize];
        Span<byte> buf = image;

        // Write 80-byte CEMB header
        BinaryPrimitives.WriteUInt32LittleEndian(buf[0..4], EmbedBinaryFormat.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[4..6], EmbedBinaryFormat.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[6..8], (ushort)flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[8..12], (uint)totalSize);
        // CRC-32C sealed below at offset 12..16
        BinaryPrimitives.WriteUInt16LittleEndian(buf[16..18], (ushort)flatProtos.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[18..20], (ushort)flatConsts.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[20..24], (uint)flatCode.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[24..26], (ushort)flatUpvals.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[26..28], (ushort)globalDescs.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[28..30], (ushort)hostDescs.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[30..32], (ushort)stringList.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[32..34], (ushort)Math.Min(ushort.MaxValue, reqStackSlots));
        BinaryPrimitives.WriteUInt16LittleEndian(buf[34..36], (ushort)reqFrames);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[36..38], (ushort)Math.Min(ushort.MaxValue, reqBoxes));
        BinaryPrimitives.WriteUInt16LittleEndian(buf[38..40], (ushort)Math.Min(ushort.MaxValue, reqClosures));
        BinaryPrimitives.WriteUInt16LittleEndian(buf[40..42], (ushort)Math.Min(ushort.MaxValue, reqRecords));
        BinaryPrimitives.WriteUInt16LittleEndian(buf[42..44], (ushort)Math.Min(ushort.MaxValue, reqRecordSlots));
        BinaryPrimitives.WriteUInt32LittleEndian(buf[44..48], (uint)protosOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[48..52], (uint)constsOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[52..56], (uint)codeOffsetBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[56..60], (uint)upvalsOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[60..64], (uint)globalsOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[64..68], (uint)hostsOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[68..72], (uint)stringsOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[72..76], (uint)stringDataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[76..80], (uint)stringData.Length);

        // Write ProtoDesc[]
        int pos = protosOffset;
        foreach (LoweredProto lp in flatProtos)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf[pos..(pos + 4)], lp.CodeOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(buf[(pos + 4)..(pos + 8)], lp.CodeCount);
            BinaryPrimitives.WriteUInt32LittleEndian(buf[(pos + 8)..(pos + 12)], lp.ConstOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(buf[(pos + 12)..(pos + 14)], lp.ConstCount);
            BinaryPrimitives.WriteUInt16LittleEndian(buf[(pos + 14)..(pos + 16)], lp.UpvalOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(buf[(pos + 16)..(pos + 18)], lp.UpvalCount);
            BinaryPrimitives.WriteUInt16LittleEndian(buf[(pos + 18)..(pos + 20)], lp.NameStringId);
            buf[pos + 20] = lp.NumParams;
            buf[pos + 21] = lp.MaxStack;
            buf[pos + 22] = lp.Flags;
            buf[pos + 23] = 0;
            pos += EmbedBinaryFormat.ProtoDescSize;
        }

        // Write ConstDesc[]
        pos = constsOffset;
        foreach (LoweredConst lc in flatConsts)
        {
            buf[pos] = (byte)lc.Kind;
            BinaryPrimitives.WriteUInt64LittleEndian(buf[(pos + 8)..(pos + 16)], lc.Payload);
            pos += EmbedBinaryFormat.ConstDescSize;
        }

        // Write Instr[]
        pos = codeOffsetBytes;
        foreach (LoweredInstr li in flatCode)
        {
            buf[pos] = (byte)li.Op;
            buf[pos + 1] = 0;
            BinaryPrimitives.WriteInt16LittleEndian(buf[(pos + 2)..(pos + 4)], (short)li.A);
            BinaryPrimitives.WriteInt16LittleEndian(buf[(pos + 4)..(pos + 6)], (short)li.B);
            BinaryPrimitives.WriteInt16LittleEndian(buf[(pos + 6)..(pos + 8)], (short)li.C);
            pos += EmbedBinaryFormat.InstructionSize;
        }

        // Write UpvalDesc[]
        pos = upvalsOffset;
        foreach (LoweredUpval lu in flatUpvals)
        {
            buf[pos] = lu.FromParentReg;
            buf[pos + 1] = lu.Index;
            pos += EmbedBinaryFormat.UpvalDescSize;
        }

        // Write GlobalDesc[]
        pos = globalsOffset;
        foreach (var (nameStrId, slot) in globalDescs)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf[pos..(pos + 2)], nameStrId);
            BinaryPrimitives.WriteUInt16LittleEndian(buf[(pos + 2)..(pos + 4)], slot);
            pos += EmbedBinaryFormat.GlobalDescSize;
        }

        // Write HostImportDesc[]
        pos = hostsOffset;
        foreach (var (hostId, nameStrId, arity) in hostDescs)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf[pos..(pos + 2)], hostId);
            BinaryPrimitives.WriteUInt16LittleEndian(buf[(pos + 2)..(pos + 4)], nameStrId);
            buf[pos + 4] = arity;
            pos += EmbedBinaryFormat.HostImportDescSize;
        }

        // Write StringDesc[]
        pos = stringsOffset;
        for (int si = 0; si < stringList.Count; si++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf[pos..(pos + 4)], stringOffsets[si]);
            BinaryPrimitives.WriteUInt32LittleEndian(buf[(pos + 4)..(pos + 8)], (uint)stringList[si].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(buf[(pos + 8)..(pos + 12)], stringHashes[si]);
            pos += EmbedBinaryFormat.StringDescSize;
        }

        // Write raw string bytes
        stringData.CopyTo(buf[stringDataOffset..]);

        // Seal header with Castagnoli CRC-32C over bytes [16 .. totalSize)
        uint crc = EmbedBinaryFormat.Crc32C(buf[16..]);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[12..16], crc);

        var report = new EmbedProgramReport(
            ImageBytes: totalSize,
            Crc32C: crc,
            Flags: flags,
            ProtoCount: flatProtos.Count,
            InstructionCount: flatCode.Count,
            ConstantCount: flatConsts.Count,
            StringCount: stringList.Count,
            GlobalCount: scriptGlobals.Count,
            RequiredStackSlots: reqStackSlots,
            RequiredFrames: reqFrames,
            RequiredBoxes: reqBoxes,
            RequiredClosures: reqClosures,
            RequiredRecords: reqRecords,
            RequiredRecordSlots: reqRecordSlots,
            Globals: scriptGlobals,
            HostImports: hostList);

        var debugMap = new EmbedDebugMap(sourceName, crc, protoDebugMaps);
        return new(new EmbedCompiledImage(image, report, sourceName, debugMap), []);
    }

    private static string? PeekNextGetField(Proto p, int pc, int regA)
    {
        if (pc + 1 >= p.Code.Length) return null;
        Instruction next = p.At(pc + 1);
        if (next.Op == OpCode.GetField && next.B == regA && next.C >= 0 && next.C < p.K.Length && p.K[next.C].Kind == ValueKind.String)
            return LuaText.Decode(p.K[next.C].AsString);
        return null;
    }

    private static void TrackStandardHost(SortedDictionary<ushort, EmbedHostImport> importedHosts, string fullName)
    {
        foreach (EmbedHostImport std in EmbedPrelude.StandardSyscalls)
        {
            if (std.Name == fullName)
            {
                importedHosts[std.HostId] = std;
                return;
            }
        }
    }

    /// <summary>
    /// Rejects dynamic dictionaries/maps (<c>{K: V}</c>, <c>{[k] = v}</c>), <c>List</c>/<c>Dict</c>,
    /// and <c>units</c>/<c>SharpUnits</c>/<c>safenum</c> constructs before type-checking so diagnostics
    /// are clear and deterministic.
    /// </summary>
    private static void ValidateAstProfile(Chunk chunk, string fileName, List<EmbedDiagnostic> diags)
    {
        void CheckType(TypeNode? t)
        {
            if (t is null) return;
            switch (t)
            {
                case MapTypeNode map:
                    diags.Add(new("CAMEOEMB018",
                        "Dynamic dictionary/map types ('{K: V}') are excluded from Cameo.Embed; use fixed braced records or fixed integer-indexed arrays",
                        fileName, map.Line, map.Col));
                    CheckType(map.Key);
                    CheckType(map.Value);
                    break;
                case NameTypeNode nt:
                    if (nt.Names.Count > 0 && DisallowedEmbedGlobals.Contains(nt.Names[0]))
                    {
                        diags.Add(new("CAMEOEMB001",
                            $"Type '{nt.FullName}' is excluded from Cameo.Embed (no units, dynamic dictionaries, or heap containers)",
                            fileName, nt.Line, nt.Col));
                    }
                    if (nt.TypeArgs is not null)
                        foreach (TypeNode ta in nt.TypeArgs) CheckType(ta);
                    break;
                case ArrayTypeNode arr:
                    CheckType(arr.Element);
                    break;
                case TupleTypeNode tup:
                    foreach (TypeNode el in tup.Elements) CheckType(el);
                    break;
                case UnionTypeNode un:
                    foreach (TypeNode m in un.Members) CheckType(m);
                    break;
                case FunctionTypeNode fn:
                    foreach (var p in fn.Params) CheckType(p.Type);
                    foreach (var r in fn.Returns) CheckType(r);
                    break;
                case RecordTypeNode rec:
                    foreach (var f in rec.Fields) CheckType(f.Type);
                    if (rec.ArrayElement is not null) CheckType(rec.ArrayElement);
                    break;
            }
        }

        void CheckFuncBody(FunctionBody fb)
        {
            foreach (Param p in fb.Params) CheckType(p.Type);
            foreach (TypeNode r in fb.Returns) CheckType(r);
            CheckBlock(fb.Body);
        }

        void CheckExpr(Expr? e)
        {
            if (e is null) return;
            switch (e)
            {
                case CameoListExpr le:
                    diags.Add(new("CAMEOEMB001",
                        "Dynamic List literal '[...]' is excluded from Cameo.Embed; use fixed array literals '{ ... }'",
                        fileName, le.Line, le.Col));
                    foreach (Expr item in le.Items) CheckExpr(item);
                    break;
                case CameoListCompExpr lce:
                    diags.Add(new("CAMEOEMB001",
                        "List comprehensions allocate dynamic heap lists and are excluded from Cameo.Embed; use numeric 'for' loops",
                        fileName, lce.Line, lce.Col));
                    CheckExpr(lce.Body);
                    CheckExpr(lce.Iterable);
                    CheckExpr(lce.Filter);
                    break;
                case TableExpr te:
                    foreach (TableItem item in te.Items)
                    {
                        if (item.Kind == TableItemKind.Keyed)
                        {
                            diags.Add(new("CAMEOEMB018",
                                "Keyed dictionary literal '{ [key] = value }' is excluded from Cameo.Embed; use fixed named record fields or positional arrays",
                                fileName, item.Line, item.Col));
                        }
                        CheckType(item.DeclaredType);
                        CheckExpr(item.Key);
                        CheckExpr(item.Value);
                    }
                    break;
                case IndexExpr ix:
                    CheckExpr(ix.Object);
                    CheckExpr(ix.Key);
                    break;
                case CallExpr ce:
                    CheckExpr(ce.Callee);
                    foreach (Expr a in ce.Args) CheckExpr(a);
                    break;
                case MethodCallExpr mc:
                    CheckExpr(mc.Object);
                    foreach (Expr a in mc.Args) CheckExpr(a);
                    break;
                case FunctionExpr fe:
                    CheckFuncBody(fe.Body);
                    break;
                case BinaryExpr be:
                    CheckExpr(be.Left);
                    CheckExpr(be.Right);
                    break;
                case UnaryExpr ue:
                    CheckExpr(ue.Operand);
                    break;
                case ParenExpr pe:
                    CheckExpr(pe.Inner);
                    break;
                case CastExpr cast:
                    CheckExpr(cast.Inner);
                    foreach (TypeNode ct in cast.Types) CheckType(ct);
                    break;
                case IsExpr ise:
                    CheckExpr(ise.Inner);
                    CheckType(ise.Type);
                    break;
                case CameoMatchExpr me:
                    CheckExpr(me.Scrutinee);
                    foreach (var arm in me.Arms)
                    {
                        CheckExpr(arm.ExprBody);
                        CheckBlock(arm.BlockBody);
                    }
                    break;
            }
        }

        void CheckBlock(Block? block)
        {
            if (block is null) return;
            foreach (Stat s in block.Stats)
            {
                switch (s)
                {
                    case LocalStat ls:
                        if (ls.Types is not null) foreach (TypeNode? t in ls.Types) CheckType(t);
                        foreach (Expr v in ls.Values) CheckExpr(v);
                        break;
                    case GlobalStat gs:
                        if (gs.Types is not null) foreach (TypeNode? t in gs.Types) CheckType(t);
                        foreach (Expr v in gs.Values) CheckExpr(v);
                        break;
                    case AssignStat ass:
                        foreach (Expr t in ass.Targets) CheckExpr(t);
                        foreach (Expr v in ass.Values) CheckExpr(v);
                        break;
                    case CallStat cs:
                        CheckExpr(cs.Call);
                        break;
                    case DoStat ds:
                        CheckBlock(ds.Body);
                        break;
                    case WhileStat ws:
                        CheckExpr(ws.Cond);
                        CheckBlock(ws.Body);
                        break;
                    case RepeatStat rs:
                        CheckBlock(rs.Body);
                        CheckExpr(rs.Cond);
                        break;
                    case IfStat ifs:
                        foreach (var (cond, body) in ifs.Clauses) { CheckExpr(cond); CheckBlock(body); }
                        CheckBlock(ifs.Else);
                        break;
                    case ForNumStat fns:
                        CheckExpr(fns.Start); CheckExpr(fns.Limit); CheckExpr(fns.Step);
                        CheckBlock(fns.Body);
                        break;
                    case ForInStat fis:
                        foreach (Expr ex in fis.Exprs) CheckExpr(ex);
                        CheckBlock(fis.Body);
                        break;
                    case FunctionStat fs:
                        CheckFuncBody(fs.Body);
                        break;
                    case LocalFunctionStat lfs:
                        CheckFuncBody(lfs.Body);
                        break;
                    case GlobalFunctionStat gfs:
                        CheckFuncBody(gfs.Body);
                        break;
                    case ReturnStat ret:
                        foreach (Expr v in ret.Values) CheckExpr(v);
                        break;
                    case TypeDeclStat tds:
                        CheckType(tds.Def);
                        break;
                    case MatchStat ms:
                        CheckExpr(ms.Match);
                        break;
                }
            }
        }

        CheckBlock(chunk.Body);
    }

    private static bool FitsInt16(int v) => v >= short.MinValue && v <= short.MaxValue;

    private readonly record struct LoweredProto(
        uint CodeOffset, uint CodeCount,
        uint ConstOffset, ushort ConstCount,
        ushort UpvalOffset, ushort UpvalCount,
        ushort NameStringId, byte NumParams, byte MaxStack, byte Flags);

    private readonly record struct LoweredConst(EmbedConstKind Kind, ulong Payload);
    private readonly record struct LoweredInstr(EmbedOpCode Op, int A, int B, int C);
    private readonly record struct LoweredUpval(byte FromParentReg, byte Index);
}
