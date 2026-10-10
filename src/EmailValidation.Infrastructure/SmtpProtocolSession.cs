using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EmailValidation.Core;

namespace EmailValidation.Infrastructure;

internal sealed record SmtpResponse(int Code, IReadOnlyList<string> Lines)
{
    public string Text => string.Join(" | ", Lines);
    // The first EHLO line is the server greeting, not an extension.
    public bool HasCapability(string capability) => Code == 250 && Lines.Skip(1).Any(line =>
        line.Length > 4 && string.Equals(line[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(), capability, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Bounded byte framing; never lets plaintext buffered before STARTTLS become TLS evidence.</summary>
internal sealed class SmtpProtocolSession(Stream stream, SmtpOptions options) : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private Stream _stream = stream;
    private SslStream? _tls;
    private readonly byte[] _buffer = new byte[1024];
    private int _position;
    private int _length;

    public async Task<SmtpResponse> CommandAsync(string command, CancellationToken cancellationToken)
    {
        if (command.Contains('\r') || command.Contains('\n'))
            throw new IOException("Invalid SMTP command framing");
        using var timeout = CommandTimeout(cancellationToken);
        await _stream.WriteAsync(Utf8.GetBytes(command + "\r\n"), timeout.Token);
        await _stream.FlushAsync(timeout.Token);
        return await ReadResponseAsync(timeout.Token);
    }

    public async Task<SmtpResponse> ReadGreetingAsync(CancellationToken cancellationToken)
    {
        using var timeout = CommandTimeout(cancellationToken);
        return await ReadResponseAsync(timeout.Token);
    }

    public async Task StartTlsAsync(string host, X509ChainPolicy? chainPolicy, CancellationToken cancellationToken)
    {
        if (_position != _length)
            throw new IOException("Unexpected plaintext following STARTTLS reply");
        using var timeout = CommandTimeout(cancellationToken);
        _tls = new SslStream(_stream, leaveInnerStreamOpen: true);
        await _tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host.TrimEnd('.'),
            CertificateChainPolicy = chainPolicy,
            // Platform trust and hostname validation; no permissive callback or plaintext fallback.
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, timeout.Token);
        _stream = _tls;
    }

    private CancellationTokenSource CommandTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.CommandTimeoutSeconds));
        return timeout;
    }

    internal async Task<SmtpResponse> ReadResponseAsync(CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var lineBuffer = new byte[options.MaximumReplyLineBytes];
        var total = 0;
        int? code = null;
        while (lines.Count < options.MaximumReplyLines)
        {
            var count = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (count == lineBuffer.Length || total == options.MaximumReplyBytes)
                    throw new IOException("SMTP reply exceeded byte limit");
                if (_position == _length)
                {
                    _length = await _stream.ReadAsync(_buffer, cancellationToken);
                    _position = 0;
                    if (_length == 0) throw new IOException("SMTP server closed an incomplete reply");
                }
                var value = _buffer[_position++];
                lineBuffer[count++] = value;
                total++;
                if (value != '\n') continue;
                if (count < 2 || lineBuffer[count - 2] != '\r')
                    throw new IOException("SMTP reply requires CRLF framing");
                break;
            }
            string line;
            try { line = Utf8.GetString(lineBuffer, 0, count - 2); }
            catch (DecoderFallbackException) { throw new IOException("Invalid UTF-8 in SMTP reply"); }
            if (line.Length < 3 || line[0] is < '2' or > '5' || line[1] is < '0' or > '5' ||
                line[2] is < '0' or > '9' || (line.Length > 3 && line[3] is not (' ' or '-')) ||
                line.Any(character => char.IsControl(character) && character != '\t'))
                throw new IOException("Malformed SMTP reply");
            var parsed = (line[0] - '0') * 100 + (line[1] - '0') * 10 + line[2] - '0';
            if (code.HasValue && parsed != code.Value)
                throw new IOException("Inconsistent SMTP multiline reply codes");
            code = parsed;
            lines.Add(line);
            if (line.Length == 3 || line[3] == ' ') return new(parsed, lines);
        }
        throw new IOException("SMTP reply exceeded line limit");
    }

    public ValueTask DisposeAsync()
    {
        // Do not send TLS close_notify during cleanup: disposal must not add an unbounded write.
        _tls?.Dispose();
        return ValueTask.CompletedTask;
    }
}
