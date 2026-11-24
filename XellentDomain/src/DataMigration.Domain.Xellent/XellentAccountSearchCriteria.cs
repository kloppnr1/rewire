namespace DataMigration.Domain.Xellent;

/// <summary>
/// Input criteria for <see cref="XellentAccountSearch.SearchAsync"/>. All pattern fields
/// (PhonePattern, IdentifierPattern, NameCriteria.Patterns, StreetCriteria.Patterns) are
/// SQL LIKE patterns built by the caller; the search does no wildcard handling itself.
/// </summary>
/// <param name="AsOfDate">Date used by the active-contract filter (typically <c>DateTime.Today</c>).</param>
/// <param name="MaxResults">Hard cap on returned matches. The search applies <c>Take(MaxResults)</c>.</param>
/// <param name="DataAreas">Restrict to these data areas. Null = all data areas (no filter).</param>
/// <param name="PreFilteredAccounts">Account numbers from a cheaper pre-query; when non-null, only these
/// accounts are considered. Caller uses this to skip the address join on field-only searches.</param>
/// <param name="Name">Name + name-alias filter (per-word AND across <see cref="NameCriteria.Patterns"/>).</param>
/// <param name="AccountNumber">Exact account number match.</param>
/// <param name="Email">Exact email match.</param>
/// <param name="PhonePattern">LIKE pattern applied to Phone OR CellularPhone.</param>
/// <param name="IdentifierPattern">LIKE pattern applied to CPR1/CPR2/CVR1/CVR2 (any match).</param>
/// <param name="InvoiceNumberPadded">Exact CustInvoiceJour.Invoiceid match (caller pre-pads to 20 chars).</param>
/// <param name="Street">Street/city/zip filter with up to 4 alternative patterns.</param>
/// <param name="City">City filter (OR-list of LIKE patterns — typically the full city name and a
/// stripped "base" city name like "Randers" from "Randers SØ").</param>
/// <param name="ZipCode">ZipCode filter — matches address Zipcode OR delpoint Zipcodeid via contract join.</param>
/// <param name="DarIdStrict">When set, restrict matches to accounts whose Address.ExuAddressid, delpoint Addressid,
/// delpoint-contact-info Addressid, OR installation Addressid equals this DAR id. Mutually exclusive in spirit
/// with <see cref="DarIdForRelevance"/> — callers use DarIdStrict when no other address fields are present.</param>
/// <param name="StreetRelevancePattern">LIKE pattern for relevance scoring (boosts results matching Address1).</param>
/// <param name="DarIdForRelevance">Exact DAR id for relevance scoring (boosts results matching Address.ExuAddressid
/// or Installation.Addressid).</param>
/// <param name="UniversalText">Alternative to field-specific filters: a single typed query whose semantics depend
/// on its <see cref="UniversalTextMode"/> (UUID address-id, numeric account/CPR/CVR, free text across all fields).</param>
/// <param name="IncludeInactive">When false, only accounts with an active contract today are returned.</param>
public sealed record XellentAccountSearchCriteria(
    DateTime AsOfDate,
    int MaxResults,
    IReadOnlyList<string>? DataAreas = null,
    IReadOnlyList<string>? PreFilteredAccounts = null,
    NameCriteria? Name = null,
    string? AccountNumber = null,
    string? Email = null,
    string? PhonePattern = null,
    string? IdentifierPattern = null,
    string? InvoiceNumberPadded = null,
    StreetCriteria? Street = null,
    CityCriteria? City = null,
    ZipCodeCriteria? ZipCode = null,
    string? DarIdStrict = null,
    string? StreetRelevancePattern = null,
    string? DarIdForRelevance = null,
    UniversalTextCriteria? UniversalText = null,
    bool IncludeInactive = false);

/// <param name="Patterns">SQL LIKE patterns. ALL must match (AND) across Name OR NameAlias.
/// Caller pre-prepends "%" so word-boundary semantics work.</param>
public sealed record NameCriteria(IReadOnlyList<string> Patterns);

/// <param name="Patterns">1-4 SQL LIKE patterns. ANY may match (OR) across Address1 / delpoint
/// addresses / installation addresses.</param>
/// <param name="AlsoSearchCityAndZip">When true, the first pattern is also tried against City and Zipcode
/// (mirrors the alsoSearchCityAndZip behaviour when no DarId is provided).</param>
public sealed record StreetCriteria(
    IReadOnlyList<string> Patterns,
    bool AlsoSearchCityAndZip);

/// <param name="Patterns">LIKE patterns OR'd against Address.City. Typical caller passes
/// two: the full city name as typed AND a stripped base name (e.g. "Randers" from "Randers SØ")
/// so database variations match either DAWA-style or short forms.</param>
public sealed record CityCriteria(IReadOnlyList<string> Patterns);

/// <param name="Pattern">LIKE pattern applied to Address.Zipcode OR delpoint Zipcodeid via
/// contract join (catches accounts whose customer-address zip differs from their delivery point's).</param>
public sealed record ZipCodeCriteria(string Pattern);

/// <param name="Mode">Determines which filter the search applies; see <see cref="UniversalTextMode"/>.</param>
/// <param name="Query">Raw query string. The caller is responsible for trimming and pattern-class detection;
/// the search uses this value verbatim (no wildcard wrapping — the search adds <c>%</c> internally for FreeText).</param>
public sealed record UniversalTextCriteria(UniversalTextMode Mode, string Query);

/// <summary>Universal-text search variants. Caller picks the mode based on the query's shape.</summary>
public enum UniversalTextMode
{
    /// <summary>UUID query: match accounts via delpoint.Addressid OR delpoint contact info Addressid.</summary>
    Uuid,
    /// <summary>Numeric 6-digit query: account-number exact match only (avoids matching phone numbers).</summary>
    NumericExact6,
    /// <summary>Other numeric query: exact match on Account/CPR/CVR, OR LIKE on Phone/Cellular.</summary>
    NumericMixed,
    /// <summary>Free-text query: %query% across Name/NameAlias/Account/Phone/Cellular/Email/Address1/City/Zipcode.</summary>
    FreeText,
}
