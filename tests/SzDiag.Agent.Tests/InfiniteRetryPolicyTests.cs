using Microsoft.AspNetCore.SignalR.Client;
using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class InfiniteRetryPolicyTests
{
    [Fact]
    public void DelayFor_GrowsThenHoldsAtCeiling()
    {
        Assert.Equal(TimeSpan.Zero, InfiniteRetryPolicy.DelayFor(0));
        Assert.Equal(TimeSpan.FromSeconds(2), InfiniteRetryPolicy.DelayFor(1));
        Assert.Equal(TimeSpan.FromSeconds(5), InfiniteRetryPolicy.DelayFor(2));
        Assert.Equal(TimeSpan.FromSeconds(10), InfiniteRetryPolicy.DelayFor(3));
        Assert.Equal(TimeSpan.FromSeconds(20), InfiniteRetryPolicy.DelayFor(4));
        Assert.Equal(InfiniteRetryPolicy.MaxDelay, InfiniteRetryPolicy.DelayFor(5));
        Assert.Equal(InfiniteRetryPolicy.MaxDelay, InfiniteRetryPolicy.DelayFor(500));
    }

    [Fact]
    public void NextRetryDelay_NeverGivesUp()
    {
        var policy = new InfiniteRetryPolicy();

        // Дефолтная политика SignalR на этих номерах уже вернула бы null (соединение
        // закрывается навсегда) — ровно то, из-за чего потерялась живая СЗ 162003.
        foreach (var attempt in new long[] { 0, 3, 4, 10, 1000, 100_000 })
        {
            var delay = policy.NextRetryDelay(Context(attempt));
            Assert.NotNull(delay);
            Assert.True(delay!.Value >= TimeSpan.Zero);
        }
    }

    [Fact]
    public void NextRetryDelay_StaysWithinJitterBandAroundStep()
    {
        var policy = new InfiniteRetryPolicy();

        for (var i = 0; i < 200; i++)
        {
            var delay = policy.NextRetryDelay(Context(9))!.Value;   // шаг с потолком 30 с
            Assert.InRange(delay,
                InfiniteRetryPolicy.MaxDelay * 0.8, InfiniteRetryPolicy.MaxDelay * 1.2);
        }
    }

    [Fact]
    public void NextRetryDelay_FirstAttemptIsImmediate()
    {
        // Джиттер на нулевой паузе не нужен: первая попытка сразу, иначе к каждому обрыву
        // добавилась бы задержка на ровном месте.
        Assert.Equal(TimeSpan.Zero, new InfiniteRetryPolicy().NextRetryDelay(Context(0)));
    }

    private static RetryContext Context(long previousRetryCount) => new()
    {
        PreviousRetryCount = previousRetryCount,
        ElapsedTime = TimeSpan.FromSeconds(previousRetryCount),
        RetryReason = new Exception("hub недоступен"),
    };
}
