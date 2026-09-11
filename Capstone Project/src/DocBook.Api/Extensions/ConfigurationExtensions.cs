using Azure.Identity;

namespace DocBook.Api.Extensions;

public static class ConfigurationExtensions
{
    // The vault's address is not a secret; everything it resolves never reaches source control.
    public static WebApplicationBuilder AddKeyVaultConfiguration(this WebApplicationBuilder builder)
    {
        var uri = builder.Configuration["KeyVault:Uri"];

        if (string.IsNullOrWhiteSpace(uri))
        {
            return builder;
        }

        // The app service's own identity in Azure, and the developer's az login locally.
        builder.Configuration.AddAzureKeyVault(new Uri(uri), new DefaultAzureCredential());

        return builder;
    }
}
