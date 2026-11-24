namespace DataMigration.Domain.Customers;

/// <summary>
/// Referencer som fakturamodtageren (køberen) har bedt om at få anført på fakturaen,
/// så modtagerens system kan håndtere fakturaen korrekt: levere elektronisk
/// (<see cref="ElectronicNumber"/>), matche mod ordre (<see cref="OrderIdentifier"/>),
/// matche mod rammeaftale (<see cref="AgreementIdentifier"/>), og rute internt
/// (<see cref="BuyerReferenceIdentifier"/>).
///
/// <para>Samme koncept optræder på <c>Contract</c>, <c>Payer</c> og <c>MeteringPoint</c>
/// for at understøtte override-hierarki: Customer-/Payer-niveau er default,
/// Contract og MeteringPoint kan overskrive med mere specifikke værdier. Når
/// property'en er <c>null</c> på et niveau, arves fra parent-niveauet.</para>
/// </summary>
public sealed record BuyerInvoicingReferences(
    string? ElectronicNumber = null,
    string? OrderIdentifier = null,
    string? AgreementIdentifier = null,
    string? BuyerReferenceIdentifier = null);
