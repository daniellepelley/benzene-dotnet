namespace Benzene.Abstractions.Validation;

/// <summary>
/// Applied to a message handler type to override the result status a failed validation produces
/// for that handler (e.g. <c>bad-request</c> instead of the default <c>validation-error</c>).
/// Class-only: the sole readers (<c>Benzene.FluentValidation</c>, <c>Benzene.DataAnnotations</c>,
/// <c>Benzene.JsonSchema</c>) resolve the attribute off the resolved handler <see cref="Type"/> via
/// <see cref="System.Reflection.CustomAttributeExtensions.GetCustomAttribute{T}(System.Reflection.MemberInfo)"/>
/// against the class - a method-level attribute is never read, so <see cref="AttributeTargets.Method"/>
/// was dropped from the allowed targets (pre-1.0, source-breaking only for code that was already
/// silently ignored).
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class ValidationStatusAttribute : Attribute
{
    public string Status { get; }

    public ValidationStatusAttribute(string status)
    {
        Status = status;
    }
}
