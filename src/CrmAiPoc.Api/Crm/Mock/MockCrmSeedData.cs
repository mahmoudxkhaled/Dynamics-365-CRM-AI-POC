using CrmAiPoc.Api.Crm.Models;
using CrmAiPoc.Api.Security;

namespace CrmAiPoc.Api.Crm.Mock;

public static class MockCrmSeedData
{
    public static readonly Guid AhmedTradingId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid AhmedTechId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid NileRetailId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid CairoLogisticsId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid DeltaServicesId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    public static IReadOnlyList<CrmAccount> CreateAccounts() =>
    [
        new CrmAccount
        {
            AccountId = AhmedTradingId,
            Name = "Ahmed Trading Co",
            AccountNumber = "ACC-1001",
            Telephone1 = "+20-2-555-0101",
            EmailAddress1 = "info@ahmedtrading.example",
            City = "Cairo",
            Country = "Egypt",
            Industry = "Wholesale",
            IsConfidential = false
        },
        new CrmAccount
        {
            AccountId = AhmedTechId,
            Name = "Ahmed Technologies",
            AccountNumber = "ACC-1002",
            Telephone1 = "+20-2-555-0102",
            EmailAddress1 = "hello@ahmedtech.example",
            City = "Giza",
            Country = "Egypt",
            Industry = "Technology",
            IsConfidential = false
        },
        new CrmAccount
        {
            AccountId = NileRetailId,
            Name = "Nile Retail Group",
            AccountNumber = "ACC-1003",
            Telephone1 = "+20-2-555-0103",
            EmailAddress1 = "sales@nileretail.example",
            City = "Alexandria",
            Country = "Egypt",
            Industry = "Retail",
            IsConfidential = false
        },
        new CrmAccount
        {
            AccountId = CairoLogisticsId,
            Name = "Cairo Logistics",
            AccountNumber = "ACC-1004",
            Telephone1 = "+20-2-555-0104",
            EmailAddress1 = "ops@cairologistics.example",
            City = "Cairo",
            Country = "Egypt",
            Industry = "Transportation",
            IsConfidential = false
        },
        new CrmAccount
        {
            AccountId = DeltaServicesId,
            Name = "Delta Business Services",
            AccountNumber = "ACC-1005",
            Telephone1 = "+20-2-555-0105",
            EmailAddress1 = "contact@deltaservices.example",
            City = "Mansoura",
            Country = "Egypt",
            Industry = "Professional Services",
            IsConfidential = false
        },
        new CrmAccount
        {
            AccountId = CrmAuthorizationService.DeniedConfidentialAccountId,
            Name = "Confidential Strategic Partner",
            AccountNumber = "ACC-9999",
            Telephone1 = "+20-2-555-9999",
            EmailAddress1 = "secure@confidential.example",
            City = "Cairo",
            Country = "Egypt",
            Industry = "Strategic",
            IsConfidential = true
        }
    ];

