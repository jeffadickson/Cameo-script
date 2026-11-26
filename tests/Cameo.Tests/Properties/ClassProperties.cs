using Hedgehog;
using Hedgehog.Linq;
using Xunit;
using Range = Hedgehog.Linq.Range;

namespace Cameo.Tests.Properties;

/// <summary>Hedgehog: a class computes what the hand-written record and metatable compute.</summary>
public class ClassProperties
{
    private static string Program(string source)
    {
        var engine = new ScriptEngine();
        var output = new StringWriter { NewLine = "\n" };
        engine.State.Stdout = output;
        RunResult r = engine.TryRunString(source);
        return r is RunCompleted ? output.ToString() : r is CompileFailed c ? "compile:" + c.Diagnostics[0] : r is RuntimeFailed f ? "error:" + f.Message : "?";
    }

    [Fact]
    public void A_class_runs_the_same_as_the_record_it_stands_for()
    {
        // Hedgehog: a point class against the hand-written record and metatable
        var gen = from x in Gen.Int64(Range.LinearFromInt64(0, -1000, 1000))
                  from y in Gen.Int64(Range.LinearFromInt64(0, -1000, 1000))
                  from k in Gen.Int64(Range.LinearFromInt64(1, -20, 20))
                  select (x, y, k);
        Prop.Hh(gen, t =>
        {
            string viaClass = Program($$$"""
                local class P
                   x: integer
                   y: integer
                   function new(x: integer, y: integer) self.x, self.y = x, y end
                   function scale(self, k: integer): P return P.new(self.x * k, self.y * k) end
                   function __add(a: P, b: P): P return P.new(a.x + b.x, a.y + b.y) end
                   function __tostring(self): string return self.x .. "," .. self.y end
                end
                print(tostring(P.new({{{t.x}}}, {{{t.y}}}):scale({{{t.k}}}) + P.new(1, 1)))
                """);
            string byHand = Program($$$"""
                local record P
                   x: integer
                   y: integer
                   metamethod __add: function(P, P): P
                end
                local mt: metatable<P>
                local function new(x: integer, y: integer): P return setmetatable({x = x, y = y} as P, mt) end
                mt = {
                   __add = function(a: P, b: P): P return new(a.x + b.x, a.y + b.y) end,
                   __tostring = function(p: P): string return p.x .. "," .. p.y end,
                }
                local function scale(p: P, k: integer): P return new(p.x * k, p.y * k) end
                print(tostring(scale(new({{{t.x}}}, {{{t.y}}}), {{{t.k}}}) + new(1, 1)))
                """);
            Assert.Equal(byHand, viaClass);
            Assert.Equal($"{t.x * t.k + 1},{t.y * t.k + 1}\n", viaClass);
        }, tests: 60);
    }
}
