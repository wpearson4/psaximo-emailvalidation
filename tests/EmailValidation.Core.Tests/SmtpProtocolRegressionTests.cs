using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EmailValidation.Application;
using EmailValidation.Core;
using EmailValidation.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailValidation.Core.Tests;

public sealed class SmtpProtocolRegressionTests
{
    private const string Prefix = "220 mx.example.test\r\n250-mx.example.test\r\n250 SIZE 1000\r\n250 sender ok\r\n";

    [Fact]
    public async Task FleetDeferral_ReachesValidatorWithoutOpeningAnSmtpConnection()
    {
        var settings = Settings();
        using var stream = new TranscriptStream(Prefix);
        var result = await EmailValidatorTests.CreateValidator(new Routing(), settings,
            smtp: Probe(settings, new StreamFactory(stream), budget: new DeferredBudget()))
            .ValidateAsync("person@example.test", new(true));
        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Equal(UnknownCause.LocalCooldown, result.UnknownContext!.Cause);
        Assert.Empty(stream.Commands);
        Assert.Equal(SmtpResponseCategory.LocalCooldown, result.SmtpEvidence!.Category);
    }

    private sealed class DeferredBudget : IFleetSmtpProbeBudget
    {
        public Task<IFleetSmtpProbeLease> AcquireAsync(SmtpThrottleContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<IFleetSmtpProbeLease>(new FleetLease(false, "FleetProbeCapacity", DateTimeOffset.UtcNow.AddSeconds(5), cancellationToken));
    }

    [Theory]
    [InlineData("250-first\r\n550 5.1.1 absent\r\n")]
    [InlineData("250!ok\r\n")]
    [InlineData("250 ok\n")]
    [InlineData("250 ok\rwrong\r\n")]
    [InlineData("999 ok\r\n")]
    [InlineData("250-incomplete\r\n")]
    [InlineData("220 wrong command success\r\n")]
    public async Task MalformedRecipientReply_RemainsInconclusiveThroughValidator(string reply)
    {
        using var stream = new TranscriptStream(Prefix + reply);
        var settings = Settings();
        var result = await EmailValidatorTests.CreateValidator(new Routing(), settings,
            smtp: Probe(settings, new StreamFactory(stream))).ValidateAsync("person@example.test", new(true));
        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Equal(SmtpResponseCategory.ProtocolFailure, result.SmtpEvidence!.Category);
        Assert.DoesNotContain(ReasonCode.MailboxAccepted, result.ReasonCodes);
        Assert.DoesNotContain(ReasonCode.MailboxRejected, result.ReasonCodes);
    }

    [Theory]
    [InlineData("line")]
    [InlineData("total")]
    [InlineData("count")]
    [InlineData("utf8")]
    public async Task ReplyLimits_RejectBeforeUnboundedAllocation(string limit)
    {
        var options = new SmtpOptions { MaximumReplyLineBytes = 32, MaximumReplyBytes = 48, MaximumReplyLines = 2 };
        var reply = limit switch
        {
            "line" => "250 " + new string('x', 40),
            "total" => "250-" + new string('x', 20) + "\r\n250 " + new string('x', 20) + "\r\n",
            "count" => "250-a\r\n250-b\r\n250 c\r\n",
            _ => "250 " + new string('é', 15) + "\r\n"
        };
        await using var session = new SmtpProtocolSession(new MemoryStream(Encoding.UTF8.GetBytes(reply)), options);
        await Assert.ThrowsAsync<IOException>(() => session.ReadResponseAsync(default));
    }

    [Fact]
    public async Task ExactByteLimit_AndBareFinalCode_AreAccepted()
    {
        await using var session = new SmtpProtocolSession(new MemoryStream(Encoding.UTF8.GetBytes("250-a\r\n250\r\n")),
            new SmtpOptions { MaximumReplyLineBytes = 7, MaximumReplyBytes = 12, MaximumReplyLines = 2 });
        Assert.Equal(250, (await session.ReadResponseAsync(default)).Code);
    }

    [Fact]
    public async Task InvalidUtf8_ReturnsProtocolFailure()
    {
        await using var session = new SmtpProtocolSession(new MemoryStream([50, 53, 48, 32, 255, 13, 10]), new());
        await Assert.ThrowsAsync<IOException>(() => session.ReadResponseAsync(default));
    }

