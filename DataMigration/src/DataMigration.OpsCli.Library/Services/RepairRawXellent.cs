using System.Reflection;
using DataMigration.Migration.Configuration;
using DataMigration.XellentData;
using DataMigration.XellentData.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Query;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace DataMigration.OpsCli.Services;

internal sealed record RepairRawSettings(int Version, string DataArea, DateOnly ReferenceDate,
    string[] IgnoredGroups, DateTime? RestoreFrom, DateTime? RestoreBefore, MappingOptions? Mappings);

/// <summary>Copies source rows without running the domain mapper or validating import values.</summary>
internal static class RepairRawXellent
{
    internal static readonly Type[] Tables = [typeof(CustTable), typeof(ExuContractTable),
        typeof(ExuContractPartTable), typeof(ExuContractPartLog), typeof(ExuAgreementTable), typeof(ExuDelpoint),
        typeof(DirPartyAddressRelationship), typeof(DirPartyAddressRelationshi1066), typeof(Address),
        typeof(DirPartyECommunicationRela2608), typeof(DirECommunicationAddress), typeof(ContactPerson),
        typeof(ExuDelpointContactInfoTable), typeof(ExuInstallation), typeof(ExuStreet), typeof(AddressZipCode),
        typeof(ExuGroupTable), typeof(EgsMeterpointTypecodeperiod), typeof(ExuBillingMethodPeriod),
        typeof(ExuMeterTableTrade), typeof(ExuDelpoint2delpointrela25505), typeof(ExuMeterPointStatusPeriod)];
    internal static string Section(Type type) => "repair-raw-" + type.Name.ToLowerInvariant();

    internal static async Task CaptureAsync(StateSourceSnapshotStore store, string connection, string dataArea,
        DateOnly reference, IReadOnlyList<string> ignoredGroups, DateTime? restoreFrom, DateTime? restoreBefore,
        CancellationToken ct, MappingOptions? mappings = null)
    {
        var options = new DbContextOptionsBuilder<XellentDbContext>().UseSqlServer(connection,
            sql => { sql.UseCompatibilityLevel(110); sql.CommandTimeout(300); }).Options;
        await using var db = new XellentDbContext(options);
        await CaptureRowsAsync(store, db, new(1, dataArea, reference, ignoredGroups.ToArray(), restoreFrom, restoreBefore, mappings), ct);
    }

