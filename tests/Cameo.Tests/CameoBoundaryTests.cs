using Cameo;
using Cameo.Runtime.Syntax;
using Xunit;

namespace Cameo.Tests;

/// <summary>Every source producer retains an explicit language or documented legacy boundary.</summary>
public class CameoBoundaryTests
{
    [Fact]
    public void Declared_lua_modules_remain_typed_and_keep_the_legacy_parser()
    {
        using var files=new ModuleFiles();
        files.Write("typed.d.tl","local record Export twice: function(integer): integer end return Export");
        files.Write("typed.lua","return {twice = function(n) return n * 2 end}");
        files.Write("arrowdecl.d.tl","local f = (n: integer) => n * 2 return f");
        files.Write("arrowdecl.lua","return function(n) return n * 2 end");
        using var cameo=new CameoEngine(files.Options());
        Assert.Equal(42,cameo.RunString("local m = require('typed') return m.twice(21)")[0].I);
        Assert.IsType<CompileFailed>(cameo.TryRunString("return require('arrowdecl')(21)").Value);
    }

    [Fact]
    public void Malformed_import_is_a_check_error_in_both_front_ends()
    {
        using var files=new ModuleFiles();
        files.Write("bad.cameo","local f = () =>");
        files.Write("bad.tl","local f = function(");
        using var cameo=new CameoEngine(files.Options());
        using var teal=new ScriptEngine(files.Options());
        Assert.NotEmpty(cameo.Check("return require('bad')"));
        Assert.NotEmpty(teal.Check("return require('bad')","main.tl"));
        Assert.DoesNotContain(cameo.Runtime.Warnings,d=>d.Message.Contains("internal type checker"));
    }

    [Fact]
    public void Module_suffixes_and_package_paths_are_separate()
    {
        using var files = new ModuleFiles();
        files.Write("helper.cameo", "return (n: integer) => n + 2");
        files.Write("helper.tl", "return 17");
        using var cameo = new CameoEngine(files.Options());
        using var teal = new ScriptEngine(files.Options());
        Assert.Equal(42, cameo.RunString("return require('helper')(40)")[0].I);
        Assert.Equal(17, teal.RunString("return require('helper')")[0].I);
        Assert.Equal("./?.cameo;./?/init.cameo",cameo.RunString("return package.path")[0].ToText());
        Assert.Equal("./?.tl;./?/init.tl",teal.RunString("return package.path")[0].ToText());
    }

    [Fact]
    public void Cameo_does_not_implicitly_import_tl_sources()
    {
        using var files = new ModuleFiles();
        files.Write("onlyteal.tl", "return 42");
        using var cameo = new CameoEngine(files.Options());
        Assert.False(cameo.TryRunString("return require('onlyteal')").Value is RunCompleted);
    }

    [Fact]
    public void Directory_init_uses_the_owning_language()
    {
        using var files = new ModuleFiles();
        files.Write("nested/init.cameo", "return () => 42");
        using var cameo = new CameoEngine(files.Options());
        Assert.Equal(42,cameo.RunString("return require('nested')()")[0].I);
    }

    [Fact]
    public void Worker_factory_keeps_cameo_module_parsing()
    {
        using var files = new ModuleFiles();
        files.Write("work.cameo","return (n: integer) => n + 2");
        using var cameo = new CameoEngine(files.Options());
        Assert.Equal(42,cameo.RunString("return Future.parallel(function(): integer return require('work')(40) end):get()")[0].I);
    }

    [Fact]
    public void Dynamic_load_follows_the_owning_language_but_is_unchecked()
    {
        const string source="local f,e = load('return ((x: integer) => x + 2)(40)') if f == nil then error(e) end return f()";
        using var cameo = new CameoEngine();
        using var teal = new ScriptEngine();
        Assert.Equal(42,cameo.RunString(source)[0].I);
        Assert.False(teal.TryRunString(source).Value is RunCompleted);
    }

    [Fact]
    public void Legacy_lua_implementations_do_not_gain_cameo_syntax()
    {
        using var files = new ModuleFiles();
        files.Write("legacy.lua","return (n: integer) => n + 2");
        using var cameo = new CameoEngine(files.Options());
        Assert.False(cameo.TryRunString("return require('legacy')(40)").Value is RunCompleted);
    }

    [Fact]
    public void Newline_and_diagnostic_location_are_not_source_text_rewriting()
    {
        using var cameo = new CameoEngine();
        var errors=cameo.Check("-- first\r\nlocal f = (x) => x", "location.cameo");
        Assert.Contains(errors,d=>d.Line==2 && d.Col==12 && d.Message.Contains("CAMEO001"));
    }

    private sealed class ModuleFiles : IDisposable
    {
        private readonly string _root;
        public ModuleFiles()
        {
            string parent=Path.GetTempPath();
            if(!Directory.Exists(parent))throw new DirectoryNotFoundException(parent);
            _root=Path.Combine(parent,"cameo-modules-"+Guid.NewGuid().ToString("N"));
            if(Directory.Exists(_root))throw new IOException("Temporary directory collision");
            Directory.CreateDirectory(_root);
        }
        public CameoRuntimeOptions Options() => new(){ModulePaths={_root}};
        public void Write(string path,string source)
        {
            string full=Path.Combine(_root,path);
            string parent=Path.GetDirectoryName(full)!;
            if(!Directory.Exists(parent))Directory.CreateDirectory(parent);
            File.WriteAllText(full,source);
        }
        public void Dispose()
        {
            if(Directory.Exists(_root))Directory.Delete(_root,recursive:true);
        }
    }
}
