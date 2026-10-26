namespace AIDesignStudio.SharedKernel;

/// <summary>
/// Thrown when an operation would violate a domain invariant.
/// <see cref="Code"/> is a stable, machine-readable identifier that the API layer
/// maps to an RFC 9457 problem-details <c>type</c>; the message is for humans and
/// may change without notice.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}