    internal static async Task CaptureRowsAsync(StateSourceSnapshotStore store, XellentDbContext db,
        RepairRawSettings settings, CancellationToken ct)
    {
        var area = settings.DataArea;
        var accounts = store.Scope?.SupportingAccounts
            ?? await db.CustTables.Where(c => c.Dataareaid == area).Select(c => c.Accountnum).ToArrayAsync(ct);
        async Task<List<T>> Save<T>(IQueryable<T> query) where T : class
        {
            var rows = await query.AsNoTracking().ToListAsync(ct);
            await store.WriteAsync(Section(typeof(T)), rows, ct);
            Console.Error.WriteLine($"  Saved {typeof(T).Name}: {rows.Count} source rows.");
            return rows;
        }
        async Task<List<T>> Batches<T, TKey>(IEnumerable<TKey> keys, Func<TKey[], IQueryable<T>> query) where T : class
        {
            var rows = new List<T>();
            foreach (var batch in keys.Distinct().Chunk(1000)) rows.AddRange(await query(batch).AsNoTracking().ToListAsync(ct));
            await store.WriteAsync(Section(typeof(T)), rows, ct);
            Console.Error.WriteLine($"  Saved {typeof(T).Name}: {rows.Count} source rows.");
            return rows;
        }
        var headers = new List<ExuContractTable>();
        foreach (var batch in accounts.Chunk(1000))
            headers.AddRange(await db.ExuContractTables.AsNoTracking().Where(c => c.Dataareaid == area && batch.Contains(c.Custaccount)).ToListAsync(ct));
        var mainRefs = headers.Select(c => c.Referencetomaincontract).Where(x => !string.IsNullOrEmpty(x))
            .Except(headers.Select(c => c.Contractnum)).ToArray();
        foreach (var batch in mainRefs.Chunk(1000))
            headers.AddRange(await db.ExuContractTables.AsNoTracking().Where(c => c.Dataareaid == area && batch.Contains(c.Contractnum)).ToListAsync(ct));
        await store.WriteAsync(Section(typeof(ExuContractTable)), headers, ct);
        var customers = await Batches(accounts.Concat(headers.Select(c => c.Custaccount)), b => db.CustTables.Where(c => c.Dataareaid == area && b.Contains(c.Accountnum)));
        var numbers = headers.Select(c => c.Contractnum).ToArray();
        var parts = await Batches(numbers, b => db.ExuContractPartTables.Where(p => p.Dataareaid == area && b.Contains(p.Contractnum)));
        var logs = await Batches(numbers, b => db.ExuContractPartLogs.Where(p => p.Dataareaid == area && b.Contains(p.Contractnum)));
        var installations = parts.Select(p => p.Instagreenum).Concat(logs.Select(p => p.Instagreenum)).Distinct().ToArray();
        await Batches(installations, b => db.ExuAgreementTables.Where(p => p.Dataareaid == area && b.Contains(p.Agreementnum)));
        var points = await Batches(installations, b => db.ExuDelpoints.Where(p => p.Dataareaid == area && b.Contains(p.Attachmentnum)));
        var pointIds = points.Select(p => p.Meteringpoint).ToArray();
        var contacts = await Batches(pointIds, b => db.ExuDelpointContactInfoTables.Where(p => p.Dataareaid == area && b.Contains(p.Meteringpoint)));
        await Batches(installations, b => db.ExuInstallations.Where(p => p.Dataareaid == area && b.Contains(p.Instnum)));
        var parties = customers.Select(c => c.Partyid).ToArray();
        var relations = await Batches(parties, b => db.DirPartyAddressRelationships.Where(p => p.Dataareaid == area && b.Contains(p.Partyid)));
        var addressLinks = await Batches(relations.Select(p => p.Recid), b => db.DirPartyAddressRelationshi1066s.Where(p => p.Dataareaid == area && b.Contains(p.Partyaddressrelationshiprecid)));
        var addresses = new List<Address>();
        foreach (var batch in addressLinks.Select(p => p.Addressrecid).Distinct().Chunk(1000))
            addresses.AddRange(await db.Addresses.AsNoTracking().Where(p => p.Dataareaid == area && batch.Contains(p.Recid)).ToListAsync(ct));
        foreach (var batch in contacts.Select(p => p.Addressid).Where(id => !string.IsNullOrEmpty(id)).Distinct().Chunk(1000))
            addresses.AddRange(await db.Addresses.AsNoTracking().Where(p => p.Dataareaid == area && batch.Contains(p.ExuAddressid)).ToListAsync(ct));
        await store.WriteAsync(Section(typeof(Address)), addresses.DistinctBy(p => (p.Dataareaid, p.Recid)).ToArray(), ct);
        var communications = await Batches(parties, b => db.DirPartyECommunicationRela2608s.Where(p => p.Dataareaid == area && b.Contains(p.Partyid)));
        await Batches(communications.Select(p => p.Valuesrecid), b => db.DirECommunicationAddresses.Where(p => p.Dataareaid == area && b.Contains(p.Recid)));
        await Batches(headers.Select(c => c.Custcontactpersonid), b => db.ContactPersons.Where(p => p.Dataareaid == area && b.Contains(p.Contactpersonid)));
        await Batches(points.Select(p => p.Streetnum), b => db.ExuStreets.Where(p => p.Dataareaid == area && b.Contains(p.Streetnum)));
        await Save(db.AddressZipCodes.Where(p => p.Dataareaid == area));
        await Save(db.ExuGroupTables.Where(p => p.Dataareaid == area));
        await Batches(installations, b => db.EgsMeterpointTypecodeperiods.Where(p => p.Dataareaid == area && b.Contains(p.Attachmentnum)));
        await Batches(installations, b => db.ExuBillingMethodPeriods.Where(p => p.Dataareaid == area && b.Contains(p.Attachmentnum)));
        await Batches(installations, b => db.ExuMeterTableTrades.Where(p => p.Dataareaid == area && b.Contains(p.Agreementnum)));
        await Batches(installations, b => db.ExuDelpoint2delpointrela25505s.Where(p => p.Dataareaid == area && b.Contains(p.Primaryattachmentnum)));
        await Batches(installations, b => db.ExuMeterPointStatusPeriods.Where(p => p.Dataareaid == area && b.Contains(p.Attachmentnum)));
        // The settings describe how to interpret the copy later; no mapping results or findings are saved.
        await store.WriteAsync("repair-raw-settings", new[] { settings }, ct);
    }

