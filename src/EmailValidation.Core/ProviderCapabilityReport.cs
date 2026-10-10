namespace EmailValidation.Core;

/// <summary>Aggregate shadow evidence only; this report never approves or promotes a policy.</summary>
public static class ProviderCapabilityReport
{
    public static IReadOnlyList<ProviderCapabilityReportRow> Summarize(IEnumerable<EmailValidationFeatureSnapshot> snapshots) =>
        snapshots.DistinctBy(snapshot => snapshot.SnapshotId).Where(snapshot => snapshot.ProviderCapabilities is not null)
            .GroupBy(snapshot => (snapshot.ProviderCapabilities!.ProfileKey,
                snapshot.ProviderCapabilities.PolicyVersion, snapshot.ProviderCapabilities.PolicyHash,
                snapshot.ProviderCapabilities.Mode, snapshot.ProviderCapabilities.Applied))
            .OrderBy(group => group.Key.ProfileKey, StringComparer.Ordinal)
            .ThenBy(group => group.Key.PolicyHash, StringComparer.Ordinal)
            .Select(group => new ProviderCapabilityReportRow(group.Key.ProfileKey, group.Key.PolicyVersion,
                group.Key.PolicyHash, group.Key.Mode, group.Key.Applied, group.Count(),
                group.Count(snapshot => snapshot.ProviderCapabilities!.WouldSkipMailbox),
                group.Count(snapshot => snapshot.ProviderCapabilities!.WouldSkipControls),
                group.Count(snapshot => snapshot.ProviderCapabilities!.ShadowStatusDisagrees is not null),
                group.Count(snapshot => snapshot.ProviderCapabilities!.ShadowStatusDisagrees == true),
                group.Where(snapshot => snapshot.ProviderCapabilities!.UnknownResponseFingerprint is not null)
                    .GroupBy(snapshot => snapshot.ProviderCapabilities!.UnknownResponseFingerprint!)
                    .ToDictionary(item => item.Key, item => item.Count(), StringComparer.Ordinal)))
            .ToArray();
}

public sealed record ProviderCapabilityReportRow(
    string ProfileKey, string PolicyVersion, string PolicyHash, ProviderCapabilityMode Mode, bool Applied,
    int Attempts, int ProposedMailboxSkips, int ProposedControlSkips, int ComparedStatuses,
    int StatusDisagreements, IReadOnlyDictionary<string, int> UnknownResponseFingerprints);
