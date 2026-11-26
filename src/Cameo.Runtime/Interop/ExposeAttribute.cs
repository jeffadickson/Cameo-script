namespace Cameo.Runtime.Interop;

/// <summary>
/// Marks a public method or property on a host object as visible to scripts via
/// <see cref="ScriptEngine.Expose"/>. When any member on a type carries <see cref="ExposeAttribute"/>,
/// only explicitly marked members are exposed (opt-in mode).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = true)]
public sealed class ExposeAttribute : Attribute
{
    /// <summary>Optional script-visible name overriding the default camelCase member name.</summary>
    public string? Name { get; }

    /// <summary>When true on a <see cref="Task"/> or <see cref="Task{TResult}"/>-returning method,
    /// exposes the call as a script <c>Future</c>.</summary>
    public bool AsFuture { get; init; }

    /// <summary>Exposes the member using its default camelCase script name.</summary>
    public ExposeAttribute() { }

    /// <summary>Exposes the member under an explicit script name.</summary>
    public ExposeAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }
}

/// <summary>
/// Excludes a public method or property from being exposed to scripts via <see cref="ScriptEngine.Expose"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = true)]
public sealed class HideAttribute : Attribute;
