using System.Globalization;
using System.Text.Json.Nodes;
using EmailValidation.Application;
using EmailValidation.Api;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Configuration.AddEmailValidationAzureAppConfiguration(builder.Environment);
builder.Configuration.AddEnvironmentVariables();
builder.Services.Configure<EmailValidationOptions>(builder.Configuration.GetSection("EmailValidation"));
builder.Services.PostConfigure<EmailValidationOptions>(options =>
    options.ProbeSenderSource.QueryJson = SerializeConfigurationNode(
        builder.Configuration.GetSection("EmailValidation:ProbeSenderSource:Query")).ToJsonString());
builder.Services.AddEmailValidation();
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();

if (!app.Environment.IsEnvironment("Testing"))
{
    await app.Services.GetRequiredService<IEmailValidationPersistenceInitializer>().InitializeAsync();
    await app.Services.GetRequiredService<IRevalidationInfrastructureInitializer>().InitializeAsync();
    await app.Services.GetRequiredService<IValidationJobInfrastructureInitializer>().InitializeAsync();
}

app.MapPost("/v1/email/validate", async (
    ValidateEmailApiRequest input,
    IEmailValidator validator,
    CancellationToken cancellationToken) =>
{
    if (input is null || string.IsNullOrWhiteSpace(input.Email))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Email is required."] });
    var result = await validator.ValidateAsync(input.Email,
        new EmailValidationRequest(input.EnableSmtp, input.Verbose, input.ValidationId), cancellationToken);
    return Results.Ok(result);
});

app.MapGet("/v1/email-validations/{validationId}", async (
    string validationId,
    HttpContext http,
    IValidationAccessPolicy accessPolicy,
    IValidationStatusQueryService statuses,
    CancellationToken cancellationToken) =>
{
    var context = new ValidationAccessContext(
        http.User.Identity?.Name,
        http.User.FindFirst("tenant_id")?.Value);
    if (!await accessPolicy.CanAccessAsync(validationId, context, cancellationToken)) return Results.Forbid();
    var status = await statuses.GetAsync(validationId, cancellationToken);
    return status is null ? Results.NotFound() : Results.Ok(status);
});

app.MapPost("/v1/email-validation/jobs", async (
    CreateValidationJobApiRequest input,
    IValidationJobService jobs,
    CancellationToken cancellationToken) =>
{
    if (input?.Emails is null || input.Emails.Count == 0)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["emails"] = ["At least one email is required."] });
    try
    {
        var job = await jobs.CreateAsync(new CreateValidationJobRequest(input.Emails, input.EnableSmtp), cancellationToken);
        return Results.Accepted($"/v1/email-validation/jobs/{job.JobId}", job);
    }
    catch (ArgumentException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["emails"] = [exception.Message] });
    }
});

app.MapGet("/v1/email-validation/jobs/{jobId}", async (
    string jobId, IValidationJobService jobs, CancellationToken cancellationToken) =>
{
    var job = await jobs.GetAsync(jobId, cancellationToken);
    return job is null ? Results.NotFound() : Results.Ok(job);
});

app.MapGet("/v1/email-validation/jobs/{jobId}/results", async (
    string jobId, int? skip, int? take, IValidationJobService jobs, CancellationToken cancellationToken) =>
{
    if (await jobs.GetAsync(jobId, cancellationToken) is null) return Results.NotFound();
    var results = await jobs.GetResultsAsync(jobId, skip ?? 0, take ?? 100, cancellationToken);
    return Results.Ok(results);
});

app.Run();

static JsonNode SerializeConfigurationNode(IConfigurationSection section)
{
    var children = section.GetChildren().ToArray();
    if (children.Length == 0)
    {
        var value = section.Value;
        if (bool.TryParse(value, out var boolean)) return JsonValue.Create(boolean);
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return JsonValue.Create(integer);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return JsonValue.Create(number);
        if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)) return null!;
        return JsonValue.Create(value ?? string.Empty);
    }
    if (children.Select(child => child.Key).SequenceEqual(
            Enumerable.Range(0, children.Length).Select(index => index.ToString(CultureInfo.InvariantCulture))))
        return new JsonArray(children.Select(SerializeConfigurationNode).ToArray());
    var result = new JsonObject();
    foreach (var child in children) result[child.Key] = SerializeConfigurationNode(child);
    return result;
}

#pragma warning disable CA1050
public partial class Program;
#pragma warning restore CA1050
