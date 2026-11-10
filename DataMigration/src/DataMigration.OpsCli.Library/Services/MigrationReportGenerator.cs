using System.Text;
using System.Web;
using DataMigration.Core;
using DataMigration.OpsCli.Reporting;

namespace DataMigration.OpsCli.Services;

public static class OpsMigrationReportGenerator
{
    private record ReportStats(
        int PersonCount, int CompanyCount, int UnknownCount,
        int DarPrimary, int DarFallback, int DarMissing,
        int JoinedMps, int MpsWithMeter, int MpsWithoutMeter,
        Dictionary<string, int> InvoiceChannels, Dictionary<string, int> PaymentChannels);

    private static ReportStats ComputeReportStats(OpsMigrationReport report, IWarningCollector warningCollector)
    {
        // En kunde uden både CVR og CPR er hverken privat eller erhverv — vi ved ikke hvem hun er.
        // Mapperen kalder hende "person" fordi CVR er tomt, så uden det her tælles hun som
        // privatkunde og forsvinder i en kategori hun ikke hører til. Kun person-siden kan være
        // ukendt: typen bliver kun "Company" når der ER et CVR.
        static bool IsUnknown(DataMigration.SettlApi.Models.CreateCustomerDto c) =>
            c.CustomerType == "Person" && string.IsNullOrWhiteSpace(c.NationalIdentificationNumber);

        var personCount = report.Customers.Count(c => c.CustomerType == "Person" && !IsUnknown(c));
        var companyCount = report.Customers.Count(c => c.CustomerType == "Company");
        var unknownCount = report.Customers.Count(IsUnknown);

        var customersWithDar = report.Customers.Count(c =>
            c.Address?.PostalAddress?.PhysicalAddress?.AddressIdentifier != null);
        var infos = warningCollector.GetInfos();
        var darFallback = infos.Count(w => w.StartsWith("Customer ") && w.Contains("(fallback)"));
        var darErrors = warningCollector.GetErrors();
        var darMissing = darErrors.Count(e => e.StartsWith("Customer ") && e.Contains("no AddressIdentifier"));
        var darPrimary = customersWithDar - darFallback;

        var joinedMps = report.JoinedMeteringPoints.Count;

        var mpsWithMeter = report.MeteringPoints.Count(m =>
            m.MeterCharacteristics?.Meters is { Count: > 0 });
        var mpsWithoutMeter = report.MeteringPoints.Count - mpsWithMeter;

        var invoiceChannels = report.Payers
            .Where(p => !string.IsNullOrEmpty(p.PayerBilling?.InvoiceChannel))
            .GroupBy(p => p.PayerBilling!.InvoiceChannel)
            .ToDictionary(g => g.Key!, g => g.Count());
        var paymentChannels = report.Payers
            .Where(p => !string.IsNullOrEmpty(p.PayerBilling?.PaymentChannel))
            .GroupBy(p => p.PayerBilling!.PaymentChannel)
            .ToDictionary(g => g.Key!, g => g.Count());

        return new ReportStats(personCount, companyCount, unknownCount,
            darPrimary, darFallback, darMissing,
            joinedMps, mpsWithMeter, mpsWithoutMeter,
            invoiceChannels, paymentChannels);
    }

