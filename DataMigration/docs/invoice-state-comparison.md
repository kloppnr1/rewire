# Compare invoice states before and after the ownership fix

Keep the old state file unchanged and generate the new state to a **different path**. For example, from `C:\Services\DataMigration\OpsCli` on the production server:

```powershell
Copy-Item -LiteralPath "C:\Services\DataMigration\data\state-invoices.json" `
  -Destination "C:\Services\DataMigration\data\state-invoices-before.json"

.\DataMigration.OpsCli.exe generate-state-from-settl `
  --env production `
  --include-active `
  --skip-unresolved-source-accounts `
  --output "C:\Services\DataMigration\data\state-invoices-after.json"
```

With invoice enrichment, the generator writes three reports beside the new state. `state-invoices-after-ownership-issues.csv` lists omitted metering points, MEH-only Settl customers, and unresolved source accounts. `state-invoices-after-historical-sources.csv` lists older Xellent accounts found through merge grouping, including the proposed Settl customer, all claimants, the **current** Settl customers of their Xellent metering points, and the earliest/latest FOR contract-end dates. `state-invoices-after-excluded-invoices.csv` lists invoice rows whose Xellent contract is not FOR or cannot be found (`--no-invoices` does not produce this third report). These reports make omissions visible before a PDF upload.

Direct metering-point ownership at COS−1 takes precedence and considers FOR contracts plus older contracts with a blank company, never explicit MEH contracts. Historical merge grouping also excludes MEH-only accounts. An older account is added when its in-scope merge group names one Settl customer. If all claimants and the older account have the same nonblank Xellent CVR, the claimant with the lowest original Xellent representative account number receives the invoices; production's leading-9 Settl padding is removed for this comparison. The historical report marks this as `AssignedSameCvrLowestXellentAccount` and retains all candidates for review. A metering point may have changed customer since the older contract ended, so its **current** Settl customer is report context, not historical ownership evidence. Different-CVR or unverifiable claims are never resolved by first-claim order. With `--skip-unresolved-source-accounts`, unresolved direct or historical accounts and **all invoices from them** are omitted and reported; without it they block the state write. Metering points without a Settl customer link or in-scope Xellent account at COS−1 are omitted and reported either way. Review all generated reports before comparing or uploading PDFs.

The customer population also excludes Settl customers whose Xellent metering-point origins are MEH with no FOR or blank-company legacy origin. For a mixed customer, MEH-only metering points are omitted but in-scope points keep the customer. Settl-native points with no Xellent company evidence are not assumed to be MEH.

Source-account invoice history is queried by account number, then each invoice's Xellent contract number is checked against the contract's company. Explicit MEH and other non-FOR invoices are omitted, including for mixed FOR/MEH accounts. Older contracts with a blank company are retained as legacy electricity contracts; an invoice with no matching contract is omitted and reported.

Once both JSON files exist, run the read-only local comparison:

```powershell
.\DataMigration.OpsCli.exe compare-state `
  "C:\Services\DataMigration\data\state-invoices-before.json" `
  "C:\Services\DataMigration\data\state-invoices-after.json" `
  --output "C:\Services\DataMigration\data\state-invoices-comparison.csv"
```

`compare-state` does not call Settl or Xellent. It prints before/after customer, source-account assignment, and invoice counts, then writes one CSV row per difference. The report covers:

- Settl customers added or removed, or with a changed change-of-supplier date;
- Xellent source accounts added, removed, moved to another Settl customer, or assigned to multiple customers;
- invoices (identified by source account and invoice ID) added, removed, moved, or duplicated.

The command requires a new output CSV path and will not overwrite either input or an existing report. Before a PDF upload, investigate every `Removed` and `AmbiguousOwnership` row and reconcile them with both generator reports. A `Moved` invoice is expected only when the ownership evidence supports its destination. The comparison checks placement and membership, not PDF contents or all invoice metadata.
