using Cameo.Runtime.Types;
using Xunit;

namespace Cameo.Tests;

public class PreludeTests
{
    // A mistake in the built-in declaration files would otherwise show up only as every
    // program failing with "unknown variable: print".
    [Fact]
    public void The_built_in_declarations_load_without_errors()
    {
        var checker = new TypeChecker();
        Assert.True(checker.PreludeErrors.Count == 0, string.Join("\n", checker.PreludeErrors));
    }
}
