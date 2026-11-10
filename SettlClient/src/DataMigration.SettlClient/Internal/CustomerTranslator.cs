using DataMigration.Domain.Customers;
using DataMigration.SettlApi.Models;

namespace DataMigration.SettlClient.Internal;

/// <summary>
/// Builds Settl <see cref="CreateCustomerDto"/> from a Domain <see cref="Customer"/>.
/// Pure function — no I/O, no Xellent access. The Domain mapper has already resolved
/// addresses (incl. legal-fallback), normalised CVR/CPR, and applied anonymisation/
/// override-email per <c>MapperOptions</c>; this translator only maps shape.
/// </summary>
public static class CustomerTranslator
{
    private const string CountryCode = "DK";

    /// <summary>
    /// The customer's effective start date — earliest contract start, or today if the
    /// customer has no contracts. Single source of truth for both <c>Address.Start</c>
    /// and the owner-link start, so the two never diverge.
    /// </summary>
    public static DateOnly EarliestStart(Customer customer) =>
        customer.Contracts.Count > 0
            ? customer.Contracts.Min(c => c.Start)
            : DateOnly.FromDateTime(DateTime.Today);

    public static CreateCustomerDto Build(Customer customer)
    {
        var address = customer.Address;
        var contact = customer.Contact;

        var earliestStart = EarliestStart(customer);
        var (vat, nin) = IdentityFields.For(customer);

        return new CreateCustomerDto
        {
            Id = Guid.NewGuid(),
            CountryCode = CountryCode,
            CustomerNumber = customer.AccountNumber,
            CustomerType = customer.Type == CustomerType.Company ? "Company" : "Person",
            VatIdentificationNumber = vat,
            NationalIdentificationNumber = nin,
            Address = new CustomerAddressDto
            {
                Start = earliestStart,
                PostalAddress = new PostalAddressDto
                {
                    Name1 = new ConsumerNameDto { FullName = customer.Name?.Trim() },
                    Name2 = !string.IsNullOrEmpty(customer.AdditionalName)
                        ? new ConsumerNameDto { FullName = customer.AdditionalName.Trim() }
                        : null,
                    PhysicalAddress = new PhysicalAddressDto
                    {
                        StreetName = address.StreetName,
                        BuildingNumber = address.BuildingNumber,
                        PostalCode = address.PostalCode,
                        City = address.City,
                        CountryCode = !string.IsNullOrEmpty(address.CountryCode) ? address.CountryCode : CountryCode,
                        // Settl emits empty-string (not null) for floor/apartment when not set;
                        // matches existing SettlExtractionService output.
                        BuildingFloor = address.Floor ?? string.Empty,
                        RoomIdentification = address.Apartment ?? string.Empty,
                        AddressIdentifier = address.Dar != null
                            ? new AddressIdentifierDto { Identification = address.Dar.Identifier.ToString() }
                            : null
                    }
                },
                // Settl emits empty-string (not null) for unset phone fields; matches existing
                // SettlExtractionService output.
                Phone = contact.Phone ?? string.Empty,
                Cellphone = contact.Cellphone ?? string.Empty,
                Fax = contact.Fax ?? string.Empty,
                Email = contact.Email
            },
            CrmData = null
        };
    }
}