    public static IReadOnlyList<CrmContact> CreateContacts() =>
    [
        new CrmContact
        {
            ContactId = Guid.Parse("a1111111-1111-1111-1111-111111111111"),
            AccountId = AhmedTradingId,
            FullName = "Sara Ahmed",
            EmailAddress1 = "sara.ahmed@ahmedtrading.example",
            Telephone1 = "+20-100-555-1001",
            JobTitle = "Purchasing Manager"
        },
        new CrmContact
        {
            ContactId = Guid.Parse("a1111111-1111-1111-1111-111111111112"),
            AccountId = AhmedTradingId,
            FullName = "Omar Hassan",
            EmailAddress1 = "omar.hassan@ahmedtrading.example",
            Telephone1 = "+20-100-555-1002",
            JobTitle = "Finance Lead"
        },
        new CrmContact
        {
            ContactId = Guid.Parse("a2222222-2222-2222-2222-222222222221"),
            AccountId = AhmedTechId,
            FullName = "Nour El-Din",
            EmailAddress1 = "nour@ahmedtech.example",
            Telephone1 = "+20-100-555-2001",
            JobTitle = "CTO"
        },
        new CrmContact
        {
            ContactId = Guid.Parse("a2222222-2222-2222-2222-222222222222"),
            AccountId = AhmedTechId,
            FullName = "Mona Khalil",
            EmailAddress1 = "mona@ahmedtech.example",
            Telephone1 = "+20-100-555-2002",
            JobTitle = "Account Executive"
        },
        new CrmContact
        {
            ContactId = Guid.Parse("a3333333-3333-3333-3333-333333333331"),
            AccountId = NileRetailId,
            FullName = "Youssef Ali",
            EmailAddress1 = "youssef@nileretail.example",
            Telephone1 = "+20-100-555-3001",
            JobTitle = "Store Director"
        },
        new CrmContact
        {
            ContactId = Guid.Parse("a4444444-4444-4444-4444-444444444441"),
            AccountId = CairoLogisticsId,
            FullName = "Hana Ibrahim",
            EmailAddress1 = "hana@cairologistics.example",
            Telephone1 = "+20-100-555-4001",
            JobTitle = "Operations Manager"
        },
        new CrmContact
        {
            ContactId = Guid.Parse("a5555555-5555-5555-5555-555555555551"),
            AccountId = DeltaServicesId,
            FullName = "Karim Farouk",
            EmailAddress1 = "karim@deltaservices.example",
            Telephone1 = "+20-100-555-5001",
            JobTitle = "Managing Partner"
        },
        new CrmContact
        {
            ContactId = Guid.Parse("afffffff-ffff-ffff-ffff-ffffffffffff"),
            AccountId = CrmAuthorizationService.DeniedConfidentialAccountId,
            FullName = "Restricted Contact",
            EmailAddress1 = "restricted@confidential.example",
            Telephone1 = "+20-100-555-9999",
            JobTitle = "Executive"
        }
    ];

    public static IReadOnlyList<CrmSalesOrder> CreateSalesOrders() =>
    [
        new CrmSalesOrder
        {
            SalesOrderId = Guid.Parse("b1111111-1111-1111-1111-111111111111"),
            Name = "SO-Ahmed Trading Q1",
            OrderNumber = "ORD-1001",
            CustomerAccountId = AhmedTradingId,
            CreatedOn = DateTimeOffset.UtcNow.AddDays(-10),
            ModifiedOn = DateTimeOffset.UtcNow.AddDays(-9),
            TotalAmount = 15000m,
            StatusCode = 1,
            StateCode = 0,
            StatusLabel = "New",
            StateLabel = "Active"
        },
        new CrmSalesOrder
        {
            SalesOrderId = Guid.Parse("b2222222-2222-2222-2222-222222222222"),
            Name = "SO-Ahmed Tech Hardware",
            OrderNumber = "ORD-1002",
            CustomerAccountId = AhmedTechId,
            CreatedOn = DateTimeOffset.UtcNow.AddDays(-5),
            ModifiedOn = DateTimeOffset.UtcNow.AddDays(-4),
            TotalAmount = 8500m,
            StatusCode = 1,
            StateCode = 0,
            StatusLabel = "New",
            StateLabel = "Active"
        },
        new CrmSalesOrder
        {
            SalesOrderId = Guid.Parse("b3333333-3333-3333-3333-333333333333"),
            Name = "SO-Nile Retail Restock ABC",
            OrderNumber = "ORD-1003",
            CustomerAccountId = NileRetailId,
            CreatedOn = DateTimeOffset.UtcNow.AddDays(-2),
            ModifiedOn = DateTimeOffset.UtcNow.AddDays(-1),
            TotalAmount = 22000m,
            StatusCode = 2,
            StateCode = 0,
            StatusLabel = "Pending",
            StateLabel = "Active"
        }
    ];
}
