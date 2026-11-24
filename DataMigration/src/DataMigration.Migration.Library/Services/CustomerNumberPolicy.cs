namespace DataMigration.Migration.Services;

/// <summary>
/// Decides the exact digit-length a Settl environment requires for CustomerNumber and
/// normalises a Xellent Accountnum to it by left-padding with the digit '9'.
///
/// <para>Only `production` enforces a length (6 digits); kortere Accountnums paddes
/// venstre med '9' op til 6. Alle andre environments (featuretest, production-hev,
/// production-vhel) er uberørte — production-vhel paddes aldrig.</para>
///
/// <para>Callers bør foretrække <see cref="PadForEnv"/> som tager env-navnet direkte og
/// holder hele policy-knowledge inde i denne klasse. Brug <see cref="LengthForEnv"/> +
/// <see cref="Pad"/> separat hvis du har brug for længden til andre formål
/// (fx validering eller output-format).</para>
///
/// <para>Source of truth efter SettlClient 0.7.2 — `CustomerNumberFormatter` er fjernet
/// derfra; padding-strategien ejes nu udelukkende af DataMigration.</para>
/// </summary>
public static class CustomerNumberPolicy
{
    /// <summary>Exact CustomerNumber digit-length for <paramref name="env"/>, or null = no normalisation.</summary>
    public static int? LengthForEnv(string? env) => env?.ToLowerInvariant() switch
    {
        "production" => 6,
        _ => null
    };

    /// <summary>
    /// Left-pads <paramref name="accountNumber"/> with '9' to <paramref name="targetLength"/>.
    /// Null target = unchanged. Over-length or non-numeric input in a normalised environment
    /// is a hard error. <paramref name="wasPadded"/> is true only when digits were added.
    /// </summary>
    public static string Pad(string accountNumber, int? targetLength, out bool wasPadded)
    {
        wasPadded = false;
        if (targetLength is not { } length)
            return accountNumber;

        if (string.IsNullOrEmpty(accountNumber) || !accountNumber.All(char.IsDigit))
            throw new InvalidOperationException(
                $"CustomerNumber '{accountNumber}' is not all-digits; cannot normalise to {length} digits.");

        if (accountNumber.Length > length)
            throw new InvalidOperationException(
                $"CustomerNumber '{accountNumber}' has {accountNumber.Length} digits but the target environment " +
                $"requires exactly {length}. Aborting (hard fail) — over-length numbers cannot be normalised.");

        if (accountNumber.Length == length)
            return accountNumber;

        wasPadded = true;
        return accountNumber.PadLeft(length, '9');
    }

    /// <summary>
    /// Den modsatte vej: de Xellent-Accountnums en Settl-CustomerNumber kan stamme fra, mest
    /// sandsynlige først. Nødvendig fordi en CustomerNumber læst i Settl ikke uden videre kan slås op
    /// i CustTable — production padder korte konti med '9', så Randers Sportsdykkerklub er konto
    /// <c>41589</c> i Xellent og kunde <c>941589</c> i Settl.
    ///
    /// <para>Tallet som Settl har det står først, så en ægte sekscifret konto der begynder med 9
    /// aldrig forveksles med en padded kortere konto. Derefter hver de-paddet form, længste først —
    /// præcis de strenge som <see cref="Pad"/> ville have padded op til inputtet. Er miljøet ikke
    /// padded, er der kun én kandidat.</para>
    /// </summary>
    public static IReadOnlyList<string> AccountCandidates(string customerNumber, string? env)
    {
        var candidates = new List<string> { customerNumber };

        if (LengthForEnv(env) is not { } length
            || customerNumber.Length != length
            || !customerNumber.All(char.IsDigit))
            return candidates;

        // Kun ledende '9'er kan være padding, og kun så mange som der faktisk står: '941589' kan
        // stamme fra '41589', men ikke fra '1589' — der ville padding have givet '991589'.
        for (var i = 1; i < length && customerNumber[i - 1] == '9'; i++)
            candidates.Add(customerNumber[i..]);

        return candidates;
    }

    /// <summary>Convenience: padd <paramref name="accountNumber"/> til den længde som
    /// <paramref name="env"/> kræver. Eneste call-site callers (runners) bør bruge —
    /// så pad-policy aldrig lækker ud til Dashboard, OpsCli Program.cs osv.</summary>
    public static string PadForEnv(string accountNumber, string? env, out bool wasPadded)
        => Pad(accountNumber, LengthForEnv(env), out wasPadded);

    /// <summary>
    /// Padder en payer-identification for <paramref name="env"/> og bevarer et multi-payer
    /// fan-out-suffix. Fan-out-identifiers har formen <c>{AccountNumber}-{n}</c>; kun
    /// AccountNumber-delen paddes (production = 6 cifre), <c>-{n}</c>-suffikset beholdes
    /// uændret. Plain identifiers (uden fan-out-suffix) falder igennem til <see cref="PadForEnv"/>.
    /// Spejler <c>CreateRunner</c>'s padding, så <c>fix-payment-config</c>'s lookup rammer
    /// samme paddede form som <c>create</c> oprettede.
    /// </summary>
    public static string PadPayerIdentifierForEnv(string identifier, string accountNumber, string? env, out bool wasPadded)
    {
        var prefix = accountNumber + "-";
        if (identifier.StartsWith(prefix, StringComparison.Ordinal))
        {
            var suffix = identifier[prefix.Length..];
            if (suffix.Length > 0 && suffix.All(char.IsDigit))
            {
                var paddedAccount = PadForEnv(accountNumber, env, out wasPadded);
                return $"{paddedAccount}-{suffix}";
            }
        }
        return PadForEnv(identifier, env, out wasPadded);
    }
}
