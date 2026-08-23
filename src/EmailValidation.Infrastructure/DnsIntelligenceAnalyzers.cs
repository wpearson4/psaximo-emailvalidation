using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed class MailRoutingAnalyzer(IDnsMailResolver resolver) : IMailRoutingAnalyzer
{
    public async Task<MailRoutingIntelligence> AnalyzeAsync(
        string domain,
        CancellationToken cancellationToken = default)
    {
        var dns = await resolver.ResolveAsync(domain, cancellationToken).ConfigureAwait(false);
        return new MailRoutingIntelligence(
            dns.Status,
            dns.DomainExists,
            dns.MxRecords
                .OrderBy(record => record.Preference)
                .ThenBy(record => record.Host, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            dns.UsedAddressFallback,
            dns.ExplicitNullMx,
            dns.Ipv4Addresses ?? [],
            dns.Ipv6Addresses ?? [],
            dns.TimeToLive,
            DateTimeOffset.UtcNow,
            dns.Error,
            dns.Duration);
    }
}

internal sealed class DnsSecurityAnalyzer(
    IDnsWireQueryClient dns,
    IOptions<EmailValidationOptions> options,
    ILogger<DnsSecurityAnalyzer> logger) : IDnsSecurityAnalyzer
{
    private static readonly Meter Meter = new("EmailValidation.DnsSecurity", "1.0.0");
    private static readonly Counter<long> SecureCount = Meter.CreateCounter<long>("dnssec_secure");
    private static readonly Counter<long> FailureCount = Meter.CreateCounter<long>("dnssec_failure");
    private readonly DnsSecurityOptions _options = options.Value.DnsSecurity;

    public async Task<DnsSecurityIntelligence> AnalyzeAsync(
        string domain,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return new(DnsSecurityState.Unknown, IntelligenceAvailability.NotAvailable,
                DateTimeOffset.UtcNow, "DNSSEC analysis is disabled.");
        try
        {
            var validated = await dns.QueryAsync(domain, DnsRecordType.DnsKey, dnssec: true,
                checkingDisabled: false, cancellationToken).ConfigureAwait(false);
            if (validated.AuthenticatedData)
            {
                SecureCount.Add(1);
                return new(DnsSecurityState.Secure, IntelligenceAvailability.Available,
                    DateTimeOffset.UtcNow, "The configured recursive resolver returned authenticated data.");
            }
            if (validated.ResponseCode == 2)
            {
                var uncheckedResponse = await dns.QueryAsync(domain, DnsRecordType.DnsKey, dnssec: true,
                    checkingDisabled: true, cancellationToken).ConfigureAwait(false);
                if (uncheckedResponse.ResponseCode == 0 && uncheckedResponse.HasAnswer(DnsRecordType.DnsKey))
                    return new(DnsSecurityState.Bogus, IntelligenceAvailability.Degraded,
                        DateTimeOffset.UtcNow, "Validation failed while unchecked DNSKEY data remained available.");
                return new(DnsSecurityState.Indeterminate, IntelligenceAvailability.Degraded,
                    DateTimeOffset.UtcNow, "The recursive resolver returned a server failure that could not be attributed conclusively.");
            }
            if (validated.ResponseCode == 0 && !validated.HasAnswer(DnsRecordType.DnsKey))
                return new(DnsSecurityState.NotPresent, IntelligenceAvailability.Available,
                    DateTimeOffset.UtcNow, "No DNSKEY was observed at the domain apex.");
            return new(DnsSecurityState.Indeterminate, IntelligenceAvailability.Degraded,
                DateTimeOffset.UtcNow, $"The recursive resolver returned DNS response code {validated.ResponseCode} without authenticated data.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SocketException or IOException or TimeoutException or FormatException or OperationCanceledException)
        {
            FailureCount.Add(1);
            logger.LogWarning("DNSSEC analysis failed for {Domain} ({ErrorType})", domain, exception.GetType().Name);
            return new(DnsSecurityState.Indeterminate, IntelligenceAvailability.Failed,
                DateTimeOffset.UtcNow, "DNSSEC state could not be obtained from the configured resolver.");
        }
    }
}

internal sealed class EmailAuthenticationAnalyzer(
    IDnsWireQueryClient dns,
    IOptions<EmailValidationOptions> options,
    ILogger<EmailAuthenticationAnalyzer> logger) : IEmailAuthenticationAnalyzer
{
    private static readonly Meter Meter = new("EmailValidation.Authentication", "1.0.0");
    private static readonly Counter<long> SpfPresent = Meter.CreateCounter<long>("spf_present");
    private static readonly Counter<long> DmarcPresent = Meter.CreateCounter<long>("dmarc_present");
    private readonly AuthenticationIntelligenceOptions _options = options.Value.AuthenticationIntelligence;

    public async Task<EmailAuthenticationIntelligence> AnalyzeAsync(
        string domain,
        CancellationToken cancellationToken = default)
    {
        var observedAt = DateTimeOffset.UtcNow;
        var spfTask = _options.SpfEnabled
            ? LookupTxtAsync(domain, cancellationToken)
            : Task.FromResult<DnsWireResponse?>(null);
        var dmarcTask = _options.DmarcEnabled
            ? LookupTxtAsync($"_dmarc.{domain}", cancellationToken)
            : Task.FromResult<DnsWireResponse?>(null);

        DnsWireResponse? spfResponse = null;
        DnsWireResponse? dmarcResponse = null;
        var failed = false;
        try
        {
            await Task.WhenAll(spfTask, dmarcTask).ConfigureAwait(false);
            spfResponse = await spfTask.ConfigureAwait(false);
            dmarcResponse = await dmarcTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SocketException or IOException or TimeoutException or FormatException or OperationCanceledException)
        {
            failed = true;
            if (spfTask.IsCompletedSuccessfully) spfResponse = spfTask.Result;
            if (dmarcTask.IsCompletedSuccessfully) dmarcResponse = dmarcTask.Result;
            logger.LogWarning("Email-authentication analysis degraded for {Domain} ({ErrorType})",
                domain, exception.GetType().Name);
        }

        var spf = !_options.SpfEnabled
            ? SpfIntelligence.Unknown
            : failed && spfResponse is null
                ? new SpfIntelligence(AuthenticationRecordState.LookupFailed, Detail: "SPF lookup failed.")
                : ParseSpf(spfResponse!);
        var dmarc = !_options.DmarcEnabled
            ? DmarcIntelligence.Unknown
            : failed && dmarcResponse is null
                ? new DmarcIntelligence(AuthenticationRecordState.LookupFailed, Detail: "DMARC lookup failed.")
                : ParseDmarc(dmarcResponse!);
        var dkim = _options.DkimObservationEnabled
            ? DkimIntelligence.NotEvaluated
            : new DkimIntelligence(DkimObservationState.NotEvaluated, [], "DKIM observation is disabled.");
        var availability = failed ? IntelligenceAvailability.Degraded : IntelligenceAvailability.Available;
        if (spf.State == AuthenticationRecordState.Valid) SpfPresent.Add(1);
        if (dmarc.State == AuthenticationRecordState.Valid) DmarcPresent.Add(1);
        return new(spf, dmarc, dkim, availability, observedAt);
    }

    private async Task<DnsWireResponse?> LookupTxtAsync(string name, CancellationToken cancellationToken) =>
        await dns.QueryAsync(name, DnsRecordType.Txt, dnssec: false,
            checkingDisabled: false, cancellationToken).ConfigureAwait(false);

    internal static SpfIntelligence ParseSpf(DnsWireResponse response)
    {
        if (response.ResponseCode != 0)
            return new(AuthenticationRecordState.LookupFailed, Detail: $"DNS response code {response.ResponseCode}.");
        var records = response.TextRecords
            .Where(record => record.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (records.Length == 0) return new(AuthenticationRecordState.NotPresent);
        if (records.Length > 1)
            return new(AuthenticationRecordState.Invalid, Record: string.Join(" | ", records),
                Detail: "Multiple SPF records were published.");
        var tokens = records[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || !tokens[0].Equals("v=spf1", StringComparison.OrdinalIgnoreCase))
            return new(AuthenticationRecordState.Invalid, Record: records[0], Detail: "Malformed SPF version tag.");
        var all = tokens.LastOrDefault(token => token.TrimStart('+', '-', '~', '?')
            .Equals("all", StringComparison.OrdinalIgnoreCase));
        return new(AuthenticationRecordState.Valid, all, records[0]);
    }

    internal static DmarcIntelligence ParseDmarc(DnsWireResponse response)
    {
        if (response.ResponseCode == 3 || response.ResponseCode == 0 && response.TextRecords.Count == 0)
            return new(AuthenticationRecordState.NotPresent);
        if (response.ResponseCode != 0)
            return new(AuthenticationRecordState.LookupFailed, Detail: $"DNS response code {response.ResponseCode}.");
        var records = response.TextRecords
            .Where(record => record.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (records.Length == 0) return new(AuthenticationRecordState.NotPresent);
        if (records.Length > 1)
            return new(AuthenticationRecordState.Invalid, Record: string.Join(" | ", records),
                Detail: "Multiple DMARC records were published.");
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in records[0].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !tags.TryAdd(pair[0], pair[1]))
                return new(AuthenticationRecordState.Invalid, Record: records[0], Detail: "Malformed or duplicate DMARC tag.");
        }
        if (!tags.TryGetValue("v", out var version) || !version.Equals("DMARC1", StringComparison.OrdinalIgnoreCase) ||
            !tags.TryGetValue("p", out var policyText) || !TryPolicy(policyText, out var policy))
            return new(AuthenticationRecordState.Invalid, Record: records[0], Detail: "DMARC requires valid v and p tags.");
        DmarcPolicy? subdomain = null;
        if (tags.TryGetValue("sp", out var sp))
        {
            if (!TryPolicy(sp, out var parsed))
                return new(AuthenticationRecordState.Invalid, Record: records[0], Detail: "The DMARC sp tag is invalid.");
            subdomain = parsed;
        }
        int? percentage = null;
        if (tags.TryGetValue("pct", out var pct))
        {
            if (!int.TryParse(pct, out var parsed) || parsed is < 0 or > 100)
                return new(AuthenticationRecordState.Invalid, Record: records[0], Detail: "The DMARC pct tag is invalid.");
            percentage = parsed;
        }
        return new(AuthenticationRecordState.Valid, policy, subdomain, percentage, records[0]);
    }

    private static bool TryPolicy(string value, out DmarcPolicy policy)
    {
        policy = value.ToLowerInvariant() switch
        {
            "none" => DmarcPolicy.None,
            "quarantine" => DmarcPolicy.Quarantine,
            "reject" => DmarcPolicy.Reject,
            _ => DmarcPolicy.Unknown
        };
        return policy != DmarcPolicy.Unknown;
    }
}

internal enum DnsRecordType : ushort { Txt = 16, DnsKey = 48 }

internal sealed record DnsWireResponse(
    int ResponseCode,
    bool AuthenticatedData,
    IReadOnlyList<ushort> AnswerTypes,
    IReadOnlyList<string> TextRecords)
{
    public bool HasAnswer(DnsRecordType type) => AnswerTypes.Contains((ushort)type);
}

internal interface IDnsWireQueryClient
{
    Task<DnsWireResponse> QueryAsync(
        string name,
        DnsRecordType type,
        bool dnssec,
        bool checkingDisabled,
        CancellationToken cancellationToken);
}

internal sealed class DnsWireQueryClient(IOptions<EmailValidationOptions> options) : IDnsWireQueryClient
{
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.Dns.TimeoutSeconds));
    private readonly int _retryCount = Math.Clamp(options.Value.Dns.RetryCount, 0, 3);

    public async Task<DnsWireResponse> QueryAsync(
        string name,
        DnsRecordType type,
        bool dnssec,
        bool checkingDisabled,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var query = BuildQuery(name, type, dnssec, checkingDisabled, out var id);
                var server = GetNameServer();
                var response = await QueryUdpAsync(server, query, cancellationToken).ConfigureAwait(false);
                if ((BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2)) & 0x0200) != 0)
                    response = await QueryTcpAsync(server, query, cancellationToken).ConfigureAwait(false);
                return ParseResponse(response, id);
            }
            catch (Exception exception) when (
                attempt < _retryCount &&
                exception is SocketException or IOException or TimeoutException or OperationCanceledException &&
                !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<byte[]> QueryUdpAsync(IPEndPoint server, byte[] query, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var client = new UdpClient(server.AddressFamily);
        await client.SendAsync(query, server, timeout.Token).ConfigureAwait(false);
        return (await client.ReceiveAsync(timeout.Token).ConfigureAwait(false)).Buffer;
    }

    private async Task<byte[]> QueryTcpAsync(IPEndPoint server, byte[] query, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var client = new TcpClient(server.AddressFamily);
        await client.ConnectAsync(server.Address, server.Port, timeout.Token).ConfigureAwait(false);
        await using var stream = client.GetStream();
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)query.Length));
        await stream.WriteAsync(length, timeout.Token).ConfigureAwait(false);
        await stream.WriteAsync(query, timeout.Token).ConfigureAwait(false);
        await ReadExactlyAsync(stream, length, timeout.Token).ConfigureAwait(false);
        var response = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
        await ReadExactlyAsync(stream, response, timeout.Token).ConfigureAwait(false);
        return response;
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new IOException("Unexpected end of DNS response.");
            offset += read;
        }
    }

    private static byte[] BuildQuery(
        string name,
        DnsRecordType type,
        bool dnssec,
        bool checkingDisabled,
        out ushort id)
    {
        id = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)(0x0100 | (checkingDisabled ? 0x0010 : 0)));
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], dnssec ? (ushort)1 : (ushort)0);
        stream.Write(header);
        WriteName(stream, name);
        Span<byte> question = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(question, (ushort)type);
        BinaryPrimitives.WriteUInt16BigEndian(question[2..], 1);
        stream.Write(question);
        if (dnssec)
        {
            stream.WriteByte(0);
            Span<byte> opt = stackalloc byte[10];
            BinaryPrimitives.WriteUInt16BigEndian(opt, 41);
            BinaryPrimitives.WriteUInt16BigEndian(opt[2..], 1232);
            BinaryPrimitives.WriteUInt32BigEndian(opt[4..], 0x00008000);
            stream.Write(opt);
        }
        return stream.ToArray();
    }

    private static void WriteName(Stream stream, string name)
    {
        foreach (var label in name.Trim().TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new FormatException("Invalid DNS label.");
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        stream.WriteByte(0);
    }

    private static DnsWireResponse ParseResponse(byte[] message, ushort expectedId)
    {
        if (message.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(message) != expectedId)
            throw new FormatException("Invalid DNS response.");
        var flags = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(2, 2));
        var questions = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4, 2));
        var answers = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(6, 2));
        var offset = 12;
        for (var index = 0; index < questions; index++)
        {
            ReadName(message, ref offset);
            EnsureAvailable(message, offset, 4);
            offset += 4;
        }
        var answerTypes = new List<ushort>(answers);
        var text = new List<string>();
        for (var index = 0; index < answers; index++)
        {
            ReadName(message, ref offset);
            EnsureAvailable(message, offset, 10);
            var recordType = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset, 2));
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 8, 2));
            offset += 10;
            EnsureAvailable(message, offset, dataLength);
            answerTypes.Add(recordType);
            if (recordType == (ushort)DnsRecordType.Txt)
                text.Add(ReadTextRecord(message.AsSpan(offset, dataLength)));
            offset += dataLength;
        }
        return new(flags & 0x000F, (flags & 0x0020) != 0, answerTypes, text);
    }

    private static string ReadTextRecord(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder();
        var offset = 0;
        while (offset < data.Length)
        {
            var length = data[offset++];
            if (offset > data.Length - length) throw new FormatException("Truncated TXT record.");
            builder.Append(Encoding.UTF8.GetString(data.Slice(offset, length)));
            offset += length;
        }
        return builder.ToString();
    }

    private static string ReadName(byte[] message, ref int offset)
    {
        var labels = new List<string>();
        var current = offset;
        var jumped = false;
        var hops = 0;
        while (true)
        {
            EnsureAvailable(message, current, 1);
            var length = message[current++];
            if (length == 0)
            {
                if (!jumped) offset = current;
                break;
            }
            if ((length & 0xC0) == 0xC0)
            {
                EnsureAvailable(message, current, 1);
                var pointer = ((length & 0x3F) << 8) | message[current++];
                if (!jumped) offset = current;
                current = pointer;
                jumped = true;
                if (++hops > 20) throw new FormatException("DNS compression loop.");
                continue;
            }
            if ((length & 0xC0) != 0) throw new FormatException("Invalid DNS label.");
            EnsureAvailable(message, current, length);
            labels.Add(Encoding.ASCII.GetString(message, current, length));
            current += length;
            if (!jumped) offset = current;
        }
        return string.Join('.', labels);
    }

    private static void EnsureAvailable(byte[] message, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > message.Length - count)
            throw new FormatException("Truncated DNS response.");
    }

    private static IPEndPoint GetNameServer()
    {
        if (File.Exists("/etc/resolv.conf"))
        {
            foreach (var line in File.ReadLines("/etc/resolv.conf"))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0] == "nameserver" && IPAddress.TryParse(parts[1], out var address))
                    return new IPEndPoint(address, 53);
            }
        }
        return new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53);
    }
}
