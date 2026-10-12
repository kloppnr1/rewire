using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent.Validation;

namespace DataMigration.Domain.Xellent.Tests.Validation;

public class CustomerValidationRunnerTests
{
    [Fact]
    public void Run_invokes_every_injected_validator()
    {
        var validators = new ICustomerValidator[]
        {
            new PersonRequiresCprValidator(),
            new RequiredNameValidator(),
            new AddressCompletenessValidator()
        };
        var runner = new CustomerValidationRunner(validators);

        var bad = TestCustomer.Build(name: "", type: CustomerType.Person, cvr: null, cpr: null,
            streetName: "", postalCode: "", city: "");

        var entries = runner.Run(bad).ToList();

        // Person+null Cpr (1) + missing Name (1) + missing Street/Postal/City (3) = 5
        Assert.Equal(5, entries.Count);
    }

    [Fact]
    public void RunAndCollect_routes_Error_severity_to_AddError_and_pushes_ValidationEntry()
    {
        var runner = new CustomerValidationRunner(new ICustomerValidator[] { new RequiredNameValidator() });
        var collector = new WarningCollector();
        var customer = TestCustomer.Build(name: "");

        runner.RunAndCollect(customer, collector);

        Assert.Equal(1, collector.ErrorCount);
        Assert.Single(collector.GetValidationEntries());
        Assert.Empty(collector.GetWarnings());
    }

    [Fact]
    public void RunAndCollect_emits_nothing_for_clean_customer()
    {
        var runner = new CustomerValidationRunner(new ICustomerValidator[]
        {
            new PersonRequiresCprValidator(),
            new RequiredNameValidator(),
            new AddressCompletenessValidator()
        });
        var collector = new WarningCollector();

        runner.RunAndCollect(TestCustomer.Build(), collector);

        Assert.Equal(0, collector.ErrorCount);
        Assert.Empty(collector.GetValidationEntries());
    }
}