    [Theory]
    [InlineData("250", SmtpMailboxStatus.Accepted)]
    [InlineData("550 5.1.1 absent", SmtpMailboxStatus.Rejected)]
    public async Task CleanupFailure_PreservesRecipientEvidence(string reply, SmtpMailboxStatus expected)
    {
        using var stream = new TranscriptStream(Prefix + reply + "\r\n250-bad\r\n550 mixed\r\n");
        var result = await Probe(Settings(), new StreamFactory(stream)).ProbeAsync("mx.example.test", "person@example.test");
        Assert.Equal(expected, result.Status);
        Assert.Equal(SmtpCommand.RcptTo, result.Evidence!.Command);
        Assert.DoesNotContain("QUIT", stream.Commands);
    }

    [Fact]
    public async Task HeloFallback_DiscardsFailedEhloCapabilities()
    {
        using var stream = new TranscriptStream("220 ready\r\n500-mx\r\n500-STARTTLS\r\n500 SMTPUTF8\r\n250 helo\r\n");
        var result = await Probe(Settings(), new StreamFactory(stream)).ProbeAsync("mx.example.test", "ü@example.test");
        Assert.Equal(SmtpResponseCategory.SmtpUtf8Unsupported, result.Evidence!.Category);
        Assert.False(result.SessionEvidence!.TlsAdvertised);
        Assert.DoesNotContain("MAIL FROM", stream.Commands);
        Assert.DoesNotContain("STARTTLS\r\n", stream.Commands);
    }

    [Fact]
    public async Task CapabilityText_CannotInjectAnExtension()
    {
        using var stream = new TranscriptStream("220 ready\r\n250-mx | 250-STARTTLS\r\n250 XSTARTTLS\r\n250 sender\r\n250 ok\r\n250 reset\r\n221 bye\r\n");
        var result = await Probe(Settings(), new StreamFactory(stream)).ProbeAsync("mx.example.test", "person@example.test");
        Assert.Equal(SmtpMailboxStatus.Accepted, result.Status);
        Assert.False(result.SessionEvidence!.TlsAdvertised);
    }