    internal static async Task<XellentDbContext> OpenOfflineAsync(StateSourceSnapshotStore store, CancellationToken ct)
    {
        var settings = (await store.ReadSectionAsync<RepairRawSettings>("repair-raw-settings", ct)).Single();
        if (settings.Version != 1) throw new InvalidOperationException("Unsupported raw repair snapshot version.");
        var db = new RepairOfflineDbContext(new DbContextOptionsBuilder<XellentDbContext>()
            .UseInMemoryDatabase("repair-" + Guid.NewGuid(), o => o.EnableNullChecks(false))
            .ReplaceService<IQueryTranslationPreprocessorFactory, RepairTextPreprocessorFactory>().Options);
        try
        {
            foreach (var type in Tables)
                await (Task)typeof(RepairRawXellent).GetMethod(nameof(LoadTableAsync), BindingFlags.Static | BindingFlags.NonPublic)!
                    .MakeGenericMethod(type).Invoke(null, [store, db, ct])!;
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }

    private sealed class RepairOfflineDbContext(DbContextOptions<XellentDbContext> options) : XellentDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            // SQL reads are untracked and can return duplicate logical keys. Preserve every row
            // in the offline copy rather than collapse it or reject valid source duplication.
            foreach (var type in Tables)
            {
                builder.Entity(type).Property<long>("SnapshotRow").ValueGeneratedOnAdd();
                builder.Entity(type).HasKey("SnapshotRow");
                var comparer = new ValueComparer<string>((left, right) => SqlTextEqual(left, right),
                    value => SqlTextHash(value), value => value);
                foreach (var property in builder.Entity(type).Metadata.GetProperties().Where(p => p.ClrType == typeof(string)))
                    property.SetValueComparer(comparer);
            }
        }
    }
    private static bool SqlTextEqual(string? left, string? right) => string.Equals(left?.TrimEnd(' '), right?.TrimEnd(' '), StringComparison.OrdinalIgnoreCase);
    private static int SqlTextHash(string? value) => value is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(value.TrimEnd(' '));
    private static readonly ConditionalWeakTable<object, HashSet<string>> TextSets = new();
    private static bool SqlTextContains(IEnumerable<string> values, string? value) => TextSets.GetValue(values,
        key => ((IEnumerable<string>)key).Select(item => item?.TrimEnd(' ')!).ToHashSet(StringComparer.OrdinalIgnoreCase))
        .Contains(value?.TrimEnd(' ')!);
    private static string? SqlJoinKey(string? value) => value?.TrimEnd(' ').ToUpperInvariant();
    private sealed class RepairTextPreprocessorFactory(QueryTranslationPreprocessorDependencies dependencies) : IQueryTranslationPreprocessorFactory
    {
        public QueryTranslationPreprocessor Create(QueryCompilationContext context) => new RepairTextPreprocessor(dependencies, context);
    }
    private sealed class RepairTextPreprocessor(QueryTranslationPreprocessorDependencies dependencies, QueryCompilationContext context)
        : QueryTranslationPreprocessor(dependencies, context)
    {
        public override Expression Process(Expression query) => base.Process(new SqlTextVisitor().Visit(query));
    }
    private sealed class SqlTextVisitor : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(Queryable) && node.Method.Name is "Join" or "GroupJoin")
            {
                var arguments = node.Arguments.Select(Visit).ToArray();
                for (var i = 2; i <= 3; i++)
                {
                    var quote = (UnaryExpression)arguments[i]!;
                    var key = (LambdaExpression)quote.Operand;
                    var body = new SqlJoinKeyVisitor().Visit(key.Body)!;
                    arguments[i] = Expression.Quote(Expression.Lambda(body, key.Parameters));
                }
                return node.Update(node.Object, arguments!);
            }
            if (node.Method.Name == "Contains" && node.Method.DeclaringType == typeof(Enumerable)
                && node.Method.IsGenericMethod && node.Method.GetGenericArguments()[0] == typeof(string) && node.Arguments.Count == 2)
                return Expression.Call(typeof(RepairRawXellent).GetMethod(nameof(SqlTextContains), BindingFlags.NonPublic | BindingFlags.Static)!,
                    Visit(node.Arguments[0]), Visit(node.Arguments[1]));
            if (node.Method.Name == "Contains" && node.Object is not null && typeof(IEnumerable<string>).IsAssignableFrom(node.Object.Type)
                && node.Arguments.Count == 1 && node.Arguments[0].Type == typeof(string))
                return Expression.Call(typeof(RepairRawXellent).GetMethod(nameof(SqlTextContains), BindingFlags.NonPublic | BindingFlags.Static)!,
                    Visit(node.Object), Visit(node.Arguments[0]));
            return base.VisitMethodCall(node);
        }
    }
    private sealed class SqlJoinKeyVisitor : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node) => node?.Type == typeof(string)
            ? Expression.Call(typeof(RepairRawXellent).GetMethod(nameof(SqlJoinKey), BindingFlags.NonPublic | BindingFlags.Static)!, node)
            : base.Visit(node);
    }
    private static async Task LoadTableAsync<T>(StateSourceSnapshotStore store, XellentDbContext db, CancellationToken ct) where T : class
    {
        var rows = await store.ReadSectionAsync<T>(Section(typeof(T)), ct);
        db.Set<T>().AddRange(rows);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    /// <summary>Changes only the disposable offline working copy; source files stay unchanged.</summary>
    internal static async Task RestoreWorkingPartsAsync(StateSourceSnapshotStore store, XellentDbContext db, CancellationToken ct)
    {
        var settings = (await store.ReadSectionAsync<RepairRawSettings>("repair-raw-settings", ct)).Single();
        if (settings.RestoreFrom is null) return;
        var restored = AborabatRepair.RestoreCustomerPayerSources(
            await store.ReadSectionAsync<XellentContractPartRow>("xellent-contract-parts", ct),
            await store.ReadSectionAsync<XellentContractPartRow>("xellent-contract-part-logs", ct),
            await store.ReadSectionAsync<XellentChargeSubscriptionRow>("xellent-charge-subscriptions", ct), settings.ReferenceDate);
        var rows = await db.ExuContractPartTables.ToListAsync(ct);
        foreach (var part in restored.Electricity.Where(p => !restored.AmbiguousPoints.Contains(p.MeteringPoint)))
            Put(part.ContractNumber, part.AgreementNumber, "El-ekstern", part.CompanyId!, part.ProductNumber, part.AmrCode!, part.Start, part.End);
        foreach (var part in restored.Chargers.Where(p => !restored.AmbiguousChargerAccounts.Contains(p.Account)))
            Put(part.ContractNumber, part.AgreementNumber!, "Charge_Abo", part.CompanyId!, part.ProductNumber, part.AmrCode!, part.Start, part.End);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        void Put(string contract, string agreement, string category, string company, string product, string amr, DateTime start, DateTime end)
        {
            var matches = rows.Where(p => SqlTextEqual(p.Contractnum, contract) && SqlTextEqual(p.Instagreenum, agreement) && SqlTextEqual(p.Deliverycategory, category)
                && SqlTextEqual(p.Companyid, company) && SqlTextEqual(p.Productnum, product) && SqlTextEqual(p.Amrcode, amr) && p.Startdate == start).ToArray();
            if (matches.Length > 1) return; // Preparation retains the original ambiguous evidence.
            var row = matches.SingleOrDefault();
            if (row is null)
            {
                row = new ExuContractPartTable();
                foreach (var property in typeof(ExuContractPartTable).GetProperties().Where(p => p.PropertyType == typeof(string) && p.CanWrite))
                    property.SetValue(row, "");
                row.Dataareaid = settings.DataArea; row.Contractnum = contract; row.Instagreenum = agreement;
                row.Deliverycategory = category; row.Companyid = company; row.Productnum = product; row.Amrcode = amr; row.Startdate = start;
                db.ExuContractPartTables.Add(row); rows.Add(row);
            }
            row.Enddate = end;
        }
    }
}
