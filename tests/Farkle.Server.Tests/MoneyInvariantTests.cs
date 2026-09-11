using System.Text.Json;
using Casino.Server;
using Farkle.Game;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace Farkle.Server.Tests;

/// <summary>
/// What a Farkle table is allowed to do to two players' money.
///
/// **Written before the settlement they check**, which is the instruction this repo has
/// carried since Roulette. The model they pin, which is decision #2 in the work order:
///
/// - A fixed stake per player leaves at sit-down and is recorded against that session.
/// - The winner is paid both stakes. The loser's row is released with nothing paid.
/// - **The forfeit rule**: standing up mid-match pays both stakes to the player who
///   stayed. Its own rule, its own test.
/// - Leaving a table nobody joined is not a forfeit: the stake comes straight back.
/// - Against the house the human's stake is real and the bot's is notional: a win pays
///   two stakes, a loss pays nothing.
/// - A row with no table behind it is a server that died mid-match, and it is refunded
///   once, on next contact, and never while the table is live.
///
/// The invariant under all of them: **across both players, the money is zero-sum**, and
/// every stash moves by exactly what the rules say it should, measured from the
/// individual movements rather than the closing balance.
/// </summary>
public class MoneyInvariantTests
{
    private const int Stake = 100_000;
    private const int Stash = 5_000_000;

    private readonly MongoId _alice = new();
    private readonly MongoId _bob = new();

    private readonly FakeBank _bank = new();
    private readonly FakeEscrow _escrow = new();
    private readonly FakeProfiles _profiles = new();
    private readonly SharedFarkleStore _store = new();
    private readonly FakeOutputs _outputs = new();
    private readonly SharedFarkleService _service;

    public MoneyInvariantTests()
    {
        _bank.Seed(_alice, Stash);
        _bank.Seed(_bob, Stash);
        _profiles.Names[_alice.ToString()] = "Alice";
        _profiles.Names[_bob.ToString()] = "Bob";

        _service = Build(new FakeRandom(2026));
    }

    private SharedFarkleService Build(FakeRandom random) => new(
        _bank,
        new TableGate(),
        new SessionGate(),
        _store,
        _profiles,
        _escrow,
        _outputs,
        new CasinoSocket(new QuietLogger<CasinoSocket>()),
        random,
        new QuietLog());

    private static OpenTableRequest Open(bool vsBot = false, long stake = Stake, string bot = "") =>
        new() { Stake = stake, VsBot = vsBot, Bot = bot };

    private static ItemEventRouterResponse Output() => new();

    private string TableId() => _store.All.Single().Id;

    private int Balance(MongoId who) => _bank.GetBalance(who, Wallet.Roubles);

    // ---- sitting down ----------------------------------------------------------------

    [Fact]
    public async Task OpeningTakesTheStakeOnceAndRecordsIt()
    {
        var opened = await _service.OpenAsync(Open(), _alice, Output());

        Assert.True(opened.Ok, opened.Error);
        Assert.Equal(0, opened.YourSeat);
        Assert.Equal("WaitingForOpponent", opened.Match!.Phase);
        Assert.Equal(1, _bank.Debits);
        Assert.Equal(Stash - Stake, Balance(_alice));
        Assert.Equal(Stake, _escrow.Get(_alice)!.Amount);
        Assert.Equal(1, _profiles.Saves);
    }

    [Fact]
    public async Task JoiningTakesTheOtherStakeAndStartsTheMatch()
    {
        await _service.OpenAsync(Open(), _alice, Output());
        var joined = await _service.JoinAsync(TableId(), _bob, Output());

        Assert.True(joined.Ok, joined.Error);
        Assert.Equal(1, joined.YourSeat);
        Assert.Equal("Rolling", joined.Match!.Phase);
        Assert.Equal(0, joined.Match.CurrentSeat);
        Assert.Equal("Alice", joined.Match.Seats[0].Name);
        Assert.Equal("Bob", joined.Match.Seats[1].Name);
        Assert.Equal(2, _bank.Debits);
        Assert.Equal(Stash - Stake, Balance(_bob));
        Assert.Equal(2, _escrow.Held);
        Assert.Equal(-2 * Stake, _bank.Moved);
    }

