using System.IO.Compression;
using System.Text.Json;
using DataMigration.Core.Models;
using DataMigration.Migration.Runners;
using DataMigration.OpsCli.Reporting;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// Bygger den data-payload som den virtualiserede HTML-rapport læser i browseren.
///
/// Rapporten er én selvstændig fil der skal kunne åbnes via <c>file://</c>. Browsere
/// blokerer <c>fetch</c> mod <c>file://</c>, så data kan ikke ligge i sidefiler — i stedet
/// gzip'es og base64-encodes hver del for sig og indlejres i HTML'en. Hvert shard
/// komprimeres separat, så et klik på én kunde kun koster dekomprimering af dét shard;
/// ét samlet blob ville tvinge hele payloaden gennem <c>DecompressionStream</c> ved
/// første opslag.
/// </summary>
internal static class ReportPayload
{
    /// <summary>Antal Settl-kunder (grupperinger) pr. shard.</summary>
    public const int CustomersPerShard = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serialiserer, gzip'er og base64-encoder. Base64-alfabetet er sikkert inde i et script-tag.</summary>
    public static string Pack(object payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(json, 0, json.Length);
        return Convert.ToBase64String(ms.ToArray());
    }

    // ---- Række-typer i træet (skal matche konstanterne i klient-JS'en) ----
    public const int KindOwner = 0;        // ejer-/gruppe-overskrift, foldbar
    public const int KindAccount = 1;      // enkeltstående konto
    public const int KindMergeHeader = 2;  // "N konti sammenlagt"
    public const int KindRepresentative = 3;
    public const int KindMergedAway = 4;
    public const int KindContract = 5;
    public const int KindMeteringPoint = 6;

    /// <summary>
    /// Én trærække. Felterne er 1-2 tegn fordi arrayet har ~150.000 elementer ved all-active;
    /// gzip komprimerer de gentagne nøgler væk, men den ukomprimerede JS-heap gør ikke.
    /// </summary>
    public sealed class Row
    {
        public int K { get; init; }
        public int D { get; init; }
        public string? L { get; init; }   // label (ejer-gruppe, kontraktnavn)
        public string? A { get; init; }   // kontonummer
        public string? Nm { get; init; }  // kundenavn
        public string? Cv { get; init; }  // CVR
        public string? G { get; init; }   // MP-identifikation (GSRN)
        public int? N { get; init; }      // MP-antal / antal sammenlagte konti
        public int? J { get; init; }      // 1 = samlefaktura-MP
        public int B { get; init; } = -1; // blok-indeks (-1 = ejer-overskrift, hører ikke til en blok)
    }

    /// <summary>Index-chunket: alt der skal være til stede for at rendere og søge i træet.</summary>
    public sealed class TreeIndex
    {
        public List<Row> Rows { get; init; } = [];
        /// <summary>[række-indeks for overskriften, antal efterfølgende rækker i gruppen] — bruges til fold.</summary>
        public List<int[]> Groups { get; init; } = [];
        /// <summary>Repræsentant-kontonummer pr. blok. Alle konto-rækker i en blok åbner denne kunde.</summary>
        public List<string> BlockAccount { get; init; } = [];
        /// <summary>Shard-indeks pr. blok.</summary>
        public List<int> BlockShard { get; init; } = [];
        /// <summary>Lowercase søgetekst pr. blok (kontonumre, navne, CVR, MP-id'er, kontraktnavne).</summary>
        public List<string> BlockSearch { get; init; } = [];
    }

