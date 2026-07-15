using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
#if DEBUG
using System.Reflection;
#else
using Azure.Identity;
using DemoFunction.Configurations;
#endif

namespace DemoFunction.Extensions;

public static class ConfigurationManagerExtensions
{
    public static IConfigurationBuilder AddCustomConfiguration(
        this IConfigurationBuilder configurationBuilder,
        IHostEnvironment environment,
        string[] args)
    {
        configurationBuilder
            .SetBasePath(environment.ContentRootPath)
            .AddJsonFile("host.json", optional: true)
            .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables();

#if DEBUG
        // Local development / Debug build: use user secrets, do NOT touch Key Vault.
        configurationBuilder.AddUserSecrets(Assembly.GetCallingAssembly(), optional: true);
#else
        // Non-Debug (Release) build only: pull secrets from Azure Key Vault.
        var config = configurationBuilder.Build();
        configurationBuilder.AddKeyVaultConfiguration(config);
#endif

        configurationBuilder.AddCommandLine(args);

        return configurationBuilder;
    }

#if !DEBUG
    private static IConfigurationBuilder AddKeyVaultConfiguration(this IConfigurationBuilder configurationBuilder, IConfigurationRoot configurationRoot)
    {
        (var vaultUrl, var azureCredentials) = GetKeyVaultSettings(configurationRoot);

        try
        {
            configurationBuilder.AddAzureKeyVault(vaultUrl, azureCredentials);
        }
        catch
        {
        }

        return configurationBuilder;
    }

    private static (Uri vaultUri, DefaultAzureCredential azureCredential) GetKeyVaultSettings(IConfigurationRoot configuration)
    {
        var settings = configuration.GetSection("KeyVault").Get<KeyVaultConfiguration>()
            ?? throw new ArgumentNullException("KeyVault");

        var keyvaultName = settings.Name.Trim();

        if (string.IsNullOrEmpty(keyvaultName))
        {
            throw new ArgumentNullException("KeyVault settings must be configured");
        }

        var clientId = settings.ClientId.Trim();

        var azCredentials = GetDefaultAzureCredential(clientId);

        var vaultUri = new Uri($"https://{keyvaultName}.vault.azure.net");

        return (vaultUri, azCredentials);
    }

    private static DefaultAzureCredential GetDefaultAzureCredential(string clientId)
    {
        var options = new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = clientId,
        };
        return new DefaultAzureCredential(options);
    }
#endif
}
