namespace DataMigration.OpsCli.Services.Reconcile;

/// <summary>
/// Change-of-supplier (COS) date helpers for the reconcile runner. The reconcile runner derives a
/// per-account COS as <c>min</c> over the account's MPs and loads Xellent once per distinct
/// account-COS at <c>refDate = COS-1</c>.
/// </summary>
public static class CosSlicer
{
    /// <summary>Xellent <c>Contractenddate = COS - 1</c>, so the create-true extract for MPs whose
    /// delivery starts on <paramref name="cos"/> is loaded with reference date COS-1.</summary>
    public static DateOnly RefDateFor(DateOnly cos) => cos.AddDays(-1);
}

/// <summary>
/// Optional inclusive change-of-supplier date window that scopes WHICH Settl InDelivery MPs the
/// reconcile run selects. Both bounds are optional — a null bound is open-ended on that side. Only
/// the MP-selection step consults this; per-account COS derivation, refDate and the diff are
/// untouched.
/// </summary>
public readonly record struct CosPeriod(DateOnly? From, DateOnly? To)
{
    /// <summary>True when neither bound is set — selection is not scoped.</summary>
    public bool IsUnbounded => From is null && To is null;

    /// <summary>Inclusive membership test for a metering point's COS date.</summary>
    public bool Contains(DateOnly cos) =>
        (From is not { } f || cos >= f) && (To is not { } t || cos <= t);
}
