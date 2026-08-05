using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SzDiag.Hub;
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Hub.Tests;

public class KbBackupServiceTests
{
    private static KbBackupService NewService(IKbBackup backup, bool enabled)
    {
        var opts = new HubOptions
        {
            KbBackup = new KbBackupOptions
            {
                Enabled = enabled,
                // Крупный интервал: в тестах нас интересуют прогоны на старте и остановке,
                // а не тики таймера.
                Interval = TimeSpan.FromHours(1),
            },
        };
        return new KbBackupService(backup, Options.Create(opts), NullLogger<KbBackupService>.Instance);
    }

    [Fact]
    public async Task Disabled_NeverRunsBackup()
    {
        var backup = new FakeBackup();
        var service = NewService(backup, enabled: false);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, backup.Calls);
    }

    [Fact]
    public async Task Enabled_RunsOnStartAndOnStop()
    {
        var backup = new FakeBackup();
        var service = NewService(backup, enabled: true);

        await service.StartAsync(CancellationToken.None);
        await backup.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(2, backup.Calls);
    }

    [Fact]
    public async Task BackupThrows_ServiceSurvives()
    {
        var backup = new FakeBackup { Throw = true };
        var service = NewService(backup, enabled: true);

        await service.StartAsync(CancellationToken.None);
        await backup.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(2, backup.Calls);
    }

    private sealed class FakeBackup : IKbBackup
    {
        private int _calls;
        public bool Throw { get; init; }
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<KbBackupResult> RunAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1) FirstCall.TrySetResult();
            if (Throw) throw new InvalidOperationException("git сломался");
            return Task.FromResult(new KbBackupResult(KbBackupOutcome.NoChanges, 0, "изменений нет"));
        }
    }
}
