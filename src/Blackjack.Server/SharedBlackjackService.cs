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

    /// <summary>
    /// How long the table waits on a seat before playing on without it.
    ///
    /// Ninety seconds is chosen against the two ways it can be wrong. Too short and a
    /// player who is thinking, or reading their mail, or was shot at in the hideout, gets
    /// their hand taken off them -- and that is real money. Too long and a table whose
    /// player has closed the game is dead for the rest of the evening.
    ///
    /// Nobody deliberates for ninety seconds over a blackjack hand. The panel also asks
    /// for the table whenever it draws, so a client that is merely idle is still speaking.
    /// </summary>
    private static readonly TimeSpan QuietAfter = TimeSpan.FromSeconds(90);

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
        await AdvancePastTheAbsentAsync(seated, output);

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

        await AdvancePastTheAbsentAsync(seated, output);

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

        await AdvancePastTheAbsentAsync(seated, output);

        return await ActCoreAsync(seated.Id, request, sessionId, output);
    }

    public async Task<BlackjackResponse> LeaveAsync(MongoId sessionId, ItemEventRouterResponse output)
    {
        if (store.For(sessionId) is not { } seated)
        {
            return BlackjackResponse.Failed("You are not at a shared table.");
        }

        using var table = await tables.EnterAsync(seated.Id);
        await AdvancePastTheAbsentAsync(seated, output);

        using var player = await sessions.EnterAsync(sessionId);

        return await LeaveCoreAsync(seated.Id, sessionId, output);
    }

    /// <summary>
    /// Test-only convenience. The throwaway response it builds is not initialised the way
    /// SPT's inventory helpers expect, so anything with a real InventoryHelper behind it
    /// must call the overload below -- same rule as <see cref="BlackjackService.State"/>.
    /// </summary>
    public Task<BlackjackResponse> StateAsync(MongoId sessionId) =>
        StateAsync(sessionId, new ItemEventRouterResponse());

    /// <summary>
    /// This player's view of the table.
    ///
    /// **It takes an output because it can move money.** Asking for the table is what
    /// notices that somebody else has gone -- see
    /// <see cref="AdvancePastTheAbsentAsync"/> -- and playing on past them can settle the
    /// round and pay everybody out. A read that is only ever a read would not need this;
    /// this one is the clock the table runs on.
    /// </summary>
    public async Task<BlackjackResponse> StateAsync(MongoId sessionId, ItemEventRouterResponse output)
    {
        if (store.For(sessionId) is not { } seated)
        {
            return BlackjackResponse.Failed("You are not at a shared table.");
        }

        using var table = await tables.EnterAsync(seated.Id);

        // Before the touch, not after: this player has just been heard from, but whoever
        // the table is waiting on has not, and it is that gap this is reading.
        await AdvancePastTheAbsentAsync(seated, output);

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

        // **Asked before a single rouble moves.** Double and Split cost the stake again,
        // and the engine refuses either one for reasons the client cannot always know it
        // has hit -- a Double is illegal the moment the hand has three cards, so a stale
        // view, a double-click or a retried request all arrive as a Double that cannot
        // happen. Charging first and discovering that afterwards is how a player is billed
        // for a card they were never dealt.
        //
        // `AvailableActions` is the engine's own answer to "what may this seat do", which
        // is the same thing the panel draws its buttons from.
        if (action is PlayerAction.Double or PlayerAction.Split
            && !table.Table.AvailableActions(seat).Contains(action))
        {
            return View(table, sessionId) with
            {
                Ok = false,
                Error = $"{action} is not available on that hand.",
            };
        }

        // What this action took, so it can be given back if the engine refuses anyway.
        var charged = 0;

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
                {
                    // Doubling stakes the same amount again, so it costs money HERE and has
                    // to be paid for before the engine is told: the engine deals a card, and
                    // a refusal after that is a card the player got for nothing.
                    var charge = await ChargeDoubleAsync(table, seat, sessionId, output);

                    if (charge.Response is not null)
                    {
                        return charge.Response;
                    }

                    charged = charge.Taken;
                    table.Table.Double(seat);
                    break;
                }

                case PlayerAction.Split:
                {
                    var charge = await ChargeDoubleAsync(table, seat, sessionId, output);

                    if (charge.Response is not null)
                    {
                        return charge.Response;
                    }

                    charged = charge.Taken;
                    table.Table.Split(seat);
                    break;
                }

                default:
                    return BlackjackResponse.Failed($"Unknown action '{request.Action}'.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            // **The money goes back.** The check above catches every refusal anybody has
            // reproduced, but the engine is the authority and this is the path that runs
            // when it refuses for a reason `AvailableActions` does not model. Without this
            // the extra stake is simply destroyed: debited, held in escrow, then released
            // at settlement against a hand that was never staked that much.
            if (charged > 0)
            {
                await RefundChargeAsync(table, seat, sessionId, charged, output);
            }

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

        // **Released here, unconditionally, and not left to `store.Remove`.**
        //
        // `Remove` frees a table's occupants by walking `Occupants` -- and this player has
        // just been taken out of it, so on the `last` branch there is nobody left to walk
        // and their own claim would survive the table they closed. They are then held at a
        // table that does not exist and every later open or join is refused with "You are
        // already at a table" until the server restarts.
        //
        // The leak is invisible to the obvious check: `store.For` returns null, because the
        // table really is gone. Only `TryClaim` can see it, and only by refusing.
        //
        // Poker escapes this by ordering -- it calls `Remove` before it touches the seat --
        // which is a correctness that a later edit could reorder away without noticing.
        // Releasing the leaver explicitly does not care what order anything else happens in.
        store.Release(sessionId);

        if (last)
        {
            store.Remove(tableId);
        }
        else
        {
            table.Table.VacateSeat(seat);

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
    /// What a charge did: either it was refused and nothing moved, or it took an amount
    /// that the caller is now responsible for handing back if the action then fails.
    ///
    /// A struct rather than a nullable response because the amount has to travel with the
    /// outcome. Returning only "refused or not" is what let a refused Double keep the
    /// player's money -- the caller had nothing to give back with.
    /// </summary>
    private readonly record struct Charge(BlackjackResponse? Response, int Taken)
    {
        internal static Charge Refused(BlackjackResponse response) => new(response, 0);

        internal static Charge Took(int amount) => new(null, amount);
    }

    /// <summary>
    /// Takes the extra stake a double or a split costs, before the engine is told.
    ///
    /// Charging first matters: the engine deals a card on a double, and a refusal after
    /// that is a card the player got for nothing. The cost of that ordering is that the
    /// caller MUST hand the money back if the engine then refuses -- see
    /// <see cref="RefundChargeAsync"/>, and the amount this returns for doing it with.
    /// </summary>
    private async Task<Charge> ChargeDoubleAsync(
        SharedBlackjackTable table,
        int seat,
        MongoId sessionId,
        ItemEventRouterResponse output)
    {
        var extra = table.Table.Seats[seat].Hands[table.Table.Seats[seat].ActiveHandIndex].Wager;

        using var player = await sessions.EnterAsync(sessionId);

        if (!bank.TryDebit(sessionId, table.Wallet, extra, output))
        {
            return Charge.Refused(BlackjackResponse.Failed(
                $"That needs another {extra:N0} and you have "
                + $"{bank.GetBalance(sessionId, table.Wallet):N0}."));
        }

        escrow.Hold(sessionId, table.Wallet, extra);

        return Charge.Took(extra);
    }

    /// <summary>
    /// Gives back a charge for an action the engine then refused.
    ///
    /// **Escrow is rewritten rather than released.** A release would drop the row entirely,
    /// and the ORIGINAL bet is still live in it -- the player would be paid their winnings
    /// at settlement but have nothing recorded if the server died first. So the row is put
    /// back to what the seat has actually staked, which is what the engine says it is.
    /// </summary>
    private async Task RefundChargeAsync(
        SharedBlackjackTable table,
        int seat,
        MongoId sessionId,
        int amount,
        ItemEventRouterResponse output)
    {
        using var player = await sessions.EnterAsync(sessionId);

        bank.Credit(sessionId, table.Wallet, amount, output);

        escrow.Release(sessionId);

        var stillStaked = table.Table.Seats[seat].TotalWagered;

        if (stillStaked > 0)
        {
            escrow.Hold(sessionId, table.Wallet, stillStaked);
        }
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
    /// Plays on past a seat whose player has gone.
    ///
    /// ## Why this has to exist
    ///
    /// **Blackjack has no fold, and standing up is refused mid-round.** So a seat whose
    /// player closed the game holds the turn, and every other player at that table is stuck
    /// behind it with their stake in escrow -- unable to act, unable to leave, for as long
    /// as the server runs. `LastSeenUtc` was being written on every request and read
    /// nowhere, which is how that shipped.
    ///
    /// ## Why standing, and not something cheaper
    ///
    /// Standing is the only action that is always legal and never spends money. The absent
    /// player keeps the hand they were dealt and is paid whatever it wins -- they are not
    /// punished for their game crashing, and nobody at the table gains from it either.
    ///
    /// Folding is not available; blackjack has no such thing. Busting them on purpose would
    /// be taking their stake. Leaving the bet uncollected would be worse than both.
    ///
    /// ## Where it is called from
    ///
    /// Every gated entry point, **after the table gate and before any session gate** -- it
    /// settles, and settling takes session gates. Calling it from inside a method that
    /// already holds the leaver's session gate would deadlock, which is why
    /// <see cref="LeaveAsync"/> calls it before taking that gate rather than inside
    /// <see cref="LeaveCoreAsync"/>.
    ///
    /// Driven by requests rather than by a timer. A background sweep would need its own
    /// gate discipline and would move money with nobody's request behind it; the panel asks
    /// for the table whenever it draws, so somebody still at the table is the clock.
    /// </summary>
    private async Task AdvancePastTheAbsentAsync(
        SharedBlackjackTable table,
        ItemEventRouterResponse output)
    {
        if (table.Table.Phase != RoundPhase.PlayerTurn)
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)QuietAfter.TotalSeconds;
        var stood = false;

        // Bounded rather than looped on the phase: several seats can be gone at once, and a
        // seat that somehow refuses to stand would otherwise spin here forever holding the
        // table gate. Seven boxes and a few split hands each is well inside twenty.
        for (var guard = 0; guard < 20; guard++)
        {
            if (table.Table.ActiveSeat is not { } seat)
            {
                break;
            }

            if (!table.Occupants.TryGetValue(seat, out var who))
            {
                break;
            }

            // Absent means "has not been heard from", not "is not connected". The socket
            // dropping is ordinary and does not mean somebody left the table.
            if (table.LastSeenUtc.TryGetValue(who, out var seen) && seen > cutoff)
            {
                break;
            }

            try
            {
                table.Table.Stand(seat);
                stood = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
            {
                // The engine will not stand that seat, so nothing here can move the round
                // on. Better to leave the table as it is than to loop on a refusal.
                break;
            }
        }

        if (!stood)
        {
            return;
        }

        await SettleIfDoneAsync(table, output);
        await PushAsync(table, Moved);
    }

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
