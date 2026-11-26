using CsCheck;
using Cameo.Runtime.Vm;
using Cameo.Tests.Properties;
using Xunit;

namespace Cameo.Tests;

/// <summary>
/// The class syntax: what it accepts and rejects, that the type checker sees through it, that
/// it works across modules, and that objects behave like a model under random use.
/// </summary>
public class ClassTests
{
    private static string Run(string source, bool typeCheck = true)
    {
        var engine = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = typeCheck });
        var output = new StringWriter { NewLine = "\n" };
        engine.State.Stdout = output;
        return engine.TryRunString(source) switch
        {
            RunCompleted => output.ToString(),
            CompileFailed f => "compile:" + f.Diagnostics[0].Message,
            RuntimeFailed f => output + "error:" + f.Message,
            RunAborted a => "aborted:" + a.Reason,
            RunExited x => "exit:" + x.Code,
        };
    }

    private const string Shapes = """
        local class Shape
           name: string
           function new(name: string) self.name = name end
           function area(self): number return 0 end
           function describe(self): string return self.name .. " " .. self:area() end
        end
        local class Rect is Shape
           w: number
           h: number
           function new(w: number, h: number) super("rect") self.w = w self.h = h end
           function area(self): number return self.w * self.h end
        end
        local class Square is Rect
           function new(side: number) super(side, side) self.name = "square" end
        end

        """;

    [Theory]
    [InlineData("print(Rect.new(2, 3):describe(), Square.new(4):describe(), Shape.new('blob'):describe())", "rect 6\tsquare 16\tblob 0\n")]
    [InlineData("local s: Shape = Square.new(2) print(s:area(), s.name)", "4\tsquare\n")]
    [InlineData("local shapes: {Shape} = {Rect.new(1, 2), Square.new(3)} local total = 0.0 for _, s in ipairs(shapes) do total += s:area() end print(total)", "11.0\n")]
    [InlineData("local r: Rect = Shape.new('x')", "compile:")]
    [InlineData("local r = Rect.new('a', 2)", "compile:")]
    [InlineData("local r = Rect.new(1, 2) r.depth = 3", "compile:")]
    [InlineData("local r = Rect.new(1, 2) print(r:volume())", "compile:")]
    [InlineData("local n: string = Rect.new(1, 2):area()", "compile:")]
    public void Classes_are_checked_like_the_records_they_become(string code, string expected)
    {
        string r = Run(Shapes + code);
        if (expected == "compile:") Assert.StartsWith("compile:", r); else Assert.Equal(expected, r);
    }

    [Theory]
    [InlineData("local class A function new() end function new() end end", "a class has one constructor")]
    [InlineData("local class A function new(self) end end", "the constructor does not declare 'self'")]
    [InlineData("local class A function f(self) end function f(self) end end", "'f' is defined twice in class 'A'")]
    [InlineData("local class A function f(self) return super.f(self) end end", "'super' in class 'A', which has no parent class")]
    [InlineData("local class A x: integer", "'end' expected (to close 'class' at line 1)")]
    [InlineData("local class A function f(self) end", "'end' expected")]
    [InlineData("local class A is 5 end", "class name expected after 'is'")]
    [InlineData("local class A 42 end", "a field ('name: type') or a function expected in class 'A'")]
    [InlineData("local class A is B is C end", "already has a parent class")]
    [InlineData("local class A is B, C end", "a class has one parent class")]
    [InlineData("local class A implements 5 end", "a name expected after 'implements'")]
    [InlineData("local class A uses end", "a name expected after 'uses'")]
    [InlineData("local class A get x(): integer return 1 end end", "a getter is written 'get x(self): Type'")]
    [InlineData("local class A set x(self) end end", "a setter is written 'set x(self, value: Type)'")]
    [InlineData("local class A get x(self): integer return 1 end get x(self): integer return 2 end end", "property 'x' has two getters")]
    [InlineData("local class A function x(self) end get x(self): integer return 1 end end", "'x' is defined twice in class 'A'")]
    [InlineData("local class A private function new() end end", "the constructor cannot be private")]
    [InlineData("local class A private function __add(a: A, b: A): A return a end end", "a metamethod cannot be private")]
    [InlineData("local class A function f(self, x) end end", "a class member needs typed parameters")]
    [InlineData("local class A function 5() end end", "method name expected")]
    public void Malformed_classes_are_reported(string source, string message) =>
        Assert.Contains(message, Run(source));

    [Fact]
    public void Class_is_not_a_reserved_word()
    {
        Assert.Equal("3\tx\n", Run("local class = 3\nlocal t = {class = 'x'}\nprint(class, t.class)"));
    }

    [Fact]
    public void Details_of_construction_and_inheritance()
    {
        Assert.Equal("true\ttrue\tfalse\ttrue\n" + "base+derived\t2\n" + "<n=1>\t<n=1>\ttrue\ttrue\n" + "10\t20\tnil\n", Run("""
            local class Base
               log: {string}
               function new() self.log = {"base"} end
               function __tostring(self): string return "<n=" .. #self.log .. ">" end
               function __eq(a: Base, b: Base): boolean return #a.log == #b.log end
               function __lt(a: Base, b: Base): boolean return #a.log < #b.log end
               function make(): Base return Base.new() end
            end
            local class Derived is Base
               function new() super() table.insert(self.log, "derived") end
            end
            local class Leaf is Derived
            end
            local b, d, l = Base.new(), Derived.new(), Leaf.new()
            -- each class has its own metatable; objects of one class share it
            print(getmetatable(b) ~= getmetatable(d), getmetatable(d) == getmetatable(Derived.new()), rawequal(b, Base.new()), b < d)
            print(table.concat(l.log, "+"), #Leaf.new().log)
            -- metamethods and class functions are inherited
            print(tostring(b), tostring(Leaf.make()), d == l, Derived.make ~= nil)
            -- fields are plain table fields
            local class Pair
               a: integer
               b: integer
               function new(a: integer, b: integer) self.a, self.b = a, b end
            end
            local p = Pair.new(10, 20)
            print(p.a, p.b, rawget(p as {string:any}, "new"))
            """));
    }

    [Fact]
    public void A_class_can_extend_one_from_another_module()
    {
        string dir = Directory.CreateTempSubdirectory("tealvm-class").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "animals.tl"), """
                global class Animal
                   name: string
                   function new(name: string) self.name = name end
                   function speak(self): string return self.name .. " makes a sound" end
                   function __tostring(self): string return "Animal(" .. self.name .. ")" end
                end
                return Animal
                """);
            File.WriteAllText(Path.Combine(dir, "main.tl"), """
                require("animals")
                local class Cat is Animal
                   function speak(self): string return super.speak(self) .. ": meow" end
                end
                local c = Cat.new("Tom")
                print(c:speak(), tostring(c))
                """);
            var engine = new ScriptEngine();
            var output = new StringWriter { NewLine = "\n" };
            engine.State.Stdout = output;
            RunResult r = engine.TryRunFile(Path.Combine(dir, "main.tl"));
            Assert.True(r is RunCompleted, r is CompileFailed c ? c.Diagnostics[0].ToString() : r is RuntimeFailed f ? f.Message : "?");
            Assert.Equal("Tom makes a sound: meow\tAnimal(Tom)\n", output.ToString());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private enum Op { Deposit, Withdraw, Interest, Balance }

    [Fact]
    public void Objects_behave_like_a_model_under_random_use()
    {
        var host = new LuaHost(typeCheck: true);
        Value fn = host.Run("""
            local class Account
               balance: integer
               history: {string}
               function new(opening: integer) self.balance = opening self.history = {} end
               function deposit(self, n: integer): Account
                  self.balance += n
                  table.insert(self.history, "d")
                  return self
               end
               function withdraw(self, n: integer): boolean
                  if n > self.balance then return false end
                  self.balance -= n
                  table.insert(self.history, "w")
                  return true
               end
            end
            local class Savings is Account
               rate: integer
               function new(opening: integer, rate: integer) super(opening) self.rate = rate end
               function addInterest(self): Account
                  return self:deposit(self.balance * self.rate // 100)
               end
               function withdraw(self, n: integer): boolean      -- at most half at a time
                  if n * 2 > self.balance then return false end
                  return super.withdraw(self, n)
               end
            end
            return function(opening: integer, rate: integer, ops: {{string | integer}}): string
               local acct = Savings.new(opening, rate)
               local out: {string} = {}
               for _, op in ipairs(ops) do
                  local kind, n = op[1] as string, op[2] as integer
                  if kind == "deposit" then acct:deposit(n)
                  elseif kind == "withdraw" then table.insert(out, tostring(acct:withdraw(n)))
                  elseif kind == "interest" then acct:addInterest()
                  else table.insert(out, tostring(acct.balance)) end
               end
               return table.concat(out, ",") .. "|" .. acct.balance .. "|" .. table.concat(acct.history)
            end
            """)[0];
        Prop.Cs(Gen.Select(Gen.Int[0, 500], Gen.Int[0, 20], Gen.Select(Gen.Enum<Op>(), Gen.Int[0, 300]).Array[0, 30]), t =>
        {
            var (opening, rate, ops) = t;
            long balance = opening;
            var history = new System.Text.StringBuilder();
            var outputs = new List<string>();
            var script = new Table();
            foreach (var (op, n) in ops)
            {
                var entry = new Table();
                entry.Append(Value.FromString(op.ToString().ToLowerInvariant()));
                entry.Append(n);
                script.Append(Value.FromTable(entry));
                switch (op)
                {
                    case Op.Deposit: balance += n; history.Append('d'); break;
                    case Op.Withdraw:
                        bool ok = n * 2L <= balance && n <= balance;
                        if (ok) { balance -= n; history.Append('w'); }
                        outputs.Add(ok ? "true" : "false");
                        break;
                    case Op.Interest: balance += balance * rate / 100; history.Append('d'); break;
                    default: outputs.Add(balance.ToString()); break;
                }
            }
            Value[] r = host.L.Call(fn, opening, rate, Value.FromTable(script));
            Assert.Equal($"{string.Join(",", outputs)}|{balance}|{history}", r[0].AsString);
        }, iter: 300);
    }

    // ---- interfaces, mixins, visibility, properties

    private static string Outcome(string source)
    {
        var engine = new ScriptEngine();
        var output = new StringWriter { NewLine = "\n" };
        engine.State.Stdout = output;
        RunResult r = engine.TryRunString(source, "t");
        return r is RunCompleted ? output.ToString()
            : r is CompileFailed c ? "compile: " + string.Join(" | ", c.Diagnostics.Where(d => !d.IsWarning).Select(d => d.Message))
            : r is RuntimeFailed f ? "error: " + f.Message : "?";
    }

    private const string Hidden = """
        local class Base
           private a: integer
           protected b: integer
           function new() self.a = 1 self.b = 2 end
           private function pm(self): integer return self.a end
           protected function qm(self): integer return self.b end
           private get secret(self): integer return self.a end
           function sum(self, other: Base): integer return self.a + other.a + self:pm() + self.secret end
        end
        local class Child is Base
           function new() super() end
           function ok(self): integer return self.b + self:qm() end
        end
        local c = Child.new()

        """;

    [Theory]
    [InlineData("print(c:ok(), c:sum(c))", "4\t4\n")]
    [InlineData("print(c.a)", "compile: 'a' is private to class Base")]
    [InlineData("c.a = 5", "compile: 'a' is private to class Base")]
    [InlineData("print(c:pm())", "compile: 'pm' is private to class Base")]
    [InlineData("print(c.secret)", "compile: 'secret' is private to class Base")]
    [InlineData("print(c.b)", "compile: 'b' is protected: only class Base and its subclasses can use it")]
    [InlineData("print(c:qm())", "compile: 'qm' is protected: only class Base and its subclasses can use it")]
    [InlineData("local class G is Child function new() super() end function f(self): integer return self.b end end print(G.new():f())", "2\n")]
    [InlineData("local class G is Child function new() super() end function f(self): integer return self.a end end", "compile: 'a' is private to class Base")]
    [InlineData("local class Other function f(self, x: Base): integer return x.b end end", "compile: 'b' is protected: only class Base and its subclasses can use it")]
    public void Private_and_protected_members_are_enforced_by_the_checker(string use, string expected)
    {
        Assert.Equal(expected, Outcome(Hidden + use));
    }

    [Theory]
    [InlineData("local interface I f: function(self): integer end local class A implements I function new() end end", "compile: class A does not define 'f', which interface I requires")]
    [InlineData("local interface I f: function(self): integer end local class A implements I function f(self): string return 'x' end end", "compile: field 'f' does not match definition in interface I")]
    [InlineData("local interface I f: function(self): integer end local class P function f(self): integer return 1 end end local class A is P implements I end local i: I = A.new() print(i:f())", "1\n")]
    [InlineData("local interface I f: function(self): integer end local class M function f(self): integer return 2 end end local class A implements I uses M end local i: I = A.new() print(i:f())", "2\n")]
    [InlineData("local interface I n: integer end local interface J g: function(self): integer end local class A implements I, J function new() self.n = 3 end function g(self): integer return self.n end end local j: J = A.new() print(j:g(), (j as A).n)", "3\t3\n")]
    public void A_class_must_define_what_its_interfaces_require(string source, string expected)
    {
        Assert.StartsWith(expected, Outcome(source));
    }

    [Fact]
    public void Mixins_are_copied_and_a_conflict_between_two_is_an_error()
    {
        const string two = """
            local class M1 function hello(self): string return "one" end function only1(self): integer return 1 end end
            local class M2 function hello(self): string return "two" end end

            """;
        Assert.Contains("mixins 1 and 2 both define 'hello'", Outcome(two + "local class A uses M1, M2 end"));
        // the class's own definition settles it
        Assert.Equal("mine\t1\n", Outcome(two + "local class A uses M1, M2 function hello(self): string return 'mine' end end local a = A.new() print(a:hello(), a:only1())"));
        // a mixin wins over the parent; the class wins over both
        Assert.Equal("one\n", Outcome(two + "local class P function hello(self): string return 'parent' end end local class A is P uses M1 end print(A.new():hello())"));
        // changing the mixin afterwards does not change the class: the functions were copied
        Assert.Equal("one\n", Outcome(two + "local class A uses M1 end M1.hello = function(self: M1): string return 'changed' end print(A.new():hello())"));
    }

    [Fact]
    public void Properties_run_their_functions_for_typed_and_untyped_access()
    {
        string source = """
            local class Gauge
               private v: number
               sets: integer
               function new() self.v = 0 self.sets = 0 end
               get value(self): number return self.v end
               set value(self, x: number) self.sets = self.sets + 1 self.v = math.max(0, x) end
               get doubled(self): number return self.v * 2 end
            end
            local g = Gauge.new()
            g.value = 5
            g.value = g.value + 1
            g.value = -3
            local any = g as {string:any}
            any.value = 9
            any["value"] = (any["value"] as number) + 1
            print(g.value, g.doubled, g.sets, rawget(g as table, "value"))
            print(pcall(function() any.doubled = 1 end))
            for k in pairs(g as table) do if k == "value" or k == "doubled" then print("stored!", k) end end
            """;
        Assert.Equal("10\t20\t5\tnil\nfalse\tproperty 'doubled' of class 'Gauge' is read-only\n", Outcome(source));
    }

    // Property: an object whose state is behind properties and inherited through several classes
    // behaves like a plain record updated by the same operations.
    [Fact]
    public void Property_backed_objects_in_a_hierarchy_follow_a_model()
    {
        Gen.Select(Gen.Int[1, 6], Gen.Select(Gen.Int[0, 2], Gen.Long[-50, 50]).Array[1, 25]).Sample(t =>
        {
            var (depth, ops) = t;
            var src = new System.Text.StringBuilder("""
                local class C0
                   private n: integer
                   function new() self.n = 0 end
                   get total(self): integer return self.n end
                   set total(self, v: integer) self.n = v end
                   function bump(self, by: integer): integer self.total = self.total + by return self.total end
                   function level(self): integer return 0 end
                end

                """);
            for (int d = 1; d <= depth; d++)
                src.Append($"local class C{d} is C{d - 1} function new() super() end function level(self): integer return super.level(self) + 1 end end\n");
            src.Append($"local o = C{depth}.new()\nlocal out: {{integer}} = {{}}\n");
            long model = 0;
            var expected = new List<long>();
            foreach (var (op, x) in ops)
            {
                switch (op)
                {
                    case 0: src.Append($"o.total = {x}\n"); model = x; break;
                    case 1: src.Append($"table.insert(out, o:bump({x}))\n"); model += x; expected.Add(model); break;
                    default: src.Append("table.insert(out, o.total + o:level())\n"); expected.Add(model + depth); break;
                }
            }
            src.Append("print(table.concat(out, ','))");
            return Outcome(src.ToString()) == string.Join(',', expected) + "\n";
        }, iter: 150, threads: 1);
    }

    // ---- abstract functions, read-only properties, is / instanceof, run-time privacy

    private const string AbstractShapes = """
        local class Shape
           name: string
           function new(name: string) self.name = name end
           abstract function area(self): number
           function describe(self): string return self.name .. " " .. self:area() end
        end
        local class Square is Shape
           private side: number
           protected tag: string
           function new(side: number) super("square") self.side = side self.tag = "sq" end
           function area(self): number return self.side * self.side end
           get perimeter(self): number return 4 * self.side end
           private function secret(self): number return self.side end
           function same(self, other: Square): boolean return self.side == other.side and self:secret() == other:secret() end
        end
        local class Cube is Square
           function new() super(2) end
           function label(self): string return self.tag .. "!" end
        end
        local class Loose is Shape end
        local s = Square.new(3)
        local u = s as {string:any}

        """;

    [Theory]
    [InlineData("print(s:describe(), s.perimeter, s:same(Square.new(3)), Cube.new():label())", "square 9\t12\ttrue\tsq!\n")]
    // abstract
    [InlineData("local x = Shape.new('x')", "compile: class Shape is abstract and cannot be created: it does not define 'area'")]
    [InlineData("local x = Loose.new('x')", "compile: class Loose is abstract and cannot be created: it does not define 'area'")]
    [InlineData("print(pcall(function(): any return ((Shape as {string:any}).area as function(any): any)(s) end))", "false\tt:23: abstract function 'area' of class 'Shape' is not defined\n")]
    // read-only
    [InlineData("s.perimeter = 4", "compile: property 'perimeter' is read-only (it has a getter and no setter)")]
    // is and instanceof
    [InlineData("local list: {Shape} = {s, Cube.new()} for _, x in ipairs(list) do if x is Cube then print('cube', x:label()) elseif x is Square then print('square', x.perimeter) end end", "square\t12\ncube\tsq!\n")]
    [InlineData("print(instanceof(s, Shape), instanceof(s, Square), instanceof(s, Cube), instanceof(Cube.new(), Square), instanceof(5, Shape), instanceof({}, Shape))", "true\ttrue\tfalse\ttrue\tfalse\tfalse\n")]
    [InlineData("print(pcall(instanceof, s, 5))", "false\tbad argument #2 to 'instanceof' (a class expected)\n")]
    // private and protected at run time, for code the checker does not see
    [InlineData("print(pcall(function(): any return u.side end))", "false\tt:23: 'side' is private in class 'Square'\n")]
    [InlineData("print(pcall(function() u.side = 9 end))", "false\tt:23: 'side' is private in class 'Square'\n")]
    [InlineData("print(pcall(function(): any return u.tag end))", "false\tt:23: 'tag' is protected in class 'Square'\n")]
    [InlineData("print(pcall(function(): any return u['secret'] end))", "false\tt:23: 'secret' is private in class 'Square'\n")]
    [InlineData("print(pcall(function(): any return (Square as {string:any}).secret end))", "false\tt:23: 'secret' is private in class 'Square'\n")]
    [InlineData("print(pcall(rawget, s as table, 'side'))", "false\t'side' is private in class 'Square'\n")]
    [InlineData("print(pcall(rawset, s as table, 'side', 1))", "false\t'side' is private in class 'Square'\n")]
    [InlineData("print(pcall(setmetatable, s as table, nil))", "false\tcannot change the metatable of an object whose class has private or protected members\n")]
    [InlineData("local keys: {string} = {} for k in pairs(u) do table.insert(keys, k) end print(table.concat(keys, ','), (json.encode(s)), next(u))", "name\t{\"name\":\"square\"}\tname\tsquare\n")]
    [InlineData("print(u.name, u.perimeter, (u.area as function(any): number)(u))", "square\t12\t9\n")]          // public members are unaffected
    public void Abstract_readonly_instanceof_and_runtime_privacy(string use, string expected)
    {
        Assert.Equal(expected, Outcome(AbstractShapes + use));
    }

    [Fact]
    public void Hidden_members_hold_across_untyped_code_compiled_code_and_other_vms()
    {
        // an untyped program: nothing is checked at compile time, everything at run time
        var engine = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = false });
        Cameo.Jit.JitLibrary.Enable(engine);
        Value[] r = engine.RunString("""
            local class Account
               private balance: integer
               protected owner: string
               function new(owner: string) self.owner = owner self.balance = 0 end
               function deposit(self, n: integer): integer self.balance = self.balance + n return self.balance end
            end
            local class Savings is Account
               function new() super("me") end
               function who(self) return self.owner end              -- protected: fine in a subclass
               function peek(self) return self.balance end           -- private to Account: refused at run time
            end
            local a = Savings.new()
            a:deposit(5)
            -- another VM: the object arrives with its class, and the rules with it
            local worker = Future.parallel(function(o)
               local ok, why = pcall(function() return o.balance end)
               return { who = o:who(), ok = ok, why = why }
            end, a):await()
            -- a compiled method keeps its class's rights; a compiled outsider has none
            Account.deposit = assert(jit.compile(Account.deposit))
            local outsider = assert(jit.compile(function(o) return o.balance end))
            local peekOk, peekWhy = pcall(a.peek, a)
            local outsiderOk, outsiderWhy = pcall(outsider, a)
            return a:who(), peekOk, peekWhy, a:deposit(1), outsiderOk, outsiderWhy, worker.who, worker.ok, worker.why
            """);
        Assert.Equal("me", r[0].ToText());
        Assert.False(r[1].IsTruthy);
        Assert.Contains("'balance' is private in class 'Account'", r[2].ToText());
        Assert.Equal(6, r[3].I);
        Assert.False(r[4].IsTruthy);
        Assert.Contains("private", r[5].ToText());
        Assert.Equal("me", r[6].ToText());
        Assert.False(r[7].IsTruthy);
        Assert.Contains("private", r[8].ToText());
    }

    [Theory]
    [InlineData("local class A abstract function f(): integer end", "an abstract function is a method")]
    [InlineData("local class A private abstract function f(self): integer end", "an abstract function cannot be private")]
    [InlineData("local class A abstract function new(self) end", "a constructor or metamethod cannot be abstract")]
    [InlineData("local class A abstract function f(self) function f(self) end end", "'f' is defined twice in class 'A'")]
    public void Malformed_abstract_functions_are_reported(string source, string message)
    {
        Assert.Contains(message, Outcome(source));
    }
}
