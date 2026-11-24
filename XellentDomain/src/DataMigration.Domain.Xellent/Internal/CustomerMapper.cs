using DataMigration.Core;
using DataMigration.Core.Helpers;
using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.XellentData;
using DataMigration.XellentData.Reporting;
using Microsoft.EntityFrameworkCore;
using static DataMigration.Domain.Xellent.Internal.XellentQueryLimits;

namespace DataMigration.Domain.Xellent.Internal;

/// <summary>
/// Maps Xellent <c>CustTable</c> rows into the generic <see cref="Customer"/> domain type.
/// Resolves CVR+Fingerprint representative-merge via <see cref="AccountRepresentativeResolver"/>
/// before loading, so each output <c>Customer</c> represents one merged group.
///
/// <para>Reports shape-errors (invalid email format, invalid DAR Guid) via
/// <see cref="IWarningCollector"/>. Policy-checks (CPR-required, email-required,
/// address-completeness) live in <c>Validation/</c> and are run by the consumer.</para>
/// </summary>
internal sealed class CustomerMapper
{
    private static readonly DateTime EndDate = MigrationConstants.EndDateNull;

    private readonly XellentDbContext _context;
    private readonly IWarningCollector _warnings;
    private readonly MapperOptions _options;
    private readonly string _dataAreaId;

    public CustomerMapper(
        XellentDbContext context,
        IWarningCollector warnings,
        MapperOptions options,
        string dataAreaId)
    {
        _context = context;
        _warnings = warnings;
        _options = options;
        _dataAreaId = dataAreaId;
    }

    /// <summary>
    /// Splits accounts into query-sized batches without ever cutting through a merge-group.
    /// Chunking on raw accounts would be wrong here: a merged account and its representative are
    /// compared against each other after loading, and a group split across two queries would make
    /// that lookup miss and skip the comparison without any error.
    ///
    /// <para>A single group larger than <see cref="XellentQueryLimits.ChunkSize"/> is kept whole
    /// and forms its own batch — correctness wins over the parameter budget, and no real
    /// merge-group approaches that size.</para>
    /// </summary>
    private static List<string[]> ChunkByMergeGroup(
        string[] accounts,
        IReadOnlyDictionary<string, string> representativeByAccount)
    {
        var groups = accounts
            .GroupBy(a => representativeByAccount.TryGetValue(a, out var rep) ? rep : a, StringComparer.Ordinal)
            .Select(g => g.ToArray())
            .ToList();

        var batches = new List<string[]>();
        var current = new List<string>(XellentQueryLimits.ChunkSize);
        foreach (var group in groups)
        {
            if (current.Count > 0 && current.Count + group.Length > XellentQueryLimits.ChunkSize)
            {
                batches.Add([.. current]);
                current.Clear();
            }
            current.AddRange(group);
        }
        if (current.Count > 0) batches.Add([.. current]);

        // Callers index batches[0]; an empty account set never reaches here.
        return batches.Count > 0 ? batches : [[]];
    }

