using DataMigration.Core;
using DataMigration.Domain.Xellent;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Reporting.PaymentMatchMp;

/// <summary>
/// Hvilke målepunkter der er i leverance i Xellent på en bestemt dag, og hvem der leverer til dem.
///
/// <para>Leverance er to ting på én gang, og begge skal holde på dagen: kontrakten skal være åben,
/// og den kontraktdel der hænger på målepunktets aftale skal være i kraft. Kun kontrakten er ikke
/// nok — en kontrakt løber videre, mens dens dele afløses, og en del der sluttede sidste år er ikke
/// en leverance i dag.</para>
///
/// <para>Vejen fra målepunkt til kontrakt er den samme som resten af stakken bruger:
/// leveringspunkt → aftale → kontraktdel → kontrakt. Den går gennem aftalen frem for at koble
/// kontraktdelen direkte på leveringspunktet, så nøglen er hel — dataområde og selskab følger med
/// hele vejen.</para>
///
/// <para><b>Og netop derfor skal målepunktstypen med.</b> En aftale kan have flere leveringspunkter
/// — en produktionsinstallation har både forbrugsmålepunktet og sine D-punkter — og de arver alle
/// kontoens kontrakt gennem aftalen. Uden filtret får man under-punkter tilskrevet en konto de ikke
/// hører til under, og de tal kan ingen bruge. Allow-listen er den samme som migreringens egen
/// udtrækning bruger.</para>
/// </summary>
public static class XellentDeliveryOnDate
{
    private const int SqlCompatibilityLevel = 110;
    private const int CommandTimeoutSeconds = 300;

    /// <summary>
    /// Den kontrakt der leverer til målepunktet på dagen. Kontraktens nummer og datoer står med, så
    /// et menneske kan slå rækken op i Xellent i stedet for at tage svaret på tro.
    /// </summary>
    public sealed record Delivery(
        string MeteringPoint,
        string Account,
        string ContractNumber,
        DateTime ContractStart,
        DateTime ContractEnd,
        string ContractTemplate);

    /// <summary>
    /// Alle målepunkter i leverance på <paramref name="on"/>, med den kontrakt der leverer.
    ///
    /// <para>Ét kald: hele leveringsbilledet er ét udtræk på et par sekunder — i hol omkring 48.000
    /// målepunkter — så der er hverken en liste at sende med eller noget at dele i portioner.</para>
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, Delivery>> ReadAllAsync(
        string connectionString,
        DateTime on,
        string dataAreaId,
        string deliveryCategory,
        CancellationToken ct = default)
    {
        var dbOptions = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(connectionString, sql =>
            {
                sql.UseCompatibilityLevel(SqlCompatibilityLevel);
                sql.CommandTimeout(CommandTimeoutSeconds);
            })
            .Options;

        await using var context = new XellentDbContext(dbOptions);

        ct.ThrowIfCancellationRequested();

        var rows = await Delivering(context, dataAreaId, deliveryCategory, on.Date, MigrationConstants.EndDateNull)
            .Distinct()
            .ToListAsync(ct);

        var result = new Dictionary<string, Delivery>(StringComparer.Ordinal);
        Fold(rows, result);
        return result;
    }

    /// <summary>Preserve dated candidates for an explicit scope; conflicting accounts must remain visible.</summary>
    public static async Task<List<Delivery>> ReadCandidatesAsync(
        string connectionString, IReadOnlyList<string> ids, DateTime on,
        string dataAreaId, string deliveryCategory, Action<int, int>? progress,
        CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(connectionString, sql =>
            {
                sql.UseCompatibilityLevel(SqlCompatibilityLevel);
                sql.CommandTimeout(CommandTimeoutSeconds);
            }).Options;
        await using var context = new XellentDbContext(options);
        var result = new List<Delivery>();
        const int batchSize = 1500;
        for (var offset = 0; offset < ids.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = ids.Skip(offset).Take(batchSize).ToArray();
            var rows = await Delivering(context, dataAreaId, deliveryCategory, on.Date,
                    MigrationConstants.EndDateNull, historicalType: true, meteringPointIds: batch)
                .Distinct().ToListAsync(ct);
            result.AddRange(rows.Select(r => new Delivery(r.MeteringPoint.Trim(), r.Account.Trim(),
                r.ContractNumber.Trim(), r.ContractStart, r.ContractEnd, r.ContractTemplate)));
            progress?.Invoke(Math.Min(offset + batchSize, ids.Count), ids.Count);
        }
        return result;
    }