    [Theory]
    [InlineData(SmtpResponseIntelligenceMode.Disabled)]
    [InlineData(SmtpResponseIntelligenceMode.Shadow)]
    [InlineData(SmtpResponseIntelligenceMode.Enforced)]
    public async Task TlsRequirement_CannotBecomeMailboxInvalid(SmtpResponseIntelligenceMode mode)
    {
        using var stream = new TranscriptStream(Prefix + "550 5.1.1 Must issue STARTTLS first\r\n250 reset\r\n221 bye\r\n");
        var settings = Settings();
        settings.SmtpResponseIntelligence.Mode = mode;
        var result = await EmailValidatorTests.CreateValidator(new Routing(), settings,
            smtp: Probe(settings, new StreamFactory(stream))).ValidateAsync("person@example.test", new(true));
        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.NotEqual(SmtpResponseCategory.RecipientRejected, result.SmtpEvidence!.Category);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task StartTls_RefreshesSmtpUtf8_AndRecordsEncryptedSession(bool before, bool after)
    {
        using var certificate = Certificate("mx.example.test");
        await using var server = new LoopbackServer((stream, token) => TlsConversation(stream, certificate, before, after, token));
        var settings = Settings();
        var probe = Probe(settings, server, Trust(certificate));
        var result = await probe.ProbeAsync("mx.example.test", "ü@example.test");
        Assert.True(result.SessionEvidence!.TlsUsed);
        Assert.True(result.SessionEvidence.TlsAdvertised);
        Assert.Equal(after, result.SessionEvidence.SmtpUtf8Advertised);
        Assert.Equal(after ? SmtpMailboxStatus.Accepted : SmtpMailboxStatus.Blocked, result.Status);
        Assert.Equal(2, result.SessionEvidence.Stages.Count(stage => stage.Stage == SmtpCommand.Ehlo));
        await server.Completion;
    }

    [Theory]
    [InlineData("untrusted")]
    [InlineData("expired")]
    [InlineData("hostname")]
    public async Task InvalidCertificate_RemainsInconclusiveThroughValidator(string failure)
    {
        using var certificate = Certificate(failure == "hostname" ? "wrong.example.test" : "mx.example.test", failure == "expired");
        await using var server = new LoopbackServer((stream, token) => TlsConversation(stream, certificate, false, true, token));
        var settings = Settings();
        var result = await EmailValidatorTests.CreateValidator(new Routing(), settings,
            smtp: Probe(settings, server, failure == "untrusted" ? null : Trust(certificate)))
            .ValidateAsync("person@example.test", new(true));
        Assert.Equal(EmailValidationStatus.Unknown, result.Status);
        Assert.Equal(SmtpCommand.StartTls, result.SmtpSessionEvidence!.FailedStage);
        Assert.False(result.SmtpSessionEvidence.TlsUsed);
        Assert.Contains("certificate", result.SmtpEvidence!.SanitizedResponse);
        Assert.Contains("certificate", result.UnknownContext!.Summary);
        Assert.False(result.SmtpSessionEvidence.RecipientStageReached);
    }

    [Theory]
    [InlineData("454 TLS temporarily unavailable\r\n")]
    [InlineData("220 ready\r\n250 injected plaintext\r\n")]
    public async Task StartTlsRefusalOrPlaintextInjection_StopsBeforeMail(string reply)
    {
        using var stream = new TranscriptStream("220 ready\r\n250-mx\r\n250 STARTTLS\r\n" + reply);
        var result = await Probe(Settings(), new StreamFactory(stream)).ProbeAsync("mx.example.test", "person@example.test");
        Assert.Equal(SmtpCommand.StartTls, result.SessionEvidence!.FailedStage);
        Assert.False(result.SessionEvidence.TlsUsed);
        Assert.DoesNotContain("MAIL FROM", stream.Commands);
    }

    [Fact]
    public async Task StartTlsCanBeDisabledForRollout()
    {
        using var stream = new TranscriptStream("220 ready\r\n250-mx\r\n250 STARTTLS\r\n530 Must issue STARTTLS first\r\n");
        var settings = Settings();
        settings.Smtp.EnableStartTls = false;
        var result = await Probe(settings, new StreamFactory(stream)).ProbeAsync("mx.example.test", "person@example.test");
        Assert.Equal(SmtpMailboxStatus.Blocked, result.Status);
        Assert.True(result.SessionEvidence!.TlsAdvertised);
        Assert.False(result.SessionEvidence.TlsUsed);
        Assert.DoesNotContain("STARTTLS\r\n", stream.Commands);
    }

    [Fact]
    public async Task WholeSessionDeadline_BoundsAStalledRecipientWithALongerCommandTimeout()
    {
        // Complete the initial exchange synchronously, then stop replying. Delaying EHLO
        // on a real socket leaves too little scheduling headroom on a busy CI worker.
        using var stream = new TranscriptStream(Prefix, stallAtEnd: true);
        var settings = Settings();
        settings.Smtp.CommandTimeoutSeconds = 10;
        settings.Smtp.SessionTimeoutSeconds = 1;

        var result = await Probe(settings, new StreamFactory(stream))
            .ProbeAsync("mx.example.test", "person@example.test")
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SmtpMailboxStatus.Timeout, result.Status);
        Assert.Equal(SmtpCommand.RcptTo, result.SessionEvidence!.FailedStage);
        Assert.True(result.SessionEvidence.Duration < TimeSpan.FromSeconds(4));
        Assert.Contains("MAIL FROM", stream.Commands);
        Assert.Contains("RCPT TO:<person@example.test>", stream.Commands);
        Assert.DoesNotContain("RSET", stream.Commands);
    }

