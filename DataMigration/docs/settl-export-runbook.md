# Settl export to SonWin: runbook

The export reads Settl and writes files to a share. **It never writes to Settl.** You run everything with one script, `settl-export.ps1`, which is deployed next to `DataMigration.OpsCli.exe` (`C:\Services\DataMigration\OpsCli\`). Every step ends with a green **OK** or a red **NOT OK**.

Open PowerShell in `C:\Services\DataMigration\OpsCli\` and replace `\\fileserver\sonwin\settl-export` below with your share.

## 1. Trial on featuretest (5 customers, a minute)

```powershell
.\settl-export.ps1 Trial -Share \\fileserver\sonwin\settl-export
```

You should see `Published ...`, then the check's summary, then **OK**. On the share you now have:

```
settl-export\featuretest-trial\2026-11-12T081500Z\
    customers.jsonl   one customer per line
    delta.jsonl       what changed since the previous run (first run: everything)
    manifest.json     counts and checksums
    _READY            written last; SonWin only reads folders that have it
```

Spot-check one customer: open `customers.jsonl` in Notepad, take the first line's `customerNumber`, find that customer in Settl and compare:

- [ ] customer number, name, CVR or CPR
- [ ] each metering point (18-digit id) and its delivery start
- [ ] payer, payment channel, EAN
- [ ] contract name and the price lists

Trial runs go to `featuretest-trial`, so they never affect the real delta. Repeat them as often as you like.

## 2. Full run on featuretest

```powershell
.\settl-export.ps1 Run -Share \\fileserver\sonwin\settl-export
```

This reads every customer linked today to an electricity metering point in delivery. Expect it to take a while; on production it is about an hour. It ends with **OK**, and the share has `settl-export\featuretest\<run>\`.

Run it a second time. The check summary should now show a small delta (`0 added, 0 changed, 0 removed` if nothing changed in Settl between the runs).

## 3. Payment agreements

Settl has no API for payment agreements, so export the **Payment Agreement** CSV from the Settl web interface and save it, for example as `D:\settl\payment-agreements.csv`. Then:

```powershell
.\settl-export.ps1 Trial -Share \\fileserver\sonwin\settl-export -PaymentCsv D:\settl\payment-agreements.csv
```

The check summary shows `payment CSV: payment-agreements.csv`. Spot-check that a customer with a payment agreement has it under `paymentAgreements`. The CSV must be **less than 24 hours old**, or the run refuses to start, so the nightly job never publishes stale payment data.

## 4. Production

The same steps with `-Env production`. It is still read-only on Settl.

```powershell
.\settl-export.ps1 Trial -Env production -Share \\fileserver\sonwin\settl-export
.\settl-export.ps1 Run   -Env production -Share \\fileserver\sonwin\settl-export -PaymentCsv D:\settl\payment-agreements.csv
```

## 5. Every night

Run this once, as administrator. It asks for the account the job runs as; that account needs Settl access and write access to the share.

```powershell
.\settl-export.ps1 Schedule -Env production -Share \\fileserver\sonwin\settl-export -PaymentCsv D:\settl\payment-agreements.csv -At 02:15
```

To try the job immediately: `Start-ScheduledTask -TaskName 'Settl export to share (production)'`. To check the newest night at any time:

```powershell
.\settl-export.ps1 Check -Env production -Share \\fileserver\sonwin\settl-export
```

To stop the nightly job: `.\settl-export.ps1 Unschedule -Env production -Share \\fileserver\sonwin\settl-export`.

Logs: `C:\Services\DataMigration\data\settl-export-logs\`, one file per step.

## When it says NOT OK

| Message | What to do |
|---|---|
| `.lock exists` | A run is going, or one crashed. If no run is going, delete `<share>\<env>\.lock` and run again. |
| `payment agreement file ... more than 24 hours ago` / `not found` | Export a fresh Payment Agreement CSV from Settl to the same path. |
| `Export failed (exit 2)` / `customer(s) carry read errors` | Settl did not answer for some customers. Nothing was published; the run is kept as `<run>.failed`. Run again. If it keeps failing, send me the log. |
| `reading Settl reference data failed` / `FindMeteringPoints failed` | Settl is not reachable from this machine (network, VPN, credentials). Check that OpsCli works with another command, then run again. |
| `the newest run finished ..., more than 26 hours ago` | The nightly job did not run or failed. Look in Task Scheduler and the newest log. |
| `does not match its checksum` / `manifest says` | Files on the share were changed or copied incompletely. Run again. |
| Anything else | Send me the log file. It holds ids and counts, no customer data. |

A failed run never replaces the last good one: SonWin keeps reading the newest folder with `_READY`.

## What SonWin gets

- `customers.jsonl`: every customer, one JSON object per line. The shape follows the approved examples `Customer_405013` (private) and `Customer_397697` (business).
- `delta.jsonl`: one line per customer that changed since the previous run, `{"op":"added"|"changed"|"removed","customerNumber":...,"customer":{...}}`. Removed lines have no `customer`.
- `manifest.json`: run time, counts, previous run, the payment CSV used and a SHA-256 checksum per file.
- The 14 newest runs are kept, older ones are deleted.

Details: [plan](../plans/settl-export-sonwin.md).
