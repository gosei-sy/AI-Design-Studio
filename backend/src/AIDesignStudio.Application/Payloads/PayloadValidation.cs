namespace AIDesignStudio.Application.Payloads;

/// <summary>
/// One reason a payload was refused, located by JSON Pointer (RFC 6901).
///
/// <para><see cref="Pointer"/> and <see cref="Keyword"/> follow the convention
/// of the schema validator the studio runs in the browser, so a client can treat
/// an API refusal and a local one the same way: the pointer is the instance that
/// failed (for <c>required</c> and <c>additionalProperties</c>, the object that
/// is missing or carrying the property), and the keyword is the schema rule.
/// <see cref="Message"/> is for people and may change.</para>
/// </summary>
public sealed record PayloadViolation(string Pointer, string Keyword, string Message);

public enum PayloadValidationOutcome
{
    /// <summary>Accepted. <see cref="PayloadValidationResult.CanonicalJson"/> is what to store.</summary>
    Valid = 1,

    /// <summary>The body is not a JSON document at all.</summary>
    NotJson = 2,

    /// <summary>JSON, but it breaks the schema or cannot be stored faithfully.</summary>
    SchemaInvalid = 3,

    /// <summary>
    /// Well-formed and schema-valid, but it refers to something that is not
    /// there — a rule JSON Schema cannot express.
    /// </summary>
    ReferenceInvalid = 4,
}

/// <param name="CanonicalJson">
/// Non-null only when <see cref="Outcome"/> is <see cref="PayloadValidationOutcome.Valid"/>:
/// the document re-serialized from the single parse that was validated. Storing
/// this rather than the caller's bytes means the value that was checked and the
/// value that is kept cannot differ.
/// </param>
/// <param name="Truncated">
/// True when more violations existed than were collected. The list is capped so
/// a hostile payload cannot make the response arbitrarily large.
/// </param>
public sealed record PayloadValidationResult(
    PayloadValidationOutcome Outcome,
    string? CanonicalJson,
    IReadOnlyList<PayloadViolation> Violations,
    bool Truncated)
{
    public bool IsValid => Outcome == PayloadValidationOutcome.Valid;
}