    [Fact]
    public async Task AStakeThatCannotBeAffordedTakesNothingAndLeavesNoRow()
    {
        _bank.Seed(_alice, Stake - 1);

        var opened = await _service.OpenAsync(Open(), _alice, Output());

        Assert.False(opened.Ok);
        Assert.Contains("costs", opened.Error);
        Assert.Equal(0, _bank.Debits);
        Assert.Equal(1, _bank.RefusedDebits);
        Assert.Equal(0, _escrow.Held);
        Assert.Empty(_store.All);

        // The claim was released with the refusal: they can sit down once they can pay.
        _bank.Seed(_alice, Stash);
        Assert.True((await _service.OpenAsync(Open(), _alice, Output())).Ok);
    }

    [Theory]
    [InlineData(9_999)]
    [InlineData(1_000_001)]
    [InlineData(0)]
    [InlineData(-50_000)]
    public async Task AStakeOutsideTheLimitsIsRefusedBeforeAnythingMoves(long stake)
    {
        var opened = await _service.OpenAsync(Open(stake: stake), _alice, Output());

        Assert.False(opened.Ok);
        Assert.Equal(0, _bank.Debits);
        Assert.Equal(0, _escrow.Held);
    }

    [Fact]
    public async Task NobodyIsAtTwoTables()
    {
        await _service.OpenAsync(Open(), _alice, Output());
        var again = await _service.OpenAsync(Open(), _alice, Output());

        Assert.False(again.Ok);
        Assert.Contains("already at a table", again.Error);
        Assert.Equal(1, _bank.Debits);
        Assert.Single(_store.All);

        await _service.OpenAsync(Open(), _bob, Output());
        var join = await _service.JoinAsync(_store.For(_bob)!.Id, _alice, Output());

        Assert.False(join.Ok);
        Assert.Contains("already at a table", join.Error);
        Assert.Equal(2, _bank.Debits);
    }

    [Fact]
    public async Task ABotTableCannotBeJoined()
    {
        await _service.OpenAsync(Open(vsBot: true, bot: "Kolya"), _alice, Output());
        var table = _store.All.Single();

        var join = await _service.JoinAsync(table.Id, _bob, Output());

        Assert.False(join.Ok);
        Assert.Contains("against the house", join.Error);
        Assert.Equal(1, _bank.Debits);
        Assert.Empty(_service.List());
    }

    // ---- settlement ------------------------------------------------------------------

    /// <summary>
    /// The milestone. Two people sit, play to the end, and the winner is paid both stakes
    /// into their own stash, the loser nothing; both rows released; zero-sum overall.
    /// </summary>
    [Fact]
    public async Task TheWinnerIsPaidBothStakesAndBothRowsAreReleased()
    {
        await _service.OpenAsync(Open(), _alice, Output());
        await _service.JoinAsync(TableId(), _bob, Output());

        var final = await PlayToTheEnd();

        Assert.Equal("Finished", final.Match!.Phase);
        Assert.Equal("ReachedTarget", final.Match.Ending);
        Assert.True(final.Settled);

        var winner = final.Match.Winner!.Value == 0 ? _alice : _bob;
        var loser = winner == _alice ? _bob : _alice;

        Assert.Equal(Stash + Stake, Balance(winner));
        Assert.Equal(Stash - Stake, Balance(loser));
        Assert.Equal(0, _bank.Moved);
        Assert.Equal(0, _escrow.Held);

        // Exactly three movements: two stakes out, one payout in.
        Assert.Equal(2, _bank.Debits);
        Assert.Equal(1, _bank.Credits);
        Assert.Contains((winner.ToString(), 2 * Stake), _bank.Movements);

        // Leaving afterwards moves nothing more.
        var left = await _service.LeaveAsync(winner, Output());
        Assert.True(left.Ok, left.Error);
        Assert.Equal(1, _bank.Credits);

        var leftToo = await _service.LeaveAsync(loser, Output());
        Assert.True(leftToo.Ok, leftToo.Error);
        Assert.Equal(0, _bank.Moved);
        Assert.Empty(_store.All);
    }