    [Theory]
    [InlineData("greeting")]
    [InlineData("tls")]
    [InlineData("cleanup")]
    [InlineData("quit")]
    public async Task SlowPeer_IsBounded(string stage)
    {
        await using var server = new LoopbackServer(async (stream, token) =>
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            if (stage == "greeting")
            {
                while (true) { await Send(stream, "2", token); await Task.Delay(100, token); }
            }
            await Send(stream, "220 ready\r\n", token);
            Assert.StartsWith("EHLO", await reader.ReadLineAsync(token));
            if (stage == "tls")
            {
                await Send(stream, "250-mx\r\n250 STARTTLS\r\n", token);
                Assert.Equal("STARTTLS", await reader.ReadLineAsync(token));
                await Send(stream, "220 ready\r\n", token);
            }
            else
            {
                await Send(stream, "250 mx\r\n", token);
                Assert.StartsWith("MAIL FROM", await reader.ReadLineAsync(token));
                await Send(stream, "250 sender\r\n", token);
                Assert.StartsWith("RCPT TO", await reader.ReadLineAsync(token));
                await Send(stream, "250 recipient\r\n", token);
                Assert.Equal("RSET", await reader.ReadLineAsync(token));
                if (stage == "quit")
                {
                    await Send(stream, "250 reset\r\n", token);
                    Assert.Equal("QUIT", await reader.ReadLineAsync(token));
                }
            }
            await Task.Delay(Timeout.Infinite, token);
        });
        var settings = Settings();
        settings.Smtp.CommandTimeoutSeconds = 1;
        settings.Smtp.SessionTimeoutSeconds = 10;
        settings.Smtp.CleanupTimeoutSeconds = 1;
        var result = await Probe(settings, server).ProbeAsync("mx.example.test", "person@example.test").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(stage is "cleanup" or "quit" ? SmtpMailboxStatus.Accepted : SmtpMailboxStatus.Timeout, result.Status);
        Assert.True(result.SessionEvidence!.Duration < TimeSpan.FromSeconds(4));
        if (stage == "tls") Assert.Equal(SmtpCommand.StartTls, result.SessionEvidence.FailedStage);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesDuringCleanup()
    {
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackServer(async (stream, token) =>
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            await Send(stream, "220 ready\r\n", token);
            await reader.ReadLineAsync(token); await Send(stream, "250 mx\r\n", token);
            await reader.ReadLineAsync(token); await Send(stream, "250 sender\r\n", token);
            await reader.ReadLineAsync(token); await Send(stream, "250 recipient\r\n", token);
            Assert.Equal("RSET", await reader.ReadLineAsync(token));
            cleanup.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = Probe(Settings(), server).ProbeAsync("mx.example.test", "person@example.test", cancellation.Token);
        await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task DisconnectDuringHandshake_ProducesTlsFailureWithoutRecipientEvidence()
    {
        await using var server = new LoopbackServer(async (stream, token) =>
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            await Send(stream, "220 ready\r\n", token);
            Assert.StartsWith("EHLO", await reader.ReadLineAsync(token));
            await Send(stream, "250-mx\r\n250 STARTTLS\r\n", token);
            Assert.Equal("STARTTLS", await reader.ReadLineAsync(token));
            await Send(stream, "220 ready\r\n", token);
        });
        var result = await Probe(Settings(), server).ProbeAsync("mx.example.test", "person@example.test");
        Assert.Equal(SmtpCommand.StartTls, result.SessionEvidence!.FailedStage);
        Assert.False(result.SessionEvidence.RecipientStageReached);
        Assert.Contains("STARTTLS", result.Evidence!.SanitizedResponse);
    }

    [Fact]
    public async Task FailedPostTlsEhlo_PreservesTlsProvenanceAndStops()
    {
        using var certificate = Certificate("mx.example.test");
        await using var server = new LoopbackServer((stream, token) =>
            TlsConversation(stream, certificate, true, false, token, "500 EHLO refused\r\n"));
        var result = await Probe(Settings(), server, Trust(certificate)).ProbeAsync("mx.example.test", "person@example.test");
        Assert.True(result.SessionEvidence!.TlsUsed);
        Assert.Equal(SmtpCommand.Ehlo, result.SessionEvidence.FailedStage);
        Assert.False(result.SessionEvidence.SmtpUtf8Advertised);
        Assert.False(result.SessionEvidence.RecipientStageReached);
    }

    private static async Task TlsConversation(Stream stream, X509Certificate2 certificate, bool before, bool after,
        CancellationToken token, string? postTlsReply = null)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        await Send(stream, "220 mx.example.test\r\n", token);
        Assert.StartsWith("EHLO", await reader.ReadLineAsync(token));
        await Send(stream, "250-mx.example.test\r\n" + (before ? "250-SMTPUTF8\r\n" : "") + "250 STARTTLS\r\n", token);
        Assert.Equal("STARTTLS", await reader.ReadLineAsync(token));
        await Send(stream, "220 ready\r\n", token);
        using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, token);
        using var secureReader = new StreamReader(tls, leaveOpen: true);
        var ehlo = await secureReader.ReadLineAsync(token);
        if (ehlo is null) return; // Client certificate validation failed.
        Assert.StartsWith("EHLO", ehlo);
        await Send(tls, postTlsReply ?? "250-mx.example.test\r\n250 " + (after ? "SMTPUTF8" : "SIZE 1000") + "\r\n", token);
        if (!after) { Assert.Null(await secureReader.ReadLineAsync(token)); return; }
        Assert.EndsWith(" SMTPUTF8", await secureReader.ReadLineAsync(token));
        await Send(tls, "250 sender\r\n", token);
        Assert.Equal("RCPT TO:<ü@example.test>", await secureReader.ReadLineAsync(token));
        await Send(tls, "250 recipient\r\n", token);
        Assert.Equal("RSET", await secureReader.ReadLineAsync(token));
        await Send(tls, "250 reset\r\n", token);
        Assert.Equal("QUIT", await secureReader.ReadLineAsync(token));
        await Send(tls, "221 bye\r\n", token);
    }

    private static async Task Send(Stream stream, string value, CancellationToken token) =>
        await stream.WriteAsync(Encoding.UTF8.GetBytes(value), token);

