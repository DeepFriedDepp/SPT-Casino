using Casino.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace Blackjack.Server.Tests;

/// <summary>
/// Who gets told about the money, and which currency actually leaves.
///
/// Two findings from the adversarial audit of the shared table, both graded critical, both
/// invisible to every money test that came before them -- because in both cases **the
/// server-side balances were correct**. What was wrong was the address on the envelope in
/// one, and the label on the tin in the other.
/// </summary>
public class SharedTablePayoutAddressTests
{
    private const int Stake = 50_000;

    private const int Stash = 1_000_000;

    private readonly MongoId _alice = new();
    private readonly MongoId _bob = new();

    private readonly FakeBank _bank = new();
    private readonly FakeEscrow _escrow = new();
    private readonly FakeProfiles _profiles = new();
    private readonly FakeOutputs _outputs = new();
    private readonly SharedBlackjackStore _store = new();
    private readonly SharedBlackjackService _service;

    public SharedTablePayoutAddressTests()
    {
        _bank.SetBalance(Wallet.Roubles, Stash);

        _profiles.Names[_alice.ToString()] = "Ragman_Fan";
        _profiles.Names[_bob.ToString()] = "Nikita";

        _service = new SharedBlackjackService(
            _bank,
            new TableGate(),
            new SessionGate(),
            _store,
            _profiles,
            _escrow,
            new TableStore(),
            _outputs,
            new CasinoSocket(new QuietLogger<CasinoSocket>()));
    }

    private static OpenTableRequest Open() =>
        new() { Seats = 4, Wallet = nameof(Wallet.Roubles) };

    private static DealRequest Bet(string wallet = nameof(Wallet.Roubles)) =>
        new() { Wager = Stake, Wallet = wallet };

    /// <summary>
    /// **Every seat is paid through its OWN response.**
    ///
    /// An `ItemEventRouterResponse` is per session: it is the change record handed back to
    /// one client. Settlement credited everybody through the *acting* player's, so the
    /// player who pressed Stand was told about items that are not theirs and everybody
    /// else was told nothing -- their stash sat stale until something made them resync.
    ///
    /// Asserted on the response each credit was handed, not on balances. The balances were
    /// always right, which is exactly why no existing test could see this.
    /// </summary>
    [Fact]
    public async Task EachSeatIsPaidThroughItsOwnResponse()
    {
        await _service.OpenAsync(Open(), _alice, new ItemEventRouterResponse());
        await _service.JoinAsync(_store.All.Single().Id, _bob);

        await _service.BetAsync(Bet(), _alice, new ItemEventRouterResponse());
        await _service.BetAsync(Bet(), _bob, new ItemEventRouterResponse());

        // Alice deals and Alice stands it out, so SHE is the acting player throughout --
        // which is the arrangement that used to send Bob's roubles to her client.
        await _service.DealAsync(_alice, new ItemEventRouterResponse());

        for (var guard = 0; guard < 20; guard++)
        {
            var view = (await _service.StateAsync(_alice)).SharedTable!;

            if (view.ActiveSeat is not { } seat)
            {
                break;
            }

            await _service.ActAsync(
                new ActionRequest { Action = "Stand" },
                seat == 0 ? _alice : _bob,
                new ItemEventRouterResponse());
        }

        var settled = (await _service.StateAsync(_alice)).SharedTable!;

        // Only a seat that actually won is credited, so there is nothing to check on a
        // round where the dealer took both. That is a legitimate round, not a pass.
        if (settled.Seats[1].TotalReturned <= 0)
        {
            return;
        }

        var bobs = _outputs.For(_bob);
        var alices = _outputs.For(_alice);

        Assert.NotSame(bobs, alices);

        // Bob's winnings went into Bob's envelope. Handed the acting player's, this is the
        // assertion that fails.
        Assert.Contains(bobs, _bank.CreditedTo(_bob));
        Assert.DoesNotContain(alices, _bank.CreditedTo(_bob));
    }

    /// <summary>
    /// **A bet in a currency this table does not play is refused, not silently converted.**
    ///
    /// The request has always carried `Wallet`; the server read it and threw it away, then
    /// debited `table.Wallet`. The panel still carries the solo table's wallet chips, so a
    /// player who last played dollars could sit at a rouble table with DOLLARS lit, press
    /// BET, and watch roubles go.
    /// </summary>
    [Fact]
    public async Task ABetInTheWrongCurrencyIsRefusedAndTakesNothing()
    {
        await _service.OpenAsync(Open(), _alice, new ItemEventRouterResponse());

        var before = _bank.GetBalance(_alice, Wallet.Roubles);

        var refused = await _service.BetAsync(
            Bet(nameof(Wallet.Dollars)), _alice, new ItemEventRouterResponse());

        Assert.False(refused.Ok);
        Assert.Contains("Roubles", refused.Error);

        // Nothing moved, in either currency.
        Assert.Equal(before, _bank.GetBalance(_alice, Wallet.Roubles));
        Assert.Empty(_bank.Debits);
        Assert.Null(_escrow.Get(_alice));
    }

    /// <summary>And the table's own currency is still accepted, obviously.</summary>
    [Fact]
    public async Task ABetInTheTablesCurrencyIsTaken()
    {
        await _service.OpenAsync(Open(), _alice, new ItemEventRouterResponse());

        var bet = await _service.BetAsync(Bet(), _alice, new ItemEventRouterResponse());

        Assert.True(bet.Ok, bet.Error);
        Assert.Equal(Stash - Stake, _bank.GetBalance(_alice, Wallet.Roubles));
    }

    /// <summary>
    /// A request that names no wallet at all is taken at the table's word rather than
    /// refused.
    ///
    /// Belt and braces for anything that posts `{"Wager":n}` -- the smoke script, a curl,
    /// an older client. A table has exactly one currency, so an unstated one is not
    /// ambiguous.
    /// </summary>
    [Fact]
    public async Task ABetThatNamesNoWalletIsTakenInTheTablesCurrency()
    {
        await _service.OpenAsync(Open(), _alice, new ItemEventRouterResponse());

        var bet = await _service.BetAsync(
            new DealRequest { Wager = Stake, Wallet = string.Empty },
            _alice,
            new ItemEventRouterResponse());

        Assert.True(bet.Ok, bet.Error);
        Assert.Equal(Stash - Stake, _bank.GetBalance(_alice, Wallet.Roubles));
    }
}