    public static string GenerateHtmlReport(
        OpsMigrationReport report,
        string profileName,
        bool dryRun,
        IWarningCollector warningCollector,
        int accountsCount = 0,
        Dictionary<string, string>? groupOwners = null)
    {
        var warnings = warningCollector.GetWarnings();
        var errors = warningCollector.GetErrors();
        var infos = warningCollector.GetInfos();
        var collectorErrors = errors;

        var h = new StringBuilder();
        string E(string? s) => HttpUtility.HtmlEncode(s ?? "");

        h.AppendLine("<!DOCTYPE html>");
        h.AppendLine("<html lang=\"da\"><head><meta charset=\"utf-8\">");
        h.AppendLine($"<title>Migreringsrapport: {E(profileName)}</title>");
        h.AppendLine("<link rel=\"preconnect\" href=\"https://fonts.googleapis.com\">");
        h.AppendLine("<link rel=\"preconnect\" href=\"https://fonts.gstatic.com\" crossorigin>");
        h.AppendLine("<link href=\"https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400;500&family=IBM+Plex+Sans:wght@400;500;600&display=swap\" rel=\"stylesheet\">");
        h.AppendLine("<style>");
        h.AppendLine("""
            :root {
                --font: 'IBM Plex Sans', -apple-system, BlinkMacSystemFont, sans-serif;
                --mono: 'IBM Plex Mono', 'Cascadia Code', 'Fira Code', monospace;
                --bg: #f4f5f7; --card: #fff; --border: #e2e4e9; --border-light: #eff0f3;
                --accent: #0d6b58; --accent-light: #ecfdf5; --accent-mid: #d1fae5;
                --text: #1a1d23; --text-secondary: #4b5060; --text-tertiary: #848a9a;
                --red: #b91c1c; --red-light: #fef2f2; --amber: #b45309; --amber-light: #fffbeb;
                --shadow: 0 1px 3px rgba(0,0,0,.04), 0 1px 2px rgba(0,0,0,.02);
                --shadow-lg: 0 20px 60px rgba(0,0,0,.12), 0 4px 16px rgba(0,0,0,.06);
                --radius: .375rem;
                --row-h: 24px;
            }
            * { box-sizing: border-box; margin: 0; padding: 0; }
            body { font-family: var(--font); background: var(--bg); color: var(--text); padding: 2.5rem 2rem; line-height: 1.55; max-width: 1060px; margin: 0 auto; -webkit-font-smoothing: antialiased; }
            h1 { font-size: 1.35rem; font-weight: 600; letter-spacing: -.015em; margin-bottom: .15rem; }
            .meta { color: var(--text-tertiary); font-size: .8rem; margin-bottom: 2rem; letter-spacing: .01em; }
            .meta span { margin-right: 1.75rem; }
            .card { background: var(--card); border: 1px solid var(--border); border-radius: var(--radius); padding: 1.25rem 1.5rem; margin-bottom: 1rem; box-shadow: var(--shadow); }
            .card h2 { font-size: .75rem; font-weight: 600; color: var(--text-tertiary); text-transform: uppercase; letter-spacing: .06em; margin-bottom: .875rem; }
            table { border-collapse: collapse; width: 100%; font-size: .85rem; }
            th, td { padding: .5rem .75rem; text-align: left; }
            th { font-weight: 500; color: var(--text-tertiary); font-size: .75rem; text-transform: uppercase; letter-spacing: .05em; border-bottom: 2px solid var(--border); }
            td { border-bottom: 1px solid var(--border-light); }
            td.num { text-align: right; font-family: var(--mono); font-size: .82rem; font-weight: 500; font-variant-numeric: tabular-nums; }
            .cvr { color: var(--text-tertiary); font-size: .78rem; }
            .mp-count { color: var(--text-tertiary); font-family: var(--font); font-weight: 500; font-size: .75rem; }
            .badge { display: inline-block; padding: .125rem .4rem; border-radius: 3px; font-size: .68rem; font-weight: 500; vertical-align: middle; margin-left: .35rem; font-family: var(--font); letter-spacing: .02em; }
            .badge-settl { background: var(--accent-light); color: var(--accent); }
            .badge-merged { background: var(--amber-light); color: var(--amber); }
            .badge-merge-count { background: var(--bg); color: var(--text-secondary); border: 1px solid var(--border); margin-left: 0; }
            .badge-customer { background: var(--accent-light); color: var(--accent); }
            .badge-payer { background: #f0fdf4; color: #166534; }
            .badge-mp { background: var(--bg); color: var(--text-secondary); }
            .errors li, .warnings li, .infos li { font-size: .82rem; padding: .4rem .75rem; list-style: none; border-left: 2px solid; font-family: var(--mono); font-size: .78rem; }
            .errors li { border-color: var(--red); color: var(--red); background: var(--red-light); }
            .warnings li { border-color: var(--amber); color: var(--amber); background: var(--amber-light); }
            .infos li { border-color: var(--accent); color: var(--accent); background: var(--accent-light); }
            .errors, .warnings, .infos { display: flex; flex-direction: column; gap: .25rem; }
            details.card { cursor: default; }
            details.card summary { cursor: pointer; user-select: none; font-size: .85rem; font-weight: 600; }
            details.card .count { color: var(--text-tertiary); font-weight: 500; }
            details.card .detail-content { margin-top: .75rem; }
            .legend { display: flex; gap: 1.5rem; margin-bottom: .75rem; font-size: .78rem; align-items: center; color: var(--text-tertiary); }
            .legend-item { display: flex; align-items: center; gap: .3rem; }

            /* Clickable entity links */
            .entity-link { color: var(--accent); cursor: pointer; background: var(--accent-light); padding: .1rem .35rem; border-radius: 3px; text-decoration: none; border: 1px solid var(--accent-mid); transition: all .12s; }
            .entity-link:hover { background: var(--accent-mid); border-color: var(--accent); }
            .mp-link { color: var(--text-secondary); background: var(--bg); border-color: var(--border); }
            .mp-link:hover { background: var(--border-light); border-color: #b0b5bf; color: var(--text); }

            /* Virtualiseret træ */
            .tree-viewport { height: 70vh; overflow-y: auto; overflow-x: hidden; border: 1px solid var(--border); border-radius: var(--radius); background: var(--card); position: relative; contain: strict; }
            .tree-sizer { position: relative; width: 100%; }
            .vrow { position: absolute; left: 0; right: 0; height: var(--row-h); line-height: var(--row-h); font-family: var(--mono); font-size: .78rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; color: var(--text-secondary); }
            .vrow strong { font-family: var(--font); font-weight: 600; color: var(--text); }
            .vrow.k0 { font-family: var(--font); font-weight: 600; color: var(--text); font-size: .82rem; background: var(--bg); border-top: 1px solid var(--border); border-bottom: 1px solid var(--border-light); cursor: pointer; user-select: none; }
            .vrow.k4 { opacity: .5; }
            .vrow.k5 { color: var(--text-secondary); font-family: var(--font); font-size: .76rem; font-weight: 500; }
            .vrow.k6 { color: var(--text-tertiary); font-size: .74rem; }
            .vcaret { display: inline-block; width: .8rem; color: var(--text-tertiary); font-size: .65em; transition: transform .15s; }
            .vrow.collapsed .vcaret { transform: rotate(-90deg); }
            .acct-num { font-weight: 500; }
            .tree-empty { padding: 2rem; text-align: center; color: var(--text-tertiary); font-size: .85rem; font-family: var(--font); }

            /* Modal */
            .modal-backdrop { position: fixed; inset: 0; background: rgba(26,29,35,.4); z-index: 1000; display: flex; align-items: center; justify-content: center; opacity: 0; visibility: hidden; transition: opacity .2s, visibility .2s; }
            .modal-backdrop.active { opacity: 1; visibility: visible; }
            .modal-card { background: var(--card); border-radius: var(--radius); box-shadow: var(--shadow-lg); max-width: 620px; width: 92%; height: 80vh; display: flex; flex-direction: column; transform: translateY(8px); transition: transform .2s ease; position: relative; border: 1px solid var(--border); }
            .modal-backdrop.active .modal-card { transform: translateY(0); }
            .modal-top { position: sticky; top: 0; background: var(--card); border-radius: var(--radius) var(--radius) 0 0; z-index: 1; }
            .modal-header { padding: 1rem 1.25rem .5rem; display: flex; align-items: center; gap: .5rem; }
            .modal-header .badge { margin-left: 0; font-size: .7rem; padding: .15rem .45rem; }
            .modal-header h3 { font-size: 1rem; color: var(--text); font-weight: 600; font-family: var(--mono); letter-spacing: -.01em; }
            .modal-close { position: absolute; right: .75rem; top: .75rem; width: 1.5rem; height: 1.5rem; border: none; background: none; cursor: pointer; font-size: 1.1rem; color: var(--text-tertiary); display: flex; align-items: center; justify-content: center; transition: color .12s; z-index: 2; }
            .modal-close:hover { color: var(--red); }
            .modal-tabs { display: flex; gap: 0; padding: 0 1.25rem; border-bottom: 1px solid var(--border); }
            .modal-tab { padding: .5rem .75rem; font-size: .8rem; font-weight: 500; color: var(--text-tertiary); cursor: pointer; border-bottom: 2px solid transparent; margin-bottom: -1px; transition: color .12s; user-select: none; white-space: nowrap; }
            .modal-tab:hover { color: var(--text-secondary); }
            .modal-tab.active { color: var(--accent); border-bottom-color: var(--accent); }
            .modal-tab .tab-count { font-size: .7rem; color: var(--text-tertiary); padding: .05rem .3rem; margin-left: .25rem; font-weight: 500; }
            .modal-body { padding: 1rem 1.25rem; overflow-y: auto; flex: 1; }
            .detail-grid { display: grid; grid-template-columns: 9rem 1fr; gap: .25rem .75rem; font-size: .82rem; }
            .detail-label { color: var(--text-tertiary); font-weight: 500; font-size: .78rem; }
            .detail-label .settl-field { display: block; font-weight: 400; font-size: .68rem; opacity: .6; }
            .detail-value { color: var(--text); word-break: break-word; font-family: var(--mono); font-size: .8rem; }
            .detail-section-title { font-size: .7rem; font-weight: 600; color: var(--text-tertiary); text-transform: uppercase; letter-spacing: .06em; margin-top: .875rem; padding-top: .625rem; border-top: 1px solid var(--border-light); margin-bottom: .35rem; }
            .mp-item { border: 1px solid var(--border); border-radius: var(--radius); padding: .5rem .75rem; margin-bottom: .35rem; cursor: pointer; transition: border-color .12s; }
            .mp-item:hover { border-color: #b0b5bf; }
            .mp-item-header { display: flex; align-items: center; gap: .35rem; font-weight: 500; font-size: .82rem; font-family: var(--mono); color: var(--text); }
            .mp-item-header .badge { margin-left: 0; }
            .mp-item-sub { font-size: .78rem; color: var(--text-tertiary); margin-top: .15rem; font-family: var(--font); }
            .mp-detail { display: none; margin-top: .5rem; padding-top: .5rem; border-top: 1px solid var(--border-light); }
            .mp-item.expanded { border-color: var(--accent); background: var(--accent-light); }
            .mp-item.expanded .mp-detail { display: block; }
            .mp-item-header::after { content: '\203A'; margin-left: auto; color: var(--border); font-size: 1em; transition: transform .12s; }
            .mp-item.expanded .mp-item-header::after { transform: rotate(90deg); color: var(--accent); }
            .copy-btn { display: inline-block; margin-left: .35rem; cursor: pointer; color: var(--text-tertiary); font-size: .75rem; opacity: 0; transition: opacity .12s, color .12s; vertical-align: middle; user-select: none; }
            .detail-value:hover .copy-btn { opacity: 1; }
            .copy-btn:hover { color: var(--accent); }
            .copy-btn.copied { opacity: 1; color: var(--accent); }

            /* Pattern sub-grouping */
            .pattern-line { padding: .4rem .75rem; font-size: .82rem; font-weight: 500; border-radius: var(--radius); margin-bottom: .25rem; display: flex; align-items: baseline; gap: .5rem; flex-wrap: wrap; }
            .pattern-line .count { font-weight: 400; }
            .pattern-ids { font-weight: 400; font-size: .75rem; opacity: .7; font-family: var(--mono); }
            .errors-bg { background: var(--red-light); color: var(--red); border-left: 2px solid var(--red); }
            .warnings-bg { background: var(--amber-light); color: var(--amber); border-left: 2px solid var(--amber); }
            .infos-bg { background: var(--accent-light); color: var(--accent); border-left: 2px solid var(--accent); }
        """);
        h.AppendLine("</style></head><body>");

        // Header
        h.AppendLine($"<h1>Migreringsrapport: {E(profileName)}</h1>");
        h.AppendLine("<div class=\"meta\">");
        h.AppendLine($"<span>Dato: {DateTime.Now:yyyy-MM-dd HH:mm}</span>");
        h.AppendLine($"<span>Testkørsel: {(dryRun ? "Ja" : "Nej")}</span>");
        if (accountsCount > 0)
            h.AppendLine($"<span>Konti: {accountsCount}</span>");
        h.AppendLine("</div>");

        // Summary table
        h.AppendLine("<div class=\"card\"><h2>Oversigt</h2>");
        h.AppendLine("<table><tr><th>Entitet</th><th>Udtræk</th><th>Oprettet</th><th>Eksisterende</th><th>Fejlet</th></tr>");
        if (report.CustomersExtracted > 0)
            h.AppendLine($"<tr><td>Kunder</td><td class=\"num\">{report.CustomersExtracted}</td><td class=\"num\">{report.CustomersCreated}</td><td class=\"num\">{report.CustomersExisting}</td><td class=\"num\">{report.CustomersFailed}</td></tr>");
        if (report.PayersExtracted > 0)
            h.AppendLine($"<tr><td>Betalere</td><td class=\"num\">{report.PayersExtracted}</td><td class=\"num\">{report.PayersCreated}</td><td class=\"num\">{report.PayersExisting}</td><td class=\"num\">{report.PayersFailed}</td></tr>");
        if (report.MeteringPointsExtracted > 0)
            h.AppendLine($"<tr><td>Målepunkter</td><td class=\"num\">{report.MeteringPointsExtracted}</td><td class=\"num\">{report.MeteringPointsCreated}</td><td class=\"num\">{report.MeteringPointsExisting}</td><td class=\"num\">{report.MeteringPointsFailed}</td></tr>");
        h.AppendLine("</table>");

        if (report.CustomerMpsLinked > 0 || report.PayerMpsLinked > 0 || report.ContractMpsLinked > 0
            || report.CustomerMpsFailed > 0 || report.PayerMpsFailed > 0 || report.ContractMpsFailed > 0)
        {
            h.AppendLine("<table style=\"margin-top:.75rem\"><tr><th>Kobling</th><th>Koblet</th><th>Fejlet</th></tr>");
            if (report.CustomerMpsLinked > 0 || report.CustomerMpsFailed > 0)
                h.AppendLine($"<tr><td>Kunde-MPs</td><td class=\"num\">{report.CustomerMpsLinked}</td><td class=\"num\">{report.CustomerMpsFailed}</td></tr>");
            if (report.PayerMpsLinked > 0 || report.PayerMpsFailed > 0)
                h.AppendLine($"<tr><td>Betaler-MPs</td><td class=\"num\">{report.PayerMpsLinked}</td><td class=\"num\">{report.PayerMpsFailed}</td></tr>");
            if (report.ContractMpsLinked > 0 || report.ContractMpsFailed > 0)
                h.AppendLine($"<tr><td>Kontrakt-MPs</td><td class=\"num\">{report.ContractMpsLinked}</td><td class=\"num\">{report.ContractMpsFailed}</td></tr>");
            h.AppendLine("</table>");
        }
        h.AppendLine("</div>");

        // Breakdown stats
        var htmlStats = ComputeReportStats(report, warningCollector);
        h.AppendLine("<div class=\"card\"><h2>Detaljer</h2>");
        h.AppendLine("<table>");
        h.AppendLine($"<tr><td>Privatkunder</td><td class=\"num\">{htmlStats.PersonCount}</td></tr>");
        h.AppendLine($"<tr><td>Erhvervskunder</td><td class=\"num\">{htmlStats.CompanyCount}</td></tr>");
        if (htmlStats.UnknownCount > 0)
            h.AppendLine($"<tr><td>Ukendt (hverken CVR eller CPR)</td><td class=\"num\">{htmlStats.UnknownCount}</td></tr>");
        h.AppendLine($"<tr><td>DAR adresse (prim&aelig;r)</td><td class=\"num\">{htmlStats.DarPrimary}</td></tr>");
        h.AppendLine($"<tr><td>DAR adresse (fallback)</td><td class=\"num\">{htmlStats.DarFallback}</td></tr>");
        h.AppendLine($"<tr><td>DAR adresse mangler</td><td class=\"num\">{htmlStats.DarMissing}</td></tr>");
        h.AppendLine($"<tr><td>Samlefaktura MPs</td><td class=\"num\">{htmlStats.JoinedMps}</td></tr>");
        h.AppendLine($"<tr><td>MPs med m&aring;ler</td><td class=\"num\">{htmlStats.MpsWithMeter}</td></tr>");
        h.AppendLine($"<tr><td>MPs uden m&aring;ler</td><td class=\"num\">{htmlStats.MpsWithoutMeter}</td></tr>");
        foreach (var (channel, count) in htmlStats.InvoiceChannels.OrderByDescending(x => x.Value))
            h.AppendLine($"<tr><td>Fakturakanal: {E(channel)}</td><td class=\"num\">{count}</td></tr>");
        foreach (var (channel, count) in htmlStats.PaymentChannels.OrderByDescending(x => x.Value))
            h.AppendLine($"<tr><td>Betalingskanal: {E(channel)}</td><td class=\"num\">{count}</td></tr>");
        h.AppendLine("</table></div>");

        // Validation legend
        h.AppendLine("<details class=\"card\"><summary>Validering</summary>");
        h.AppendLine("<div class=\"detail-content\">");
        h.AppendLine("<table>");
        h.AppendLine("<tr><th>Niveau</th><th>Entitet</th><th>Felt</th><th>Beskrivelse</th></tr>");
        h.AppendLine("<tr><td>&#10060; Fejl</td><td>Kunde</td><td>Name, StreetName, PostalCode, City, Email</td><td>Obligatoriske felter</td></tr>");
        h.AppendLine("<tr><td>&#10060; Fejl</td><td>Kunde</td><td>CVR, CPR</td><td>Formatvalidering (8/10 cifre)</td></tr>");
        h.AppendLine("<tr><td>&#10060; Fejl</td><td>Kunde</td><td>AddressIdentifier (DAR)</td><td>Ingen adresse-ID fundet</td></tr>");
        h.AppendLine("<tr><td>&#10060; Fejl</td><td>Betaler</td><td>Name, StreetName, PostalCode, City, Email</td><td>Obligatoriske felter</td></tr>");
        h.AppendLine("<tr><td>&#10060; Fejl</td><td>Betaler</td><td>AddressIdentifier (DAR)</td><td>Ingen adresse-ID fundet</td></tr>");
        h.AppendLine("<tr><td>&#10060; Fejl</td><td>MP</td><td>StreetName, BuildingNumber, PostalCode, City</td><td>Installationsadresse</td></tr>");
        h.AppendLine("<tr><td>&#10060; Fejl</td><td>MP</td><td>Name1, StreetName, BuildingNumber, PostalCode, City</td><td>LegalAddress kontaktpunkt</td></tr>");
        h.AppendLine("<tr><td>&#10060; Fejl</td><td>MP</td><td>PowerExchangeArea</td><td>Ukendt prisomr&aring;de</td></tr>");
        h.AppendLine("<tr><td>&#9888; Advarsel</td><td>Kunde</td><td>Phone, SupplementaryName, BuildingNumber</td><td>Opsummeret (antal/total)</td></tr>");
        h.AppendLine("<tr><td>&#9888; Advarsel</td><td>Betaler</td><td>SupplementaryName, BuildingNumber</td><td>Opsummeret (antal/total)</td></tr>");
        h.AppendLine("<tr><td>&#9888; Advarsel</td><td>Betaler</td><td>Billing channel default</td><td>PBS_BS fallback</td></tr>");
        h.AppendLine("<tr><td>&#8505; Info</td><td>Kunde</td><td>DAR address fallback</td><td>Adresse fra kontaktinfo</td></tr>");
        h.AppendLine("<tr><td>&#8505; Info</td><td>Betaler</td><td>DAR address fallback</td><td>Adresse fra kontaktinfo</td></tr>");
        h.AppendLine("<tr><td>&#8505; Info</td><td>Betaler</td><td>FIK payment channel</td><td>Standard betalingskanal</td></tr>");
        h.AppendLine("</table>");
        h.AppendLine("</div></details>");

        // Errors / warnings / infos — sub-grouped by message pattern
        var allHtmlErrors = report.Errors.Concat(collectorErrors).ToList();
        AppendMessageCard(h, E, "Fejl", warningCollector.ErrorCount, allHtmlErrors, "errors-bg");
        AppendMessageCard(h, E, "Advarsler", warningCollector.WarningCount, warnings, "warnings-bg");
        AppendMessageCard(h, E, "Info", infos.Count, infos, "infos-bg");

        // Account grouping tree — rækkerne ligger i payloaden, ikke i DOM'en
        if (report.AccountGroupings.Count > 0)
        {
            var totalAccounts = report.AccountGroupings.Sum(g => g.MemberAccounts.Count);
            var mergedAway = totalAccounts - report.AccountGroupings.Count;
            var totalMps = report.AccountMeteringPoints.Values.Sum(v => v.Count);

            h.AppendLine("<div class=\"card\">");
            h.AppendLine($"<h2>Kontogruppering ({totalAccounts} konti &rarr; {report.AccountGroupings.Count} kunder i Settl, {totalMps} målepunkter)</h2>");

            h.AppendLine("<div class=\"legend\">");
            h.AppendLine("<div class=\"legend-item\"><span class=\"badge badge-settl\">Settl-kunde</span> Oprettes i Settl</div>");
            if (mergedAway > 0)
                h.AppendLine($"<div class=\"legend-item\"><span class=\"badge badge-merged\">Sammenlagt</span> Oprettes ikke &mdash; MPs flyttes til hovedkonto ({mergedAway} konti)</div>");
            h.AppendLine("<div class=\"legend-item\" style=\"margin-left:auto; color:var(--text-tertiary); font-style:italic\">Klik p&aring; kontonr. eller m&aring;lepunkt for detaljer</div>");
            h.AppendLine("</div>");

            h.AppendLine("<div style=\"position:relative;margin-bottom:.75rem\"><input type=\"text\" id=\"treeSearch\" placeholder=\"&#x1F50D; S&oslash;g efter kontonr., navn eller m&aring;lepunkt...\" disabled style=\"width:100%;padding:.75rem 1rem;padding-right:6rem;font-size:.9rem;border:2px solid var(--border);border-radius:var(--radius);background:var(--card);color:var(--text);outline:none;font-family:var(--font);box-shadow:var(--shadow);transition:border-color .15s\" onfocus=\"this.style.borderColor='var(--accent)'\" onblur=\"this.style.borderColor='var(--border)'\"><span id=\"searchCount\" style=\"position:absolute;right:1rem;top:50%;transform:translateY(-50%);font-size:.8rem;color:var(--text-tertiary);font-weight:500\"></span></div>");
            h.AppendLine("<div class=\"tree-viewport\" id=\"treeViewport\"><div class=\"tree-sizer\" id=\"treeSizer\"><div class=\"tree-empty\" id=\"treeStatus\">Indl&aelig;ser data&hellip;</div></div></div>");
            h.AppendLine("</div>");
        }

        // Modal container
        h.AppendLine("<div class=\"modal-backdrop\" id=\"modal\" onclick=\"if(event.target===this)closeModal()\">");
        h.AppendLine("<div class=\"modal-card\">");
        h.AppendLine("<button class=\"modal-close\" onclick=\"closeModal()\">&times;</button>");
        h.AppendLine("<div class=\"modal-top\">");
        h.AppendLine("<div class=\"modal-header\" id=\"modalHeader\"></div>");
        h.AppendLine("<div class=\"modal-tabs\" id=\"modalTabs\"></div>");
        h.AppendLine("</div>");
        h.AppendLine("<div class=\"modal-body\" id=\"modalBody\"></div>");
        h.AppendLine("</div></div>");

        // ---- Payload: gzip + base64, indlejret fordi fetch() er blokeret på file:// ----
        var builder = new ReportTreeBuilder(report, groupOwners);
        builder.Build();

        h.Append("<script id=\"reportIndex\" type=\"text/plain\">");
        h.Append(ReportPayload.Pack(builder.Index));
        h.AppendLine("</script>");

        h.Append("<script id=\"reportShards\" type=\"application/json\">[");
        for (var i = 0; i < builder.Shards.Count; i++)
        {
            if (i > 0) h.Append(',');
            h.Append('"').Append(ReportPayload.Pack(builder.Shards[i])).Append('"');
        }
        h.AppendLine("]</script>");

        h.AppendLine("<script>");
        h.AppendLine(ClientScript);
        h.AppendLine("</script>");

        h.AppendLine("</body></html>");
        return h.ToString();
    }