    /// <summary>
    /// THE FORFEIT RULE. Standing up mid-match pays both stakes to the player who stayed.
    /// Named and tested on its own, per the decision, rather than inferred from "forfeits".
    /// </summary>
    [Fact]
    public async Task AForfeitPaysBothStakesToTheRemainingPlayer()
    {
        await _service.OpenAsync(Open(), _alice, Output());
        await _service.JoinAsync(TableId(), _bob, Output());

        // A move or two, so it is a live match rather than one that never started.
        var rolled = await _service.RollAsync(_alice, Output());
        Assert.True(rolled.Ok, rolled.Error);

        var left = await _service.LeaveAsync(_bob, Output());

        Assert.True(left.Ok, left.Error);
        Assert.Contains("goes to Alice", left.Note);

        Assert.Equal(Stash + Stake, Balance(_alice));
        Assert.Equal(Stash - Stake, Balance(_bob));
        Assert.Equal(0, _bank.Moved);
        Assert.Equal(0, _escrow.Held);
        Assert.Contains((_alice.ToString(), 2 * Stake), _bank.Movements);

        var view = await _service.StateAsync(_alice, Output());
        Assert.Equal("Finished", view.Match!.Phase);
        Assert.Equal("Forfeit", view.Match.Ending);
        Assert.Equal(0, view.Match.Winner);
        Assert.True(view.Settled);
    }

    [Fact]
    public async Task LeavingATableNobodyJoinedRefundsTheStake()
    {
        await _service.OpenAsync(Open(), _alice, Output());

        var left = await _service.LeaveAsync(_alice, Output());

        Assert.True(left.Ok, left.Error);
        Assert.Contains("came back", left.Note);
        Assert.Equal(Stash, Balance(_alice));
        Assert.Equal(0, _bank.Moved);
        Assert.Equal(0, _escrow.Held);
        Assert.Empty(_store.All);

        // And they are free to sit again.
        Assert.True((await _service.OpenAsync(Open(), _alice, Output())).Ok);
    }

    [Fact]
    public async Task AgainstTheHouseAWinPaysTwoStakesAndALossPaysNothing()
    {
        var sawWin = false;
        var sawLoss = false;

        // Different dice each time until both outcomes have been seen. Fifty matches is
        // far more than enough; the bot is beatable and beats.
        for (var seed = 1; seed <= 50 && !(sawWin && sawLoss); seed++)
        {
            var bank = new FakeBank();
            var escrow = new FakeEscrow();
            var store = new SharedFarkleStore();
            bank.Seed(_alice, Stash);

            var service = new SharedFarkleService(
                bank, new TableGate(), new SessionGate(), store, _profiles, escrow, _outputs,
                new CasinoSocket(new QuietLogger<CasinoSocket>()), new FakeRandom(seed), new QuietLog());

            var opened = await service.OpenAsync(Open(vsBot: true), _alice, Output());
            Assert.True(opened.Ok, opened.Error);
            Assert.True(opened.Match!.Seats[1].IsBot);
            Assert.Equal("Rolling", opened.Match.Phase);

            var final = await PlayToTheEnd(service, _alice, null);

            Assert.Equal("Finished", final.Match!.Phase);
            Assert.True(final.Settled);
            Assert.Equal(0, escrow.Held);

            if (final.Match.Winner == 0)
            {
                sawWin = true;
                Assert.Equal(Stash + Stake, bank.GetBalance(_alice, Wallet.Roubles));
                Assert.Equal(1, bank.Credits);
            }
            else
            {
                sawLoss = true;
                Assert.Equal(Stash - Stake, bank.GetBalance(_alice, Wallet.Roubles));
                Assert.Equal(0, bank.Credits);
            }
        }

        Assert.True(sawWin, "never saw the human beat the bot in fifty matches");
        Assert.True(sawLoss, "never saw the bot win in fifty matches");
    }

