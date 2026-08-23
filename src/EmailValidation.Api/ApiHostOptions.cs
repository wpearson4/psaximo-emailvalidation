namespace EmailValidation.Api;

public sealed class ApiHostOptions
{
    public ApiLimitsOptions Limits { get; set; } = new();
    public ApiRateLimitOptions RateLimiting { get; set; } = new();
    public ApiCorsOptions Cors { get; set; } = new();
    public ApiOpenApiOptions OpenApi { get; set; } = new();
}

public sealed class ApiLimitsOptions
{
    public long MaximumRequestBodyBytes { get; set; } = 1_048_576;
    public int MaximumEmailLength { get; set; } = 320;
    public int MaximumIdentifierLength { get; set; } = 128;
    public int MaximumIdempotencyKeyLength { get; set; } = 128;
    public int DefaultJobResultPageSize { get; set; } = 100;
}

public sealed class ApiRateLimitOptions
{
    public int PermitLimit { get; set; } = 120;
    public int WindowSeconds { get; set; } = 60;
    public int QueueLimit { get; set; }
    public int StreamConcurrencyLimit { get; set; } = 20;
}

public sealed class ApiCorsOptions
{
    public string[] AllowedOrigins { get; set; } = [];
}

public sealed class ApiOpenApiOptions
{
    public bool ExposeInProduction { get; set; }
    public string? AuthorizationUrl { get; set; }
    public string? TokenUrl { get; set; }
    public string? SwaggerClientId { get; set; }
}

public sealed class ApiAuthenticationOptions
{
    public string Authority { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public bool RequireHttpsMetadata { get; set; } = true;
}
