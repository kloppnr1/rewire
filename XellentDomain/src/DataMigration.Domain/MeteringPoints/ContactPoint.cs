using DataMigration.Domain.Common;

namespace DataMigration.Domain.MeteringPoints;

/// <summary>
/// A contact point on a metering point — typically a person or address associated with the site.
/// <see cref="PrimaryName"/> is required (first line of the contact's name);
/// <see cref="SecondaryName"/> is optional (e.g. department, attention-of, c/o-line).
/// </summary>
public sealed record ContactPoint(
    ContactPointType Type,
    string PrimaryName,
    string? SecondaryName,
    Address Address,
    IReadOnlyList<CommunicationLine>? CommunicationLines = null);

/// <summary>
/// A communication channel on a metering-point contact: phone, mobile or email.
/// <see cref="Type"/> matches the Settl protocol values ("Phone", "Mobile", "Email").
/// </summary>
public sealed record CommunicationLine(string Type, string Value);

/// <summary>
/// Kind of contact point on a metering point.
/// </summary>
public enum ContactPointType
{
    Technical,
    Legal
}
