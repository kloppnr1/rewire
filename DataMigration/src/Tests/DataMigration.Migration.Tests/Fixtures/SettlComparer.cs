using System.Text.Json;
using DataMigration.Migration.Runners;
using DataMigration.SettlApi.Models;

namespace DataMigration.Migration.Tests.Fixtures;

/// <summary>
/// Compares extraction output against Settl production data.
/// Fields not returned by Settl GET responses are excluded from comparison.
/// </summary>
public static class SettlComparer
{
    public record Diff(string Entity, string Field, string? Extracted, string? Settl);

    /// <summary>
    /// Compares extracted customers against Settl CustomerResponse (GET /api/v2/customers/{id}).
    /// Settl does not return: SupplementaryName (Name2), Fax, AddressIdentifier.
    /// Phone/Cellphone are inconsistently mapped — excluded.
    /// </summary>
    public static List<Diff> CompareCustomers(
        List<CreateCustomerDto> extracted,
        Func<string, SettlAccountSeedData?> loadSettl,
        Dictionary<string, ExpectedDiff[]>? knownDiffs = null)
    {
        var allDiffs = new List<Diff>();

        foreach (var customer in extracted)
        {
            if (customer.CustomerNumber == null) continue;
            var settlData = loadSettl(customer.CustomerNumber);
            if (settlData?.CustomerFull == null) continue;

            var settl = settlData.CustomerFull;
            var known = (knownDiffs?.GetValueOrDefault(customer.CustomerNumber) ?? [])
                .ToDictionary(d => d.Field, d => d.ExpectedSettlValue);

            void Check(string field, string? a, string? b)
            {
                a = Normalize(a); b = Normalize(b);
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return;
                if (known.TryGetValue(field, out var exp) && string.Equals(b, exp, StringComparison.OrdinalIgnoreCase)) return;
                allDiffs.Add(new Diff(customer.CustomerNumber, field, a, b));
            }

            Check("CustomerType", customer.CustomerType, settl.CustomerType);
            Check("VatIdentificationNumber", customer.VatIdentificationNumber, settl.VatIdentificationNumber);
            Check("NationalIdentificationNumber", customer.NationalIdentificationNumber, settl.NationalIdentificationNumber);

            var eAddr = customer.Address;
            var sAddr = settl.Address;
            Check("FullName", eAddr?.PostalAddress?.Name1?.FullName, sAddr?.PostalAddress?.Name1?.FullName);
            Check("StreetName", eAddr?.PostalAddress?.PhysicalAddress?.StreetName, sAddr?.PostalAddress?.PhysicalAddress?.StreetName);
            Check("BuildingNumber", eAddr?.PostalAddress?.PhysicalAddress?.BuildingNumber, sAddr?.PostalAddress?.PhysicalAddress?.BuildingNumber);
            Check("PostalCode", eAddr?.PostalAddress?.PhysicalAddress?.PostalCode, sAddr?.PostalAddress?.PhysicalAddress?.PostalCode);
            Check("City", eAddr?.PostalAddress?.PhysicalAddress?.City, sAddr?.PostalAddress?.PhysicalAddress?.City);
            Check("CountryCode", eAddr?.PostalAddress?.PhysicalAddress?.CountryCode, sAddr?.PostalAddress?.PhysicalAddress?.CountryCode);
            Check("Email", eAddr?.Email, sAddr?.Email);
        }

        return allDiffs;
    }

