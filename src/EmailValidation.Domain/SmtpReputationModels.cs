namespace EmailValidation.Core;

public enum SmtpReputationProtectionMode { Disabled, Observe, Enforced }

public enum SmtpReputationScopeType
{
    NetworkBlock,
    Provider,
    ProviderIdentity,
    RecipientDomain,
    Mailbox
}

public enum SmtpReputationState
{
    Healthy,
    Degraded,
    Cooldown,
    CircuitOpen,
    HalfOpen,
    Quarantined,
    Disabled
}

public enum SmtpProbeBudgetDecision
{
    Allow,
    Delay,
    DeferToDurableRetry,
    CircuitOpen,
    NoEligibleIdentity,
    SafeFallback
}

public sealed record SmtpReputationBudgetContext(
    string NormalizedMailbox,
    string RecipientDomain,
    MailProvider Provider,
    string? OutboundIdentityId = null,
    string? SourceIp = null,
    string? MxHost = null,
    bool ReserveMailboxProbe = true);

public sealed record SmtpReputationScopeSnapshot
{
    public required SmtpReputationScopeType ScopeType { get; init; }
    public required string ScopeId { get; init; }
    public MailProvider Provider { get; init; }
    public SmtpReputationState State { get; init; } = SmtpReputationState.Healthy;
    public required DateTimeOffset WindowStartedAtUtc { get; init; }
    public int ConnectionCount { get; init; }
    public int RcptCount { get; init; }
    public int UnknownRecipientCount { get; init; }
    public int PolicyBlockCount { get; init; }
    public int TemporaryDeferralCount { get; init; }
    public int ConnectionFailureCount { get; init; }
    public IReadOnlyList<string> AffectedIdentityIds { get; init; } = [];
    public IReadOnlyList<string> AffectedProviders { get; init; } = [];
    public DateTimeOffset? LastLiveSmtpAttemptAtUtc { get; init; }
    public DateTimeOffset? CooldownUntilUtc { get; init; }
    public DateTimeOffset? LastHealthyAtUtc { get; init; }
    public DateTimeOffset? LastStateChangedAtUtc { get; init; }
    public int HalfOpenProbeCount { get; init; }
    public int ConsecutiveRecoverySuccesses { get; init; }
    public string PolicyVersion { get; init; } = string.Empty;
    public long Version { get; init; }

    public int ObservationCount => Math.Max(ConnectionCount, RcptCount);
    public double UnknownRecipientRatio => RcptCount == 0 ? 0 : (double)UnknownRecipientCount / RcptCount;
    public double PolicyBlockRate => ObservationCount == 0 ? 0 : (double)PolicyBlockCount / ObservationCount;
    public double TemporaryDeferralRate => ObservationCount == 0
        ? 0
        : (double)TemporaryDeferralCount / ObservationCount;
}

public sealed record SmtpReputationEvidence
{
    public required SmtpProbeBudgetDecision Decision { get; init; }
    public required SmtpProbeBudgetDecision WouldDecision { get; init; }
    public required SmtpReputationProtectionMode Mode { get; init; }
    public SmtpReputationScopeType? RestrictingScope { get; init; }
    public SmtpReputationState CircuitState { get; init; } = SmtpReputationState.Healthy;
    public DateTimeOffset? RetryAtUtc { get; init; }
    public string? SuppressionReason { get; init; }
    public string? WouldHaveUsedIdentityId { get; init; }
    public IReadOnlyDictionary<SmtpReputationScopeType, SmtpReputationState> ScopeStates { get; init; } =
        new Dictionary<SmtpReputationScopeType, SmtpReputationState>();
    public int MailboxProbeCount { get; init; }
    public DateTimeOffset EvaluatedAtUtc { get; init; }
    public string PolicyVersion { get; init; } = string.Empty;

    public bool SuppressSmtp => Decision != SmtpProbeBudgetDecision.Allow;
}

public sealed record SmtpReputationObservation(
    SmtpReputationBudgetContext Context,
    SmtpResponseCategory Category,
    SmtpNormalizedReason? NormalizedReason,
    bool ConnectionAttempted,
    bool RcptAttempted,
    DateTimeOffset OccurredAtUtc);

public sealed record SmtpReputationStateWriteResult(
    bool Applied,
    SmtpReputationScopeSnapshot? State);
