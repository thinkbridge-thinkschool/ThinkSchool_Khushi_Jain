using QuotesApi.Controllers;
using QuotesApi.Extensions;

// The Service Bus SDK still keeps its send and receive spans behind this switch, and the consumer has no span to attach its work to without them.
AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);

var builder = WebApplication.CreateBuilder(args);

builder.AddInfrastructure();

var app = builder.Build();

app.UseApiPipeline();

await app.MigrateAndSeedAsync();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");

app.MapAuthEndpoints();
app.MapQuoteEndpoints();
app.MapCollectionEndpoints();

app.Run();
