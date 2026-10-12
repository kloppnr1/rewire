using DataMigration.Domain.Common;

namespace DataMigration.Domain.Tests.Common;

public class DarAddressTests
{
    [Fact]
    public void EmptyGuid_Throws()
    {
        Assert.Throws<ArgumentException>(() => new DarAddress(Guid.Empty));
    }
}
