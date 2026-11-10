using System.Data;
using System.Data.Common;
using DataMigration.OpsCli.Reporting.PaymentMatchMp;
using DataMigration.XellentData;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace DataMigration.OpsCli.Tests.Reporting.PaymentMatchMp;

public class PaymentMatchMpReaderTests
{
    [Fact]
    public async Task Real_sql_projection_is_batched_dated_and_recovers_commodity_from_parts()
    {
        var commands = new SyntheticCommands();
        var options = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer("Server=invalid;Database=synthetic;Integrated Security=true", sql => sql.UseCompatibilityLevel(110))
            .AddInterceptors(new NoConnection(), commands).Options;
        await using var db = new XellentDbContext(options);
        var result = await PaymentMatchMpReader.ReadSnapshotAsync(db,
            Enumerable.Range(0, 1501).Select(i => "A" + i).ToHashSet(), new(2026, 8, 31), CancellationToken.None);
        commands.CustomerQueries.Should().Be(2);
        commands.ContractQueries.Should().Be(2);
        commands.PartQueries.Should().Be(2);
        commands.CountQueries.Should().Be(2);
        result.PbsOwnAgreementCounts["A0"].Should().Be(2);
        result.Customers.Should().OnlyContain(c => c.Name == "Synthetic source" && c.IsBusiness);
        result.Contracts.Should().OnlyContain(c => c.Commodity == "Vand/Varme" && c.IsWaterOrHeat
            && c.PbsCode == 3 && !c.HasPbs && c.HasMobile);
    }

    private sealed class NoConnection : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class SyntheticCommands : DbCommandInterceptor
    {
        public int CustomerQueries, ContractQueries, PartQueries, CountQueries;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            command.Parameters.Count.Should().BeLessThan(2100);
            var sql = command.CommandText;
            sql.Should().NotContain("SELECT *");
            var table = new DataTable();
            void Columns(params (string Name, Type Type)[] fields) { foreach (var field in fields) table.Columns.Add(field.Name, field.Type); }
            if (sql.Contains("[EGS_METERPOINTTYPECODEPERIOD]", StringComparison.OrdinalIgnoreCase))
            {
                CountQueries++;
                Columns(("Custaccount", typeof(string)), ("Pbsagreementnum", typeof(int)), ("Pbstransactioncode", typeof(int)), ("Pbstransdate", typeof(DateTime)));
                table.Rows.Add("A0", 123, 1, new DateTime(1900, 1, 1));
                table.Rows.Add("A0", 123, 3, new DateTime(1900, 1, 1));
                table.Rows.Add("A0", 456, 6, new DateTime(1900, 1, 1));
                table.Rows.Add("A0", 789, 4, new DateTime(1900, 1, 1));
                sql.Should().Contain("[CONTRACTENDDATE] >=").And.Contain("E17/Forb").And.Contain("FORB")
                    .And.Contain("[DATAAREAID]").And.Contain("[CUSTACCOUNT]");
            }
            else if (sql.Contains("[CUSTTABLE]", StringComparison.OrdinalIgnoreCase))
            {
                CustomerQueries++;
                Columns(("Accountnum", typeof(string)), ("Name", typeof(string)), ("Cvr", typeof(string)));
                table.Rows.Add("A0", "Synthetic source", "12345678");
                sql.Should().Contain("[ACCOUNTNUM]").And.Contain("[DATAAREAID]");
            }
            else if (sql.Contains("[EXU_CONTRACTPARTTABLE]", StringComparison.OrdinalIgnoreCase))
            {
                PartQueries++;
                Columns(("Contractnum", typeof(string)), ("Deliverycategory", typeof(string)));
                table.Rows.Add("C1", "Vand"); table.Rows.Add("C1", "Varme");
                sql.Should().Contain("[STARTDATE] <=").And.Contain("[ENDDATE] >=");
                command.Parameters.Cast<DbParameter>().Should().Contain(p => Equals(p.Value, new DateTime(2026, 8, 31)));
            }
            else
            {
                ContractQueries++;
                Columns(("Custaccount", typeof(string)), ("Contractnum", typeof(string)), ("Deliverycategory", typeof(string)),
                    ("Referencetomaincontract", typeof(string)), ("Contractstartdate", typeof(DateTime)), ("Contractenddate", typeof(DateTime)),
                    ("Pbsagreementnum", typeof(int)), ("Pbstransactioncode", typeof(int)), ("Pbstransdate", typeof(DateTime)),
                    ("Mobilepaysubscriptionagre20135", typeof(string)), ("Mobilepayagreementcanceldate", typeof(DateTime)));
                table.Rows.Add("A0", "C1", "", DBNull.Value, new DateTime(2020, 1, 1), new DateTime(1900, 1, 1),
                    123, 3, new DateTime(1900, 1, 1), "synthetic-mobile", new DateTime(1900, 1, 1));
                sql.Should().Contain("[CUSTACCOUNT]").And.Contain("[DATAAREAID]");
            }
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader()));
        }
    }
}
