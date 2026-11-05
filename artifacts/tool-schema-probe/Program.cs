using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
class P {
  [Description("THE only CRM read tool.")]
  public static Task<string> query_crm([Description("entity")] string entity, string operation = "list", int limit = 10, CancellationToken ct = default) => Task.FromResult("ok");
  static void Main() {
    var f = AIFunctionFactory.Create(query_crm);
    Console.WriteLine("Name=" + f.Name);
    Console.WriteLine("Desc=" + f.Description);
    Console.WriteLine("Schema=" + f.JsonSchema);
  }
}
