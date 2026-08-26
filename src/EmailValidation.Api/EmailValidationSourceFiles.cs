using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace EmailValidation.Api;

public sealed class SourceFileAccessException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public interface IEmailValidationSourceFileClient
{
    Task<EmailValidationSourceFile> OpenAsync(
        string sourceFileId,
        string? authorization,
        CancellationToken cancellationToken = default);
}

public sealed class EmailValidationSourceFile(
    Stream content,
    string fileName,
    HttpResponseMessage? response = null) : IAsyncDisposable
{
    public Stream Content { get; } = content;
    public string FileName { get; } = fileName;

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync().ConfigureAwait(false);
        response?.Dispose();
    }
}

public sealed class OpenMetaEmailValidationSourceFileClient(
    HttpClient httpClient,
    IOptions<ApiHostOptions> options) : IEmailValidationSourceFileClient
{
    public async Task<EmailValidationSourceFile> OpenAsync(
        string sourceFileId,
        string? authorization,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = options.Value.OpenMeta.BaseUrl.TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Api:OpenMeta:BaseUrl must be an absolute HTTP or HTTPS URL.");

        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(origin, $"/api/search-requests/{Uri.EscapeDataString(sourceFileId)}/download"));
        if (!string.IsNullOrWhiteSpace(authorization))
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new SourceFileAccessException(status, "The selected source file could not be opened.");
        }

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar ??
            response.Content.Headers.ContentDisposition?.FileName ?? "source.csv";
        fileName = fileName.Trim('"');
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new EmailValidationSourceFile(stream, fileName, response);
    }
}
