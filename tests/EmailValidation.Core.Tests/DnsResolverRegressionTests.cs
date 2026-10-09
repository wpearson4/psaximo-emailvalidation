using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class DnsResolverRegressionTests
{
    [Theory]
    [InlineData(120u, 30u, 30)]
    [InlineData(20u, 120u, 20)]
    [InlineData(0u, 30u, 0)]
    public async Task NxDomain_UsesMinimumOfSoaTtlAndMinimum(uint soaTtl, uint minimum, int expected)
    {
        var result = await Resolver(bytes =>
        {
            bytes[3] |= 3;
            bytes[9] = 1;
            using var stream = new MemoryStream();
            stream.Write(bytes);
            var soa = new byte[36];
            soa[0] = 0xc0; soa[1] = 0x0c;
            soa[3] = 6; soa[5] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(soa.AsSpan(6), soaTtl);
            soa[11] = 24;
            soa[12] = 0xc0; soa[13] = 0x0c;
            soa[14] = 0xc0; soa[15] = 0x0c;
            BinaryPrimitives.WriteUInt32BigEndian(soa.AsSpan(32), minimum);
            stream.Write(soa);
            return stream.ToArray();
        }).ResolveAsync("example.test");
        Assert.Equal(DnsStatus.DomainNotFound, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(expected), result.TimeToLive);
    }

    [Fact]
    public async Task CallerCancellation_IsNotClassifiedAsDnsFailure()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var resolver = Resolver(null, (_, token) => Task.FromCanceled<IPAddress[]>(token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync("example.test", cancellation.Token));
    }

    [Theory]
    [InlineData(SocketError.TryAgain, DnsStatus.Failure)]
    [InlineData(SocketError.TimedOut, DnsStatus.Timeout)]
    [InlineData(SocketError.NoRecovery, DnsStatus.Failure)]
    [InlineData(SocketError.HostNotFound, DnsStatus.Success)]
    [InlineData(SocketError.NoData, DnsStatus.Success)]
    public async Task ImplicitMxFallback_PreservesTransientErrors(SocketError error, DnsStatus expected)
    {
        var result = await Resolver(null, (_, _) => throw new SocketException((int)error)).ResolveAsync("example.test");
        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == DnsStatus.Success, result.HasDefinitiveNoRoute);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("id")]
    [InlineData("question")]
    [InlineData("query")]
    [InlineData("truncated")]
    [InlineData("servfail")]
    [InlineData("mixed-null")]
    [InlineData("invalid-null")]
    public async Task MalformedOrFailedDns_ReturnsStructuredFailure(string kind)
    {
        var resolver = Resolver(bytes =>
        {
            switch (kind)
            {
                case "short": return [0];
                case "id": bytes[0] ^= 1; break;
                case "question": bytes[13] ^= 1; break;
                case "query": bytes[2] = 1; break;
                case "truncated": bytes[2] |= 2; break;
                case "servfail": bytes[3] |= 2; break;
                case "mixed-null": return AppendMx(bytes, (0, ""), (10, "mx.example.test"));
                case "invalid-null": return AppendMx(bytes, (10, ""));
            }
            return bytes;
        });
        var result = await resolver.ResolveAsync("example.test");
        Assert.Equal(DnsStatus.Failure, result.Status);
        Assert.False(result.HasDefinitiveNoRoute);
    }

    [Fact]
    public async Task ValidNullMx_IsDefinitive()
    {
        var result = await Resolver(bytes => AppendMx(bytes, (0, ""))).ResolveAsync("example.test");
        Assert.Equal(DnsStatus.Success, result.Status);
        Assert.True(result.ExplicitNullMx);
    }

    private static MxDnsResolver Resolver(Func<byte[], byte[]>? mutate = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? addresses = null) =>
        new(Options.Create(new EmailValidationOptions()), NullLogger<MxDnsResolver>.Instance,
            (domain, _) =>
            {
                var bytes = MxDnsResolver.BuildQuery(domain, out var id);
                bytes[2] = 0x81;
                bytes[3] = 0x80;
                return Task.FromResult((mutate?.Invoke(bytes) ?? bytes, id));
            }, addresses ?? ((_, _) => Task.FromResult(Array.Empty<IPAddress>())));

    private static byte[] AppendMx(byte[] query, params (ushort Preference, string Host)[] records)
    {
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(6), (ushort)records.Length);
        using var stream = new MemoryStream();
        stream.Write(query);
        foreach (var (preference, host) in records)
        {
            var data = new List<byte> { (byte)(preference >> 8), (byte)preference };
            foreach (var label in host.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                data.Add((byte)label.Length);
                data.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
            }
            data.Add(0);
            stream.Write([0xc0, 0x0c, 0, 15, 0, 1, 0, 0, 0, 30, 0, (byte)data.Count]);
            stream.Write(data.ToArray());
        }
        return stream.ToArray();
    }
}