    /// <summary>
    /// Compares extracted payers against Settl PayerResponse (GET /api/v1/Payers/{id}).
    /// </summary>
    public static List<Diff> ComparePayers(
        List<CreatePayerDto> extracted,
        Dictionary<Guid, string> customerIdToNumber,
        Func<string, SettlAccountSeedData?> loadSettl)
    {
        var allDiffs = new List<Diff>();

        foreach (var payer in extracted)
        {
            if (!customerIdToNumber.TryGetValue(payer.CustomerId, out var customerNumber)) continue;
            var settlData = loadSettl(customerNumber);
            if (settlData?.PayersFull is not { Count: > 0 }) continue;

            var settl = settlData.PayersFull[0];
            var payerId = payer.PayerInformation?.PayerIdentifier ?? customerNumber;

            void Check(string field, string? a, string? b)
            {
                a = Normalize(a); b = Normalize(b);
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return;
                allDiffs.Add(new Diff(payerId, field, a, b));
            }

            // PayerIdentifier excluded: Settl production has StartingPayerId values ("10010"-"10021")
            // but current extraction uses AccountNum. This is a known migration-era difference.
            Check("Payer.FullName", payer.PayerInformation?.PostalAddress?.Name1?.FullName, settl.PayerInformation?.PostalAddress?.Name1?.FullName);
            Check("Payer.StreetName", payer.PayerInformation?.PostalAddress?.PhysicalAddress?.StreetName, settl.PayerInformation?.PostalAddress?.PhysicalAddress?.StreetName);
            Check("Payer.PostalCode", payer.PayerInformation?.PostalAddress?.PhysicalAddress?.PostalCode, settl.PayerInformation?.PostalAddress?.PhysicalAddress?.PostalCode);
            Check("Payer.City", payer.PayerInformation?.PostalAddress?.PhysicalAddress?.City, settl.PayerInformation?.PostalAddress?.PhysicalAddress?.City);
            Check("InvoiceChannel", payer.PayerBilling.InvoiceChannel, settl.PayerBilling?.InvoiceChannel);
            Check("PaymentChannel", payer.PayerBilling.PaymentChannel, settl.PayerBilling?.PaymentChannel);
            Check("PayerType", payer.PayerBilling.PayerType, settl.PayerBilling?.PayerType);
            Check("Billing.Email", payer.PayerBilling.Email, settl.PayerBilling?.Email);
            Check("ElectronicNumber", payer.PayerBilling.ElectronicNumber, settl.PayerBilling?.ElectronicNumber);
        }

        return allDiffs;
    }

    /// <summary>
    /// Compares extracted metering points against Settl raw JSON (GET /api/v3/meteringPoints/{id}).
    /// </summary>
    public static List<Diff> CompareMeteringPoints(
        List<MeteringPointDto> extracted,
        Dictionary<string, JsonElement> settlMpByIdentification)
    {
        var allDiffs = new List<Diff>();

        foreach (var mp in extracted)
        {
            var mpId = mp.Identification?.MeteringPointIdentification;
            if (mpId == null || !settlMpByIdentification.TryGetValue(mpId, out var settl)) continue;

            void Check(string field, string? a, string? b)
            {
                a = Normalize(a); b = Normalize(b);
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return;
                allDiffs.Add(new Diff(mpId, field, a, b));
            }

            Check("InstallationNumber", mp.Identification?.InstallationNumber, GetStr(settl, "identification", "installationNumber"));
            Check("MP.StreetName", mp.InstallationAddress?.Address?.StreetName, GetStr(settl, "installationAddress", "address", "streetName"));
            Check("MP.PostalCode", mp.InstallationAddress?.Address?.PostalCode, GetStr(settl, "installationAddress", "address", "postalCode"));
            Check("MP.City", mp.InstallationAddress?.Address?.City, GetStr(settl, "installationAddress", "address", "city"));
            Check("Consumer.CustomerType", mp.Consumer?.CustomerType, GetStr(settl, "consumer", "customerType"));
            Check("Consumer.Name", mp.Consumer?.FirstConsumerParty?.Name?.FullName, GetStr(settl, "consumer", "firstConsumerParty", "name", "fullName"));
            Check("GridArea", mp.Grid?.GridAreaCode, GetStr(settl, "grid", "gridAreaIdentification"));
            Check("SettlementMethod", mp.SettlementMethod?.SettlementMethod, GetStr(settl, "settlementMethod", "settlementMethod"));
            Check("ConnectionStatus", mp.ConnectionInfo?.ConnectionStatus, GetStr(settl, "connectionInfo", "connectionStatus"));
        }

        return allDiffs;
    }

    /// <summary>
    /// Builds a lookup from MeteringPointIdentification → JsonElement from all Settl seed data.
    /// </summary>
    public static Dictionary<string, JsonElement> BuildMeteringPointLookup(
        string[] accounts, Func<string, SettlAccountSeedData?> loadSettl)
    {
        var lookup = new Dictionary<string, JsonElement>();
        foreach (var account in accounts)
        {
            var settlData = loadSettl(account);
            if (settlData == null) continue;
            foreach (var raw in settlData.MeteringPointsRaw)
            {
                try
                {
                    var doc = JsonDocument.Parse(raw);
                    var mpIdent = GetStr(doc.RootElement, "identification", "meteringPointIdentification");
                    if (mpIdent != null && !lookup.ContainsKey(mpIdent))
                        lookup[mpIdent] = doc.RootElement.Clone();
                }
                catch { }
            }
        }
        return lookup;
    }

    private static string? Normalize(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? GetStr(JsonElement el, params string[] path)
    {
        var current = el;
        foreach (var p in path)
        {
            if (!current.TryGetProperty(p, out current)) return null;
            if (current.ValueKind == JsonValueKind.Null) return null;
        }
        return current.GetString();
    }
}