    private static void AppendMessageCard(
        StringBuilder h, Func<string?, string> e, string title, int count, IReadOnlyList<string> messages, string cssClass)
    {
        if (messages.Count == 0) return;
        h.AppendLine($"<details class=\"card\"><summary>{title} <span class=\"count\">({count})</span></summary>");
        h.AppendLine("<div class=\"detail-content\">");
        foreach (var (group, msgs) in MessageGrouper.GroupByEntity(messages))
        {
            h.AppendLine($"<h4>{e(group)} ({msgs.Count})</h4>");
            foreach (var (pattern, ids) in MessageGrouper.GroupByPattern(msgs))
            {
                var preview = string.Join(", ", ids.Take(3).Select(id => e(id)));
                var ellipsis = ids.Count > 3 ? ", ..." : "";
                h.AppendLine($"<div class=\"pattern-line {cssClass}\">{e(pattern)} <span class=\"count\">({ids.Count})</span> <span class=\"pattern-ids\">{preview}{ellipsis}</span></div>");
            }
        }
        h.AppendLine("</div></details>");
    }

    private const string ClientScript = """
        // ============================================================================
        // Virtualiseret træ + lazy detaljedata.
        //
        // Rækkerne ligger som data i #reportIndex (gzip+base64) og materialiseres kun
        // for det synlige vindue. Detaljer ligger i #reportShards, hvor hvert shard er
        // komprimeret for sig og først dekomprimeres når man klikker på en kunde i det.
        // ============================================================================

        const ROW_H = 24;
        const KIND = { OWNER: 0, ACCOUNT: 1, MERGE_HEADER: 2, REPRESENTATIVE: 3, MERGED_AWAY: 4, CONTRACT: 5, MP: 6 };

        let ix = null;                  // index-chunket
        let shardB64 = [];              // base64 pr. shard, uåbnet
        const shardCache = new Map();   // shardIdx -> Promise<shard>
        let groupOfHeader = new Map();  // række-indeks for overskrift -> gruppe-indeks
        let collapsed = new Set();
        let matched = null;             // Uint8Array over blokke, null = ingen søgning
        let visible = [];               // synlige række-indeks
        let scrollPending = false;

        async function inflate(b64) {
            const bin = atob(b64);
            const bytes = new Uint8Array(bin.length);
            for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
            const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream('gzip'));
            return JSON.parse(await new Response(stream).text());
        }

        function loadShard(idx) {
            if (!shardCache.has(idx)) shardCache.set(idx, inflate(shardB64[idx]));
            return shardCache.get(idx);
        }

        function esc(s) {
            const d = document.createElement('div');
            d.textContent = s;
            return d.innerHTML;
        }

        // ---- Virtuel liste -------------------------------------------------------

        function rebuildVisible() {
            const rows = [];
            for (let gi = 0; gi < ix.groups.length; gi++) {
                const hdr = ix.groups[gi][0], cnt = ix.groups[gi][1];
                if (matched) {
                    // Under søgning ignoreres fold — som den gamle rapport gjorde.
                    const hits = [];
                    for (let i = hdr + 1; i <= hdr + cnt; i++) {
                        const b = ix.rows[i].b;
                        if (b >= 0 && matched[b]) hits.push(i);
                    }
                    if (hits.length === 0) continue;
                    rows.push(hdr);
                    for (const i of hits) rows.push(i);
                } else {
                    rows.push(hdr);
                    if (!collapsed.has(gi)) {
                        for (let i = hdr + 1; i <= hdr + cnt; i++) rows.push(i);
                    }
                }
            }
            visible = rows;
        }

        function rowHtml(rowIdx, top) {
            const r = ix.rows[rowIdx];
            const pad = 0.75 + r.d * 1.25;
            const style = `top:${top}px;padding-left:${pad}rem`;
            const attrs = `data-row="${rowIdx}" style="${style}"`;
            const mpCount = r.n ? ` <span class="mp-count">(${r.n} MPs)</span>` : '';

            switch (r.k) {
                case KIND.OWNER: {
                    const gi = groupOfHeader.get(rowIdx);
                    const cls = collapsed.has(gi) ? ' collapsed' : '';
                    return `<div class="vrow k0${cls}" ${attrs}><span class="vcaret">▾</span>${esc(r.l)}${mpCount}</div>`;
                }
                case KIND.ACCOUNT: {
                    const name = r.nm ? ` <strong>${esc(r.nm)}</strong>` : '';
                    const cvr = r.cv ? ` <span class="cvr">CVR: ${esc(r.cv)}</span>` : '';
                    return `<div class="vrow k1" ${attrs}><span class="acct-num entity-link">${esc(r.a)}</span>${name}${cvr}${mpCount}</div>`;
                }
                case KIND.MERGE_HEADER: {
                    const cvr = r.cv ? ` <span class="cvr">CVR: ${esc(r.cv)}</span>` : '';
                    return `<div class="vrow k2" ${attrs}><span class="badge badge-merge-count">${r.n} konti sammenlagt</span>${cvr}</div>`;
                }
                case KIND.REPRESENTATIVE: {
                    const name = r.nm ? ` <strong>${esc(r.nm)}</strong>` : '';
                    return `<div class="vrow k3" ${attrs}><span class="acct-num entity-link">${esc(r.a)}</span>${name} <span class="badge badge-settl">Settl-kunde</span>${mpCount}</div>`;
                }
                case KIND.MERGED_AWAY: {
                    const name = r.nm ? ` ${esc(r.nm)}` : '';
                    return `<div class="vrow k4" ${attrs}><span class="acct-num entity-link">${esc(r.a)}</span>${name} <span class="badge badge-merged">Sammenlagt</span></div>`;
                }
                case KIND.CONTRACT:
                    return `<div class="vrow k5" ${attrs}>${esc(r.l)}${mpCount}</div>`;
                case KIND.MP: {
                    const joined = r.j ? ' <span class="badge badge-merged">Samlefaktura</span>' : '';
                    return `<div class="vrow k6" ${attrs}><span class="entity-link mp-link">${esc(r.g)}</span>${joined}</div>`;
                }
                default:
                    return '';
            }
        }

        function render() {
            const vp = document.getElementById('treeViewport');
            const sizer = document.getElementById('treeSizer');
            sizer.style.height = (visible.length * ROW_H) + 'px';

            if (visible.length === 0) {
                sizer.innerHTML = '<div class="tree-empty">Ingen match.</div>';
                return;
            }

            const first = Math.max(0, Math.floor(vp.scrollTop / ROW_H) - 10);
            const last = Math.min(visible.length, first + Math.ceil(vp.clientHeight / ROW_H) + 20);
            let html = '';
            for (let k = first; k < last; k++) html += rowHtml(visible[k], k * ROW_H);
            sizer.innerHTML = html;
        }

        function refresh(resetScroll) {
            rebuildVisible();
            if (resetScroll) document.getElementById('treeViewport').scrollTop = 0;
            render();
        }

        // ---- Søgning -------------------------------------------------------------

        let searchTimer = null;
        function onSearchInput(value) {
            clearTimeout(searchTimer);
            searchTimer = setTimeout(() => applySearch(value), 120);
        }

        function applySearch(value) {
            const q = value.trim().toLowerCase();
            const countEl = document.getElementById('searchCount');
            if (!q) {
                matched = null;
                countEl.textContent = '';
            } else {
                matched = new Uint8Array(ix.blockSearch.length);
                let hits = 0;
                for (let b = 0; b < ix.blockSearch.length; b++) {
                    if (ix.blockSearch[b].includes(q)) { matched[b] = 1; hits++; }
                }
                countEl.textContent = hits + ' / ' + ix.blockSearch.length;
            }
            refresh(true);
        }

        // ---- Modal ---------------------------------------------------------------

        let ctx = null;   // { shard, acct }

        function openModal() { document.getElementById('modal').classList.add('active'); }
        function closeModal() { document.getElementById('modal').classList.remove('active'); }
        document.addEventListener('keydown', e => { if (e.key === 'Escape') closeModal(); });

        function val(v) {
            if (v === null || v === undefined || v === '') return null;
            return String(v);
        }
        function row(label, value, settlField) {
            const v = val(value);
            const display = v ? esc(v) : '<span style="color:var(--text-tertiary)">—</span>';
            const sf = settlField ? `<span class="settl-field">${settlField}</span>` : '';
            const copyBtn = v ? `<span class="copy-btn" onclick="event.stopPropagation();copyVal(this,'${esc(v.replace(/'/g, "\\'"))}')" title="Kopiér">&#x2398;</span>` : '';
            return `<div class="detail-label">${label}${sf}</div><div class="detail-value">${display}${copyBtn}</div>`;
        }
        function copyVal(el, text) {
            navigator.clipboard.writeText(text).then(() => {
                el.textContent = '✓';
                el.classList.add('copied');
                setTimeout(() => { el.innerHTML = '⎘'; el.classList.remove('copied'); }, 1200);
            });
        }
        function fmtSlimAddress(a) {
            if (!a) return null;
            const parts = [];
            if (a.streetName) {
                let s = a.streetName;
                if (a.buildingNumber) s += ' ' + a.buildingNumber;
                if (a.buildingFloor) s += ', ' + a.buildingFloor;
                if (a.roomIdentification) s += ' ' + a.roomIdentification;
                parts.push(s);
            }
            if (a.postalCode || a.city) parts.push([a.postalCode, a.city].filter(Boolean).join(' '));
            return parts.length > 0 ? parts.join(', ') : null;
        }

        function switchTab(tabName) {
            document.querySelectorAll('.modal-tab').forEach(t => t.classList.toggle('active', t.dataset.tab === tabName));
            const body = document.getElementById('modalBody');
            if (tabName === 'customer') body.innerHTML = renderCustomerTab();
            else if (tabName === 'payer') body.innerHTML = renderPayerTab();
            else if (tabName === 'mps') body.innerHTML = renderMpsTab();
        }

        // Alle konto-rækker i en blok åbner blokkens repræsentant: de øvrige medlemmer er
        // smeltet ind i den og findes ikke som selvstændige kunder i Settl.
        async function openBlock(block, initialTab, focusGsrn) {
            const acct = ix.blockAccount[block];
            const shard = await loadShard(ix.blockShard[block]);
            ctx = { shard, acct };

            const c = shard.c[acct];
            document.getElementById('modalHeader').innerHTML =
                `<span class="badge badge-customer">Kunde</span><h3>${esc(acct)}</h3>`;

            const payers = shard.p[acct] || [];
            const mpList = shard.am[acct] || [];
            const tab = initialTab || 'customer';
            document.getElementById('modalTabs').innerHTML =
                `<div class="modal-tab${tab === 'customer' ? ' active' : ''}" data-tab="customer" onclick="switchTab('customer')">Kunde</div>` +
                `<div class="modal-tab${tab === 'payer' ? ' active' : ''}" data-tab="payer" onclick="switchTab('payer')">Betaler<span class="tab-count">${payers.length}</span></div>` +
                `<div class="modal-tab${tab === 'mps' ? ' active' : ''}" data-tab="mps" onclick="switchTab('mps')">Målepunkter<span class="tab-count">${mpList.length}</span></div>`;

            switchTab(tab);
            openModal();

            if (focusGsrn) {
                const el = document.querySelector(`[data-gsrn="${focusGsrn}"]`);
                if (el) { el.classList.add('expanded'); el.scrollIntoView({ block: 'nearest' }); }
            }
        }

        function renderCustomerTab() {
            const c = ctx.shard.c[ctx.acct];
            if (!c) return '<em>Ingen kundedata</em>';
            let html = '<div class="detail-grid">';
            html += row('Kundenr.', c.customerNumber, 'CustomerNumber');
            const ukendt = c.customerType === 'Person' && !c.nationalIdentificationNumber;
            html += row('Type', ukendt ? 'Ukendt' : (c.customerType === 'Person' ? 'Privat' : c.customerType), 'CustomerType');
            html += row('CVR', c.vatIdentificationNumber, 'VatIdentificationNumber');
            html += row('CPR', c.nationalIdentificationNumber, 'NationalIdentificationNumber');
            // For en ukendt kunde er de to felter ovenfor tomme, og det er hele spørgsmålet:
            // stod der ingenting i Xellent, eller stod der noget mapperen kasserede? Her er
            // felterne som de faktisk står, plus hvilken af de grunde det er.
            if (c.unknown) {
                const u = c.unknown;
                html += row('Hvorfor ukendt', u.causeText, '');
                html += row('CVR i Xellent', u.rawCvr, 'EXU_CVR_DISPONENT1');
                html += row('CPR i Xellent', u.rawCpr, 'EXU_CPR_DISPONENT1');
                html += row(
                    'Alternativt nr.',
                    u.alternativeNumber ? u.alternativeNumber + ' (kode ' + u.alternativeNumberCode + ')' : '',
                    'EXU_ALTERNATIVECUSTOMERNUMBER');
            }
            {
                const a = c.addr || {};
                html += '</div><div class="detail-section-title">Adresse</div><div class="detail-grid">';
                html += row('Navn', a.name, 'Name1');
                html += row('Navn 2', a.supName, 'Name2');
                html += row('Vejnavn', a.street, 'StreetName');
                html += row('Husnr.', a.bldNr, 'BuildingNumber');
                html += row('Postnr.', a.zip, 'PostalCode');
                html += row('By', a.city, 'City');
                html += row('Land', a.country, 'CountryCode');
                html += row('DAR ID', a.dar, 'AddressIdentifier');
                html += row('Telefon', a.phone, 'Phone');
                html += row('Mobil', a.cellphone, 'Cellphone');
                html += row('Fax', a.fax, 'Fax');
                html += row('Email', a.email, 'Email');
                html += row('Start', a.start, 'Start');
            }
            html += '</div><div class="detail-section-title">Ejer</div><div class="detail-grid">';
            html += row('Ejer', c.ownerLabel, 'Owner');
            html += '</div>';
            return html;
        }

        function renderPayerTab() {
            const payers = ctx.shard.p[ctx.acct] || [];
            if (payers.length === 0) return '<div style="color:var(--text-tertiary);padding:1rem 0">Ingen betalere fundet for denne kunde.</div>';
            let html = '';
            payers.forEach((p, i) => {
                if (i > 0) html += '<div style="margin-top:1rem;padding-top:1rem;border-top:1px solid var(--border)"></div>';
                html += '<div class="detail-grid">';
                html += row('Betaler-ID', p.payerId, 'PayerIdentification');
                html += '</div><div class="detail-section-title">Adresse</div><div class="detail-grid">';
                html += row('Navn', p.name, 'Name1');
                html += row('Vejnavn', p.street, 'StreetName');
                html += row('Husnr.', p.bldNr, 'BuildingNumber');
                html += row('Postnr.', p.zip, 'PostalCode');
                html += row('By', p.city, 'City');
                html += row('DAR ID', p.dar, 'AddressIdentifier');
                html += '</div><div class="detail-section-title">Fakturering</div><div class="detail-grid">';
                html += row('Email', p.email, 'Email');
                html += row('EAN', p.ean, 'ElectronicNumber');
                html += row('Kanal', p.invCh, 'InvoiceChannel');
                html += row('Betaling', p.payCh, 'PaymentChannel');
                html += row('Type', p.payerType, 'PayerType');
                html += row('CVR', p.cvr, 'VatIdentificationNumber');
                html += row('Køberref.', p.buyerRef, 'BuyerReferenceIdentifier');
                html += row('Aftale-ID', p.agrId, 'AgreementIdentifier');
                html += row('Start', p.start, 'Start');
                html += '</div>';
            });
            return html;
        }

        function renderMpsTab() {
            const mpList = ctx.shard.am[ctx.acct] || [];
            if (mpList.length === 0) return '<div style="color:var(--text-tertiary);padding:1rem 0">Ingen målepunkter fundet for denne kunde.</div>';
            const joined = new Set(ctx.shard.j || []);
            let html = '';
            mpList.forEach(gsrn => {
                const m = ctx.shard.m[gsrn];
                const addrStr = m ? fmtSlimAddress(m.addr) : null;
                const sub = [addrStr, m && m.consName].filter(Boolean).join(' — ');
                html += `<div class="mp-item" data-gsrn="${esc(gsrn)}" onclick="this.classList.toggle('expanded')">`;
                const jBadge = joined.has(gsrn) ? '<span class="badge badge-merged">Samlefaktura</span>' : '';
                html += `<div class="mp-item-header"><span class="badge badge-mp">MP</span>${esc(gsrn)}${jBadge}</div>`;
                if (sub) html += `<div class="mp-item-sub">${esc(sub)}</div>`;
                html += `<div class="mp-detail">${m ? renderMpDetail(m, joined) : '<em>Ingen data</em>'}</div>`;
                html += '</div>';
            });
            return html;
        }

        function renderMpDetail(m, joined) {
            let html = '<div class="detail-grid">';
            html += row('GSRN', m.gsrn, 'MeteringPointIdentification');
            html += row('Installationsnr.', m.instNr, 'InstallationNumber');
            html += row('Kontrakt', ctx.shard.cn[m.gsrn] || '(ingen kontrakt)', 'ContractName');
            html += row('Samlefaktura', joined.has(m.gsrn) ? 'Ja' : 'Nej', 'JointInvoice');
            {
                const a = m.addr || {};
                html += '</div><div class="detail-section-title">Installationsadresse</div><div class="detail-grid">';
                html += row('Vejnavn', a.streetName, 'StreetName');
                html += row('Vejkode', a.streetCode, 'StreetCode');
                html += row('Husnr.', a.buildingNumber, 'BuildingNumber');
                html += row('Etage', a.buildingFloor, 'BuildingFloor');
                html += row('Dør', a.roomIdentification, 'RoomIdentification');
                html += row('Postnr.', a.postalCode, 'PostalCode');
                html += row('By', a.city, 'City');
                html += row('Bydel', a.citySubDivisionName, 'CitySubDivisionName');
                html += row('Kommunekode', a.municipalityCode, 'MunicipalityCode');
                html += row('Land', a.countryCode, 'CountryCode');
                html += row('Start', m.addrStart, 'Start');
            }
            {
                html += '</div><div class="detail-section-title">Forbruger</div><div class="detail-grid">';
                html += row('Navn', m.consName, 'FullName');
                html += row('Type', m.consType, 'CustomerType');
                html += row('CVR', m.consCvr, 'VatIdentificationNumber');
                html += row('CPR', m.consCpr, 'NationalIdentificationNumber');
            }
            {
                const meter = m.meter || {};
                html += '</div><div class="detail-section-title">Måler</div><div class="detail-grid">';
                html += row('Måler-ID', meter.meterIdentifier, 'MeterIdentification');
                html += row('Cifre', meter.digitsOfPrecision, 'DigitsOfPrecision');
                html += row('Decimaler', meter.decimals, 'Decimals');
                html += row('Faktor', meter.multiplicationFactor, 'MultiplicationFactor');
                html += row('Aflæsning', meter.meterReadingOccurrence, 'MeterReadingOccurrence');
                html += row('Gyldig fra', m.meterStart, 'MeterCharacteristics.Start');
                html += row('Gyldig til', m.meterEnd, 'MeterCharacteristics.End');
            }
            {
                html += '</div><div class="detail-section-title">Net</div><div class="detail-grid">';
                html += row('Balanceområde', m.gridBa, 'MarketBalanceAreaIdentification');
                html += row('Netområde', m.gridArea, 'GridAreaIdentification');
            }
            {
                html += '</div><div class="detail-section-title">Status</div><div class="detail-grid">';
                html += row('Afregning', m.settl, 'SettlementMethod');
                html += row('Tilslutning', m.connStatus, 'ConnectionStatus');
                html += row('I levering', (m.inDelivery !== null && m.inDelivery !== undefined) ? (m.inDelivery ? 'Ja' : 'Nej') : null, 'InDelivery');
                html += row('Samlefakturanr.', m.jointInvNo, 'JointInvoiceNumber');
                html += row('AdditionalInfo1', m.additionalInfo1, 'AdditionalInfo1');
            }
            if (m.orderId || m.agreementId || m.buyerRef) {
                html += '</div><div class="detail-section-title">E-faktura-ref (MP-override)</div><div class="detail-grid">';
                html += row('Køberref.', m.buyerRef, 'BuyerReferenceIdentifier');
                html += row('Aftale-ID', m.agreementId, 'AgreementIdentifier');
                html += row('Ordre-ID', m.orderId, 'OrderIdentifier');
            }
            if (m.contactPoints && m.contactPoints.length > 0) {
                m.contactPoints.forEach(cp => {
                    const catLabel = cp.category === 'TechnicalAddress' ? 'Teknisk adresse'
                        : cp.category === 'LegalAddress' ? 'Juridisk adresse'
                        : (cp.category || 'Kontaktpunkt');
                    html += `</div><div class="detail-section-title">${esc(catLabel)}</div><div class="detail-grid">`;
                    html += row('Navn', cp.name1, 'Name1');
                    html += row('Navn 2', cp.name2, 'Name2');
                    html += row('Vejnavn', cp.street, 'StreetName');
                    html += row('Husnr.', cp.buildingNumber, 'BuildingNumber');
                    html += row('Etage', cp.floor, 'BuildingFloor');
                    html += row('Dør', cp.room, 'RoomIdentification');
                    html += row('Postnr.', cp.postalCode, 'PostalCode');
                    html += row('By', cp.city, 'City');
                    html += row('Land', cp.countryCode, 'CountryCode');
                });
            }
            html += '</div>';
            return html;
        }

        // ---- Opstart -------------------------------------------------------------

        function onTreeClick(event) {
            const el = event.target.closest('.vrow');
            if (!el) return;
            const rowIdx = Number(el.dataset.row);
            const r = ix.rows[rowIdx];
            if (r.k === KIND.OWNER) {
                const gi = groupOfHeader.get(rowIdx);
                if (collapsed.has(gi)) collapsed.delete(gi); else collapsed.add(gi);
                refresh(false);
            } else if (r.k === KIND.MP) {
                openBlock(r.b, 'mps', r.g);
            } else if (r.b >= 0) {
                openBlock(r.b, 'customer');
            }
        }

        (async function init() {
            const indexEl = document.getElementById('reportIndex');
            if (!indexEl) return;

            const status = document.getElementById('treeStatus');
            try {
                ix = await inflate(indexEl.textContent.trim());
                shardB64 = JSON.parse(document.getElementById('reportShards').textContent);
            } catch (err) {
                if (status) {
                    status.textContent = 'Kunne ikke indlæse rapportdata: ' + err.message
                        + ' (kræver en browser med DecompressionStream — Chrome/Edge 80+)';
                }
                return;
            }

            for (let gi = 0; gi < ix.groups.length; gi++) groupOfHeader.set(ix.groups[gi][0], gi);

            const vp = document.getElementById('treeViewport');
            vp.addEventListener('click', onTreeClick);
            vp.addEventListener('scroll', () => {
                if (scrollPending) return;
                scrollPending = true;
                requestAnimationFrame(() => { scrollPending = false; render(); });
            });

            const search = document.getElementById('treeSearch');
            search.disabled = false;
            search.addEventListener('input', e => onSearchInput(e.target.value));

            refresh(false);
        })();
        """;
}