    [Fact]
    public async Task LeavingABotTableMidMatchLosesTheStakeToTheHouse()
    {
        await _service.OpenAsync(Open(vsBot: true, bot: "Vanya"), _alice, Output());
        await _service.RollAsync(_alice, Output());

        var left = await _service.LeaveAsync(_alice, Output());

        Assert.True(left.Ok, left.Error);
        Assert.Contains("stays with the house", left.Note);
        Assert.Equal(Stash - Stake, Balance(_alice));
        Assert.Equal(0, _bank.Credits);
        Assert.Equal(0, _escrow.Held);
        Assert.Empty(_store.All);
    }

    // ---- stranded stakes -------------------------------------------------------------

    [Fact]
    public async Task AStrandedStakeComesBackOnPingExactlyOnce()
    {
        _escrow.Strand(_alice, 250_000);

        var ping = await _service.PingAsync(_alice, Output());

        Assert.Contains("returned", ping.Note);
        Assert.Equal(Stash + 250_000, Balance(_alice));
        Assert.Equal(0, _escrow.Held);

        var again = await _service.PingAsync(_alice, Output());

        Assert.Null(again.Note);
        Assert.Equal(Stash + 250_000, Balance(_alice));
        Assert.Equal(1, _bank.Credits);
    }

    [Fact]
    public async Task ALiveStakeIsNotRefundedByPing()
    {
        await _service.OpenAsync(Open(), _alice, Output());
        await _service.JoinAsync(TableId(), _bob, Output());

        var ping = await _service.PingAsync(_alice, Output());

        Assert.Null(ping.Note);
        Assert.Equal(TableId(), ping.YourTable);
        Assert.Equal(0, _bank.Credits);
        Assert.Equal(2, _escrow.Held);
    }

    [Fact]
    public async Task AStrandedStakeIsReturnedBeforeANewOneIsRecorded()
    {
        _escrow.Strand(_alice, 250_000);

        var opened = await _service.OpenAsync(Open(), _alice, Output());

        Assert.True(opened.Ok, opened.Error);
        Assert.Contains("returned", opened.Note);
        Assert.Equal(Stash + 250_000 - Stake, Balance(_alice));
        Assert.Equal(Stake, _escrow.Get(_alice)!.Amount);
    }

    // ---- the table, not the money ----------------------------------------------------

    [Fact]
    public async Task BothSeatsSeeTheSameTable()
    {
        await _service.OpenAsync(Open(), _alice, Output());
        await _service.JoinAsync(TableId(), _bob, Output());
        await _service.RollAsync(_alice, Output());

        var hers = await _service.StateAsync(_alice, Output());
        var his = await _service.StateAsync(_bob, Output());

        Assert.Equal(JsonSerializer.Serialize(hers.Match), JsonSerializer.Serialize(his.Match));
        Assert.Equal(0, hers.YourSeat);
        Assert.Equal(1, his.YourSeat);
    }

    [Fact]
    public async Task OutOfTurnMovesAreRefusedAndChangeNothing()
    {
        await _service.OpenAsync(Open(), _alice, Output());
        await _service.JoinAsync(TableId(), _bob, Output());

        var before = JsonSerializer.Serialize((await _service.StateAsync(_bob, Output())).Match);
        var rolled = await _service.RollAsync(_bob, Output());

        Assert.False(rolled.Ok);
        Assert.Equal("It is not your turn.", rolled.Error);
        Assert.Equal(before, JsonSerializer.Serialize(rolled.Match));
    }

    [Fact]
    public async Task AnIllegalKeepIsRefusedWithTheRealView()
    {
        await _service.OpenAsync(Open(), _alice, Output());
        await _service.JoinAsync(TableId(), _bob, Output());
        await _service.RollAsync(_alice, Output());

        var kept = await _service.KeepAsync(new KeepRequest { Indices = [0, 1, 2, 3, 4, 5, 6] }, _alice, Output());

        Assert.False(kept.Ok);
        Assert.NotNull(kept.Match);
        Assert.Equal("Choosing", kept.Match!.Phase);
    }

