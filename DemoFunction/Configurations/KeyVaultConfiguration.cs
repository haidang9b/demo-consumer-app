namespace DemoFunction.Configurations;

public record KeyVaultConfiguration
{
    public string Name { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;
}