    /// <summary>
    /// De rigtige målepunkter, ikke de tekniske under-punkter. E17 forbrug og E18 produktion — de
    /// samme koder migreringens egen udtrækning bruger, så de to er enige om hvad et målepunkt er.
    ///
    /// <para>Uden dem kommer en produktionsinstallations D-punkter med: overskudsproduktion, leveret
    /// til net, forbrugt fra net, afgift af nettoforbrug. De hænger på den samme aftale som
    /// forbrugsmålepunktet, så de arver kontoens kontrakt og ser ud til at være kundens målepunkter.
    /// Det er de ikke, og de bliver heller ikke migreret.</para>
    /// </summary>
    private static readonly string[] ParentMeterCodes =
        ExtractionMappings.ExpandDataHubCodesToXellent(["E17", "E18"])!;

    /// <summary>
    /// Leverance-reglen, ét sted. Kontrakten skal være åben på dagen, den kontraktdel der hænger på
    /// målepunktets aftale skal være i kraft, og leveringspunktet skal være et målepunkt frem for et
    /// under-punkt — alle tre, ellers er det ikke en leverance til kunden.
    /// </summary>
    private static IQueryable<Row> Delivering(
        XellentDbContext context, string dataAreaId, string deliveryCategory, DateTime day, DateTime endDateNull,
        bool historicalType = false, string[]? meteringPointIds = null) =>
        from d in context.ExuDelpoints
        join ag in context.ExuAgreementTables
            on new { Agreementnum = d.Attachmentnum, d.Dataareaid, d.Companyid }
            equals new { ag.Agreementnum, ag.Dataareaid, ag.Companyid }
        join cp in context.ExuContractPartTables
            on new { Instagreenum = ag.Agreementnum, ag.Dataareaid, ag.Companyid }
            equals new { cp.Instagreenum, cp.Dataareaid, cp.Companyid }
        join c in context.ExuContractTables
            on new { cp.Contractnum, cp.Dataareaid, cp.Companyid }
            equals new { c.Contractnum, c.Dataareaid, c.Companyid }
        join mt in context.EgsMeterpointTypecodeperiods
            on new { Delpointnum = d.Num, Attachmentnum = d.Attachmentnum, d.Dataareaid, d.Companyid, d.Deliverycategory }
            equals new { mt.Delpointnum, mt.Attachmentnum, mt.Dataareaid, mt.Companyid, mt.Deliverycategory }
        where d.Dataareaid == dataAreaId
            && (meteringPointIds == null || meteringPointIds.Contains(d.Meteringpoint))
            && d.Deliverycategory == deliveryCategory
            && cp.Deliverycategory == deliveryCategory
            && (historicalType
                ? mt.Startdate <= day && (mt.Enddate == endDateNull || mt.Enddate >= day)
                : mt.Enddate == endDateNull)
            && ParentMeterCodes.Contains(mt.Meterpointtypecode)
            && cp.Startdate <= day
            && (cp.Enddate == endDateNull || cp.Enddate >= day)
            && c.Contractstartdate <= day
            && (c.Contractenddate == endDateNull || c.Contractenddate >= day)
        select new Row(
            d.Meteringpoint, c.Custaccount, c.Contractnum, c.Contractstartdate, c.Contractenddate,
            c.Contracttemplatenum);

    /// <summary>
    /// Et målepunkt kan have flere åbne kontrakter på samme dag — flere produkter, eller en rest der
    /// aldrig blev lukket. Nyeste kontrakt vinder, og kontonummer og kontraktnummer bryder uafgjort,
    /// så to opslag på de samme data peger på den samme række.
    /// </summary>
    private static void Fold(IEnumerable<Row> rows, Dictionary<string, Delivery> into)
    {
        foreach (var group in rows.GroupBy(r => r.MeteringPoint, StringComparer.Ordinal))
        {
            var best = group
                .OrderByDescending(r => r.ContractStart)
                .ThenBy(r => r.Account, StringComparer.Ordinal)
                .ThenBy(r => r.ContractNumber, StringComparer.Ordinal)
                .First();

            into[group.Key] = new Delivery(
                group.Key, best.Account, best.ContractNumber, best.ContractStart, best.ContractEnd,
                best.ContractTemplate);
        }
    }

    /// <summary>Én række som databasen giver den, før flere kontrakter på samme målepunkt er skilt ad.</summary>
    private sealed record Row(
        string MeteringPoint,
        string Account,
        string ContractNumber,
        DateTime ContractStart,
        DateTime ContractEnd,
        string ContractTemplate);
}
