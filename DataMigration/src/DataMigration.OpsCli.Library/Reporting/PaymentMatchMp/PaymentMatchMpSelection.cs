using DataMigration.Core;
using DataMigration.Core.Reporting;
using DataMigration.XellentData.Reporting;

namespace DataMigration.OpsCli.Reporting.PaymentMatchMp;

public sealed record PaymentMpCustomer(string Account, string Name, bool IsBusiness)
{
    // Same first-disponent CVR convention as the Xellent customer mapper. Do not log invalid identifiers.
    public static bool IsBusinessCvr(string? cvr) => cvr is { Length: 8 } && cvr.All(char.IsDigit);
}

public sealed record PaymentMpSource(string Account, string Contract, string Commodity,
    string? MainContract, DateTime Start, DateTime End, int PbsNumber, int PbsCode,
    DateTime PbsDate, string? MobileId, DateTime? MobileCancel)
{
    public bool ValidOn(DateTime on) => Start.Date <= on.Date && (End == new DateTime(1900, 1, 1) || End.Date >= on.Date);
    public bool HasPbs => PbsContractSelection.CountsAsAgreement(PbsCode, PbsDate, false, null);
    public bool HasMobile => !string.IsNullOrWhiteSpace(MobileId) && MobileCancel == new DateTime(1900, 1, 1);
    public bool IsWaterOrHeat => Commodity.Split('/').Any(c => c.Equals(MigrationConstants.DeliveryCategoryWater, StringComparison.OrdinalIgnoreCase)
        || c.Equals(MigrationConstants.DeliveryCategoryHeating, StringComparison.OrdinalIgnoreCase));
    public bool Eligible(string mode) => mode == "pbs" ? HasPbs : HasMobile;
    public string Agreement(string mode) => mode == "pbs" ? PbsNumber.ToString() : MobileId!;
}

public sealed record PaymentMpSnapshot(IReadOnlyList<PaymentMpCustomer> Customers, IReadOnlyList<PaymentMpSource> Contracts)
{
    public IReadOnlyDictionary<string, int> PbsOwnAgreementCounts { get; init; } = new Dictionary<string, int>();
}
public sealed record PaymentMpSelection(XellentDeliveryOnDate.Delivery Owner, PaymentMpCustomer Customer,
    PaymentMpSource Own, IReadOnlyList<PaymentMpSource> Agreements, bool Ambiguous,
    IReadOnlyList<PaymentMpSource> BusinessErrors)
{
    public bool OwnHasChildren { get; init; }
    public PaymentMpSource? Main { get; init; }
    public string Mode { get; init; } = "pbs";
    public string NoAgreementExplanation => string.IsNullOrWhiteSpace(Own.MainContract)
        ? "Elkontrakten har ingen hovedkontrakt og ingen relevant egen betalingsaftale."
        : "Ingen relevant betalingsaftale på en gyldig hovedkontrakt på samme kunde. Ingen anden kontrakt bruges.";
    public const string BusinessErrorCode = "BusinessPaymentOnlyOnWaterHeat";
    public string? ErrorCode => BusinessErrors.Count > 0 ? BusinessErrorCode : null;
    public string ErrorExplanation => BusinessErrors.Count == 0 ? "" :
        "Fejl: Erhvervskunden har ingen " + (Mode == "pbs" ? "PBS" : "MobilePay") + "-aftale på elkontrakten, men har på vand/varme: "
        + string.Join("; ", BusinessErrors.Select(s => $"{s.Contract} ({s.Commodity})"
            + (Mode == "mb" && s.HasMobile ? $": MobilePay {s.MobileId}" : "")));

    public static PaymentMpSelection Select(XellentDeliveryOnDate.Delivery owner, PaymentMpCustomer customer,
        IReadOnlyList<PaymentMpSource> contracts, DateTime on, string mode)
    {
        var own = contracts.SingleOrDefault(c => c.Account == owner.Account && c.Contract == owner.ContractNumber);
        if (own is null || !own.ValidOn(on))
            throw new InvalidOperationException("En historisk ejerkontrakt kunne ikke genfindes med de forventede datoer i Xellent. Kør match igen.");
        var valid = contracts.Where(c => c.Account == owner.Account && c.ValidOn(on)).ToArray();
        var main = string.IsNullOrWhiteSpace(own.MainContract) || own.MainContract == own.Contract ? null
            : contracts.SingleOrDefault(c => c.Account == own.Account && c.Contract == own.MainContract);
        PaymentMpSource[] choices = string.IsNullOrWhiteSpace(own.MainContract)
            ? own.Eligible(mode) ? [own] : []
            : main is not null && main.ValidOn(on) && main.Eligible(mode)
                && (mode != "pbs" || main.PbsNumber != 0) ? [main] : [];
        var errors = customer.IsBusiness && !own.Eligible(mode)
            ? valid.Where(c => c.Contract != own.Contract && c.IsWaterOrHeat && c.Eligible(mode)).ToArray() : [];
        return new(owner, customer, own, choices, false, errors)
            { Main = main, Mode = mode,
                OwnHasChildren = contracts.Any(c => c.Account == own.Account && c.MainContract == own.Contract) };
    }

    public PbsContractQueryResult PbsQuery(PaymentMpSource? selectedSource = null)
    {
        var source = selectedSource ?? Agreements.FirstOrDefault();
        var candidate = new PbsContractCandidate(Own.Contract, Own.Account, Customer.Name,
            source?.PbsNumber ?? 0, Owner.MeteringPoint, Own.MainContract,
            Own.Start, Own.End, source?.PbsDate ?? new DateTime(1900, 1, 1), source?.PbsCode ?? 0, OwnHasChildren);
        var query = PbsContractSelection.Select([candidate], false, null);
        // Selection dates were already checked against the requested cutover. The shared matcher
        // keeps the owning contract/MP keys; the source contract is provenance, not a substitute key.
        foreach (var row in query.Contracts)
        {
            row.PbsAgreementSourceContract = source?.Contract == Own.Contract ? null : source?.Contract;
            row.PbsAgreementSourceDeliveryCategory = source?.Commodity;
        }
        return query;
    }
}
