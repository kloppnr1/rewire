# DataMigration: answering business validation questions

Reviewed on 2026-09-22 against local C# implementations and the existing model
documents. This is **task 2: validation decisions**, independent of the
[CustomerSupport retrieval review](customer-support-investigation-review.md).

The question is whether the documents enable an investigator to state the correct
decision, its reason, and its scope. A successful account lookup does not establish
that its data passes a migration validation rule.

This is a manual source review using hypothetical cases, not a live migration,
an executed test suite, or an independent AI accuracy measurement. The cases cover
selected identity, contact, grouping, numbering, invoice-scope, and
configuration decisions. They are not an exhaustive inventory of every check in
DataMigration or of downstream Settl API validation.

## Which checks apply

The cases below concern mapping and decisions used by migration creation,
plus a separately labelled invoice-selection rule. The unused diagnostic export
and its optional validators are excluded, as confirmed by the domain owner.

| Stage or operation | What it does | Evidence |
|---|---|---|
| Shared customer mapping | Resolves primary identity, customer type, email and address. Some malformed values become null and produce a warning or error. | V-A |
| Migration creation: missing identity | Excludes mapped customers without either primary CVR or CPR, subject to a narrowly configured placeholder exception. | V-B |
| Migration creation: email split | Runs the batch email-split validator when representative merging is enabled. | V-D |
| Target customer-number formatting | Applies the selected environment's number policy; invalid input can throw an exception. | V-E |
| FOR invoice selection | Determines invoice inclusion through the linked contract's company, in the operation that applies this scope. | V-F |
| Settlement-group preflight | Looks up configured groups in Settl when online and a mapping exists; records errors for missing groups or a failed list result. | V-D |

An **error entry**, **warning**, **exclusion**, **normalised value**, and **thrown
exception** have different consequences. The validation runner collects entries;
it does not itself remove customers. A rule passing says only that this rule
found no issue, not that the entire customer is ready for migration.

## Identity and contact cases

Assume ordinary mapping without anonymisation or overrides unless a case states
otherwise. “Primary” refers to the mapped customer's `Cvr`/`Cpr`; the raw fields
and secondary person's values remain separate evidence.

| Case | Business question and hypothetical facts | Correct decision and reason | Evidence |
|---|---|---|---|
| V01 | The primary person has a usable CPR but no CVR. Must we exclude the customer? | **Pass this identity gate.** A nonblank mapped primary CPR is sufficient for `MissingIdentityFilter`. A CVR is not required for every customer. | V-A, V-B |
| V02 | Only the second named person has an identifier. Is that sufficient for creation? | **Exclude at the missing-identity gate** when both primary mapped values are blank. Secondary CPR/CVR is report evidence and does not supply the primary identity. | V-B |
| V03 | The primary CVR is seven characters and there is no usable CPR. | **Mapping warning; CVR becomes null; then exclusion.** CVR normalisation requires length 8 and `char.IsDigit` for each character. The missing-identity cause is `MalformedCvr` when no higher-priority alternative-number cause exists. | V-A, V-B |
| V04 | The CVR has the accepted eight-digit format. Have we proved the company exists? | **No.** This normaliser checks format, not registration or legal existence. It classifies the mapped customer as Company when the CVR survives; later checks remain independent. | V-A |
| V05 | The CPR contains ten letters rather than digits. Will the current resolver reject it? | **No: current implementation accepts ten characters.** `ResolveCpr` checks length, not digits, date, checksum, or register validity. A nonblank ten-character result can therefore pass the missing-identity gate. This is a validation limitation, not a declaration of a valid identity. | V-A, V-B |
| V06 | The primary CPR is unusable, but the alternative number is `PNR=` followed by ten characters. | **Resolve CPR from the alternative field.** The prefix comparison is case-insensitive; the helper checks the remaining length and does not consult the alternative-number code. It does not perform full identity validation. | V-A |
| V07 | A company identifier is present only in the alternative number with code 2; mapped primary identity remains empty. | **Exclude and classify `CvrInAlternativeNumber`.** The classifier identifies the source of the problem; it does not promote that value into mapped CVR or repair the customer. Code 6 analogously classifies a CPR stored there. | V-B |
| V08 | There is a malformed primary CVR and also an alternative number with code 2. Which cause is reported? | **`CvrInAlternativeNumber`.** Recognised alternative-number causes precede malformed primary CVR, malformed primary CPR, unrecognised alternative code, then completely missing identity. | V-B |
| V09 | All primary raw and alternative identity fields are blank. Is a placeholder always allowed? | **Exclude by default.** In `create`, the exception requires both the explicit fictive-CPR option and the `El-ekstern` delivery category, and applies only to cause `Nothing`. With those conditions it assigns a placeholder and retains the customer. This electricity-supplier policy must not be generalised to water or heat. | V-B |
| V10 | The placeholder option is enabled, but the record has malformed or uninterpreted identity data. | **Still exclude** when no mapped primary identity exists. The exception does not apply to these causes. It must not hide a value that needs interpretation or correction. | V-B |
| V11 | The primary email is empty and the party communication source has a well-formed email. | **Use the fallback.** The mapper prefers a nonempty primary value, otherwise the loaded fallback, then trims and validates the selected value. | V-A |
| V12 | The primary email is malformed but a valid fallback email exists. | **Record an error and map email to null.** A nonempty primary is chosen first; the mapper does not retry the fallback after format failure. This step itself does not exclude the customer. | V-A |
| V13 | The email matches the mapper's expression. Does that prove we can contact the customer? | **No.** The check requires non-whitespace components around `@` and a dot in the domain portion. It does not verify delivery, mailbox ownership, or communication consent. | V-A |