    [Fact]
    public async Task TheQuietSeatYieldsAndTheTableMovesOn()
    {
        await _service.OpenAsync(Open(), _alice, Output());
        await _service.JoinAsync(TableId(), _bob, Output());

        var table = _store.All.Single();
        table.LastSeenUtc[_alice.ToString()] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120;

        var view = await _service.StateAsync(_bob, Output());

        Assert.True(view.Ok, view.Error);
        Assert.Equal(1, view.Match!.CurrentSeat);
        Assert.Contains(view.Match.LastTurn, e => e.Kind == "Yielded" && e.Seat == 0);
        Assert.Equal(0, _bank.Credits);
    }

    [Fact]
    public async Task TheAbsentSeatForfeitsAndTheOtherIsPaid()
    {
        await _service.OpenAsync(Open(), _alice, Output());
        await _service.JoinAsync(TableId(), _bob, Output());

        var table = _store.All.Single();
        table.LastSeenUtc[_alice.ToString()] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 400;

        var view = await _service.StateAsync(_bob, Output());

        Assert.Equal("Finished", view.Match!.Phase);
        Assert.Equal("Forfeit", view.Match.Ending);
        Assert.Equal(1, view.Match.Winner);
        Assert.Equal(Stash + Stake, Balance(_bob));
        Assert.Equal(Stash - Stake, Balance(_alice));
        Assert.Equal(0, _escrow.Held);
    }

    /// <summary>
    /// A host who opened a table and closed the game. The next person to try the chair
    /// is what notices: the table goes, the host's stake goes back to the host, and the
    /// joiner has paid nothing.
    /// </summary>
    [Fact]
    public async Task AnUnattendedOpenTableIsClosedAndRefundedWhenSomebodyTriesIt()
    {
        await _service.OpenAsync(Open(), _alice, Output());

        var hers = _store.For(_alice)!;
        hers.LastSeenUtc[_alice.ToString()] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 400;

        var join = await _service.JoinAsync(hers.Id, _bob, Output());

        Assert.False(join.Ok);
        Assert.Contains("gone", join.Error);
        Assert.Equal(Stash, Balance(_alice));
        Assert.Equal(Stash, Balance(_bob));
        Assert.Equal(1, _bank.Debits);
        Assert.Equal(1, _bank.Credits);
        Assert.Equal(0, _escrow.Held);
        Assert.Empty(_store.All);

        // Neither is held anywhere afterwards.
        Assert.True((await _service.OpenAsync(Open(), _bob, Output())).Ok);
        Assert.True((await _service.OpenAsync(Open(), _alice, Output())).Ok);
    }

    // ---- driving a match -------------------------------------------------------------

    private Task<FarkleResponse> PlayToTheEnd() => PlayToTheEnd(_service, _alice, _bob);

    /// <summary>
    /// Plays the humans with a plain policy -- take the best keep, bank once the turn is
    /// worth 350 or the dice are down to two -- until the match ends. What is being
    /// tested is the money, not the play.
    /// </summary>
    private static async Task<FarkleResponse> PlayToTheEnd(SharedFarkleService service, MongoId seat0, MongoId? seat1)
    {
        var view = await service.StateAsync(seat0, Output());

        for (var guard = 0; guard < 5000; guard++)
        {
            var match = view.Match!;

            if (match.Phase == "Finished")
            {
                return view;
            }

            var who = match.CurrentSeat == 0 ? seat0 : seat1;
            Assert.NotNull(who);

            if (match.Phase == "Rolling")
            {
                var bank = match.CanBank && (match.TurnScore >= 350 || match.DiceInHand <= 2);
                view = bank ? await service.BankAsync(who.Value, Output()) : await service.RollAsync(who.Value, Output());
            }
            else
            {
                var best = match.Keeps.OrderByDescending(k => k.Points).ThenBy(k => k.Indices.Count).First();
                view = await service.KeepAsync(new KeepRequest { Indices = best.Indices.ToList() }, who.Value, Output());
            }

            Assert.True(view.Ok, view.Error);
        }

        throw new InvalidOperationException("the match never ended");
    }
}
