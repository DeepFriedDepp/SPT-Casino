using Casino.Server;
using SPTarkov.Server.Core.Models.Common;

namespace Blackjack.Server.Tests;

/// <summary>
/// The table gate, and the lock order it exists to make safe.
///
/// These live in Blackjack.Server.Tests only because it is the test project that already
/// references Casino.Server. Nothing here is about blackjack.
/// </summary>
public class TableGateTests
{
    private readonly TableGate _tables = new();

    private readonly SessionGate _sessions = new();

    private const string Table = "table-1";

    [Fact]
    public async Task OneTableIsTakenOneAtATime()
    {
        var secondGotIn = new TaskCompletionSource();

        var first = await _tables.EnterAsync(Table);

        var second = Task.Run(async () =>
        {
            using var _ = await _tables.EnterAsync(Table);
            secondGotIn.SetResult();
        });

        var tooEarly = await Task.WhenAny(secondGotIn.Task, Task.Delay(200));
        Assert.NotSame(secondGotIn.Task, tooEarly);

        first.Dispose();

        await second.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Two tables do not queue behind each other. If this fails, one table's slow hand
    /// stalls every other table on the server.
    /// </summary>
    [Fact]
    public async Task ADifferentTableIsNotBlocked()
    {
        using var held = await _tables.EnterAsync(Table);

        var other = _tables.EnterAsync("table-2").AsTask();
        var winner = await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(other, winner);
        (await other).Dispose();
    }

    /// <summary>
    /// THE ONE THAT MATTERS: a deal that charges somebody else's blind, racing that
    /// somebody else's own request.
    ///
    /// The naive version of this test -- two players each taking only their OWN session
    /// gate inside the table gate -- proves nothing, and I wrote it that way first. Two
    /// different players never contend on a session gate, so the two orders behave
    /// identically and the test passes whichever way round you write the locks.
    ///
    /// The real hazard needs a second SHARED resource, and a poker table has an obvious
    /// one: **posting blinds charges a player who did not send the request.** So Alice's
    /// deal takes the table and then reaches for Bob's session gate. If Bob's own request
    /// has meanwhile taken Bob's session gate and is waiting for the table, neither can
    /// move -- the textbook cycle, and it is reachable in ordinary play rather than only
    /// under stress.
    ///
    /// A deadlock cannot be asserted directly, so this asserts its observable
    /// consequence: with the correct order everything finishes far inside the 30-second
    /// gate. Written the other way round it sits there until the gate gives up, and the
    /// WaitAsync below fails -- confirmed by doing exactly that.
    /// </summary>
    [Fact]
    public async Task ADealThatChargesAnotherPlayersBlindDoesNotDeadlockAgainstThem()
    {
        var alice = new MongoId();
        var bob = new MongoId();
        var done = 0;

        // Alice deals: the table, then every seated player she has to charge.
        async Task Deal()
        {
            for (var hand = 0; hand < 20; hand++)
            {
                using var table = await _tables.EnterAsync(Table);

                using (await _sessions.EnterAsync(alice))
                {
                    await Task.Yield();
                }

                // Bob's blind, taken by Alice's request. This is the line that makes the
                // order matter.
                using (await _sessions.EnterAsync(bob))
                {
                    await Task.Yield();
                }
            }

            Interlocked.Increment(ref done);
        }

        // Bob acts on his own account, same order: table first, then himself.
        async Task Act()
        {
            for (var hand = 0; hand < 20; hand++)
            {
                using var table = await _tables.EnterAsync(Table);
                using var player = await _sessions.EnterAsync(bob);

                await Task.Yield();
            }

            Interlocked.Increment(ref done);
        }

        await Task.WhenAll(Deal(), Act()).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, Volatile.Read(ref done));
    }

    /// <summary>
    /// A player's own gate is still theirs while they sit at a shared table -- so the
    /// blackjack tab open in their second client is not queueing behind somebody else's
    /// poker hand, only behind their own.
    /// </summary>
    [Fact]
    public async Task HoldingATableDoesNotHoldAnyPlayersSession()
    {
        using var table = await _tables.EnterAsync(Table);

        var player = _sessions.EnterAsync(new MongoId()).AsTask();
        var winner = await Task.WhenAny(player, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(player, winner);
        (await player).Dispose();
    }
}