    /// <param name="headerExemption">
    /// Lets contract headers the batch end-dated back in, by name. Null when the restore is off.
    /// </param>
    public async Task<CustomerMapResult> MapAsync(
        string[]? accountNumbers,
        ContractHeaderExemption? headerExemption = null,
        CancellationToken cancellationToken = default)
    {
        var resolution = await AccountRepresentativeResolver.ResolveAsync(
            _context, accountNumbers, _dataAreaId, _options.LegacyTg4Merge, _options.ReferenceDate, cancellationToken,
            _options.IgnoredGroupNumbers, _options.DisableRepresentativeMerge);

        var representatives = resolution.Accounts.ToArray();
        if (representatives.Length == 0)
            return new CustomerMapResult([], new Dictionary<string, string>(), new Dictionary<string, AccountFingerprint>(StringComparer.Ordinal));

        var deliveryCategory = MapCommodity(_options.Commodity);
        var today = _options.ReferenceDate.ToDateTime(TimeOnly.MinValue);
        var includeEndedCutoff = _options.IncludeEndedCutoff?.ToDateTime(TimeOnly.MinValue);

        // Load Xellent rows for ALL input accounts (reps + merged) so we can compare
        // merged-account billing to rep billing. Customer is only built for reps.
        // Always materialised, also for a whole-commodity run: after the resolver the account set
        // IS known, and the queries below need it as a selective predicate. Dropping it made SQL
        // Server unable to plan the multi-join fallback queries at all — a different failure than
        // the parameter limit, and the reason these are chunked rather than left unfiltered.
        var allInputAccounts = resolution.RepresentativeByAccount.Keys.ToArray();

        // Billing channel info (PBS, MobilePay, EAN) is looked up across ALL active contracts —
        // not just those matching the delivery category — because a customer may have PBS configured
        // on a contract of a different commodity type (e.g. Vand/Varme) than the profile's
        // deliveryCategory (El-ekstern). The main rows query below is delivery-category-scoped for
        // address and identity data; billing channel uses the cross-commodity best row.
        // Chunked at the call site rather than inside: both fetchers return a per-account map, so
        // merging chunk results is exact — no group spans a chunk boundary.
        var billingByAccount = new Dictionary<string, BillingSourceRow>(StringComparer.Ordinal);
        foreach (var chunk in allInputAccounts.Chunk(ChunkSize))
            foreach (var kv in await FetchBillingChannelInfoAsync(chunk, today, includeEndedCutoff, headerExemption, cancellationToken))
                billingByAccount[kv.Key] = kv.Value;

        // MobilePay is resolved independently of the billing best-row above: an active agreement
        // may sit on ANY contract under the customer number — main or sub, any commodity — and no
        // rule predicts which one (verified against Xellent 2026-07: 44 of 2.663 customers with an
        // active agreement had it outside the best row; 29 of those on a sub-contract). Reading it
        // from the single best row silently dropped those customers' MobilePay channel.
        var mobilePayByAccount = new Dictionary<string, MobilePaySubscription>(StringComparer.Ordinal);
        foreach (var chunk in allInputAccounts.Chunk(ChunkSize))
            foreach (var kv in await FetchMobilePayAsync(chunk, today, includeEndedCutoff, headerExemption, cancellationToken))
                mobilePayByAccount[kv.Key] = kv.Value;

        // Chunked on whole merge-groups, not on individual accounts: the divergence check further
        // down compares a merged account's row against its representative's row, so both must be
        // loaded by the same query. Splitting a group would make that lookup miss and skip the
        // comparison silently — wrong data rather than an error.
        var accountChunks = ChunkByMergeGroup(allInputAccounts, resolution.RepresentativeByAccount);

        // Written as a lambda so the anonymous projection can be accumulated across chunks: the
        // first call gives `rows` its element type, the rest append to it.
        var rowsFor = (string[] chunk) =>
            from cust in _context.CustTables
            join contract in ContractHeaderScope.InDateWindow(_context, today, includeEndedCutoff, headerExemption?.ForAccounts(chunk))
                on new { cust.Accountnum, cust.Dataareaid }
                equals new { Accountnum = contract.Custaccount, contract.Dataareaid }
            // LEFT JOIN: customers must not be dropped when Referencetomaincontract points to
            // a non-existent or expired main contract. Falls back to null on the Main* fields,
            // which downstream BuildCustomer / BuildInvoiceConfiguration treat as "no value".
            // Delivery-category filter ensures a Referencetomaincontract pointing to a Vand/Varme
            // contract is treated as "no main contract" for address-row ordering and divergence checks.
            // Note: billing-channel data (PBS/MobilePay/EAN) is intentionally fetched cross-commodity
            // via FetchBillingChannelInfoAsync — the filter here does NOT affect billing output.
            join mainContract in _context.ExuContractTables
                    .Where(c => c.Deliverycategory == deliveryCategory || c.Deliverycategory == "") on new
            {
                Contractnum = string.IsNullOrEmpty(contract.Referencetomaincontract)
                    ? contract.Contractnum
                    : contract.Referencetomaincontract,
                contract.Dataareaid
            }
                equals new { mainContract.Contractnum, mainContract.Dataareaid } into mainContractJoin
            from mainContract in mainContractJoin.DefaultIfEmpty()
            join partyAddr in _context.DirPartyAddressRelationships
                on new { cust.Partyid, cust.Dataareaid }
                equals new { partyAddr.Partyid, partyAddr.Dataareaid }
            join addrLink in _context.DirPartyAddressRelationshi1066s
                on new { Recid = partyAddr.Recid, partyAddr.Dataareaid }
                equals new { Recid = addrLink.Partyaddressrelationshiprecid, addrLink.Dataareaid }
            join addr in _context.Addresses
                on new { Recid = addrLink.Addressrecid, addrLink.Dataareaid }
                equals new { addr.Recid, addr.Dataareaid }
            where cust.Dataareaid == _dataAreaId
                && chunk.Contains(cust.Accountnum)
                && (contract.Deliverycategory == deliveryCategory
                    || _context.ExuContractPartTables.Any(cp =>
                        cp.Contractnum == contract.Contractnum
                        && cp.Dataareaid == contract.Dataareaid
                        && cp.Deliverycategory == deliveryCategory))
                && partyAddr.Isprimary == 1
                && addr.Addrtableid == MigrationConstants.AddressTableId
                && partyAddr.Validtodatetime >= today
            select new
            {
                cust.Accountnum,
                cust.Name,
                cust.Namealias,
                cust.ExuAdditionalname,
                Cvr = cust.ExuCvrDisponent1,
                Cpr = cust.ExuCprDisponent1,
                Cvr2 = cust.ExuCvrDisponent2,
                Cpr2 = cust.ExuCprDisponent2,
                AlternativeCustomerNumber = cust.Exualternativecustomernumber,
                AlternativeCustomerCode = cust.Exualternativecustomercode,
                addr.Street,
                addr.Zipcode,
                addr.City,
                addr.Countryregionid,
                addr.ExuAddressid,
                cust.Cellularphone,
                cust.Phone,
                cust.Telefax,
                cust.Email,
                // Billing-channel fields (PBS/MobilePay/EAN) are intentionally absent here —
                // they are fetched cross-commodity by FetchBillingChannelInfoAsync and reused
                // via billingByAccount. Only non-billing fields from mainContract are projected.
                MainContractnum = mainContract.Contractnum,
                MainPurchaseOrder = mainContract.Custpurchaseorder,
                MainContactPerson = mainContract.Custcontactpersonid,
            };

        var rows = await rowsFor(accountChunks[0]).ToListAsync(cancellationToken);
        for (var i = 1; i < accountChunks.Count; i++)
            rows.AddRange(await rowsFor(accountChunks[i]).ToListAsync(cancellationToken));

        // When an account has multiple contracts, pick the row with the lowest MainContractnum
        // for determinism. Billing-channel row selection is handled separately in
        // FetchBillingChannelInfoAsync.
        var byAccount = rows
            .GroupBy(r => r.Accountnum, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(x => (string?)x.MainContractnum ?? string.Empty, StringComparer.Ordinal)
                      .First(),
                StringComparer.Ordinal);

        // Re-pick representatives within the *filtered* set: AccountRepresentativeResolver
        // groups by CVR+Fingerprint (or CVR+TG4 in legacy mode) over ALL input accounts,
        // but contract/address filters drop accounts whose contracts have ended. The original
        // representative may have no active contract; pick the lowest member that survived
        // the filter so the group still produces a customer (matches SettlExtractionService
        // behaviour: groupedCustomers.OrderBy(Accountnum).First() over already-filtered rows).
        var membersByRep = resolution.RepresentativeByAccount
            .GroupBy(kv => kv.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList(), StringComparer.Ordinal);

        var representativeAccounts = new List<string>(membersByRep.Count);
        var effectiveRepByAccount = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, members) in membersByRep)
        {
            var validMembers = members.Where(byAccount.ContainsKey)
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (validMembers.Count == 0) continue;
            var effectiveRep = validMembers[0];
            representativeAccounts.Add(effectiveRep);
            foreach (var m in members) effectiveRepByAccount[m] = effectiveRep;
        }
        representativeAccounts.Sort(StringComparer.Ordinal);

        var fallbackEmails = await FetchFallbackEmailsAsync(
            representativeAccounts.Where(a => string.IsNullOrEmpty(byAccount[a].Email)).ToArray(),
            cancellationToken);

        var primaryStreetByAccount = representativeAccounts
            .ToDictionary(a => a, a => byAccount[a].Street ?? string.Empty);
        // Hint for zip-code collisions in FetchLegalAddressFallbacksAsync: the primary
        // CustTable/Address row's own country is reliably populated and, when a customer's
        // legal-contact zip collides across countries in ADDRESSZIPCODE (same Zipcode+Dataareaid,
        // different Countryregionid — a real pattern in dataarea 'hol', not a one-off), it is the
        // best signal for which candidate city row is correct.
        var primaryCountryByAccount = representativeAccounts
            .ToDictionary(a => a, a => (string?)byAccount[a].Countryregionid);
        var legalFallbacks = await FetchLegalAddressFallbacksAsync(
            representativeAccounts.ToArray(), primaryStreetByAccount, primaryCountryByAccount,
            deliveryCategory, today, includeEndedCutoff, headerExemption, cancellationToken);

        var customers = new List<Customer>(representativeAccounts.Count);
        foreach (var accountNum in representativeAccounts)
        {
            var r = byAccount[accountNum];
            billingByAccount.TryGetValue(accountNum, out var billingSource);
            mobilePayByAccount.TryGetValue(accountNum, out var mobilePay);
            customers.Add(BuildCustomer(r, fallbackEmails, legalFallbacks, billingSource, mobilePay));

            // Info, not a warning: a MobilePay agreement sits on whichever contract the customer
            // happened to sign up on, so a sub-contract find is normal and not something anyone
            // has to act on. The provenance is still worth recording — where the agreement was
            // found is not derivable from the migrated result.
            if (mobilePay is not null && mobilePay.Origin == MobilePayOrigin.SubContract)
                _warnings.AddInfo(
                    $"Customer {accountNum}: MobilePay {mobilePay.Id} found on sub-contract " +
                    $"{mobilePay.SourceContractNumber} ({mobilePay.SourceDeliveryCategory}) — not on a " +
                    "main contract [Xellent: ExuContractTable.Mobilepaysubscriptionagre20135]");

            // Info, not a warning: several active agreements on one account is expected, and the
            // pick is deterministic (main contract before sub-contract, then lowest contract
            // number — see MobilePayResolver). Nothing for anyone to do, so it does not belong
            // in the warning stream; the provenance is still worth recording.
            if (mobilePay is not null && mobilePay.CandidateCount > 1)
                _warnings.AddInfo(
                    $"Customer {accountNum}: {mobilePay.CandidateCount} active MobilePay agreements on the " +
                    $"account — using {mobilePay.Id} from contract {mobilePay.SourceContractNumber} " +
                    $"({mobilePay.SourceDeliveryCategory}); the others are not migrated " +
                    "[Xellent: ExuContractTable.Mobilepaysubscriptionagre20135]");
        }

        // Cross-source aggregation (trin 5.1d): warn when merged accounts have different
        // billing fields than their representative. The rep's billing is what gets migrated;
        // merged accounts' divergent values are silently dropped — make that visible.
        // Billing-channel fields (PBS/MobilePay/EAN) are compared via billingByAccount —
        // same cross-commodity source as BuildInvoiceConfiguration — so divergence is
        // meaningful even when billing lives on a non-El-ekstern contract.
        foreach (var (account, rep) in effectiveRepByAccount)
        {
            if (account == rep) continue;
            if (!byAccount.TryGetValue(account, out var mergedRow)) continue;
            if (!byAccount.TryGetValue(rep, out var repRow)) continue;

            void CheckDivergence(string field, object? mergedValue, object? repValue)
            {
                if (!Equals(NormalizeForCompare(mergedValue), NormalizeForCompare(repValue)))
                    _warnings.AddWarning(
                        $"Customer {rep}: merged account {account} has different {field} " +
                        $"('{mergedValue}' vs rep '{repValue}') — merged value dropped " +
                        "[Xellent: ExuContractTable main contract]");
            }

            billingByAccount.TryGetValue(account, out var mergedBilling);
            billingByAccount.TryGetValue(rep, out var repBilling);

            // PBS divergence is semantic, not raw: only IsPbsActive (agreement != 0 AND
            // transactioncode in {1,2}) drives any downstream channel. Distinct raw values that
            // collapse to the same active/inactive state — transactioncode 1 vs 2, or an on-file
            // agreement number paired with an inactive code — are NOT a real divergence, so
            // comparing Pbsagreementnum/Pbstransactioncode raw produced false positives.
            bool mergedPbsActive = PaymentInformation.IsPbsActive(
                mergedBilling?.PbsAgreement ?? 0, mergedBilling?.PbsTransactionCode ?? 0);
            bool repPbsActive = PaymentInformation.IsPbsActive(
                repBilling?.PbsAgreement ?? 0, repBilling?.PbsTransactionCode ?? 0);
            if (mergedPbsActive != repPbsActive)
                _warnings.AddWarning(
                    $"Customer {rep}: merged account {account} has different PBS-active state " +
                    $"(merged agreement={mergedBilling?.PbsAgreement}/code={mergedBilling?.PbsTransactionCode} " +
                    $"vs rep agreement={repBilling?.PbsAgreement}/code={repBilling?.PbsTransactionCode}) " +
                    "— merged value dropped [Xellent: ExuContractTable main contract]");

            // MobilePay is compared on the resolved agreement (cross-contract, active-only),
            // not on the billing best row — same source the migrated value comes from.
            mobilePayByAccount.TryGetValue(account, out var mergedMobilePay);
            mobilePayByAccount.TryGetValue(rep, out var repMobilePay);
            CheckDivergence("MobilePay", mergedMobilePay?.Id, repMobilePay?.Id);
            CheckDivergence("Einvoiceeannum", mergedBilling?.Ean, repBilling?.Ean);
            CheckDivergence("EInvoiceAccountCode", mergedBilling?.EInvoiceAccountCode, repBilling?.EInvoiceAccountCode);
            CheckDivergence("Custcontactpersonid", mergedRow.MainContactPerson, repRow.MainContactPerson);
            CheckDivergence("Custpurchaseorder", mergedRow.MainPurchaseOrder, repRow.MainPurchaseOrder);
        }

        // Project per-account fingerprint data onto representatives (members share the
        // rep's fingerprint by construction — that's why they merge).
        var fingerprintByRep = representativeAccounts
            .Where(rep => resolution.FingerprintByAccount.ContainsKey(rep))
            .ToDictionary(
                rep => rep,
                rep => resolution.FingerprintByAccount[rep],
                StringComparer.Ordinal);

        return new CustomerMapResult(customers, effectiveRepByAccount, fingerprintByRep);
    }

    private Customer BuildCustomer(
        dynamic r,
        IReadOnlyDictionary<string, string> fallbackEmails,
        IReadOnlyDictionary<string, LegalAddressFallback> legalFallbacks,
        BillingSourceRow? billing,
        MobilePaySubscription? mobilePay)
    {
        string accountnum = r.Accountnum;
        string? cvr = NormalizeCvr(r.Cvr, accountnum, 1);
        string? cpr = IdentifierHelpers.ResolveCpr(r.Cpr, r.AlternativeCustomerNumber);
        string? cvr2 = NormalizeCvr(r.Cvr2, accountnum, 2);
        string? cpr2 = string.IsNullOrEmpty(r.Cpr2) ? null : r.Cpr2;
        var customerType = !string.IsNullOrEmpty(cvr) ? CustomerType.Company : CustomerType.Person;

        legalFallbacks.TryGetValue(accountnum, out var legalFallback);
        var address = AddressMapper.ResolveCustomerAddress(
            new CustomerAddressInput(
                Street: r.Street,
                Zipcode: r.Zipcode,
                City: r.City,
                CountryCode: !string.IsNullOrEmpty(r.Countryregionid) ? r.Countryregionid : null,
                DarIdentifier: r.ExuAddressid,
                LegalFallback: legalFallback),
            _warnings,
            $"Customer {accountnum}");

        var resolvedEmail = ResolveEmail(accountnum, r.Email, fallbackEmails);
        var contact = new ContactInfo(
            Email: _options.OverrideEmail ?? (_options.Anonymize ? AnonEmail(resolvedEmail) : resolvedEmail),
            Phone: _options.Anonymize ? AnonPhone(r.Phone) : NullIfEmpty(r.Phone),
            Cellphone: _options.Anonymize ? AnonPhone(r.Cellularphone) : NullIfEmpty(r.Cellularphone),
            Fax: _options.Anonymize ? AnonPhone(r.Telefax) : NullIfEmpty(r.Telefax));

        var customerBilling = BuildBilling(billing, mobilePay);
        var customerName = !string.IsNullOrEmpty(r.Name) ? r.Name : r.Namealias ?? string.Empty;

        return new Customer(
            AccountNumber: accountnum,
            Name: customerName,
            AdditionalName: NullIfEmpty(r.ExuAdditionalname),
            Type: customerType,
            Cvr: cvr,
            Cpr: _options.Anonymize ? AnonCpr(cpr) : cpr,
            Address: address,
            Contact: contact,
            Billing: customerBilling,
            Contracts: Array.Empty<Contract>())  // populated by orchestrator after ContractMapper runs
        {
            Cvr2 = cvr2,
            Cpr2 = _options.Anonymize ? AnonCpr(cpr2) : cpr2,
            RawIdentification = BuildRawIdentification(
                r.Cvr, r.Cpr, r.AlternativeCustomerNumber, r.AlternativeCustomerCode, _options.Anonymize),
        };
    }

    /// <summary>
    /// Carries the Xellent identification fields out untouched — no length check, no PNR-parsing,
    /// no fallback from one field to another. That work belongs to <c>Cvr</c>/<c>Cpr</c>, and doing
    /// it twice would defeat the purpose: the consumer needs the values as they stand in order to
    /// say why a customer has no usable identification.
    ///
    /// <para>Empty string and null both become null — Xellent stores "no value" as an empty string,
    /// and passing that through would just make every consumer write the same check.</para>
    ///
    /// <para>The CPR-bearing fields honour <c>Anonymize</c> the same way <c>Cpr</c> does: a
    /// placeholder that is still non-empty, so "the field has something in it" survives an
    /// anonymised run even though the value itself does not.</para>
    /// </summary>
    internal static RawIdentification BuildRawIdentification(
        string? cvr,
        string? cpr,
        string? alternativeNumber,
        int alternativeCode,
        bool anonymize)
    {
        var rawCpr = NullIfEmpty(cpr);
        var rawAlternative = NullIfEmpty(alternativeNumber);
        return new RawIdentification(
            Cvr: NullIfEmpty(cvr),
            Cpr: anonymize ? AnonCpr(rawCpr) : rawCpr,
            AlternativeNumber: anonymize ? AnonCpr(rawAlternative) : rawAlternative,
            AlternativeNumberCode: alternativeCode);
    }

    /// <param name="mobilePay">Resolved by <see cref="FetchMobilePayAsync"/> across all of the
    /// customer's contracts — deliberately NOT read from <paramref name="billing"/>, whose
    /// single best row only covers agreements that happen to sit on the picked main contract.</param>
    private static CustomerBilling BuildBilling(BillingSourceRow? billing, MobilePaySubscription? mobilePay)
    {
        int pbsAgreement = billing?.PbsAgreement ?? 0;
        int pbsTransactionCode = billing?.PbsTransactionCode ?? 0;
        string ean = billing?.Ean ?? string.Empty;

        var pbsActive = pbsAgreement != 0 && (pbsTransactionCode == 1 || pbsTransactionCode == 2);
        var pbs = pbsActive ? new PbsAgreement(pbsAgreement) : null;

        return new CustomerBilling(
            DefaultEan: string.IsNullOrWhiteSpace(ean) ? null : ean,
            Pbs: pbs,
            MobilePay: mobilePay);
    }

    private string? ResolveEmail(string accountnum, string? primary, IReadOnlyDictionary<string, string> fallback)
    {
        var email = !string.IsNullOrEmpty(primary)
            ? primary
            : (fallback.TryGetValue(accountnum, out var fb) ? fb : null);
        email = email?.Trim();

        if (string.IsNullOrEmpty(email)) return null;
        if (!IdentifierHelpers.IsValidEmail(email))
        {
            _warnings.AddError(
                $"Customer {accountnum}: invalid email format '{email}' [Xellent: CustTable.Email / DirECommunication]");
            return null;
        }
        return email;
    }

    private string? NormalizeCvr(string? cvr, string accountnum, int disponent)
    {
        if (string.IsNullOrEmpty(cvr)) return null;
        if (cvr.Length != 8 || !cvr.All(char.IsDigit))
        {
            _warnings.AddWarning(
                $"Customer {accountnum}: invalid CVR '{cvr}' (expected 8 digits) [Xellent: CustTable.ExuCvrDisponent{disponent}]");
            return null;
        }
        return cvr;
    }

    /// <summary>
    /// Loads billing-channel source data (PBS, MobilePay, EAN) from ALL active contracts for
    /// the given accounts — no delivery-category filter. A customer's PBS agreement may live on
    /// a contract of a different commodity type than the profile's deliveryCategory (e.g. a
    /// Vand/Varme contract when the profile is El-ekstern). Restricting to the profile's category
    /// would miss that agreement and produce the wrong InvoiceChannel/PaymentChannel.
    /// Uses the same PBS-active-first ordering as the main rows query.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, BillingSourceRow>> FetchBillingChannelInfoAsync(
        string[]? accounts,
        DateTime today,
        DateTime? includeEndedCutoff,
        ContractHeaderExemption? headerExemption,
        CancellationToken cancellationToken)
    {
        if (accounts is { Length: 0 }) return new Dictionary<string, BillingSourceRow>(StringComparer.Ordinal);

        var rows = await (
            from contract in ContractHeaderScope.InDateWindow(_context, today, includeEndedCutoff, headerExemption?.ForAccounts(accounts))
            join mainContract in _context.ExuContractTables on new
            {
                Contractnum = string.IsNullOrEmpty(contract.Referencetomaincontract)
                    ? contract.Contractnum
                    : contract.Referencetomaincontract,
                contract.Dataareaid
            }
                equals new { mainContract.Contractnum, mainContract.Dataareaid } into mainContractJoin
            from mainContract in mainContractJoin.DefaultIfEmpty()
            where contract.Dataareaid == _dataAreaId
                && (accounts == null || accounts.Contains(contract.Custaccount))
            select new
            {
                Account = contract.Custaccount,
                MainContractnum = mainContract.Contractnum,
                PbsAgreement = (int?)mainContract.Pbsagreementnum,
                PbsTransactionCode = (int?)mainContract.Pbstransactioncode,
                MobilePay = mainContract.Mobilepaysubscriptionagre20135,
                MobilePayCancel = mainContract.Mobilepayagreementcanceldate,
                Ean = mainContract.Einvoiceeannum,
                EInvoiceAccountCode = mainContract.Einvoiceaccountcode,
            }
        ).ToListAsync(cancellationToken);

        static bool HasActivePbs(int pbsAgreement, int pbsTransactionCode)
            => pbsAgreement != 0 && (pbsTransactionCode == 1 || pbsTransactionCode == 2);

        return rows
            .GroupBy(r => r.Account, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var best = g
                        .OrderByDescending(x => HasActivePbs(x.PbsAgreement ?? 0, x.PbsTransactionCode ?? 0))
                        .ThenBy(x => x.MainContractnum ?? string.Empty, StringComparer.Ordinal)
                        .First();
                    return new BillingSourceRow(
                        best.PbsAgreement ?? 0,
                        best.PbsTransactionCode ?? 0,
                        best.MobilePay ?? string.Empty,
                        best.MobilePayCancel,
                        best.Ean ?? string.Empty,
                        best.EInvoiceAccountCode);
                },
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Finds the active MobilePay agreement for each account by scanning ALL of the account's
    /// contracts — main and sub, every commodity — rather than the single billing best row.
    ///
    /// <para>Candidate set: the account's active contracts, plus the main contracts they point
    /// at (a main contract may have ended while an active sub-contract still hangs under it —
    /// that inherited agreement was migrated before this change and must keep working).
    /// An agreement counts as active when the id is non-empty AND the cancel date is the AX
    /// "never cancelled" sentinel.</para>
    ///
    /// <para>When several agreements are active on one account, main contracts win over
    /// sub-contracts and then the lowest contract number — deterministic, and it keeps the
    /// pick unchanged for accounts that already resolved correctly. The runner-up agreements
    /// are not migrated; <c>CandidateCount</c> carries the fact that they exist.</para>
    /// </summary>
    private async Task<IReadOnlyDictionary<string, MobilePaySubscription>> FetchMobilePayAsync(
        string[]? accounts,
        DateTime today,
        DateTime? includeEndedCutoff,
        ContractHeaderExemption? headerExemption,
        CancellationToken cancellationToken)
    {
        if (accounts is { Length: 0 })
            return new Dictionary<string, MobilePaySubscription>(StringComparer.Ordinal);

        var rows = await (
            from contract in ContractHeaderScope.InDateWindow(_context, today, includeEndedCutoff, headerExemption?.ForAccounts(accounts))
            join mainContract in _context.ExuContractTables on new
            {
                Contractnum = string.IsNullOrEmpty(contract.Referencetomaincontract)
                    ? contract.Contractnum
                    : contract.Referencetomaincontract,
                contract.Dataareaid
            }
                equals new { mainContract.Contractnum, mainContract.Dataareaid } into mainContractJoin
            from mainContract in mainContractJoin.DefaultIfEmpty()
            where contract.Dataareaid == _dataAreaId
                && (accounts == null || accounts.Contains(contract.Custaccount))
            select new MobilePayScanRow(
                contract.Custaccount,
                contract.Contractnum,
                contract.Referencetomaincontract,
                contract.Mobilepaysubscriptionagre20135,
                contract.Mobilepayagreementcanceldate,
                mainContract.Contractnum,
                mainContract.Mobilepaysubscriptionagre20135,
                mainContract.Mobilepayagreementcanceldate)
        ).ToListAsync(cancellationToken);

        var picks = MobilePayResolver.Resolve(rows);
        if (picks.Count == 0)
            return new Dictionary<string, MobilePaySubscription>(StringComparer.Ordinal);

        var categories = await ContractDeliveryCategoryReader.LoadAsync(
            _context,
            _dataAreaId,
            picks.Values.Select(p => p.Winner.ContractNumber).Distinct(StringComparer.Ordinal).ToArray(),
            cancellationToken);

        return picks.ToDictionary(
            kv => kv.Key,
            kv => new MobilePaySubscription(
                Id: kv.Value.Winner.AgreementId,
                SourceContractNumber: kv.Value.Winner.ContractNumber,
                Origin: kv.Value.Winner.Origin,
                SourceDeliveryCategory: categories.TryGetValue(kv.Value.Winner.ContractNumber, out var cat)
                    ? cat
                    : string.Empty,
                CandidateCount: kv.Value.CandidateCount),
            StringComparer.Ordinal);
    }

    private async Task<IReadOnlyDictionary<string, string>> FetchFallbackEmailsAsync(
        string[] accountsWithoutEmail,
        CancellationToken cancellationToken)
    {
        if (accountsWithoutEmail.Length == 0) return new Dictionary<string, string>();

        // Chunked: this list is derived from the extract, so a whole-commodity run makes it as
        // large as the customer set — far past what SQL Server will accept as IN-parameters.
        var data = new List<(string Accountnum, string Email)>();
        foreach (var chunk in accountsWithoutEmail.Chunk(ChunkSize))
        {
            var rows = await (
                from cust in _context.CustTables
                join rel in _context.DirPartyECommunicationRela2608s
                    on new { cust.Partyid, cust.Dataareaid }
                    equals new { Partyid = rel.Partyid, Dataareaid = rel.Dataareaid }
                join addr in _context.DirECommunicationAddresses
                    on new { Recid = rel.Valuesrecid, rel.Dataareaid }
                    equals new { addr.Recid, addr.Dataareaid }
                where cust.Dataareaid == _dataAreaId
                    && chunk.Contains(cust.Accountnum)
                    && addr.Ecommunicationtypeid == "Email"
                    && addr.Email != ""
                orderby rel.Priority
                select new { cust.Accountnum, addr.Email }
            ).ToListAsync(cancellationToken);
            data.AddRange(rows.Select(r => (r.Accountnum, r.Email)));
        }

        return data
            .GroupBy(x => x.Accountnum, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Email, StringComparer.Ordinal);
    }

    private async Task<IReadOnlyDictionary<string, LegalAddressFallback>> FetchLegalAddressFallbacksAsync(
        string[] accounts,
        IReadOnlyDictionary<string, string> primaryStreetByAccount,
        IReadOnlyDictionary<string, string?> primaryCountryByAccount,
        string deliveryCategory,
        DateTime today,
        DateTime? includeEndedCutoff,
        ContractHeaderExemption? headerExemption,
        CancellationToken cancellationToken)
    {
        if (accounts.Length == 0) return new Dictionary<string, LegalAddressFallback>();

        // Chunked, and grouped per chunk — the group key is the account the chunks partition on.
        // This query joins six tables; without the account predicate SQL Server cannot produce a
        // plan for it at all over a whole data area, which is a different failure than the
        // parameter limit and the reason it is filtered rather than left open.
        var result = new Dictionary<string, LegalAddressFallback>(StringComparer.Ordinal);
        foreach (var chunk in accounts.Chunk(ChunkSize))
        {
        var data = await (
            from cust in _context.CustTables
            join contract in ContractHeaderScope.InDateWindow(_context, today, includeEndedCutoff, headerExemption?.ForAccounts(chunk))
                on new { cust.Accountnum, cust.Dataareaid }
                equals new { Accountnum = contract.Custaccount, contract.Dataareaid }
            join contractPart in _context.ExuContractPartTables on new { contract.Contractnum, contract.Dataareaid }
                equals new { contractPart.Contractnum, contractPart.Dataareaid }
            join agreement in _context.ExuAgreementTables on new { Agreementnum = contractPart.Instagreenum, contractPart.Dataareaid, contractPart.Companyid }
                equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
            join delpoint in _context.ExuDelpoints on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                equals new { Attachmentnum = delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid }
            join contactInfo in _context.ExuDelpointContactInfoTables
                on new { delpoint.Meteringpoint, delpoint.Dataareaid, delpoint.Deliverycategory, delpoint.Companyid }
                equals new { contactInfo.Meteringpoint, contactInfo.Dataareaid, contactInfo.Deliverycategory, contactInfo.Companyid }
            join zipCode in _context.AddressZipCodes on new { Zipcode = contactInfo.Zipcode, contactInfo.Dataareaid }
                equals new { zipCode.Zipcode, zipCode.Dataareaid } into zipJoin
            from zipCode in zipJoin.DefaultIfEmpty()
            join legalAddr in _context.Addresses on new { Addressid = contactInfo.Addressid, contactInfo.Dataareaid }
                equals new { Addressid = legalAddr.ExuAddressid, legalAddr.Dataareaid } into legalAddrJoin
            from legalAddr in legalAddrJoin.DefaultIfEmpty()
            where cust.Dataareaid == _dataAreaId
                && chunk.Contains(cust.Accountnum)
                && contract.Deliverycategory == deliveryCategory
                && delpoint.Deliverycategory == deliveryCategory
                && contactInfo.Contacttype == 4
                && contactInfo.Addressid != null && contactInfo.Addressid != ""
                && (contactInfo.Enddate == EndDate || contactInfo.Enddate >= today)
            select new
            {
                cust.Accountnum,
                contactInfo.Streetname,
                contactInfo.Housenumber,
                contactInfo.Floor,
                contactInfo.Apartment,
                contactInfo.Zipcode,
                CityName = zipCode != null ? zipCode.City : "",
                ZipCountryCode = zipCode != null ? zipCode.Countryregionid : null,
                contactInfo.Addressid,
                CountryCode = legalAddr != null ? legalAddr.Countryregionid : null
            }
        ).ToListAsync(cancellationToken);

        foreach (var g in data.GroupBy(x => x.Accountnum, StringComparer.Ordinal))
        {
            primaryStreetByAccount.TryGetValue(g.Key, out var primaryStreet);
            primaryCountryByAccount.TryGetValue(g.Key, out var primaryCountry);

            // ADDRESSZIPCODE's key is (Dataareaid, Countryregionid, Zipcode, County), so the same
            // zip number genuinely recurs across countries (e.g. dataarea 'hol' zip 1000 has both
            // a Brussels and a København K row). The join above is on Zipcode+Dataareaid alone, so
            // a collision fans out into one row per candidate city here. Collapse those candidates
            // per underlying contact row (same street/address, different city) before picking
            // between distinct contact rows on street-match, so a zip collision never leaks into
            // the address/street tie-break below.
            var candidates = g
                .GroupBy(x => new { x.Streetname, x.Housenumber, x.Floor, x.Apartment, x.Zipcode, x.Addressid, x.CountryCode })
                .Select(cg =>
                {
                    var zipRows = cg.ToList();
                    if (zipRows.Count == 1) return zipRows[0];

                    var distinctCountries = zipRows
                        .Select(x => x.ZipCountryCode)
                        .Where(c => !string.IsNullOrEmpty(c))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();
                    if (distinctCountries <= 1) return zipRows[0];

                    // Genuine zip collision: prefer the candidate whose ADDRESSZIPCODE country
                    // matches the customer's own primary address country. If there's no hint, or
                    // none of the candidates match it, fall back to a deterministic (not just
                    // whatever order SQL happened to return) pick and say so — the migration
                    // should not hard-fail over one ambiguous zip, but the pick is a guess.
                    var matched = primaryCountry != null
                        ? zipRows.FirstOrDefault(x =>
                            string.Equals(x.ZipCountryCode, primaryCountry, StringComparison.OrdinalIgnoreCase))
                        : null;
                    if (matched != null) return matched;

                    var fallback = zipRows.OrderBy(x => x.ZipCountryCode, StringComparer.Ordinal).First();
                    _warnings.AddWarning(
                        $"Customer {g.Key}: legal-address zip {fallback.Zipcode} collides across countries " +
                        $"in ADDRESSZIPCODE ({string.Join("/", zipRows.Select(x => x.ZipCountryCode).Distinct(StringComparer.OrdinalIgnoreCase))}) " +
                        $"and no primary-address country hint matched — picked '{fallback.CityName}' " +
                        $"({fallback.ZipCountryCode}) arbitrarily [Xellent: ADDRESSZIPCODE]");
                    return fallback;
                })
                .ToList();

            var pick = candidates
                .OrderByDescending(x => StreetMatchScore(primaryStreet, x.Streetname, x.Housenumber))
                .ThenBy(x => x.Addressid, StringComparer.Ordinal)
                .First();

            // Country precedence: the fallback's own resolved Address row (legalAddr) is
            // authoritative when it exists — that is ff15a1e's fix and stays first. When it
            // doesn't (Addressid unresolved, e.g. account 429529), the ADDRESSZIPCODE row we just
            // picked is still a real signal — for a collision it is literally the row that matched
            // the primary-address hint — so it beats falling straight through to the DK module
            // default in AddressMapper.
            var effectiveCountry = !string.IsNullOrEmpty(pick.CountryCode)
                ? pick.CountryCode
                : (!string.IsNullOrEmpty(pick.ZipCountryCode) ? pick.ZipCountryCode : null);

            result[g.Key] = new LegalAddressFallback(
                pick.Streetname, pick.Housenumber, pick.Floor, pick.Apartment,
                pick.Zipcode, pick.CityName ?? string.Empty, pick.Addressid,
                effectiveCountry);
        }
        }

        return result;
    }

    private static int StreetMatchScore(string? primaryStreet, string streetname, string housenumber)
    {
        if (string.IsNullOrEmpty(primaryStreet)) return 0;
        var candidate = streetname + housenumber;
        var a = new string(primaryStreet.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        var b = new string(candidate.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    private static string MapCommodity(Commodity commodity) => commodity switch
    {
        Commodity.Electricity => MigrationConstants.DeliveryCategoryElectricity,
        Commodity.Water => MigrationConstants.DeliveryCategoryWater,
        Commodity.Heat => MigrationConstants.DeliveryCategoryHeating,
        Commodity.EvCharging => MigrationConstants.DeliveryCategoryCharge,
        _ => throw new ArgumentOutOfRangeException(nameof(commodity), commodity, "Unsupported commodity")
    };

    /// <summary>Treat null and empty string as equivalent so divergence-check ignores
    /// whitespace-only or null differences that are migration-equivalent.</summary>
    private static object? NormalizeForCompare(object? value) => value switch
    {
        null => null,
        string s => string.IsNullOrWhiteSpace(s) ? null : s,
        _ => value
    };

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static string? AnonEmail(string? email) =>
        string.IsNullOrEmpty(email) ? null : "anonymized@example.com";

    private static string? AnonPhone(string? phone) =>
        string.IsNullOrEmpty(phone) ? null : "00000000";

    private static string? AnonCpr(string? cpr) =>
        string.IsNullOrEmpty(cpr) ? null : "0000000000";
}

internal sealed record CustomerMapResult(
    IReadOnlyList<Customer> Customers,
    IReadOnlyDictionary<string, string> AccountToRepresentative,
    IReadOnlyDictionary<string, AccountFingerprint> FingerprintByRepresentative);

/// <summary>Billing-channel source data resolved from all active contracts (cross-commodity).
/// The <c>MobilePay</c>/<c>MobilePayCancel</c> fields are no longer read for the migrated value —
/// MobilePay is resolved cross-contract by <c>FetchMobilePayAsync</c>.</summary>
internal sealed record BillingSourceRow(
    int PbsAgreement,
    int PbsTransactionCode,
    string MobilePay,
    DateTime? MobilePayCancel,
    string Ean,
    string? EInvoiceAccountCode);
