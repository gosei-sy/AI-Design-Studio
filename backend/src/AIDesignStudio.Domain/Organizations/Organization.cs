using AIDesignStudio.SharedKernel;

namespace AIDesignStudio.Domain.Organizations;

/// <summary>
/// The tenant boundary. Every project, asset and job already carries an
/// <c>OrganizationId</c>; this is the aggregate that id refers to.
///
/// <para>Deliberately almost empty. An organization in the MVP is a name and an
/// identity — nothing about billing, domains or settings, because none of that is
/// decided (D-5) and a speculative column is harder to delete than to add.</para>
///
/// <para>Organizations are created by **trusted operator provisioning only**
/// (ADR-0009). There is no HTTP endpoint that creates one, and public
/// registration cannot reach this type at all.</para>
/// </summary>
public sealed class Organization : AggregateRoot
{
    private Organization() { } // EF Core

    public string Name { get; private set; } = string.Empty;

    public static Organization Create(string name, IClock clock)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException(
                "organization.name_required", "An organization must have a name.");
        }

        var trimmed = name.Trim();

        if (trimmed.Length > 200)
        {
            throw new DomainException(
                "organization.name_too_long",
                $"An organization name may be at most 200 characters; got {trimmed.Length}.");
        }

        return new Organization
        {
            Name = trimmed,
            CreatedAt = clock.UtcNow,
        };
    }

    public void Rename(string name, IClock clock)
    {
        var renamed = Create(name, clock);

        Name = renamed.Name;
        Touch(clock);
    }
}
