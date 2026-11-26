using Hedgehog;
using Hedgehog.Linq;
using Range = Hedgehog.Linq.Range;
using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests.Properties;

/// <summary>Hedgehog: a function gives the same answer in a parallel worker as it does locally.</summary>
public class ParallelProperties
{
    [Fact]
    public void A_worker_computes_what_the_same_function_computes_locally()
    {
        var host = new LuaHost();
        var gen = from k in Gen.Int64(Range.ConstantBoundedInt64())
                  from n in Gen.Int32(Range.LinearInt32(1, 60))
                  select (k, n);
        Prop.Hh(gen, t =>
        {
            Value[] r = host.Run($"""
                local k = {(t.Item1 == long.MinValue ? "math.mininteger" : t.Item1.ToString())}
                local function f(n)
                  local acc = k
                  for i = 1, n do acc = (acc * 31 + i) ~ (acc >> 7) end
                  return acc, string.rep('x', n % 7)
                end
                local a = f({t.Item2})
                return a, Future.parallel(f, {t.Item2}):get()
                """);
            Assert.Equal(r[0].I, r[1].I);
        }, tests: 60);
    }
}
