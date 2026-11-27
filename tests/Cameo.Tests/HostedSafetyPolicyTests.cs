using System.Net;
using Cameo;
using CsCheck;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;
using Cameo.Tests.Properties;
using Xunit;

namespace Cameo.Tests;

/// <summary>Verifies hosted-safety policies: CameoLibraries.Sandboxed, AllowDebug, FilePolicy, NetworkPolicy, and isolated sandboxes.</summary>
public sealed class HostedSafetyPolicyTests
{
    [Fact]
    public void Sandboxed_library_profile_exposes_compute_and_omits_host_capabilities()
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions
        {
            Libraries = CameoLibraries.Sandboxed,
            TypeCheck = false,
        });
        var values = engine.RunString("""
            let decoded = unwrap(json.decode('{"x": 21}'))
            let total = Query.from({decoded.x, decoded.x}).sum()
            let res = Result.ok(total)
            let workerVal = Future.parallel(() => io == nil && fs == nil && aio == nil && debug == nil && os.getenv == nil).get()
            return res.value(),
                io == nil, fs == nil, aio == nil, db == nil, cli == nil, debug == nil, dofile == nil,
                os.getenv == nil, os.remove == nil, os.rename == nil, os.tmpname == nil,
                type(os.time()) == "number", workerVal
            """);
        Assert.Equal(42, values[0].I);
        for (int i = 1; i < values.Length; i++)
            Assert.True(values[i].IsTruthy, $"Expected check #{i} to be true");
    }

    [Fact]
    public void AllowDebug_false_removes_debug_library_and_propagates_to_workers()
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { AllowDebug = false, TypeCheck = false });
        var values = engine.RunString("""
            let inMain = debug == nil
            let inWorker = Future.parallel(() => debug == nil).get()
            let inSandbox = sandbox.run("return debug == nil").get()
            return inMain, inWorker, inSandbox
            """);
        Assert.True(values[0].IsTruthy);
        Assert.True(values[1].IsTruthy);
        Assert.True(values[2].IsTruthy);
    }

    [Fact]
    public void Isolated_sandbox_strips_io_fs_env_debug_and_rejects_external_grants()
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions
        {
            TypeCheck = false,
            AllowNetwork = true,
            AllowProcesses = true,
            AllowDatabase = true,
        });
        var values = engine.RunString(""""
            let check = sandbox.run("""
                return io == nil && fs == nil && aio == nil && db == nil
                    && debug == nil && dofile == nil && os.getenv == nil
                    && Result.ok(42).value() == 42
            """, { isolated = true }).get()

            let _, denied = sandbox.run("return 1", { isolated = true, allow = {"net"} }).await()
            return check, denied.kind
            """");
        Assert.True(values[0].IsTruthy);
        Assert.Equal("failed", values[1].AsString);
    }

    [Fact]
    public void FilePolicy_enforces_readonly_readwrite_traversal_sibling_prefix_and_workers()
    {
        string baseDir = Directory.CreateTempSubdirectory("tealvm-filepolicy-").FullName;
        string roDir = Path.Combine(baseDir, "ro");
        string rwDir = Path.Combine(baseDir, "rw");
        string siblingDir = Path.Combine(baseDir, "ro2");
        Directory.CreateDirectory(roDir);
        Directory.CreateDirectory(rwDir);
        Directory.CreateDirectory(siblingDir);
        string roFile = Path.Combine(roDir, "data.txt");
        string rwFile = Path.Combine(rwDir, "out.txt");
        string secretFile = Path.Combine(siblingDir, "secret.txt");
        File.WriteAllText(roFile, "allowed-read");
        File.WriteAllText(secretFile, "top-secret");

        try
        {
            var policy = new FilePolicy([
                new FileRule(roDir, allowWrite: false),
                new FileRule(rwDir, allowWrite: true),
            ]);

            using var engine = new CameoEngine(new CameoRuntimeOptions { FilePolicy = policy, TypeCheck = false });
            engine.State.SetGlobal("roFile", Value.FromText(roFile));
            engine.State.SetGlobal("rwFile", Value.FromText(rwFile));
            engine.State.SetGlobal("secretFile", Value.FromText(secretFile));
            engine.State.SetGlobal("traversal", Value.FromText(Path.Combine(roDir, "..", "ro2", "secret.txt")));

            var values = engine.RunString(""""
                let readOk, readErr = fs.read(roFile)
                let writeRoOk, writeRoErr = fs.write(roFile, "x")
                let writeRwOk, writeRwErr = fs.write(rwFile, "written")
                let secretOk, secretErr = fs.read(secretFile)
                let travOk, travErr = fs.read(traversal)
                let ioSecret, ioErr = io.open(secretFile, "rb")
                let osRemOk, osRemErr = os.remove(roFile)
                let _, aioSecretErr = aio.readFile(secretFile).await()
                fn workerRead(p) {
                    let v, e = fs.read(p)
                    return v == nil && e.kind == "permission_denied"
                }
                let workerDenied = Future.parallel(workerRead, secretFile).get()
                let sandboxDenied = sandbox.run("""
                    let p = ...
                    let v, e = fs.read(p)
                    return v == nil && e.kind == "permission_denied"
                """, { args = { secretFile } }).get()
                return readOk, writeRoErr.kind, writeRwOk, secretErr.kind, travErr.kind,
                    ioSecret == nil, osRemOk == nil, aioSecretErr.cause.kind, workerDenied, sandboxDenied
                """");

            Assert.Equal("allowed-read", values[0].AsString);
            Assert.Equal("permission_denied", values[1].AsString);
            Assert.True(values[2].IsTruthy);
            Assert.Equal("written", File.ReadAllText(rwFile));
            Assert.Equal("permission_denied", values[3].AsString);
            Assert.Equal("permission_denied", values[4].AsString);
            Assert.True(values[5].IsTruthy);
            Assert.True(values[6].IsTruthy);
            Assert.Equal("permission_denied", values[7].AsString);
            Assert.True(values[8].IsTruthy);
            Assert.True(values[9].IsTruthy);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void NetworkPolicy_enforces_schemes_hosts_wildcards_ports_and_private_ip_blocking()
    {
        var policy = new NetworkPolicy([
            new NetworkRule("api.example.com"),
            new NetworkRule("*.svc.example.com", allowHttp: true, port: 8080),
        ], blockPrivateAddresses: true);

        Assert.True(policy.Allows("https://api.example.com/v1/items"));
        Assert.False(policy.Allows("http://api.example.com/v1/items")); // allowHttp is false
        Assert.False(policy.Allows("https://evil.com/"));
        Assert.False(policy.Allows("file:///etc/passwd"));
        Assert.True(policy.Allows("http://a.svc.example.com:8080/health"));
        Assert.True(policy.Allows("https://a.b.svc.example.com:8080/health"));
        Assert.False(policy.Allows("http://svc.example.com:8080/health")); // wildcard requires subdomain
        Assert.False(policy.Allows("http://a.svc.example.com:9090/health")); // wrong port
        Assert.False(policy.Allows("http://127.0.0.1:8080/"));
        Assert.False(policy.Allows("https://169.254.169.254/latest/meta-data/"));

        using var engine = new CameoEngine(new CameoRuntimeOptions
        {
            AllowNetwork = true,
            NetworkPolicy = policy,
            TypeCheck = false,
        });
        var values = engine.RunString(""""
            let _, e1 = aio.httpGet("http://127.0.0.1:9/").await()
            let _, e2 = aio.httpGet("https://evil.example.org/").await()
            let _, e3 = aio.httpGet("http://api.example.com/").await()
            let sandboxBlocked = sandbox.run("""
                let _, e = aio.httpGet("https://evil.example.org/"):await()
                return e.cause.kind
            """, { allow = { "net" } }).get()
            return e1.cause.kind, e2.cause.kind, e3.cause.kind, sandboxBlocked
            """");
        Assert.Equal("permission_denied", values[0].AsString);
        Assert.Equal("permission_denied", values[1].AsString);
        Assert.Equal("permission_denied", values[2].AsString);
        Assert.Equal("permission_denied", values[3].AsString);
    }

    [Fact]
    public void Property_FilePolicy_never_permits_sibling_prefix_or_parent_traversal()
    {
        string baseDir = Directory.CreateTempSubdirectory("tealvm-fp-prop-").FullName;
        string allowed = Path.Combine(baseDir, "box");
        Directory.CreateDirectory(allowed);
        var policy = new FilePolicy([new FileRule(allowed, allowWrite: true)]);
        try
        {
            Prop.Cs(Gen.String[Gen.Char.AlphaNumeric, 1, 12], suffix =>
            {
                string sibling = Path.Combine(baseDir, "box" + suffix, "file.txt");
                string escaped = Path.Combine(allowed, "..", "outside-" + suffix);
                string inside = Path.Combine(allowed, suffix + ".txt");
                Assert.False(policy.AllowsRead(sibling));
                Assert.False(policy.AllowsWrite(sibling));
                Assert.False(policy.AllowsRead(escaped));
                Assert.True(policy.AllowsRead(inside));
                Assert.True(policy.AllowsWrite(inside));
            }, iter: 100);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void Property_NetworkPolicy_blocks_rfc1918_loopback_and_mapped_ipv6_addresses()
    {
        Prop.Cs(Gen.Select(Gen.Byte, Gen.Byte, Gen.Byte), tuple =>
        {
            var (b, c, d) = tuple;
            Assert.True(NetworkPolicy.IsPrivateOrLoopback(new IPAddress([127, b, c, d])));
            Assert.True(NetworkPolicy.IsPrivateOrLoopback(new IPAddress([10, b, c, d])));
            Assert.True(NetworkPolicy.IsPrivateOrLoopback(new IPAddress([192, 168, c, d])));
            Assert.True(NetworkPolicy.IsPrivateOrLoopback(new IPAddress([169, 254, c, d])));
            Assert.True(NetworkPolicy.IsPrivateOrLoopback(new IPAddress([127, b, c, d]).MapToIPv6()));
            Assert.True(NetworkPolicy.IsPrivateOrLoopback(new IPAddress([169, 254, c, d]).MapToIPv6()));
        }, iter: 100);
    }
}
