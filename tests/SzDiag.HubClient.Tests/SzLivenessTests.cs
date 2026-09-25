using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.HubClient.Tests;

public class SzLivenessTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static SessionInfo Session(SessionStatus status, TimeSpan silentFor, string? revertNote = null)
        => new("161432", "10.0.0.5", "PC-1", status, Now.AddHours(-2), Now - silentFor, RevertNote: revertNote);

    [Fact]
    public void Online_IsOnline()
        => Assert.Equal(SzLivenessState.Online, SzLiveness.Classify(Session(SessionStatus.Online, TimeSpan.Zero), Now));

    [Fact]
    public void OfflineShortSilence_IsLagSuspected()
        => Assert.Equal(SzLivenessState.LagSuspected,
            SzLiveness.Classify(Session(SessionStatus.Offline, TimeSpan.FromMinutes(9)), Now));

    [Fact]
    public void OfflineAtThreshold_IsNoContact()
        => Assert.Equal(SzLivenessState.NoContact,
            SzLiveness.Classify(Session(SessionStatus.Offline, SzLiveness.LikelyFailureThreshold), Now));

    [Fact]
    public void RevertNote_WinsOverOnline()
        => Assert.Equal(SzLivenessState.RevertFailed,
            SzLiveness.Classify(Session(SessionStatus.Online, TimeSpan.Zero, "sshd не снят"), Now));
}
