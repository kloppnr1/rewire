using DataMigration.Domain.Customers;
using DataMigration.SettlApi.Models;

namespace DataMigration.SettlClient.Internal;

/// <summary>
/// Translates a single Domain <see cref="Payer"/> (under a <see cref="Customer"/>) to a
/// Settl <see cref="CreatePayerDto"/>. Pure 1:1 mapping — no fan-out, no EAN-grouping.
/// Multi-payer construction is owned by Domain (PayerMapper in XellentDomain.Domain.Xellent).
/// </summary>
public static class PayerTranslator
{
    private const string CountryCode = "DK";

    public static CreatePayerDto Build(Customer customer, Payer payer, Guid customerId)
    {
        var address = customer.Address;
        var contact = customer.Contact;

        var earliestStart = customer.Contracts.Count > 0
            ? customer.Contracts.Min(c => c.Start)
            : DateOnly.FromDateTime(DateTime.Today);
        var (vat, nin) = IdentityFields.For(customer);

        return new CreatePayerDto
        {
            Id = Guid.NewGuid(),
            CountryCode = CountryCode,
            CustomerId = customerId,
            IsPrimaryCustomerPayer = payer.IsPrimary,
            PayerInformation = new PayerInformationDto
            {
                Start = earliestStart,
                PayerIdentifier = payer.Identifier,
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
                        BuildingFloor = address.Floor ?? string.Empty,
                        RoomIdentification = address.Apartment ?? string.Empty,
                        AddressIdentifier = address.Dar != null
                            ? new AddressIdentifierDto { Identification = address.Dar.Identifier.ToString() }
                            : null
                    }
                }
            },
            PayerBilling = new CreatePayerBillingDto
            {
                Start = earliestStart,
                Email = contact.Email ?? string.Empty,
                InvoiceChannel = MapInvoiceChannel(payer.InvoiceChannel, hasEmail: !string.IsNullOrEmpty(contact.Email)),
                PaymentChannel = MapPaymentChannel(payer.PaymentChannel),
                ElectronicNumber = payer.BuyerInvoicing?.ElectronicNumber,
                PayerType = customer.Type == CustomerType.Company ? "Company" : "Person",
                VatIdentificationNumber = vat,
                NationalIdentificationNumber = nin,
                BuyerReferenceIdentifier = payer.BuyerInvoicing?.BuyerReferenceIdentifier,
                AgreementIdentifier = payer.BuyerInvoicing?.AgreementIdentifier,
                OrderIdentifier = payer.BuyerInvoicing?.OrderIdentifier
            }
        };
    }

    private static string MapInvoiceChannel(InvoiceChannel channel, bool hasEmail) => channel switch
    {
        InvoiceChannel.Pdf => "PDF",
        InvoiceChannel.Email => "Email",
        InvoiceChannel.OioUbl => "OioUbl",
        InvoiceChannel.PbsBs => "PBS_BS",
        // Settl's InvoiceChannel enum has no MobilePay value — downgrade. Settl requires an
        // invoice email when the channel is PDF (ValidationRules.InvoiceChannel.EmptyInvoiceCcEmail),
        // so the downgrade follows the same rule the Domain's PayerMapper.ResolveChannels applies:
        // PDF when the payer has an email, PBS_BS otherwise. (PaymentChannel.MobilePay below IS
        // accepted by Settl.) MigrateAsync emits a warning naming the chosen channel.
        InvoiceChannel.MobilePay => hasEmail ? "PDF" : "PBS_BS",
        _ => "PDF"
    };

    private static string MapPaymentChannel(PaymentChannel channel) => channel switch
    {
        PaymentChannel.Fik => "FIK",
        PaymentChannel.Bs => "BS",
        PaymentChannel.EInvoiceFik => "OIOUBL_FIK",
        PaymentChannel.MobilePay => "MobilePay",
        _ => "FIK"
    };
}
