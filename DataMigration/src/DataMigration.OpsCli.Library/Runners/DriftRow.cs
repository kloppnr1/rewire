namespace DataMigration.OpsCli.Runners;

/// <summary>
/// One row in the unified "forkert-i-Settl" drift report (<c>report-drift</c>).
/// A single shape shared across the MP-scan, payer-scan and payer fan-out checks:
/// the common core (<see cref="EntityType"/>/<see cref="Identification"/>/
/// <see cref="SettlId"/>/<see cref="Status"/>/<see cref="Årsager"/>) plus a compact
/// entity-specific <see cref="Detalje"/> blob (semicolon-separated <c>key=value</c>),
/// so MP grid-mismatches, payer format-leaks and fan-out deltas live in one table
/// without a wide, sparse column union.
/// </summary>
/// <param name="EntityType">"MP", "Payer" or "Fan-out".</param>
/// <param name="Identification">MP identification / payer identification / representative account.</param>
/// <param name="SettlId">Settl GUID of the entity (empty when not applicable).</param>
/// <param name="Status">"OK", "NeedsUpdate" or "Error".</param>
/// <param name="Årsager">Comma/plus-separated reason codes (same vocabulary as the source scans).</param>
/// <param name="Detalje">Semicolon-separated <c>key=value</c> blob of the mismatched values.</param>
public sealed record DriftRow(
    string EntityType,
    string Identification,
    string SettlId,
    string Status,
    string Årsager,
    string Detalje);
