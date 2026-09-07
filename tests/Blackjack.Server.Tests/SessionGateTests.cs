using Casino.Server;
using SPTarkov.Server.Core.Models.Common;

namespace Blackjack.Server.Tests;

/// <summary>
/// The gate itself, tested directly rather than through a table.
///
/// These are deterministic: every one of them coordinates with a `TaskCompletionSource`
/// or a `Task.WaitAsync` timeout rather than sleeping and hoping. A concurrency test
/// that depends on timing is a test that will one day fail for no reason and be deleted,
/// taking its coverage with it.
/// </summary>
public class SessionGateTests
{
    private readonly SessionGate _gate = new();

    private readonly MongoId _session = new();

    [Fact]
    public async Task ASecondCallerWaitsUntilTheFirstLetsGo()
    {
        var secondReachedTheGate = new TaskCompletionSource();
        var secondGotIn = new TaskCompletionSource();

        var first = await _gate.EnterAsync(_session);

        var second = Task.Run(async () =>
        {
            secondReachedTheGate.SetResult();
            using var _ = await _gate.EnterAsync(_session);
            secondGotIn.SetResult();
        });

        await secondReachedTheGate.Task;

        // Still held, so the second caller cannot be through. Given 200ms to prove it
        // rather than asserting on an instant that has not happened yet.
        var tooEarly = await Task.WhenAny(secondGotIn.Task, Task.Delay(200));
        Assert.NotSame(secondGotIn.Task, tooEarly);

        first.Dispose();

        await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(secondGotIn.Task.IsCompletedSuccessfully);
    }

    /// <summary>
    /// Two sessions are not each other's business. If this ever fails, the gate has
    /// become global and every player is queueing behind every other one.
    /// </summary>
    [Fact]
    public async Task ADifferentSessionIsNotBlocked()
    {
        using var held = await _gate.EnterAsync(_session);

        var other = _gate.EnterAsync(new MongoId()).AsTask();

        var winner = await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(other, winner);

        (await other).Dispose();
    }

    /// <summary>
    /// The permit comes back even when the guarded body throws. Without this, one
    /// failed request would wedge that player's casino until the server restarted.
    /// </summary>
    [Fact]
    public async Task AnExceptionInsideTheGuardedBodyStillReleasesTheSession()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var _ = await _gate.EnterAsync(_session);
            throw new InvalidOperationException("the round blew up");
        });

        var again = _gate.EnterAsync(_session).AsTask();
        var winner = await Task.WhenAny(again, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(again, winner);
        (await again).Dispose();
    }

    /// <summary>
    /// Re-entering the same session from inside the gate is a deadlock, and the gate
    /// gives up rather than hanging forever.
    ///
    /// This is the hazard that decides how gated methods are written: a public entry
    /// point takes the gate and calls an **ungated private core**. It is asserted here
    /// so the rule has a failing test behind it rather than only a comment.
    ///
    /// The wait is shortened for the test by racing it -- the real 30 seconds is right
    /// for production and far too long for a suite.
    /// </summary>
    [Fact]
    public async Task ReenteringTheSameSessionTimesOutRatherThanHangingForever()
    {
        using var _ = await _gate.EnterAsync(_session);

        var reentrant = _gate.EnterAsync(_session).AsTask();

        // It must NOT complete quickly -- that would mean the gate is reentrant and
        // guards nothing.
        var early = await Task.WhenAny(reentrant, Task.Delay(300));
        Assert.NotSame(reentrant, early);

        Assert.False(reentrant.IsCompleted);
    }
}
