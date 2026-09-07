using System.Collections.Concurrent;
using System.Text.Json;
using Blackjack.Game;
using Casino.Server;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace Blackjack.Server;

/// <summary>One open blackjack table, as the lobby lists it.</summary>
public sealed record SharedBlackjackSummary(
    string Id,
    string HostName,
    int Seats,
    int People,
    int FreeSeats,
    int MinBet,
    int MaxBet,
    string Wallet,
    bool InRound);

/// <summary>What the server pushes down the socket when a shared blackjack table moves.</summary>
public sealed record BlackjackMessage(string Table, string Kind, TableView? View);

/// <summary>
/// Blackjack at a table more than one person is sitting at.
///
/// ## Why this is a broadcast where poker is not
///
/// **Every player's cards are face up. That is the game.** The only concealed card is the
/// dealer's hole card, hidden from everybody equally by the engine's own view. So there
/// is ONE view for the whole table and it goes to everybody unchanged.
///
/// Poker needs a different object per seat because hole cards are secret. Carrying that
/// habit here would be work that makes the game wrong -- a blackjack table where you
/// cannot see the other hands is not a blackjack table, it is people playing alone in the
/// same room.
///
/// ## Where the money is
///
/// Each seat's bet leaves that player's own stash and settles back to it. Nothing is
/// pooled: the dealer is the house, and one seat winning has no effect on another's
/// money. That is what keeps the existing per-session money code correct here, exactly
/// as it did for poker.
///
/// Unlike poker there is no stack -- a wager is taken and settled inside one round, so
/// between rounds a seat owes nothing and is owed nothing.
///
/// ## THE LOCK ORDER
///
/// Every public method takes <see cref="TableGate"/> FIRST and a <see cref="SessionGate"/>
/// INSIDE it, never the other way round. Settling touches the table and SEVERAL players'
/// profiles in one call, which is exactly the shape that deadlocks if two requests take
/// the two locks in different orders.
///
/// Every method is a gated wrapper over an ungated `*Core`, because neither gate is
/// reentrant.
/// </summary>
[Injectable]
public class SharedBlackjackService(
    IBank bank,
    TableGate tables,
    SessionGate sessions,
    SharedBlackjackStore store,
    IProfileGateway profiles,
    IEscrowStore escrow,
    TableStore solo,
    CasinoSocket socket)
{
    private const string Moved = "table";

    // ---- the gated entrance --------------------------------------------------------

    /// <summary>Open tables anybody could join. No gate: reads a snapshot, moves nothing.</summary>
    public IReadOnlyList<SharedBlackjackSummary> List() =>
        store.All
            .Where(table => table.HasRoom)
            .Select(Summarise)
            .OrderBy(summary => summary.Id, StringComparer.Ordinal)
            .ToList();

    public async Task<BlackjackResponse> OpenAsync(
        OpenTableRequest request,
        MongoId sessionId,
        ItemEventRouterResponse output)
    {
        var tableId = Guid.NewGuid().ToString("N")[..8];

        // A table that does not exist yet cannot be gated, so the claim on the PLAYER is
        // what stops two simultaneous opens making two tables.
        if (!store.TryClaim(sessionId, tableId))
        {
            return BlackjackResponse.Failed("You are already at a table. Leave it first.");
        }

        using var player = await sessions.EnterAsync(sessionId);

        try
        {
            return OpenCore(request, sessionId, tableId);
        }
        catch
        {
            store.Release(sessionId);
            throw;
        }
    }

    public async Task<BlackjackResponse> JoinAsync(string tableId, MongoId sessionId)
    {
        if (store.Get(tableId) is null)
        {
            return BlackjackResponse.Failed("That table is gone.");
        }

        if (!store.TryClaim(sessionId, tableId))
        {
            return BlackjackResponse.Failed("You are already at a table. Leave it first.");
        }

        using var table = await tables.EnterAsync(tableId);

        BlackjackResponse response;

        try
        {
            response = await JoinCoreAsync(tableId, sessionId);
        }
        catch
        {
            store.Release(sessionId);
            throw;
        }

        if (!response.Ok)
        {
            store.Release(sessionId);
        }

        return response;
    }

    /// <summary>Puts a bet in the box. The money leaves here, before any card is dealt.</summary>
    public async Task<BlackjackResponse> BetAsync(
        DealRequest request,
        MongoId sessionId,
        ItemEventRouterResponse output)
    {
        if (store.For(sessionId) is not { } seated)
        {
            return BlackjackResponse.Failed("You are not at a shared table.");
        }

        using var table = await tables.EnterAsync(seated.Id);
        using var player = await sessions.EnterAsync(sessionId);

        return await BetCoreAsync(seated.Id, request, sessionId, output);
    }

    /// <summary>Deals to every box that bet. Anybody seated may start it.</summary>
    public async Task<BlackjackResponse> DealAsync(MongoId sessionId, ItemEventRouterResponse output)
    {
        if (store.For(sessionId) is not { } seated)
        {
            return BlackjackResponse.Failed("You are not at a shared table.");
        }

        using var table = await tables.EnterAsync(seated.Id);

        return await DealCoreAsync(seated.Id, sessionId, output);
    }

    public async Task<BlackjackResponse> ActAsync(
        ActionRequest request,
        MongoId sessionId,
        ItemEventRouterResponse output)
    {
        if (store.For(sessionId) is not { } seated)
        {
            return BlackjackResponse.Failed("You are not at a shared table.");
        }

        using var table = await tables.EnterAsync(seated.Id);

        return await ActCoreAsync(seated.Id, request, sessionId, output);
    }

    public async Task<BlackjackResponse> LeaveAsync(MongoId sessionId, ItemEventRouterResponse output)
    {
        if (store.For(sessionId) is not { } seated)
        {
            return BlackjackResponse.Failed("You are not at a shared table.");
        }

        using var table = await tables.EnterAsync(seated.Id);
        using var player = await sessions.EnterAsync(sessionId);

        return await LeaveCoreAsync(seated.Id, sessionId, output);
    }

    public async Task<BlackjackResponse> StateAsync(MongoId sessionId)
    {
        if (store.For(sessionId) is not { } seated)
        {
            return BlackjackResponse.Failed("You are not at a shared table.");
        }

        using var table = await tables.EnterAsync(seated.Id);

        Touch(seated, sessionId);

        return View(seated, sessionId);
    }

    // ---- the ungated work ----------------------------------------------------------

    private BlackjackResponse OpenCore(OpenTableRequest request, MongoId sessionId, string tableId)
    {
        if (!profiles.HasProfile(sessionId))
        {
            store.Release(sessionId);
            return BlackjackResponse.Failed("No PMC profile for this session.");
        }

        if (PrivateRoundInTheWay(sessionId) is { } busy)
        {
            store.Release(sessionId);
            return busy;
        }

        if (!Enum.TryParse<Wallet>(request.Wallet, ignoreCase: true, out var wallet))
        {
            store.Release(sessionId);
            return BlackjackResponse.Failed($"Unknown currency '{request.Wallet}'.");
        }

        if (request.Seats is < 2 or > 7)
        {
            store.Release(sessionId);
            return BlackjackResponse.Failed("A blackjack table has 2 to 7 boxes.");
        }

        // The limits belong to the wallet, not to the engine: `Rules.MinBet` is a single
        // pair of numbers and a minimum of 1,000 is beneath notice in roubles and
        // impossible in bitcoin. `WalletInfo` is where the single-player table already
        // gets them from.
        var limits = WalletInfo.For(wallet);
        var rules = new Rules { MinBet = limits.MinBet, MaxBet = limits.MaxBet };

        // Their real nickname, never the engine's "You" fallback. Poker shipped a shared
        // table without this and both players saw the same word for a different person.
        var hostName = NameFor(sessionId, 0);

        var engine = new BlackjackTable(
            rules,
            rng: null,
            seats: request.Seats,
            occupiedSeats: [0],
            seatNames: new Dictionary<int, string> { [0] = hostName });

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var table = new SharedBlackjackTable
        {
            Id = tableId,
            HostName = hostName,
            Table = engine,
            Occupants = new ConcurrentDictionary<int, string>(
                new[] { new KeyValuePair<int, string>(0, sessionId.ToString()) }),
            Wallet = wallet,
            MinBet = limits.MinBet,
            MaxBet = limits.MaxBet,
            OpenedAtUtc = now,
        };

        table.LastSeenUtc[sessionId.ToString()] = now;
        store.Add(table);


        return View(table, sessionId);
    }

    private async Task<BlackjackResponse> JoinCoreAsync(string tableId, MongoId sessionId)
    {
        if (store.Get(tableId) is not { } table)
        {
            return BlackjackResponse.Failed("That table is gone.");
        }

        if (!profiles.HasProfile(sessionId))
        {
            return BlackjackResponse.Failed("No PMC profile for this session.");
        }

        if (PrivateRoundInTheWay(sessionId) is { } busy)
        {
            return busy;
        }

        // Between rounds only. The engine refuses anyway; refusing here keeps the message
        // useful and costs nothing, because no money moves on a blackjack join.
        if (table.Table.Phase is not (RoundPhase.AwaitingBet or RoundPhase.Settled))
        {
            return BlackjackResponse.Failed("A round is in progress. Wait for it to finish.");
        }

        if (table.FirstFreeSeat() is not { } free)
        {
            return BlackjackResponse.Failed("That table is full.");
        }

        var name = NameFor(sessionId, free);

        try
        {
            table.Table.TakeSeat(free, name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            return BlackjackResponse.Failed(ex.Message);
        }

        table.Occupants[free] = sessionId.ToString();
        table.LastSeenUtc[sessionId.ToString()] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();


        // **Nothing is debited here.** Unlike poker there is no buy-in: a blackjack seat
        // costs nothing until it bets, and a player who sits down and never bets has paid
        // nothing and is owed nothing.
        await PushAsync(table, "joined");

        return View(table, sessionId);
    }

    private async Task<BlackjackResponse> BetCoreAsync(
        string tableId,
        DealRequest request,
        MongoId sessionId,
        ItemEventRouterResponse output)
    {
        if (store.Get(tableId) is not { } table)
        {
            return BlackjackResponse.Failed("That table is gone.");
        }

        if (table.SeatOf(sessionId) is not { } seat)
        {
            return BlackjackResponse.Failed("You are not at that table.");
        }

        Touch(table, sessionId);

        if (table.Table.Phase is not (RoundPhase.AwaitingBet or RoundPhase.Settled))
        {
            return BlackjackResponse.Failed("A round is already running. Wait for it to finish.");
        }

        if (request.Wager < table.MinBet || request.Wager > table.MaxBet)
        {
            return BlackjackResponse.Failed(
                $"This table takes {table.MinBet:N0} to {table.MaxBet:N0}.");
        }

        if (table.Table.Seats[seat].PendingBet > 0)
        {
            return BlackjackResponse.Failed("Your bet is already in the box.");
        }

        // Taken before the bet is recorded, and refused loudly if it cannot be. The debit
        // is the thing that can fail; putting it first means a box never shows a bet that
        // was never paid for.
        if (!bank.TryDebit(sessionId, table.Wallet, request.Wager, output))
        {
            return BlackjackResponse.Failed(
                $"Not enough -- you have {bank.GetBalance(sessionId, table.Wallet):N0}.");
        }

        // Recorded the instant the money is gone. From here until the round settles, this
        // is the only thing that knows this player is owed anything.
        escrow.Hold(sessionId, table.Wallet, request.Wager);

        try
        {
            // The natural pays what this WALLET pays, fixed onto the seat as the bet is
            // placed. Currency settles 3:2; a valuable settles even money, because one
            // bitcoin at 3:2 is two and a half and half a bitcoin does not exist. One
            // shoe serving several currencies is why this cannot live on the table.
            table.Table.PlaceBet(seat, request.Wager, WalletInfo.For(table.Wallet).BlackjackPayout);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            // The engine refused after the money moved. Put it straight back rather than
            // leaving them paid up with no bet on the table.
            bank.Credit(sessionId, table.Wallet, request.Wager, output);
            escrow.Release(sessionId);


            return BlackjackResponse.Failed(ex.Message);
        }

        await profiles.SaveAsync(sessionId);
        await PushAsync(table, Moved);

        return View(table, sessionId);
    }

    private async Task<BlackjackResponse> DealCoreAsync(
        string tableId,
        MongoId sessionId,
        ItemEventRouterResponse output)
    {
        if (store.Get(tableId) is not { } table)
        {
            return BlackjackResponse.Failed("That table is gone.");
        }

        if (table.SeatOf(sessionId) is null)
        {
            return BlackjackResponse.Failed("You are not at that table.");
        }

        Touch(table, sessionId);

        try
        {
            table.Table.StartRound();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            return View(table, sessionId) with { Ok = false, Error = ex.Message };
        }

        // A round can settle the moment it is dealt -- a natural, or every box standing
        // pat on a dealer blackjack -- so settlement is checked here as well as after an
        // action, or the money would sit unpaid until somebody happened to act.
        await SettleIfDoneAsync(table, output);
        await PushAsync(table, Moved);

        return View(table, sessionId);
    }

    private async Task<BlackjackResponse> ActCoreAsync(
        string tableId,
        ActionRequest request,
        MongoId sessionId,
        ItemEventRouterResponse output)
    {
        if (store.Get(tableId) is not { } table)
        {
            return BlackjackResponse.Failed("That table is gone.");
        }

        if (table.SeatOf(sessionId) is not { } seat)
        {
            return BlackjackResponse.Failed("You are not at that table.");
        }

        Touch(table, sessionId);

        if (!table.Table.IsTurnFor(seat))
        {
            return View(table, sessionId) with { Ok = false, Error = "It is not your turn." };
        }

        if (!Enum.TryParse<PlayerAction>(request.Action, ignoreCase: true, out var action))
        {
            return BlackjackResponse.Failed($"Unknown action '{request.Action}'.");
        }

        try
        {
            switch (action)
            {
                case PlayerAction.Hit:
                    table.Table.Hit(seat);
                    break;
                case PlayerAction.Stand:
                    table.Table.Stand(seat);
                    break;
                case PlayerAction.Double:
                    // Doubling stakes the same amount again, so it costs money HERE and
                    // has to be paid for before the engine is told. Refusing after the
                    // card is dealt would be a free double.
                    if (await ChargeDoubleAsync(table, seat, sessionId, output) is { } refusal)
                    {
                        return refusal;
                    }

                    table.Table.Double(seat);
                    break;
                case PlayerAction.Split:
                    if (await ChargeDoubleAsync(table, seat, sessionId, output) is { } splitRefusal)
                    {
                        return splitRefusal;
                    }

                    table.Table.Split(seat);
                    break;
                default:
                    return BlackjackResponse.Failed($"Unknown action '{request.Action}'.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            // The engine is the authority on legality. A refusal means this client's view
            // drifted, so hand back the real one rather than a bare error.
            return View(table, sessionId) with { Ok = false, Error = ex.Message };
        }

        await SettleIfDoneAsync(table, output);
        await PushAsync(table, Moved);

        return View(table, sessionId);
    }

    private async Task<BlackjackResponse> LeaveCoreAsync(
        string tableId,
        MongoId sessionId,
        ItemEventRouterResponse output)
    {
        if (store.Get(tableId) is not { } table)
        {
            return BlackjackResponse.Failed("That table is gone.");
        }

        if (table.SeatOf(sessionId) is not { } seat)
        {
            return BlackjackResponse.Failed("You are not at that table.");
        }

        if (table.Table.Phase is not (RoundPhase.AwaitingBet or RoundPhase.Settled))
        {
            return BlackjackResponse.Failed("Finish the round first.");
        }

        // A bet in the box that was never dealt is theirs to take back.
        var pending = table.Table.Seats[seat].PendingBet;

        if (pending > 0)
        {
            bank.Credit(sessionId, table.Wallet, pending, output);
            escrow.Release(sessionId);
        }

        var last = table.Occupants.Count == 1;

        table.Occupants.TryRemove(seat, out _);
        table.LastSeenUtc.TryRemove(sessionId.ToString(), out _);

        if (last)
        {
            store.Remove(tableId);
        }
        else
        {
            table.Table.VacateSeat(seat);
            store.Release(sessionId);

            await PushAsync(table, "left");
        }

        await profiles.SaveAsync(sessionId);

        return new BlackjackResponse
        {
            Balance = bank.GetBalance(sessionId, table.Wallet),
            Wallet = table.Wallet.ToString(),
            Note = pending > 0 ? $"Your {pending:N0} came back with you." : null,
        };
    }

    // ---- money -----------------------------------------------------------------------

    /// <summary>
    /// Takes the extra stake a double or a split costs, before the engine is told.
    ///
    /// Returns a refusal when it cannot be paid, and null when the money has moved.
    /// Charging first matters: the engine deals a card on a double, and a refusal after
    /// that is a card the player got for nothing.
    /// </summary>
    private async Task<BlackjackResponse?> ChargeDoubleAsync(
        SharedBlackjackTable table,
        int seat,
        MongoId sessionId,
        ItemEventRouterResponse output)
    {
        var extra = table.Table.Seats[seat].Hands[table.Table.Seats[seat].ActiveHandIndex].Wager;

        using var player = await sessions.EnterAsync(sessionId);

        if (!bank.TryDebit(sessionId, table.Wallet, extra, output))
        {
            return BlackjackResponse.Failed(
                $"That needs another {extra:N0} and you have "
                + $"{bank.GetBalance(sessionId, table.Wallet):N0}.");
        }

        escrow.Hold(sessionId, table.Wallet, extra);

        return null;
    }

    /// <summary>
    /// Pays everybody out once the dealer has played, and only then.
    ///
    /// **Each seat is paid its own return, into its own profile.** The dealer is the
    /// house, so one box winning has no bearing on another's money -- there is no pot to
    /// divide and nothing to get wrong between players.
    ///
    /// Takes each player's session gate INSIDE the table gate already held by the caller.
    /// That order is the whole reason <see cref="TableGate"/> exists: this one method
    /// touches several profiles, and doing it the other way round deadlocks against those
    /// players' own requests.
    /// </summary>
    private async Task SettleIfDoneAsync(SharedBlackjackTable table, ItemEventRouterResponse output)
    {
        if (table.Table.Phase != RoundPhase.Settled)
        {
            return;
        }

        foreach (var seat in table.Table.Seats.Where(s => s.IsInRound))
        {
            if (!table.Occupants.TryGetValue(seat.Index, out var who))
            {
                continue;
            }

            var session = new MongoId(who);

            using (await sessions.EnterAsync(session))
            {
                if (seat.TotalReturned > 0)
                {
                    bank.Credit(session, table.Wallet, seat.TotalReturned, output);
                }

                // Released whether they won or lost: the round is over either way, and a
                // row left behind would refund a stake that has already been settled.
                escrow.Release(session);
            }

            await profiles.SaveAsync(session);
        }
    }

    // ---- pushing ---------------------------------------------------------------------

    /// <summary>
    /// Tells everybody at the table that it moved.
    ///
    /// **One object, broadcast** -- the opposite of poker, and correct here. Every
    /// player's cards are face up in blackjack, and the only concealed card is the
    /// dealer's hole card which the engine's own view hides from everybody until the
    /// dealer plays. There is nothing per-seat to filter, so building a view per person
    /// would be work that produces the same object N times.
    ///
    /// A push that does not arrive is not an error -- somebody closing the game is the
    /// ordinary case, and the socket says so by returning false rather than throwing.
    /// </summary>
    private async Task PushAsync(SharedBlackjackTable table, string kind)
    {
        var message = JsonSerializer.Serialize(
            new BlackjackMessage(table.Id, kind, table.Table.ViewTable()));

        var everybody = table.Sessions.Select(id => new MongoId(id)).ToList();

        await socket.SendAsync(everybody, message);
    }

    // ---- odds and ends ---------------------------------------------------------------

    private static SharedBlackjackSummary Summarise(SharedBlackjackTable table) =>
        new(
            table.Id,
            table.HostName,
            table.Table.Seats.Count,
            table.Occupants.Count,
            table.Table.Seats.Count - table.Occupants.Count,
            table.MinBet,
            table.MaxBet,
            table.Wallet.ToString(),
            table.Table.Phase is not (RoundPhase.AwaitingBet or RoundPhase.Settled));

    /// <summary>
    /// Refuses a seat while this player's own table still owes them money.
    ///
    /// **There is one escrow row per session**, and a shared table would give a session a
    /// second place to owe from. `escrow-blackjack.json` holds a single
    /// <see cref="OutstandingStake"/> keyed by session, which was right while a player
    /// could only be at their own table.
    ///
    /// Left unguarded it does not merely miscount. The two rounds settle independently and
    /// each releases the one row, so whichever finishes first takes the other's record with
    /// it -- and `BlackjackService.RefundAbandonedStake`, which cannot tell a shared
    /// table's stake from an orphaned one, hands it back as a refund. The other half of
    /// this rule lives in <see cref="BlackjackService"/>, because either half alone leaves
    /// the opposite order open.
    ///
    /// Null when there is nothing in the way.
    /// </summary>
    private BlackjackResponse? PrivateRoundInTheWay(MongoId sessionId)
    {
        if (escrow.Get(sessionId) is null)
        {
            return null;
        }

        // A live round is the ordinary case and says so. The other case is a stake left
        // over from a round the server did not live to finish -- the row is real money and
        // must not be walked past, but "finish the round" would be advice about a round
        // that no longer exists. Opening the solo table refunds it, so say that instead.
        var live = solo.Has(sessionId) && solo.For(sessionId).Table.Phase == RoundPhase.PlayerTurn;

        return BlackjackResponse.Failed(
            live
                ? "Your own table still has a hand on it. Finish that round first."
                : "There is an unsettled stake on your own table. Open it to collect it "
                  + "first, then come back.");
    }

    /// <summary>
    /// What to call the person in a box: their PMC nickname, never "You".
    ///
    /// Falls back to the box number rather than refusing a seat -- a nameless box is
    /// cosmetic. See <see cref="IProfileGateway.NameOf"/> for what went wrong at a poker
    /// table without this.
    /// </summary>
    private string NameFor(MongoId sessionId, int seat) =>
        profiles.NameOf(sessionId) ?? $"Box {seat}";

    private static void Touch(SharedBlackjackTable table, MongoId sessionId) =>
        table.LastSeenUtc[sessionId.ToString()] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private BlackjackResponse View(SharedBlackjackTable table, MongoId sessionId) =>
        new()
        {
            SharedTable = table.Table.ViewTable(),
            YourSeat = table.SeatOf(sessionId),
            Balance = bank.GetBalance(sessionId, table.Wallet),
            Wallet = table.Wallet.ToString(),
        };
}