    /// <summary>
    /// Detaljedata for de kunder der ligger i ét shard. Nøglet på repræsentant-kontonummer,
    /// fordi det er dét træets konto-rækker slår op med.
    /// </summary>
    public sealed class Shard
    {
        /// <summary>acct → kunde</summary>
        public Dictionary<string, object> C { get; init; } = [];
        /// <summary>acct → betalere</summary>
        public Dictionary<string, List<object>> P { get; init; } = [];
        /// <summary>gsrn → målepunkt</summary>
        public Dictionary<string, object> M { get; init; } = [];
        /// <summary>acct → alle gruppens MP-id'er</summary>
        public Dictionary<string, List<string>> Am { get; init; } = [];
        /// <summary>gsrn → kontraktnavn</summary>
        public Dictionary<string, string> Cn { get; init; } = [];
        /// <summary>samlefaktura-MP'er i dette shard</summary>
        public List<string> J { get; init; } = [];
    }

    /// <summary>Projicerer en Settl-kunde til den slanke form modalen bruger.</summary>
    /// <param name="unknown">Sat når kunden hverken har CVR eller CPR: de rå Xellent-værdier plus
    /// hvorfor. For alle andre er den null, og modalen viser som før.</param>
    public static object SlimCustomer(CreateCustomerDto c, string? ownerLabel, UnknownIdentitySource? unknown = null)
    {
        var a = c.Address;
        return new
        {
            c.CustomerNumber,
            c.CustomerType,
            c.VatIdentificationNumber,
            c.NationalIdentificationNumber,
            Unknown = unknown is null ? null : new
            {
                unknown.CauseText,
                unknown.RawCvr,
                unknown.RawCpr,
                unknown.AlternativeNumber,
                unknown.AlternativeNumberCode,
            },
            Addr = a is null ? null : new
            {
                Name = a.PostalAddress?.Name1?.FullName,
                SupName = a.PostalAddress?.Name2?.FullName,
                Street = a.PostalAddress?.PhysicalAddress?.StreetName,
                BldNr = a.PostalAddress?.PhysicalAddress?.BuildingNumber,
                Floor = a.PostalAddress?.PhysicalAddress?.BuildingFloor,
                Room = a.PostalAddress?.PhysicalAddress?.RoomIdentification,
                Zip = a.PostalAddress?.PhysicalAddress?.PostalCode,
                City = a.PostalAddress?.PhysicalAddress?.City,
                Country = a.PostalAddress?.PhysicalAddress?.CountryCode,
                Dar = a.PostalAddress?.PhysicalAddress?.AddressIdentifier?.Identification,
                a.Phone,
                a.Cellphone,
                a.Fax,
                a.Email,
                Start = a.Start.ToString("yyyy-MM-dd"),
            },
            OwnerLabel = ownerLabel,
        };
    }

    /// <summary>Projicerer en betaler til den slanke form modalen bruger.</summary>
    public static object SlimPayer(CreatePayerDto p) => new
    {
        PayerId = p.PayerInformation?.PayerIdentifier,
        Name = p.PayerInformation?.PostalAddress?.Name1?.FullName,
        Street = p.PayerInformation?.PostalAddress?.PhysicalAddress?.StreetName,
        BldNr = p.PayerInformation?.PostalAddress?.PhysicalAddress?.BuildingNumber,
        Zip = p.PayerInformation?.PostalAddress?.PhysicalAddress?.PostalCode,
        City = p.PayerInformation?.PostalAddress?.PhysicalAddress?.City,
        Dar = p.PayerInformation?.PostalAddress?.PhysicalAddress?.AddressIdentifier?.Identification,
        Email = p.PayerBilling?.Email,
        Ean = p.PayerBilling?.ElectronicNumber,
        InvCh = p.PayerBilling?.InvoiceChannel,
        PayCh = p.PayerBilling?.PaymentChannel,
        PayerType = p.PayerBilling?.PayerType,
        Cvr = p.PayerBilling?.VatIdentificationNumber,
        BuyerRef = p.PayerBilling?.BuyerReferenceIdentifier,
        AgrId = p.PayerBilling?.AgreementIdentifier,
        Start = p.PayerBilling?.Start.ToString("yyyy-MM-dd"),
    };

