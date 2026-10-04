using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace EmailValidation.Api;

public sealed class SourceFileAccessException(
    HttpStatusCode statusCode,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed class SourceFileSizeLimitExceededException()
    : IOException("The selected result exceeds the validation size limit.");

public interface IEmailValidationSourceFileClient
{
    Task DemandAccessAsync(
        string sourceFileId,
        string? authorization,
        CancellationToken cancellationToken = default);

    Task<EmailValidationSourceFile> OpenAsync(
        string sourceFileId,
        string? authorization,
        CancellationToken cancellationToken = default);
}

public interface IPurchasedResultClient
{
    Task<EmailValidationSourceFile> OpenAsync(
        string transactionId,
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
    IOptions<ApiHostOptions> options,
    IHttpContextAccessor contextAccessor) : IEmailValidationSourceFileClient
{
    public async Task DemandAccessAsync(
        string sourceFileId,
        string? authorization,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(sourceFileId, authorization, download: false);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new SourceFileAccessException(
                response.StatusCode,
                "The selected source file is not available to the current account membership.");
    }

    public async Task<EmailValidationSourceFile> OpenAsync(
        string sourceFileId,
        string? authorization,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(sourceFileId, authorization, download: true);

        var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new SourceFileAccessException(status, "The selected source file could not be opened.");
        }

        var maximumBytes = options.Value.Limits.MaximumPurchasedResultBytes;
        if (response.Content.Headers.ContentLength is > 0 &&
            response.Content.Headers.ContentLength > maximumBytes)
        {
            response.Dispose();
            throw new SourceFileAccessException(
                HttpStatusCode.RequestEntityTooLarge,
                "The selected source file exceeds the validation size limit.");
        }

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar ??
            response.Content.Headers.ContentDisposition?.FileName ?? "source.csv";
        fileName = fileName.Trim('"');
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new EmailValidationSourceFile(
            new MaximumLengthReadStream(stream, maximumBytes), fileName, response);
    }

    private HttpRequestMessage CreateRequest(
        string sourceFileId,
        string? authorization,
        bool download)
    {
        var baseUrl = options.Value.OpenMeta.BaseUrl.TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Api:OpenMeta:BaseUrl must be an absolute HTTP or HTTPS URL.");

        // The status route accepts either the Mongo request id or execution
        // search id and applies the same ownership and entitlement checks as
        // output download without opening the result payload.
        var suffix = download ? "/download" : "/status";
        var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(origin, $"/api/search-requests/{Uri.EscapeDataString(sourceFileId)}{suffix}"));
        if (!string.IsNullOrWhiteSpace(authorization))
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        ImpersonationHeaderForwarder.Forward(request, contextAccessor);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            download ? "application/octet-stream" : "application/json"));
        return request;
    }
}

public sealed class PublicApiPurchasedResultClient(
    HttpClient httpClient,
    IOptions<ApiHostOptions> options) : IPurchasedResultClient
{
    public async Task<EmailValidationSourceFile> OpenAsync(
        string transactionId,
        string? authorization,
        CancellationToken cancellationToken = default)
    {
        var configuredBaseUrl = options.Value.OpenMeta.PublicApiBaseUrl.TrimEnd('/') + "/";
        if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https"))
            throw new InvalidOperationException(
                "Api:OpenMeta:PublicApiBaseUrl must be an absolute HTTP or HTTPS URL.");

        using var describeRequest = new HttpRequestMessage(HttpMethod.Get,
            new Uri(origin, $"v1/transactions/{Uri.EscapeDataString(transactionId)}/results"));
        AddAuthorization(describeRequest, authorization);
        describeRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var describeResponse = await httpClient.SendAsync(
            describeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!describeResponse.IsSuccessStatusCode)
            throw AccessFailure(describeResponse.StatusCode);

        PublicResultDescriptor? descriptor;
        try
        {
            descriptor = await describeResponse.Content.ReadFromJsonAsync<PublicResultDescriptor>(
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new SourceFileAccessException(
                HttpStatusCode.BadGateway,
                "The purchased-result service returned an invalid download descriptor.",
                exception);
        }
        if (descriptor is null || !Uri.TryCreate(descriptor.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
            !SameOrigin(origin, downloadUri) ||
            !string.Equals(downloadUri.AbsolutePath,
                $"/v1/transactions/{Uri.EscapeDataString(transactionId)}/results/download",
                StringComparison.Ordinal))
            throw new SourceFileAccessException(
                HttpStatusCode.BadGateway, "The purchased-result service returned an invalid download descriptor.");

        var maximumBytes = options.Value.Limits.MaximumPurchasedResultBytes;
        using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, downloadUri);
        AddAuthorization(downloadRequest, authorization);
        downloadRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/csv"));
        var downloadResponse = await httpClient.SendAsync(
            downloadRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!downloadResponse.IsSuccessStatusCode)
        {
            var status = downloadResponse.StatusCode;
            downloadResponse.Dispose();
            throw AccessFailure(status);
        }
        if (downloadResponse.Content.Headers.ContentLength is > 0 &&
            downloadResponse.Content.Headers.ContentLength > maximumBytes)
        {
            downloadResponse.Dispose();
            throw new SourceFileAccessException(
                HttpStatusCode.RequestEntityTooLarge, "The purchased result exceeds the validation size limit.");
        }

        var fileName = string.IsNullOrWhiteSpace(descriptor.FileName)
            ? $"{transactionId}.csv"
            : Path.GetFileName(descriptor.FileName);
        var content = await downloadResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new EmailValidationSourceFile(
            new MaximumLengthReadStream(content, maximumBytes), fileName, downloadResponse);
    }

    private static SourceFileAccessException AccessFailure(HttpStatusCode statusCode) =>
        new(statusCode, "The purchased result could not be opened.");

    private static void AddAuthorization(HttpRequestMessage request, string? authorization)
    {
        if (!string.IsNullOrWhiteSpace(authorization))
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
    }

    private static bool SameOrigin(Uri expected, Uri actual) =>
        string.Equals(expected.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.Host, actual.Host, StringComparison.OrdinalIgnoreCase) &&
        expected.Port == actual.Port;

    private sealed record PublicResultDescriptor(string DownloadUrl, string FileName);
}

internal static class ImpersonationHeaderForwarder
{
    public static void Forward(
        HttpRequestMessage request,
        IHttpContextAccessor contextAccessor)
    {
        var sessionId = contextAccessor.HttpContext?.Request.Headers[
            ImpersonationProtocol.SessionHeader].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(sessionId))
            request.Headers.TryAddWithoutValidation(ImpersonationProtocol.SessionHeader, sessionId);
    }
}

internal sealed class MaximumLengthReadStream(Stream inner, long maximumBytes) : Stream
{
    private long _read;
    private bool _disposed;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) =>
        Count(inner.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private int Count(int count)
    {
        _read += count;
        if (_read > maximumBytes)
            throw new SourceFileSizeLimitExceededException();
        return count;
    }
}
