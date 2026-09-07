using System.Text.Json;
using Casino.Server;
using Poker.Game;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace Poker.Server;

/// <summary>One open table, as the lobby lists it.</summary>
public sealed record SharedTableSummary(
    string Id,
    string HostName,
    int Seats,
    int People,
    int FreeSeats,
    int BuyIn,
    int BigBlind,
    string Wallet,
    bool InHand);

/// <summary>What the server pushes down the socket when a shared table moves.</summary>
/// <param name="Table">Which table. One socket carries the whole casino, so this is needed.</param>
/// <param name="Kind">What happened, so a client can ignore what it does not care about.</param>
/// <param name="View">
/// The table **as this recipient may see it**. Every player gets a different object --
/// see <see cref="SharedPokerService.PushAsync"/>.
/// </param>
public sealed record TableMessage(string Table, string Kind, HoldemView? View);

/// <summary>
/// Poker at a table more than one person is sitting at.
///
/// ## Where the money is, and why nothing about it changed
///
/// Each person's buy-in leaves their own stash and is recorded against their own
/// session, exactly as the single-player table already does; each person's cash-out
/// pays their own stack back. There is no shared pot of real currency -- the pot is
/// chips, and chips only become money when somebody stands up. That is what keeps the
/// existing per-session money code correct here: it was written for one profile at a
/// time and one profile at a time is still all it ever sees.
///
/// ## THE LOCK ORDER
///
/// Every public method takes <see cref="TableGate"/> FIRST and a
/// <see cref="SessionGate"/> INSIDE it. Never the other way round. The type's own
/// remarks explain the cycle; the short version is that dealing charges blinds to
/// players who did not send the request, so one request routinely needs somebody
/// else's session gate while holding the table.
///
/// Every method here is a gated wrapper over an ungated `*Core`, for the same reason
/// <see cref="PokerService"/> is: neither gate is reentrant.
/// </summary>
[Injectable]
public class SharedPokerService(
    IBank bank,
    TableGate tables,
    SessionGate sessions,
    SharedTableStore store,
    IProfileGateway profiles,
    IEscrowStore escrow,
    INameSource names,
    CasinoSocket socket,
    IPokerLog log)
{
    /// <summary>What a player is told when the table has moved without them asking.</summary>
    private const string Moved = "table";

    // ---- the gated entrance --------------------------------------------------------

    /// <summary>Open tables anybody could join. No gate: it reads a snapshot and moves nothing.</summary>
    public IReadOnlyList<SharedTableSummary> List() =>
        store.All
            .Where(table => table.HasRoom)
            .Select(Summarise)
            .OrderBy(summary => summary.Id, StringComparer.Ordinal)
            .ToList();

    public async Task<PokerResponse> CreateAsync(
        SitRequest request,
        MongoId sessionId,
        ItemEventRouterResponse output)
    {
        // A table that does not exist yet cannot be gated, so the claim on the PLAYER is
        // what stops two simultaneous creates making two tables and taking two buy-ins.
        var tableId = Guid.NewGuid().ToString("N")[..8];

        if (!store.TryClaim(sessionId, tableId))
        {
            return PokerResponse.Failed("You are already at a table. Leave it first and take your chips.");
        }

        using var player = await sessions.EnterAsync(sessionId);

        try
        {
            return await CreateCoreAsync(request, sessionId, tableId, output);
        }
        catch
        {
            store.Release(sessionId);
            throw;
        }
    }

    public async Task<PokerResponse> JoinAsync(
        string tableId,
        MongoId sessionId,
        ItemEventRouterResponse output)
    {
        if (store.Get(tableId) is null)
        {
            return PokerResponse.Failed("That table is gone.");
        }

        if (!store.TryClaim(sessionId, tableId))
        {
            return PokerResponse.Failed("You are already at a table. Leave it first and take your chips.");
        }

        using var table = await tables.EnterAsync(tableId);
        using var player = await sessions.EnterAsync(sessionId);

        PokerResponse response;

        try
        {
            response = await JoinCoreAsync(tableId, sessionId, output);
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

    public async Task<PokerResponse> LeaveAsync(MongoId sessionId, ItemEventRouterResponse output)
    {
        if (store.For(sessionId) is not { } seated)
        {
            return PokerResponse.Failed("You are not at a shared table.");
        }

        using var table = await tables.EnterAsync(seated.Id);
        using var player = await sessions.EnterAsync(sessionId);

        return await LeaveCoreAsync(seated.Id, sessionId, output);
    }

    public async Task<PokerResponse> DealAsync(MongoId sessionId)
    {
        if (store.For(sessionId) is not { } seated)
        {
            return PokerResponse.Failed("You are not at a shared table.");
        }

        using var table = await tables.EnterAsync(seated.Id);

        return await DealCoreAsync(seated.Id, sessionId);
    }

    public async Task<PokerResponse> ActAsync(ActRequest request, MongoId sessionId)
    {
        if (store.For(sessionId) is not { } seated)
        {
            return PokerResponse.Failed("You are not at a shared table.");
        }

        using var table = await tables.EnterAsync(seated.Id);

        return await ActCoreAsync(seated.Id, request, sessionId);
    }

    /// <summary>This player's own view of their table.</summary>
    public async Task<PokerResponse> StateAsync(MongoId sessionId)
    {
        if (store.For(sessionId) is not { } seated)
        {
            return PokerResponse.Failed("You are not at a shared table.");
        }

        using var table = await tables.EnterAsync(seated.Id);

        Touch(seated, sessionId);

        return ViewFor(seated, sessionId);
    }

    // ---- the ungated work ----------------------------------------------------------

    private async Task<PokerResponse> CreateCoreAsync(
        SitRequest request,
        MongoId sessionId,
        string tableId,
        ItemEventRouterResponse output)
    {
        if (!profiles.HasProfile(sessionId))
        {
            store.Release(sessionId);
            return PokerResponse.Failed("No PMC profile for this session.");
        }

        if (!Enum.TryParse<Wallet>(request.Wallet, ignoreCase: true, out var wallet))
        {
            store.Release(sessionId);
            return PokerResponse.Failed($"Unknown currency '{request.Wallet}'.");
        }

        if (Refuse(request, wallet) is { } refusal)
        {
            store.Release(sessionId);
            return refusal;
        }

        // Validated before a chip is taken. Letting the table throw after the debit
        // would pocket the buy-in and seat nobody -- the same rule the solo table keeps.
        if (!bank.TryDebit(sessionId, wallet, request.BuyIn, output))
        {
            store.Release(sessionId);

            return PokerResponse.Failed(
                $"Not enough {WalletInfo.For(wallet).Label} -- you have "
                + $"{bank.GetBalance(sessionId, wallet):N0} and the buy-in is {request.BuyIn:N0}.");
        }

        // Recorded the instant the money is gone. From here until they stand up, this is
        // the only thing that knows they are owed anything.
        escrow.Record(sessionId, wallet, request.BuyIn);

        var rules = new HoldemRules
        {
            SmallBlind = request.BigBlind / 2,
            BigBlind = request.BigBlind,
            BuyIn = request.BuyIn,
        };

        var seed = request.Seed ?? Environment.TickCount;
        var rng = new Random(seed);
        var engineLog = log.ForEngine();

        var characters = Enumerable.Range(0, request.Seats - 1)
            .Select(_ => PokerPersonality.Improvise(rng))
            .ToList();

        var agents = characters
            .Select((character, index) => new BotAgent(character, new Random(seed + index + 1), engineLog))
            .ToList();

        var seatNames = names.Take(request.Seats - 1, rng);

        var engine = new HoldemTable(
            rules,
            request.Seats,
            rng,
            engineLog,
            agents.Cast<IPokerAgent>().ToList(),
            seatNames);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var table = new SharedTable
        {
            Id = tableId,
            HostName = "Host",
            Table = engine,
            Agents = agents,
            Characters = characters,
            Seats = engine.Seats.ToDictionary(
                seat => seat.Index,
                seat => new SharedSeat
                {
                    Index = seat.Index,
                    Kind = seat.IsPlayer ? SeatKind.Human : SeatKind.Bot,
                    Name = seat.Name,
                    SessionId = seat.IsPlayer ? sessionId.ToString() : null,
                    LastSeenUtc = now,
                }),
            BuyIn = request.BuyIn,
            BigBlind = request.BigBlind,
            Wallet = wallet,
            OpenedAtUtc = now,
        };

        store.Add(table);

        await profiles.SaveAsync(sessionId);

        log.Info(
            $"shared table {tableId} opened [{sessionId}] -- {request.Seats} seats, "
            + $"blinds {rules.SmallBlind}/{rules.BigBlind}, {request.BuyIn} chips each");

        return ViewFor(table, sessionId);
    }

    private async Task<PokerResponse> JoinCoreAsync(
        string tableId,
        MongoId sessionId,
        ItemEventRouterResponse output)
    {
        if (store.Get(tableId) is not { } table)
        {
            return PokerResponse.Failed("That table is gone.");
        }

        if (!profiles.HasProfile(sessionId))
        {
            return PokerResponse.Failed("No PMC profile for this session.");
        }

        // Between hands only. The engine refuses anyway, but saying so here means the
        // buy-in is never taken for a seat that cannot be filled.
        if (table.Table.Street is not (HoldemStreet.Idle or HoldemStreet.Showdown))
        {
            return PokerResponse.Failed("A hand is in progress. Wait for it to finish.");
        }

        if (table.Seats.Values.FirstOrDefault(seat => seat.Kind == SeatKind.Bot) is not { } free)
        {
            return PokerResponse.Failed("That table is full.");
        }

        if (!bank.TryDebit(sessionId, table.Wallet, table.BuyIn, output))
        {
            return PokerResponse.Failed(
                $"Not enough {WalletInfo.For(table.Wallet).Label} -- you have "
                + $"{bank.GetBalance(sessionId, table.Wallet):N0} and the buy-in is {table.BuyIn:N0}.");
        }

        escrow.Record(sessionId, table.Wallet, table.BuyIn);

        var name = $"Seat {free.Index}";

        try
        {
            table.Table.TakeSeat(free.Index, table.BuyIn, name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            // The seat went while the money was moving. Put it straight back rather than
            // leaving them paid-up and standing.
            bank.Credit(sessionId, table.Wallet, table.BuyIn, output);
            escrow.Release(sessionId);

            log.Error($"join refused by the engine after the buy-in was taken; refunded. {ex.Message}");

            return PokerResponse.Failed(ex.Message);
        }

        table.Seats[free.Index] = new SharedSeat
        {
            Index = free.Index,
            Kind = SeatKind.Human,
            Name = name,
            SessionId = sessionId.ToString(),
            LastSeenUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        // The engine dropped the bot's agent inside TakeSeat, so the chair cannot have
        // two things deciding for it. `table.Agents` is only the list kept for building
        // replacements later, and a spare entry in it plays nothing.
        await profiles.SaveAsync(sessionId);

        log.Info($"[{sessionId}] joined shared table {tableId} in seat {free.Index}");

        await PushAsync(table, "joined", except: sessionId);

        return ViewFor(table, sessionId);
    }

    private async Task<PokerResponse> LeaveCoreAsync(
        string tableId,
        MongoId sessionId,
        ItemEventRouterResponse output)
    {
        if (store.Get(tableId) is not { } table)
        {
            return PokerResponse.Failed("That table is gone.");
        }

        if (table.SeatOf(sessionId) is not { } seat)
        {
            return PokerResponse.Failed("You are not at that table.");
        }

        if (table.Table.Street is not (HoldemStreet.Idle or HoldemStreet.Showdown))
        {
            return PokerResponse.Failed("Finish the hand first.");
        }

        var chips = table.Table.Seats[seat.Index].Stack;
        var last = table.HumanSeats.Count() == 1;

        if (chips > 0)
        {
            bank.Credit(sessionId, table.Wallet, chips, output);
        }

        escrow.Release(sessionId);

        if (last)
        {
            // Nobody left to wait for. The table goes rather than playing itself.
            store.Remove(tableId);
            log.Info($"shared table {tableId} closed -- the last person left with {chips:N0}");
        }
        else
        {
            var seed = Environment.TickCount;
            var character = PokerPersonality.Improvise(new Random(seed));
            var agent = new BotAgent(character, new Random(seed + 1), log.ForEngine());

            table.Table.VacateSeat(seat.Index, agent, character.Name, table.BuyIn);
            table.Agents.Add(agent);

            table.Seats[seat.Index] = new SharedSeat
            {
                Index = seat.Index,
                Kind = SeatKind.Bot,
                Name = character.Name,
            };

            store.Release(sessionId);

            await PushAsync(table, "left");
        }

        await profiles.SaveAsync(sessionId);

        return new PokerResponse
        {
            Balance = bank.GetBalance(sessionId, table.Wallet),
            Wallet = table.Wallet.ToString(),
            Note = $"You stood up with {chips:N0} {WalletInfo.For(table.Wallet).Label}.",
        };
    }

    private async Task<PokerResponse> DealCoreAsync(string tableId, MongoId sessionId)
    {
        if (store.Get(tableId) is not { } table)
        {
            return PokerResponse.Failed("That table is gone.");
        }

        if (table.SeatOf(sessionId) is null)
        {
            return PokerResponse.Failed("You are not at that table.");
        }

        Touch(table, sessionId);

        try
        {
            table.Table.StartHand();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            return ViewFor(table, sessionId) with { Ok = false, Error = ex.Message };
        }

        await PushAsync(table, Moved, except: sessionId);

        return ViewFor(table, sessionId);
    }

    private async Task<PokerResponse> ActCoreAsync(string tableId, ActRequest request, MongoId sessionId)
    {
        if (store.Get(tableId) is not { } table)
        {
            return PokerResponse.Failed("That table is gone.");
        }

        if (table.SeatOf(sessionId) is not { } seat)
        {
            return PokerResponse.Failed("You are not at that table.");
        }

        Touch(table, sessionId);

        if (!table.Table.IsTurnFor(seat.Index))
        {
            return ViewFor(table, sessionId) with { Ok = false, Error = "It is not your turn." };
        }

        if (!Enum.TryParse<HoldemMove>(request.Move, ignoreCase: true, out var move))
        {
            return PokerResponse.Failed($"Unknown move '{request.Move}'. Fold, Check, Call or Raise.");
        }

        try
        {
            table.Table.Act(seat.Index, new HoldemDecision(move, request.To));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            // The engine is the authority on legality. A refusal means this client's view
            // drifted, so hand it the real one back rather than a bare error.
            return ViewFor(table, sessionId) with { Ok = false, Error = ex.Message };
        }

        // Everybody else finds out without asking. This is the whole reason the socket
        // exists: without it the other seats sit on a stale table until they poll.
        await PushAsync(table, Moved, except: sessionId);

        return ViewFor(table, sessionId);
    }

    // ---- pushing, and the rule that makes it safe ----------------------------------

    /// <summary>
    /// Tells every other seated person that the table moved.
    ///
    /// **One message per person, each built for their own seat.** Not a broadcast. The
    /// view carries hole cards, and <see cref="HoldemView.From"/> only omits the ones
    /// belonging to seats other than the viewer -- so a single object sent to everybody
    /// is everybody's cards sent to everybody. A client told not to draw them still has
    /// them, and nothing on the server can take that back.
    ///
    /// A push that does not arrive is not an error. Somebody closing the game mid-hand is
    /// the ordinary case, and <see cref="CasinoSocket.SendAsync(MongoId, string, CancellationToken)"/>
    /// says so by returning false rather than throwing. The table plays on and their seat
    /// times out.
    /// </summary>
    private async Task PushAsync(SharedTable table, string kind, MongoId? except = null)
    {
        var skip = except?.ToString();

        foreach (var seat in table.HumanSeats.ToList())
        {
            if (seat.SessionId is null || seat.SessionId == skip)
            {
                continue;
            }

            // Safe to construct rather than parse: this string was only ever put here
            // from a MongoId in the first place.
            var message = JsonSerializer.Serialize(
                new TableMessage(table.Id, kind, HoldemView.From(table.Table, seat.Index)));

            await socket.SendAsync(new MongoId(seat.SessionId), message);
        }
    }

    // ---- odds and ends -------------------------------------------------------------

    private static SharedTableSummary Summarise(SharedTable table) =>
        new(
            table.Id,
            table.HostName,
            table.Seats.Count,
            table.HumanSeats.Count(),
            table.Seats.Values.Count(seat => seat.Kind == SeatKind.Bot),
            table.BuyIn,
            table.BigBlind,
            table.Wallet.ToString(),
            table.Table.Street is not (HoldemStreet.Idle or HoldemStreet.Showdown));

    /// <summary>Notes that this player is still there, so their seat is not timed out.</summary>
    private static void Touch(SharedTable table, MongoId sessionId)
    {
        if (table.SeatOf(sessionId) is { } seat)
        {
            seat.LastSeenUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }

    private PokerResponse ViewFor(SharedTable table, MongoId sessionId)
    {
        var seat = table.SeatOf(sessionId);

        return new PokerResponse
        {
            Table = seat is null ? null : HoldemView.From(table.Table, seat.Index),
            Characters = table.Seats.Values.OrderBy(s => s.Index).Select(s => s.Name).ToList(),
            Balance = bank.GetBalance(sessionId, table.Wallet),
            Wallet = table.Wallet.ToString(),
        };
    }

    private static PokerResponse? Refuse(SitRequest request, Wallet wallet)
    {
        if (request.Seats is < 2 or > 5)
        {
            return PokerResponse.Failed("A table seats 2 to 5, the player included.");
        }

        if (request.BigBlind < 2)
        {
            return PokerResponse.Failed("The big blind has to be at least 2, so the small blind is a whole chip.");
        }

        if (request.BuyIn < request.BigBlind * 10)
        {
            return PokerResponse.Failed(
                $"A buy-in of {request.BuyIn} is under ten big blinds. There would be nothing to play with.");
        }

        var limits = WalletInfo.For(wallet);

        if (request.BuyIn > limits.MaxBuyIn || request.BuyIn < limits.MinBuyIn)
        {
            return PokerResponse.Failed(
                $"A {request.BuyIn:N0} chip buy-in cannot be paid in {limits.Label}, which takes "
                + $"{limits.MinBuyIn:N0} to {limits.MaxBuyIn:N0}.");
        }

        return null;
    }
}
