namespace DataMigration.Migration.Configuration;

/// <summary>
/// The create path's charger table, compiled for lookup: an El-ekstern metering point whose account
/// holds an EV charger resolves through the charger's own live product, not just its own.
///
/// <para>Two productnums on two different contract parts of the same account decide one Settl
/// contract — the electricity product (<c>V LadVariP</c>/<c>V LadVarUA</c>) and the charger's live
/// <c>Charge_Abo</c> product. Unlike <see cref="TemplateProductContractRules"/> this is a plain
/// exact-match table: both keys are Xellent productnums, there is no wildcard and no per-customer
/// split, so a nested string map is enough. <see cref="NoChargeKey"/> stands in for "the account has
/// no live Charge/Charge_Abo contract part at all" — the charger points at a metering point but the
/// charger contract itself hasn't started yet.</para>
/// </summary>
public sealed class ElectricityChargerContractRules
{
    public const string HomeChargeOwnedContract = "HomeChargeLadeEjeMel(O HCEjeMEL)";
    public const string PendingInstallationContract = "LadeAbo_Afventer_Installation";
    // Confirmed 2026-10-01 for ordinary and UA electricity periods. UA needs manual follow-up.
    public static string? ResolveConfirmedHomeCharge(string electricityProduct, string chargerProduct) =>
        (electricityProduct.Equals("V LadVariP", StringComparison.OrdinalIgnoreCase)
            || electricityProduct.Equals("V LadVarUA", StringComparison.OrdinalIgnoreCase))
        && (chargerProduct.Equals("O HCEjeUL3", StringComparison.OrdinalIgnoreCase)
            || chargerProduct.Equals("O HCEjeUL4", StringComparison.OrdinalIgnoreCase)
            || chargerProduct.Equals("O HCEjeUL5", StringComparison.OrdinalIgnoreCase)) ? HomeChargeOwnedContract : null;
    /// <summary>
    /// Reserved second-level key for "no live Charge/Charge_Abo part on the account at all". Not a
    /// real Xellent productnum, so it cannot collide with one.
    /// </summary>
    public const string NoChargeKey = "__NoCharge__";

    /// <summary>
    /// Reserved second-level key for "two or more charger installations on the metering point, every
    /// one confirmed at its address, every live Charge_Abo product owned" (user decision 2026-09-01
    /// on account 491704). One household, several charging boxes: the MP gets ONE contract for the
    /// whole electricity tuple, not one per charger. Not a real Xellent productnum, so it cannot
    /// collide with one. Which products count as owned is <see cref="OwnedChargeAboProducts"/>.
    /// </summary>
    public const string MultipleInstallationsKey = "__MultipleInstallations__";

    /// <summary>
    /// The Charge_Abo productnums that mean the customer OWNS the charger — the only product mix the
    /// multi-installation contract (<see cref="MultipleInstallationsKey"/>) exists for. Settl has no
    /// rented or mixed variant, so a metering point whose chargers carry anything else stays paused.
    /// </summary>
    public static readonly IReadOnlySet<string> OwnedChargeAboProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "OAboEjeMEL",
        "OAboEje0",
    };

    /// <summary>
    /// The Charge_Abo productnums (plus <see cref="NoChargeKey"/> and
    /// <see cref="MultipleInstallationsKey"/>) confirmed with the domain owner
    /// so far — see <c>docs/charge-account-classification.md</c> and the 2026-08-27 confirmation
    /// round (owned/rented + 0-kr + old/new charge-partner rental products + HomeCharge, all
    /// cross-checked against production Xellent data). Deliberately excludes
    /// <c>OAboEjeUEL</c>/<c>OAboLejUEL</c>/<c>Gl.CLejUEL</c>: confirmed by the business to have no
    /// Settl contract at all — those accounts have no live electricity delivery and stay in
    /// Xellent, not a pause waiting on confirmation. Anything not in this set still falls through
    /// to the next contract-resolution rule instead of firing on an unconfirmed mapping.
    /// </summary>
    public static readonly IReadOnlySet<string> ConfirmedChargeAboProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "OAboEjeMEL",
        "OAboLejMEL",
        "OAboEje0",
        "Gl.CLejMEL",
        "NY.CLejMEL",
        "O HCEjeML",
        "O HCEjeUL3",
        "O HCEjeUL4",
        "O HCEjeUL5",
        "O HCLejML",
        NoChargeKey,
        MultipleInstallationsKey,
    };

    private readonly Dictionary<string, Dictionary<string, string>> _byElProduct;

    private ElectricityChargerContractRules(Dictionary<string, Dictionary<string, string>> byElProduct) =>
        _byElProduct = byElProduct;

    /// <summary>
    /// Compiles the raw profile/appsettings table. Returns null when nothing is configured, so a
    /// caller can skip the whole dimension rather than resolving against an empty table.
    /// </summary>
    public static ElectricityChargerContractRules? Build(
        IReadOnlyDictionary<string, Dictionary<string, string>>? raw)
    {
        if (raw is null || raw.Count == 0) return null;

        var byElProduct = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (elProduct, chargeProducts) in raw)
        {
            if (string.IsNullOrWhiteSpace(elProduct) || chargeProducts is not { Count: > 0 }) continue;

            var byChargeProduct = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (chargeProduct, contractName) in chargeProducts)
            {
                if (string.IsNullOrWhiteSpace(chargeProduct) || string.IsNullOrWhiteSpace(contractName)) continue;
                byChargeProduct[chargeProduct] = contractName;
            }
            if (byChargeProduct.Count > 0) byElProduct[elProduct] = byChargeProduct;
        }

        return byElProduct.Count > 0 ? new ElectricityChargerContractRules(byElProduct) : null;
    }

    /// <summary>
    /// The Settl contract this electricity product + charger state maps to, or null when the table
    /// says nothing about the combination. Null means "ask the next rule", never "no contract".
    /// </summary>
    /// <param name="elProductNum">The MP's own El-ekstern productnum.</param>
    /// <param name="chargeAboProductNum">
    /// The charger's single live Charge_Abo productnum, <see cref="NoChargeKey"/> when the
    /// account has no live Charge/Charge_Abo part at all, or <see cref="MultipleInstallationsKey"/>
    /// for several owned chargers on the one metering point.
    /// </param>
    public string? Resolve(string elProductNum, string chargeAboProductNum)
    {
        if (string.IsNullOrEmpty(elProductNum) || string.IsNullOrEmpty(chargeAboProductNum)) return null;
        if (_byElProduct.TryGetValue(elProductNum, out var byChargeProduct)
            && byChargeProduct.TryGetValue(chargeAboProductNum, out var contract)) return contract;
        return ResolveConfirmedHomeCharge(elProductNum, chargeAboProductNum);
    }
}