    private static X509Certificate2 Certificate(string hostname, bool expired = false)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + hostname, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(hostname);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(expired ? -1 : 1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    private static X509ChainPolicy Trust(X509Certificate2 certificate)
    {
        var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        policy.CustomTrustStore.Add(certificate);
        return policy;
    }

    private static EmailValidationOptions Settings() => new()
    {
        Smtp = new() { Enabled = true, RetryCount = 0, MaxMxAttempts = 1 },
        CatchAll = new() { Enabled = false }
    };

    private static SmtpMailboxProbe Probe(EmailValidationOptions settings, ISmtpConnectionFactory factory,
        X509ChainPolicy? trust = null, IFleetSmtpProbeBudget? budget = null)
    {
        var options = Options.Create(settings);
        var classifier = new SmtpResponseClassificationOrchestrator(new CanonicalSmtpResponseClassifierAdapter(),
            new SmtpResponseClassifier(options), new SmtpResponseDecisionPolicy(options), new SmtpResponseIntelligenceMetrics(), options);
        return new(options, NullLogger<SmtpMailboxProbe>.Instance, new AllowThrottle(), classifier,
            new SmtpSessionBudget(), new ProviderPolicyResolver(options), new Selector(), new Health(), factory, fleetBudget: budget)
        { TlsCertificateChainPolicy = trust };
    }

    private sealed class Routing : IDnsMailResolver
    {
        public Task<DnsLookupResult> ResolveAsync(string domain, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DnsLookupResult(DnsStatus.Success, true, [new MxRecord(0, "mx.example.test")], false, TimeSpan.Zero));
    }

    private sealed class Selector : IOutboundIdentitySelector
    {
        public Task<OutboundIdentitySelectionResult> SelectAsync(OutboundIdentitySelectionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OutboundIdentitySelectionResult(new OutboundIdentity
            {
                IdentityId = "test", Address = IPAddress.Loopback, ProbeSenderAddress = "probe@sender.test",
                InterfaceName = "loopback", ExpectedPtrHostName = "sender.test", EhloHostName = "sender.test"
            }, OutboundIdentitySelectionReason.Selected, "test", "v1", []));
    }

    private sealed class Health : IOutboundIdentityHealthStore
    {
        public Task<OutboundIdentityHealth> GetAsync(string identityId, MailProvider provider, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OutboundIdentityHealth(identityId, provider, OutboundIdentityHealthState.Healthy));
        public Task RecordAsync(OutboundIdentityOutcome outcome, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AllowThrottle : ISmtpProbeThrottle
    {
        public ValueTask<ISmtpThrottleLease> AcquireAsync(SmtpThrottleContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ISmtpThrottleLease>(new Lease());
        private sealed class Lease : ISmtpThrottleLease
        {
            public bool Acquired => true;
            public DateTimeOffset? RetryAfter => null;
            public string? Reason => null;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class StreamFactory(Stream stream) : ISmtpConnectionFactory
    {
        public Task<ISmtpConnection> ConnectAsync(string host, int port, IPAddress localAddress, CancellationToken cancellationToken = default) =>
            Task.FromResult<ISmtpConnection>(new Connection(stream));
    }

    private sealed class Connection(Stream stream, TcpClient? client = null) : ISmtpConnection
    {
        public Stream Stream => stream;
        public string LocalAddress => IPAddress.Loopback.ToString();
        public ValueTask DisposeAsync() { stream.Dispose(); client?.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class LoopbackServer : ISmtpConnectionFactory, IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
        public Task Completion { get; }
        public LoopbackServer(Func<Stream, CancellationToken, Task> conversation)
        {
            _listener.Start();
            Completion = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                try { await conversation(client.GetStream(), _stop.Token); }
                catch (Exception exception) when (exception is IOException or AuthenticationException or OperationCanceledException) { }
            });
        }
        public async Task<ISmtpConnection> ConnectAsync(string host, int port, IPAddress localAddress, CancellationToken cancellationToken = default)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port, cancellationToken);
            return new Connection(client.GetStream(), client);
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try { await Completion; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }

    private sealed class TranscriptStream(string transcript, bool stallAtEnd = false) : Stream
    {
        private readonly MemoryStream _input = new(Encoding.UTF8.GetBytes(transcript));
        private readonly MemoryStream _output = new();
        public string Commands => Encoding.UTF8.GetString(_output.ToArray());
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stallAtEnd && _input.Position == _input.Length)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            return await _input.ReadAsync(buffer, cancellationToken);
        }
        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _output.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
