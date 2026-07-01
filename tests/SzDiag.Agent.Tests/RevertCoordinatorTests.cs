using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class RevertCoordinatorTests
{
    [Fact]
    public async Task Trigger_RunsActionOnce()
    {
        var count = 0;
        var coord = new RevertCoordinator(() => { count++; return Task.CompletedTask; });

        await coord.TriggerAsync();
        await coord.TriggerAsync();
        await coord.TriggerAsync();

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Trigger_Concurrent_RunsActionOnce()
    {
        var count = 0;
        var coord = new RevertCoordinator(async () =>
        {
            await Task.Delay(50);
            Interlocked.Increment(ref count);
        });

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => coord.TriggerAsync()));

        Assert.Equal(1, count);
    }
}