## Customer grouping during migration creation

| Case | Business question and hypothetical facts | Correct decision and reason | Evidence |
|---|---|---|---|
| V14 | Same-CVR accounts have the same name/address, but some have email and others do not. | **Structured Warning** from `EmailOnlyFingerprintSplitValidator` when invoked. It uses the email-free name/address fingerprint, detects the present/absent mixture, and does not change merging. `LogAsMessage=false` means it does not also add a free-text warning through this runner. | V-G, V-D |
| V15 | Same-CVR accounts instead have two different nonempty emails. Does the same warning fire? | **No warning from this specific rule** if all emails are nonempty. Different email values can still keep accounts in different merge groups. No warning is not proof they will merge. | V-G, V-H |
| V16 | Accounts share CVR and technical group. Must they become one customer? | **Depends on merge mode.** Default resolution uses same CVR plus matching street fingerprint or matching legal-contact DAR/name key; email participates in both. Legacy mode uses CVR plus technical group. Disabling merging keeps accounts separate. CustomerSupport display grouping is not this decision. | V-H |

## Numbering, invoice scope, and configuration cases

| Case | Business question and hypothetical facts | Correct decision and reason | Evidence |
|---|---|---|---|
| V17 | Can a short or malformed account number be used unchanged in every target? | **Environment-specific.** `production` requires six digits: shorter numeric values are left-padded with `9`; nonnumeric, empty, or overlength values throw. `production-hev`, `production-vhel`, staging and featuretest have no length transformation in this policy. Passing this formatter does not prove the target accepts every value. | V-E |
| V18 | An invoice's linked contract has blank company classification. Is that the same as no linked contract? | **Different decisions in FOR invoice scope.** An existing contract with company `for` or blank remains in scope. A missing contract or explicit other company is excluded. Invoice company is derived through `ExuContractnum` plus data area; it is not read directly from the invoice header. | V-F |
| V19 | A configured settlement group is absent from Settl's returned list. Is it silently acceptable or an immediate exception? | **Collected Error.** Online `create` preflight records a missing-group error. This method does not throw or return an exclusion list, and its caller continues after the check; do not describe it as a guaranteed stop before any later work. | V-D |
| V20 | Settlement groups could not be listed, or the operation is offline. Does either establish that all configured groups exist? | **Neither establishes existence.** A null online list result records an Error. Offline execution skips this preflight. A skipped check is not a successful check. | V-D |

## Documentation assessment and corrections

Before this review, the documents described identifiers, legal parties, address
roles and representative grouping conceptually. That supported understanding the
data but did not supply the operational decision table above. In particular:

- Primary and secondary identity needed explicit separation for the exclusion rule.
- Raw alternative-number codes needed separation from the resolver's `PNR=` parsing.
- “Valid CPR” needed qualification: the current resolver checks ten-character
  length and the missing-identity gate checks presence. Neither proves identity validity.
- Evidence needed actual migration call sites. The presence of a validator class
  in an unused diagnostic command does not establish migration behaviour.
- Errors needed separation from exclusions and exceptions; warnings needed their
  reporting channel and activation conditions.
- The current fingerprint merge mode needed separation from the legacy CVR/TG4
  mode. Older workspace summaries describing only CVR/TG4 are insufficient.
- The placeholder and FOR invoice policies needed explicit scope, so they cannot
  become generic water/heat or utility business rules.

The field-semantics reference now records the identity limitations and links to
this decision table. The business model distinguishes retrieval, validation and
the consequence chosen by the consuming operation.

For a subsequent documents-only evaluation, an answer to each case should state
**the decision, the rule, the operation/option, and what remains unproven**. Do not
grade a correct abstention about registry validity or target API acceptance as a
failure. Those facts are not established by these source checks.

## Source evidence and invocation

- **V-A:** [CustomerMapper](../src/DataMigration.Domain.Xellent/Internal/CustomerMapper.cs), especially `BuildCustomer`, `NormalizeCvr`, and `ResolveEmail`; [IdentifierHelpers](../src/DataMigration.Domain.Xellent/IdentifierHelpers.cs). DataMigration invokes the shared loader through [MigrationDomainLoad](../../NewSettlement.DataMigration/src/DataMigration.Migration.Library/Services/MigrationDomainLoad.cs).
- **V-B:** DataMigration [MissingIdentityFilter](../../NewSettlement.DataMigration/src/DataMigration.Migration.Library/Services/Extraction/MissingIdentityFilter.cs), [cause classifier](../../NewSettlement.DataMigration/src/DataMigration.Migration.Library/Services/Extraction/MissingIdentityCause.cs), and `CreateRunner.ApplyMissingIdentityFilter` in [CreateRunner](../../NewSettlement.DataMigration/src/DataMigration.Migration.Library/Runners/CreateRunner.cs). The caller applies the delivery-category/option condition.
- **V-D:** DataMigration [CreateRunner](../../NewSettlement.DataMigration/src/DataMigration.Migration.Library/Runners/CreateRunner.cs): conditional batch validation and `ValidateSettlementGroupsAsync`, including its caller's online/configuration gate.
- **V-E:** DataMigration [CustomerNumberPolicy](../../NewSettlement.DataMigration/src/DataMigration.Migration.Library/Services/CustomerNumberPolicy.cs), called from [CreateRunner](../../NewSettlement.DataMigration/src/DataMigration.Migration.Library/Runners/CreateRunner.cs). Representative resolution precedes translating an account into a target customer lookup.
- **V-F:** DataMigration [ForInvoiceScope](../../NewSettlement.DataMigration/src/DataMigration.OpsCli.Library/Services/ForInvoiceScope.cs), invoked in [GenerateStateFromSettlRunner](../../NewSettlement.DataMigration/src/DataMigration.OpsCli.Library/Runners/GenerateStateFromSettlRunner.cs). This is a scoped selection rule, not a universal invoice-validity rule.
- **V-G:** Shared [EmailOnlyFingerprintSplitValidator](../src/DataMigration.Domain.Xellent/Validation/EmailOnlyFingerprintSplitValidator.cs) and [CustomerValidationRunner](../src/DataMigration.Domain.Xellent/Validation/CustomerValidationRunner.cs). Invocation is shown in V-D; a structured entry remains distinct from the free-text warning list.
- **V-H:** Shared [AccountRepresentativeResolver](../src/DataMigration.Domain.Xellent/AccountRepresentativeResolver.cs), [FingerprintCalculator](../src/DataMigration.Domain.Xellent/FingerprintCalculator.cs), and DataMigration's [representation principle](../../NewSettlement.DataMigration/docs/representation-principle.md). Prefer executable mode selection and keys over simplified or historical prose.
