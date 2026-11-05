from pathlib import Path

p = Path(r"d:\CRM POC\tools\CrmReportingEntityDiscovery\Program.cs")
text = p.read_text(encoding="utf-8")
start = text.index("static IEnumerable<(string Question, string Feasibility)> ExampleQuestions")
end = text.index("static string JoinOrNone")
new_fn = r'''static IEnumerable<(string Question, string Feasibility)> ExampleQuestions(string logical)
{
    switch (logical.ToLowerInvariant())
    {
        case "opportunity":
            yield return ("How many open opportunities by month this year?", "Supported: aggregate + date + groupBy month (root-only)");
            yield return ("Top opportunities with account name", "Supported: list + linksJson to account");
            yield return ("Opportunity revenue by account this quarter", "Unsupported until aggregate+linksJson");
            yield break;
        case "salesorderdetail":
            yield return ("Monthly ordered quantity by product for 2025", "Supported if aggregating on salesorderdetail root");
            yield return ("Order lines for a sales order with product name", "Supported: list + linksJson product");
            yield return ("Total line amount by customer account", "Unsupported as aggregate+link; needs engine enhancement");
            yield break;
        case "quote":
            yield return ("Count of quotes created last 8 calendar months", "Supported: count/aggregate + dateRange");
            yield return ("Quotes with customer account", "Supported: list + linksJson");
            yield break;
        case "invoice":
            yield return ("Invoiced amount by month for 2025", "Supported: aggregate on invoice root if Money + date fields exist");
            yield return ("Invoices with related sales order", "Supported: list + linksJson (not aggregate+link)");
            yield break;
        case "product":
            yield return ("List active products", "Supported: list + status filter");
            yield return ("Count products by product type", "Supported: aggregate + groupBy picklist if present");
            yield break;
        case "campaign":
            yield return ("Campaigns started this year", "Supported: list/count + date filter");
            yield return ("Campaign responses by campaign", "May require campaignresponse approval; aggregate+link unsupported");
            yield break;
        case "systemuser":
            yield return ("List users", "Supported: list on systemuser");
            yield return ("Count orders by owner", "Supported via aggregate groupBy ownerid on salesorder; systemuser helps list enrichment");
            yield break;
        case "businessunit":
            yield return ("List business units", "Supported: list");
            yield return ("Orders by business unit", "Supported: aggregate groupBy owningbusinessunit on salesorder");
            yield break;
        default:
            yield return ("List recent records", "Supported when approved: list + sort + date");
            yield return ("Count records in a calendar period", "Supported: count/aggregate + dateRange");
            yield return ("Aggregate with related entity attributes", "Unsupported: aggregate + linksJson");
            yield break;
    }
}

'''
p.write_text(text[:start] + new_fn + text[end:], encoding="utf-8")
print("patched ok")
