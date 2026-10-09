using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using EmailValidation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailValidation.Infrastructure;

public sealed class MxDnsResolver(IOptions<EmailValidationOptions> options, ILogger<MxDnsResolver> logger) : IDnsMailResolver
{
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.Dns.TimeoutSeconds));
    private readonly int _retryCount = Math.Clamp(options.Value.Dns.RetryCount, 0, 3);
    private readonly Func<string, CancellationToken, Task<(byte[] Response, ushort QueryId)>>? _query;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _addresses = Dns.GetHostAddressesAsync;

    internal MxDnsResolver(IOptions<EmailValidationOptions> options, ILogger<MxDnsResolver> logger,
        Func<string, CancellationToken, Task<(byte[] Response, ushort QueryId)>> query,
        Func<string, CancellationToken, Task<IPAddress[]>> addresses) : this(options, logger)
    {
        _query = query;
        _addresses = addresses;
    }

    public async Task<DnsLookupResult> ResolveAsync(string domain, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var server = GetNameServer();
            var (response, queryId) = _query is null
                ? await QueryWithRetryAsync(server, domain, cancellationToken)
                : await _query(domain, cancellationToken);

            var parsed = ParseResponse(response, queryId, domain);
            if (parsed.Status == DnsStatus.DomainNotFound)
                return new(parsed.Status, false, [], false, stopwatch.Elapsed,
                    TimeToLive: TimeSpan.FromSeconds(parsed.MinimumTtlSeconds ?? 0));
            if (parsed.MxRecords.Count > 0 || parsed.NullMx)
                return new(DnsStatus.Success, true, parsed.MxRecords, false, stopwatch.Elapsed,
                    ExplicitNullMx: parsed.NullMx,
                    TimeToLive: parsed.MinimumTtlSeconds is { } ttl ? TimeSpan.FromSeconds(ttl) : null);

            // RFC 5321 implicit MX fallback: if no MX exists but the domain has an address,
            // delivery may target the domain itself with preference zero.
            try
            {
                var addresses = await _addresses(domain, cancellationToken).WaitAsync(_timeout, cancellationToken);
                if (addresses.Length > 0)
                    return new(
                        DnsStatus.Success,
                        true,
                        [new MxRecord(0, domain)],
                        true,
                        stopwatch.Elapsed,
                        TimeToLive: parsed.MinimumTtlSeconds is { } negativeTtl
                            ? TimeSpan.FromSeconds(Math.Min(negativeTtl, Math.Max(0, options.Value.DomainIntelligence.MissingRoutingTtlSeconds)))
                            : null,
                        Ipv4Addresses: addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                            .Select(address => address.ToString()).ToArray(),
                        Ipv6Addresses: addresses.Where(address => address.AddressFamily == AddressFamily.InterNetworkV6)
                            .Select(address => address.ToString()).ToArray());
            }
            catch (SocketException exception) when (exception.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
            {
                // The MX response already established that the DNS name exists; no usable fallback remains.
            }

            // The system address resolver exposes no negative TTL. Do not cache
            // the combined absence of MX and address records as fresh evidence.
            return new(DnsStatus.Success, true, [], false, stopwatch.Elapsed, TimeToLive: TimeSpan.Zero);
        }
        catch (TimeoutException exception)
        {
            logger.LogWarning(exception, "DNS timed out for {Domain}", domain);
            return new(DnsStatus.Timeout, false, [], false, stopwatch.Elapsed, exception.Message);
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.TimedOut)
        {
            return new(DnsStatus.Timeout, false, [], false, stopwatch.Elapsed, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(DnsStatus.Timeout, false, [], false, stopwatch.Elapsed, "DNS lookup timed out");
        }
        catch (Exception exception) when (exception is SocketException or IOException or FormatException)
        {
            logger.LogWarning(exception, "DNS failed for {Domain}", domain);
            return new(DnsStatus.Failure, false, [], false, stopwatch.Elapsed, exception.Message);
        }
    }

    private async Task<(byte[] Response, ushort QueryId)> QueryWithRetryAsync(
        IPEndPoint server,
        string domain,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var query = BuildQuery(domain, out var queryId);
                var response = await QueryUdpAsync(server, query, cancellationToken);
                ValidateHeader(response, queryId);
                if ((BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2)) & 0x0200) != 0)
                    response = await QueryTcpAsync(server, query, cancellationToken);
                ValidateHeader(response, queryId);
                var responseCode = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2)) & 0x000F;
                if (responseCode != 2 || attempt >= _retryCount) return (response, queryId);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < _retryCount)
            {
                // A per-attempt timeout is transient; bounded retry continues below.
            }
            catch (SocketException) when (attempt < _retryCount)
            {
                // A temporary transport failure is retried, but never beyond the configured bound.
            }

            var delay = TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt));
            logger.LogWarning("Transient DNS failure for {Domain}; retry {Attempt} after {DelayMs} ms", domain, attempt + 1, delay.TotalMilliseconds);
            await Task.Delay(delay, cancellationToken);
        }
    }

    private async Task<byte[]> QueryUdpAsync(IPEndPoint server, byte[] query, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var client = new UdpClient(server.AddressFamily);
        client.Connect(server);
        await client.SendAsync(query, timeout.Token);
        var result = await client.ReceiveAsync(timeout.Token);
        return result.Buffer;
    }

    private async Task<byte[]> QueryTcpAsync(IPEndPoint server, byte[] query, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var client = new TcpClient(server.AddressFamily);
        await client.ConnectAsync(server.Address, server.Port, timeout.Token);
        await using var stream = client.GetStream();
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)query.Length));
        await stream.WriteAsync(length, timeout.Token);
        await stream.WriteAsync(query, timeout.Token);
        await ReadExactlyAsync(stream, length, timeout.Token);
        var response = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];
        await ReadExactlyAsync(stream, response, timeout.Token);
        return response;
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0) throw new IOException("Unexpected end of DNS response");
            offset += read;
        }
    }

    internal static byte[] BuildQuery(string domain, out ushort id)
    {
        id = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
        stream.Write(header);
        foreach (var label in domain.Split('.'))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            stream.WriteByte(checked((byte)bytes.Length));
            stream.Write(bytes);
        }
        stream.WriteByte(0);
        Span<byte> question = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(question, 15);
        BinaryPrimitives.WriteUInt16BigEndian(question[2..], 1);
        stream.Write(question);
        return stream.ToArray();
    }

    private static void ValidateHeader(byte[] message, ushort expectedId)
    {
        if (message.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(message) != expectedId)
            throw new FormatException("Invalid DNS response");
        var flags = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(2, 2));
        if ((flags & 0x8000) == 0 || (flags & 0x7800) != 0)
            throw new FormatException("Invalid DNS response flags");
    }

    private static (DnsStatus Status, List<MxRecord> MxRecords, bool NullMx, uint? MinimumTtlSeconds) ParseResponse(byte[] message, ushort expectedId, string domain)
    {
        ValidateHeader(message, expectedId);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(2, 2));
        if ((flags & 0x0200) != 0) throw new FormatException("Truncated DNS response");
        var responseCode = flags & 0x000F;
        if (responseCode is not (0 or 3)) throw new FormatException($"DNS server returned code {responseCode}");

        var questions = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4, 2));
        var answers = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(6, 2));
        var authorities = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(8, 2));
        var additionals = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(10, 2));
        var offset = 12;
        if (questions != 1) throw new FormatException("DNS question count mismatch");
        for (var index = 0; index < questions; index++)
        {
            var name = ReadName(message, ref offset);
            EnsureAvailable(message, offset, 4);
            if (!string.Equals(name, domain.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) ||
                BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset, 2)) != 15 ||
                BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 2, 2)) != 1)
                throw new FormatException("DNS question mismatch");
            offset += 4;
        }

        var records = new List<MxRecord>();
        var nullMx = false;
        var hasReferral = false;
        uint? negativeTtl = null;
        uint? minimumTtl = null;
        for (var index = 0; index < answers; index++)
        {
            ReadName(message, ref offset);
            EnsureAvailable(message, offset, 10);
            var type = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset, 2));
            var recordClass = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 2, 2));
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(offset + 4, 4));
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 8, 2));
            offset += 10;
            EnsureAvailable(message, offset, dataLength);
            if (type == 15 && recordClass == 1)
            {
                if (dataLength < 3) throw new FormatException("Invalid MX data");
                minimumTtl = minimumTtl is null ? ttl : Math.Min(minimumTtl.Value, ttl);
                var preference = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset, 2));
                var nameOffset = offset + 2;
                var exchange = ReadName(message, ref nameOffset).TrimEnd('.');
                if (nameOffset != offset + dataLength) throw new FormatException("Invalid MX data length");
                if (exchange.Length == 0)
                {
                    if (preference != 0) throw new FormatException("Invalid null MX preference");
                    nullMx = true;
                }
                else records.Add(new MxRecord(preference, exchange));
            }
            offset += dataLength;
        }
        for (var index = 0; index < authorities + additionals; index++)
        {
            var owner = ReadName(message, ref offset);
            EnsureAvailable(message, offset, 10);
            var type = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset, 2));
            var recordClass = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 2, 2));
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(offset + 4, 4));
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 8, 2));
            offset += 10;
            EnsureAvailable(message, offset, dataLength);
            if (index < authorities && recordClass == 1)
            {
                if (type == 2) hasReferral = true;
                if (type == 6)
                {
                    var soaOffset = offset;
                    ReadName(message, ref soaOffset);
                    ReadName(message, ref soaOffset);
                    if (soaOffset + 20 != offset + dataLength) throw new FormatException("Invalid SOA data");
                    EnsureAvailable(message, soaOffset, 20);
                    if (owner.Length == 0 || domain.Equals(owner, StringComparison.OrdinalIgnoreCase) ||
                        domain.EndsWith("." + owner, StringComparison.OrdinalIgnoreCase))
                    {
                        var minimum = BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(soaOffset + 16, 4));
                        var lifetime = Math.Min(ttl, minimum);
                        negativeTtl = negativeTtl is null ? lifetime : Math.Min(negativeTtl.Value, lifetime);
                    }
                }
            }
            offset += dataLength;
        }
        if (records.Count == 0 && !nullMx && responseCode == 0 && hasReferral && negativeTtl is null)
            throw new FormatException("DNS returned an unresolved referral");
        if (responseCode == 3 && (records.Count > 0 || nullMx))
            throw new FormatException("DNS returned contradictory routing evidence");
        if (nullMx && records.Count > 0) throw new FormatException("Mixed null and ordinary MX records");
        return (responseCode == 3 ? DnsStatus.DomainNotFound : DnsStatus.Success,
            records.OrderBy(record => record.Preference).ThenBy(record => record.Host, StringComparer.Ordinal).ToList(),
            nullMx,
            minimumTtl ?? negativeTtl);
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
                if (++hops > 20) throw new FormatException("DNS compression loop");
                continue;
            }
            if ((length & 0xC0) != 0) throw new FormatException("Invalid DNS label");
            EnsureAvailable(message, current, length);
            labels.Add(System.Text.Encoding.ASCII.GetString(message, current, length));
            current += length;
            if (!jumped) offset = current;
        }
        return string.Join('.', labels);
    }

    private static void EnsureAvailable(byte[] message, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > message.Length - count)
            throw new FormatException("Truncated DNS response");
    }

    private static IPEndPoint GetNameServer()
    {
        const string resolvConf = "/etc/resolv.conf";
        if (File.Exists(resolvConf))
        {
            foreach (var line in File.ReadLines(resolvConf))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0] == "nameserver" && IPAddress.TryParse(parts[1], out var address))
                    return new IPEndPoint(address, 53);
            }
        }
        return new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53);
    }
}