internal static class MessageGrouper
{
    private static readonly (string Prefix, string Label)[] EntityPrefixes =
    [
        ("Customer ", "Kunder"),
        ("Payer ", "Betalere"),
        ("MP ", "Målepunkter")
    ];

    private static readonly (string Contains, string Label)[] EntityContains =
    [
        (" customers ", "Kunder"),
        (" payers ", "Betalere"),
        (" MPs ", "Målepunkter")
    ];

    public static List<(string Group, List<string> Messages)> GroupByEntity(IReadOnlyList<string> messages)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var msg in messages)
        {
            var group = "Andet";
            foreach (var (prefix, label) in EntityPrefixes)
            {
                if (msg.StartsWith(prefix))
                {
                    group = label;
                    break;
                }
            }
            if (group == "Andet")
            {
                foreach (var (contains, label) in EntityContains)
                {
                    if (msg.Contains(contains, StringComparison.Ordinal))
                    {
                        group = label;
                        break;
                    }
                }
            }
            if (!groups.TryGetValue(group, out var list))
            {
                list = new List<string>();
                groups[group] = list;
            }
            list.Add(msg);
        }

        var result = new List<(string, List<string>)>();
        foreach (var (_, label) in EntityPrefixes)
        {
            if (groups.TryGetValue(label, out var list))
                result.Add((label, list));
        }
        if (groups.TryGetValue("Andet", out var other))
            result.Add(("Andet", other));
        return result;
    }

    private static readonly string[] EntityIdPrefixes = ["Customer ", "Payer ", "MP "];

    public static List<(string Pattern, List<string> Ids)> GroupByPattern(List<string> messages)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var msg in messages)
        {
            var colonIdx = msg.IndexOf(": ", StringComparison.Ordinal);
            var id = colonIdx >= 0 ? msg[..colonIdx] : null;
            var isEntityId = id != null && EntityIdPrefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal));

            if (isEntityId)
            {
                var pattern = msg[(colonIdx + 2)..];
                if (!groups.TryGetValue(pattern, out var list))
                {
                    list = [];
                    groups[pattern] = list;
                }
                list.Add(id!);
            }
            else
            {
                if (!groups.TryGetValue(msg, out var list))
                {
                    list = [];
                    groups[msg] = list;
                }
                list.Add("");
            }
        }
        return groups.Select(kv => (kv.Key, kv.Value)).OrderByDescending(x => x.Value.Count).ToList();
    }
}