    /// <summary>Projicerer et målepunkt til den slanke form modalen bruger.</summary>
    public static object SlimMeteringPoint(MeteringPointDto m, MpBillingOverrideInfo? billingOverride)
    {
        var a = m.InstallationAddress?.Address;
        var meter = m.MeterCharacteristics?.Meters.FirstOrDefault();
        var consName = m.Consumer?.FirstConsumerParty?.Name?.FullName
            ?? string.Join(" ", new[]
                {
                    m.Consumer?.FirstConsumerParty?.Name?.FirstName,
                    m.Consumer?.FirstConsumerParty?.Name?.LastName
                }.Where(s => !string.IsNullOrEmpty(s)));

        return new
        {
            Gsrn = m.Identification?.MeteringPointIdentification,
            InstNr = m.Identification?.InstallationNumber,
            Addr = a is null ? null : new
            {
                a.StreetName,
                a.StreetCode,
                a.BuildingNumber,
                a.BuildingFloor,
                a.RoomIdentification,
                a.PostalCode,
                a.City,
                a.CitySubDivisionName,
                a.MunicipalityCode,
                a.CountryCode,
            },
            AddrStart = m.InstallationAddress?.Start,
            ConsName = string.IsNullOrEmpty(consName) ? null : consName,
            ConsType = m.Consumer?.CustomerType,
            ConsCvr = m.Consumer?.FirstConsumerParty?.VatIdentificationNumber,
            ConsCpr = m.Consumer?.FirstConsumerParty?.NationalIdentificationNumber,
            Meter = meter is null ? null : new
            {
                meter.MeterIdentifier,
                meter.MeterType,
                meter.DigitsOfPrecision,
                meter.Decimals,
                meter.MultiplicationFactor,
                meter.MeterReadingOccurrence,
            },
            MeterStart = m.MeterCharacteristics?.Start,
            MeterEnd = m.MeterCharacteristics?.End,
            GridBa = m.Grid?.MarketBalanceAreaName,
            GridArea = m.Grid?.GridAreaCode,
            Settl = m.SettlementMethod?.SettlementMethod,
            ConnStatus = m.ConnectionInfo?.ConnectionStatus,
            ConnStart = m.ConnectionInfo?.Start,
            InDelivery = m.DeliveryStatus?.InDelivery,
            DelStart = m.DeliveryStatus?.Start,
            JointInvNo = m.JointInvoiceNumber,
            AdditionalInfo1 = m.AdditionalInfo1,
            // E-faktura-reference override skubbet til MP-niveau (null = arves fra payer; EAN aldrig på MP).
            OrderId = billingOverride?.OrderIdentifier,
            AgreementId = billingOverride?.AgreementIdentifier,
            BuyerRef = billingOverride?.BuyerReferenceIdentifier,
            ContactPoints = m.Consumer?.ContactPoints?.Count > 0
                ? m.Consumer.ContactPoints.Select(cp => new
                {
                    Category = cp.Category?.Category,
                    Name1 = cp.Address?.PostalAddress?.Name1?.FullName,
                    Name2 = cp.Address?.PostalAddress?.Name2?.FullName,
                    Street = cp.Address?.PostalAddress?.PhysicalAddress?.StreetName,
                    BuildingNumber = cp.Address?.PostalAddress?.PhysicalAddress?.BuildingNumber,
                    Floor = cp.Address?.PostalAddress?.PhysicalAddress?.BuildingFloor,
                    Room = cp.Address?.PostalAddress?.PhysicalAddress?.RoomIdentification,
                    PostalCode = cp.Address?.PostalAddress?.PhysicalAddress?.PostalCode,
                    City = cp.Address?.PostalAddress?.PhysicalAddress?.City,
                    CountryCode = cp.Address?.PostalAddress?.PhysicalAddress?.CountryCode,
                    CommunicationLines = cp.CommunicationLines?.Select(cl => new { cl.Type, cl.Value }).ToList(),
                }).ToList<object>()
                : null,
        };
    }
}
