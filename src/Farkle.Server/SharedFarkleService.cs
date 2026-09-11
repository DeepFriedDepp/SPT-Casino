using System.Collections.Concurrent;
using System.Text.Json;
using Casino.Server;
using Farkle.Game;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;

namespace Farkle.Server;

/// <summary>What the server pushes down the socket when a table moves.</summary>
public sealed record FarkleMessage(string Table, string Kind, MatchView? View);

/// <summary>
/// Farkle: two seats, one match, two stakes.
///
/// ## Where the money is
///
/// A **fixed match wager, per player, escrowed at sit-down; the winner is paid both.**
/// Blackjack's bet-then-resolve shape stretched over a match rather than a round. Each
/// stake leaves its own player's stash the moment they sit and is recorded against their
/// own session in `escrow-farkle.json`; nothing is pooled in real currency until
/// settlement, which is what keeps the per-session money code correct here as it was at
/// the other two shared tables.
///
/// Against the house's regular the human still stakes, and the house covers the bot's
/// side: a win pays two stakes, a loss pays nothing. Even money against the house.
///
/// ## The forfeit rule
///
/// **Standing up mid-match pays both stakes to the player who stayed.** Not an inference
/// from "forfeits" -- a named rule, decided with the stakes decision and pinned by its own
/// money test. A player who leaves a bot table mid-match loses their stake to the house
/// the same way. Leaving a table that never found an opponent is not a forfeit: the stake
/// comes straight back.
///
/// ## Why this is a broadcast where poker is not
///
/// Every die is on the table for both players. There is ONE view for the whole table and
/// it goes to everybody unchanged -- the blackjack shape, confirmed from that service
/// rather than inferred from the rules.
///
/// ## THE LOCK ORDER
///
/// Every public method takes <see cref="TableGate"/> FIRST and a <see cref="SessionGate"/>
/// INSIDE it, never the other way round. Every method is a gated wrapper over an ungated
/// `*Core`, because neither gate is reentrant. Read `src/Casino.Server/Gates.cs`.
///
/// ## The quiet seat, and the absent one
///
/// A seat that has not been heard from for <see cref="QuietAfter"/> while it is its turn
/// **yields**: banks what it may, loses what it may not, and the table moves on. That is
/// simpler than blackjack's play-the-round-past-them, because Farkle has a natural pass.
/// A seat absent for <see cref="AbsentAfter"/> at any point in a live match **forfeits**,
/// because a race to 10,000 cannot be finished against an empty chair. Both are driven by
/// whichever request arrives next, including <see cref="StateAsync"/> -- asking for the
/// table is the clock the table runs on.
/// </summary>
[Injectable]
public class SharedFarkleService(
    IBank bank,
    TableGate tables,
    SessionGate sessions,
    SharedFarkleStore store,
    IProfileGateway profiles,
    IEscrowStore escrow,
    IOutputs outputs,
    CasinoSocket socket,
    IRandomSource random,
    IFarkleLog log)
{
    private const string Moved = "table";

    /// <summary>
    /// How long the table waits on the seat whose turn it is before playing on without it.
    /// Ninety seconds: nobody deliberates that long over a keep, and the panel asks for the
    /// table whenever it draws, so a client that is merely idle is still speaking.
    /// </summary>
    public static readonly TimeSpan QuietAfter = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long a human may be silent in a live match before they forfeit it. Long enough
    /// that a raid invite or a hideout trip does not cost a stake; short enough that a
    /// table whose player closed the game is not dead for the rest of the evening.
    /// </summary>
    public static readonly TimeSpan AbsentAfter = TimeSpan.FromMinutes(5);

    private static readonly IReadOnlyList<ScoreLine> ScoringLines =
        Scoring.Table.Select(row => new ScoreLine { Combination = row.Combination, Points = row.Points }).ToList();

    private static readonly IReadOnlyList<double> FarkleChances =
        Enumerable.Range(1, Dice.InPlay).Select(n => Math.Round(Odds.FarkleChance(n) * 100, 2)).ToList();

    private static readonly FarkleRules Rules = new();

    // ---- the gated entrance --------------------------------------------------------

    /// <summary>Tables waiting for a second human. No gate: reads a snapshot, moves nothing.</summary>
    public IReadOnlyList<FarkleSummary> List() =>
        store.All
            .Where(table => table.HasRoom)
            .Select(Summarise)
            .OrderBy(summary => summary.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The health check. Gated because it is not the read it looks like: it hands back a
    /// stake stranded by a server that died mid-match, and it must not do that while a
    /// live sit-down is recording one.
    /// </summary>
    public async Task<PingResponse> PingAsync(MongoId sessionId, ItemEventRouterResponse output)
    {
        using var player = await sessions.EnterAsync(sessionId);

        return await PingCoreAsync(sessionId, output);
    }

    public async Task<FarkleResponse> OpenAsync(OpenTableRequest request, MongoId sessionId, ItemEventRouterResponse output)
    {
        var tableId = Guid.NewGuid().ToString("N")[..8];

        // A table that does not exist yet cannot be gated, so the claim on the PLAYER is
        // what stops two simultaneous opens making two tables and taking two stakes.
        if (!store.TryClaim(sessionId, tableId))
        {
            return FarkleResponse.Failed("You are already at a table. Leave it first.");
        }

        using var player = await sessions.EnterAsync(sessionId);

        FarkleResponse response;

        try
        {
            response = await OpenCoreAsync(request, sessionId, tableId, output);
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

    public async Task<FarkleResponse> JoinAsync(string tableId, MongoId sessionId, ItemEventRouterResponse output)
    {
        if (store.Get(tableId) is null)
        {
            return FarkleResponse.Failed("That table is gone.");
        }

        if (!store.TryClaim(sessionId, tableId))
        {
            return FarkleResponse.Failed("You are already at a table. Leave it first.");
        }

        using var table = await tables.EnterAsync(tableId);

        FarkleResponse response;

        try
        {
            // A join is the one touch a waiting table gets from anybody but its host, so it
            // is where a host who opened a table and vanished is noticed and refunded.
            if (store.Get(tableId) is { } waiting)
            {
                await AdvancePastTheAbsentAsync(waiting);
            }

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

    public Task<FarkleResponse> RollAsync(MongoId sessionId, ItemEventRouterResponse output) =>
        AtTableAsync(sessionId, output, (table, session) => MoveAsync(table, session, output, m => m.RollDice(random.Create())));

    public Task<FarkleResponse> KeepAsync(KeepRequest request, MongoId sessionId, ItemEventRouterResponse output) =>
        AtTableAsync(sessionId, output, (table, session) => MoveAsync(table, session, output, m => m.KeepDice(request.Indices)));

    public Task<FarkleResponse> BankAsync(MongoId sessionId, ItemEventRouterResponse output) =>
        AtTableAsync(sessionId, output, (table, session) => MoveAsync(table, session, output, m => m.Bank()));

    /// <summary>
    /// This player's view of the table. **It takes an output because it can move money**:
    /// asking for the table is what notices somebody else has gone, and playing on past
    /// them can end the match and pay out.
    /// </summary>
    public Task<FarkleResponse> StateAsync(MongoId sessionId, ItemEventRouterResponse output) =>
        AtTableAsync(sessionId, output, (table, session) =>
        {
            Touch(table, session);
            return Task.FromResult(View(table, session));
        });

    public async Task<FarkleResponse> LeaveAsync(MongoId sessionId, ItemEventRouterResponse output)
    {
        if (store.For(sessionId) is not { } seated)
        {
            return FarkleResponse.Failed("You are not at a table.");
        }

        using var table = await tables.EnterAsync(seated.Id);
        await AdvancePastTheAbsentAsync(seated);

        return await LeaveCoreAsync(seated.Id, sessionId, output);
    }

    /// <summary>Table gate, then the clock, then the work. Every in-match route is this shape.</summary>
    private async Task<FarkleResponse> AtTableAsync(
        MongoId sessionId,
        ItemEventRouterResponse output,
        Func<FarkleTable, MongoId, Task<FarkleResponse>> work)
    {
        if (store.For(sessionId) is not { } seated)
        {
            return FarkleResponse.Failed("You are not at a table.");
        }

        using var table = await tables.EnterAsync(seated.Id);

        // Before the work, not after: this player has just been heard from, but whoever
        // the table is waiting on has not, and it is that gap this is reading.
        await AdvancePastTheAbsentAsync(seated);

        return await work(seated, sessionId);
    }

    // ---- the ungated work ----------------------------------------------------------

    private async Task<PingResponse> PingCoreAsync(MongoId sessionId, ItemEventRouterResponse output)
    {
        var known = profiles.HasProfile(sessionId);
        var refunded = await RefundStrandedAsync(sessionId, output);

        return new PingResponse
        {
            ModVersion = TableInfo.Version,
            SessionId = sessionId.ToString(),
            HasProfile = known,
            Balance = known ? bank.GetBalance(sessionId, Wallet.Roubles) : 0,
            MinStake = WalletInfo.For(Wallet.Roubles).MinStake,
            MaxStake = WalletInfo.For(Wallet.Roubles).MaxStake,
            Target = Rules.Target,
            OpeningThreshold = Rules.OpeningThreshold,
            Scoring = ScoringLines,
            FarkleChance = FarkleChances,
            Bots = BotCharacter.All.Select(c => c.Name).ToList(),
            YourTable = store.For(sessionId)?.Id,
            Note = refunded,
        };
    }

    private async Task<FarkleResponse> OpenCoreAsync(
        OpenTableRequest request,
        MongoId sessionId,
        string tableId,
        ItemEventRouterResponse output)
    {
        if (!profiles.HasProfile(sessionId))
        {
            return FarkleResponse.Failed("No PMC profile for this session.");
        }

        if (!WalletInfo.Allows(Wallet.Roubles, request.Stake))
        {
            var info = WalletInfo.For(Wallet.Roubles);
            return FarkleResponse.Failed($"A match is played for {info.MinStake:N0} to {info.MaxStake:N0} roubles.");
        }

        var stake = (int)request.Stake;

        // A row left by a server that died mid-match is theirs, and it goes back before a
        // new one is written over it -- Record would otherwise silently lose the old stake.
        var refunded = await RefundStrandedAsync(sessionId, output);

        if (!TakeStake(sessionId, stake, output, out var refusal))
        {
            return refusal with { Note = refunded };
        }

        var hostName = profiles.NameOf(sessionId) ?? "Seat 0";
        var match = new FarkleMatch(Rules, GameLog.To(log.Detail));
        match.Sit(0, hostName);

        FarkleBot? bot = null;

        if (request.VsBot)
        {
            var character = BotCharacter.Named(request.Bot) ?? BotCharacter.All[random.Create().Next(BotCharacter.All.Count)];
            bot = new FarkleBot(character, GameLog.To(log.Detail));
            match.Sit(1, character.Name, isBot: true);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var table = new FarkleTable
        {
            Id = tableId,
            HostName = hostName,
            Match = match,
            Occupants = new ConcurrentDictionary<int, string>([new KeyValuePair<int, string>(0, sessionId.ToString())]),
            Wallet = Wallet.Roubles,
            Stake = stake,
            VsBot = request.VsBot,
            Bot = bot,
            OpenedAtUtc = now,
        };

        table.LastSeenUtc[sessionId.ToString()] = now;
        store.Add(table);

        await profiles.SaveAsync(sessionId);

        log.Info(
            $"{hostName} opened table {tableId} for {stake:N0} roubles"
            + (bot is null ? ", waiting for an opponent" : $" against {bot.Character.Name}"));

        return View(table, sessionId) with { Note = refunded };
    }

    private async Task<FarkleResponse> JoinCoreAsync(string tableId, MongoId sessionId, ItemEventRouterResponse output)
    {
        if (store.Get(tableId) is not { } table)
        {
            return FarkleResponse.Failed("That table is gone.");
        }

        if (!profiles.HasProfile(sessionId))
        {
            return FarkleResponse.Failed("No PMC profile for this session.");
        }

        if (table.VsBot)
        {
            return FarkleResponse.Failed("That table is against the house. Open your own.");
        }

        if (table.Match.Phase != Phase.WaitingForOpponent)
        {
            return FarkleResponse.Failed("That match has already started.");
        }

        // The table gate is held by the caller; the session gate goes INSIDE it. This is
        // the one place on a join where money moves, and it must not run beside a ping
        // that is refunding this player's stranded row.
        using var player = await sessions.EnterAsync(sessionId);

        var refunded = await RefundStrandedAsync(sessionId, output);

        if (!TakeStake(sessionId, table.Stake, output, out var refusal))
        {
            return refusal with { Note = refunded };
        }

        var name = profiles.NameOf(sessionId) ?? "Seat 1";
        table.Match.Sit(1, name);
        table.Occupants[1] = sessionId.ToString();
        Touch(table, sessionId);

        await profiles.SaveAsync(sessionId);
        await PushAsync(table, "joined");

        log.Info($"{name} joined table {tableId} for {table.Stake:N0} roubles. Match on.");

        return View(table, sessionId) with { Note = refunded };
    }

    /// <summary>
    /// One move by the human whose turn it is, then everything that follows from it: the
    /// bot's whole turn if it is next, settlement if the match ended, and the push.
    /// </summary>
    private async Task<FarkleResponse> MoveAsync(
        FarkleTable table,
        MongoId sessionId,
        ItemEventRouterResponse output,
        Action<FarkleMatch> move)
    {
        if (table.SeatOf(sessionId) is not { } seat)
        {
            return FarkleResponse.Failed("You are not at that table.");
        }

        Touch(table, sessionId);

        if (table.Match.Phase == Phase.WaitingForOpponent)
        {
            return View(table, sessionId) with { Ok = false, Error = "Nobody to play against yet." };
        }

        if (table.Match.Phase == Phase.Finished)
        {
            return View(table, sessionId) with { Ok = false, Error = "The match is over." };
        }

        if (table.Match.CurrentSeat != seat)
        {
            return View(table, sessionId) with { Ok = false, Error = "It is not your turn." };
        }

        try
        {
            move(table.Match);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // The engine is the authority on legality. A refusal means this client's view
            // drifted, so hand back the real one rather than a bare error.
            return View(table, sessionId) with { Ok = false, Error = ex.Message };
        }

        await AfterMoveAsync(table);

        return View(table, sessionId);
    }

    private async Task<FarkleResponse> LeaveCoreAsync(string tableId, MongoId sessionId, ItemEventRouterResponse output)
    {
        if (store.Get(tableId) is not { } table)
        {
            return FarkleResponse.Failed("That table is gone.");
        }

        if (table.SeatOf(sessionId) is not { } seat)
        {
            return FarkleResponse.Failed("You are not at that table.");
        }

        string? note = null;

        switch (table.Match.Phase)
        {
            case Phase.WaitingForOpponent:
            {
                // Nobody ever sat opposite. The stake was held for a match that never
                // happened and it goes straight back.
                using var player = await sessions.EnterAsync(sessionId);

                bank.Credit(sessionId, table.Wallet, table.Stake, output);
                escrow.Release(sessionId);
                await profiles.SaveAsync(sessionId);

                note = $"Nobody sat down. Your {table.Stake:N0} came back with you.";
                log.Info($"{table.HostName} closed table {tableId} unplayed; {table.Stake:N0} refunded.");
                break;
            }

            case Phase.Rolling:
            case Phase.Choosing:
            {
                // THE FORFEIT RULE. Standing up mid-match hands the match, and both stakes,
                // to the player who stayed. Settlement below does the paying; this is the
                // line that decides it.
                table.Match.Forfeit(seat);
                await SettleIfDoneAsync(table);

                note = table.VsBot
                    ? $"You left the match. Your {table.Stake:N0} stays with the house."
                    : $"You left the match. Your {table.Stake:N0} goes to {table.Match.Other(seat).Name}.";
                break;
            }

            case Phase.Finished:
                // Already settled when it finished. Nothing to pay, nothing to refund.
                break;
        }

        table.Occupants.TryRemove(seat, out _);
        table.LastSeenUtc.TryRemove(sessionId.ToString(), out _);

        // Released here, unconditionally, and not left to store.Remove -- which walks the
        // occupants this player has just been taken out of. Blackjack's leave has the
        // whole story of the claim that outlived its table.
        store.Release(sessionId);

        if (table.Occupants.IsEmpty)
        {
            store.Remove(tableId);
        }
        else
        {
            await PushAsync(table, "left");
        }

        return new FarkleResponse
        {
            Balance = bank.GetBalance(sessionId, table.Wallet),
            Wallet = table.Wallet.ToString(),
            Note = note,
        };
    }

    // ---- what follows a move -------------------------------------------------------

    /// <summary>The bot's turn if it is next, settlement if the match ended, and the push.</summary>
    private async Task AfterMoveAsync(FarkleTable table)
    {
        PlayBot(table);
        await SettleIfDoneAsync(table);
        await PushAsync(table, Moved);
    }

    /// <summary>
    /// Plays the bot's whole turn, to the bank or the farkle, inside this request.
    ///
    /// All at once rather than a step per poll, because a bot that needed prodding would
    /// stall the moment its opponent's client stopped asking. The client is handed the
    /// turn's events in the view and replays them at its own pace; the server has no
    /// interest in how long a bot appears to think.
    /// </summary>
    private void PlayBot(FarkleTable table)
    {
        if (table.Bot is not { } bot)
        {
            return;
        }

        var match = table.Match;
        var rng = random.Create();
        var steps = 0;

        while (match.Phase is Phase.Rolling or Phase.Choosing && match.Current.IsBot)
        {
            if (++steps > 200)
            {
                // A turn cannot honestly last this long; hot dice forty times running is
                // not a thing that happens. Whatever this is, end it rather than spin.
                log.Error($"bot turn at table {table.Id} ran away after {steps} steps; yielding.");
                match.Yield();
                break;
            }

            if (match.Phase == Phase.Choosing)
            {
                var decision = bot.Decide(match);
                match.KeepDice(decision.Keep);
                table.BotWillBank = decision.Bank;
                continue;
            }

            if (table.BotWillBank && match.CanBank)
            {
                table.BotWillBank = false;
                match.Bank();
                continue;
            }

            table.BotWillBank = false;
            match.RollDice(rng);
        }

        if (!match.Current.IsBot || match.Phase == Phase.Finished)
        {
            bot.Observe(match, 1);
        }
    }

    /// <summary>
    /// Pays out once, when the match is over.
    ///
    /// The winner is paid **two stakes**, into their own profile, through their own change
    /// record. The loser's row is released with nothing paid: their stake is what the
    /// winner was just paid. On a bot table the house covers the bot's side. Both rows are
    /// released whichever way it went, because a row left behind would refund a stake that
    /// has already been settled.
    ///
    /// Takes each player's session gate INSIDE the table gate the caller holds. That order
    /// is the whole reason <see cref="TableGate"/> exists.
    /// </summary>
    private async Task SettleIfDoneAsync(FarkleTable table)
    {
        if (table.Match.Phase != Phase.Finished || table.Settled || table.Match.Winner is not { } winner)
        {
            return;
        }

        table.Settled = true;

        foreach (var pair in table.Occupants)
        {
            var session = new MongoId(pair.Value);
            var won = pair.Key == winner;

            // That player's own response, not the acting player's. An
            // ItemEventRouterResponse is the change record handed back to ONE client.
            var theirs = outputs.For(session);

            using (await sessions.EnterAsync(session))
            {
                if (won)
                {
                    bank.Credit(session, table.Wallet, table.Stake * 2, theirs);
                }

                escrow.Release(session);
            }

            await profiles.SaveAsync(session);

            log.Info(won
                ? $"{table.Match.Seats[pair.Key].Name} wins table {table.Id} and is paid {table.Stake * 2:N0} roubles ({table.Match.Ending})."
                : $"{table.Match.Seats[pair.Key].Name} loses table {table.Id}; {table.Stake:N0} roubles settled ({table.Match.Ending}).");
        }
    }

    /// <summary>
    /// Plays on past a seat whose player has gone. Called under the table gate before every
    /// in-match request does its own work. See the class remarks.
    /// </summary>
    private async Task AdvancePastTheAbsentAsync(FarkleTable table)
    {
        var match = table.Match;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (match.Phase == Phase.Finished)
        {
            return;
        }

        if (match.Phase == Phase.WaitingForOpponent)
        {
            // A host who opened a table and vanished. The table would otherwise list
            // forever; the stake goes back to them and the table goes.
            if (table.Occupants.TryGetValue(0, out var hostKey)
                && now - table.LastSeenUtc.GetValueOrDefault(hostKey, now) > AbsentAfter.TotalSeconds)
            {
                var host = new MongoId(hostKey);

                using (await sessions.EnterAsync(host))
                {
                    bank.Credit(host, table.Wallet, table.Stake, outputs.For(host));
                    escrow.Release(host);
                }

                await profiles.SaveAsync(host);
                store.Remove(table.Id);
                log.Info($"table {table.Id} closed: {table.HostName} left it unattended. {table.Stake:N0} refunded.");
            }

            return;
        }

        foreach (var pair in table.Occupants)
        {
            var idle = now - table.LastSeenUtc.GetValueOrDefault(pair.Value, now);

            if (idle > AbsentAfter.TotalSeconds)
            {
                // Gone. A race cannot be finished against an empty chair, so it is forfeit
                // -- the same rule as standing up, applied by the clock.
                log.Info($"{match.Seats[pair.Key].Name} has been silent for {idle}s at table {table.Id} and forfeits.");
                match.Forfeit(pair.Key);
                await SettleIfDoneAsync(table);
                await PushAsync(table, Moved);
                return;
            }

            if (pair.Key == match.CurrentSeat && idle > QuietAfter.TotalSeconds)
            {
                log.Info($"{match.Seats[pair.Key].Name} has been quiet for {idle}s on their turn at table {table.Id}; yielding.");
                match.Yield();
                await AfterMoveAsync(table);
                return;
            }
        }
    }

    // ---- money -----------------------------------------------------------------------

    /// <summary>
    /// Takes a stake, recording it first. The order is the slot machine's, deliberately:
    /// record, then debit, and release the record if the debit refuses. Die between the
    /// two lines and the row claims a stake that was never taken, so the next contact
    /// hands back money that never left -- the never-destroy side of the two windows
    /// `CLAUDE.md` describes, and the one every recent table picked.
    /// </summary>
    private bool TakeStake(MongoId sessionId, int stake, ItemEventRouterResponse output, out FarkleResponse refusal)
    {
        escrow.Record(sessionId, Wallet.Roubles, stake);

        if (bank.TryDebit(sessionId, Wallet.Roubles, stake, output))
        {
            refusal = null!;
            return true;
        }

        escrow.Release(sessionId);

        var balance = bank.GetBalance(sessionId, Wallet.Roubles);
        log.Info($"stake refused [{sessionId}] -- {stake:N0} roubles, {balance:N0} held");

        refusal = FarkleResponse.Failed($"A seat here costs {stake:N0} roubles and you have {balance:N0}.");
        return false;
    }

    /// <summary>
    /// Gives back a stake left behind by a match the server did not live to settle.
    ///
    /// A row with no table behind it can only mean the server died with the match in
    /// progress -- tables live in memory and rows do not. Both players' rows come back this
    /// way, each on their own next contact, and at most once.
    ///
    /// **Only when this player is not at a table.** A live row belongs to a live match and
    /// is exactly what settlement will release. The claim check is what tells the two
    /// apart, and it is why Open and Join claim before they record.
    /// </summary>
    private async Task<string?> RefundStrandedAsync(MongoId sessionId, ItemEventRouterResponse output)
    {
        var owed = escrow.Get(sessionId);

        if (owed is null)
        {
            return null;
        }

        if (store.For(sessionId) is { } seated && seated.Match.Phase != Phase.Finished)
        {
            // Legitimately held: they are mid-match. Open and Join cannot reach here with
            // a live table, because the claim would have refused them first.
            if (seated.Occupants.ContainsKey(seated.SeatOf(sessionId) ?? -1))
            {
                return null;
            }
        }

        if (owed.Amount <= 0)
        {
            escrow.Release(sessionId);
            return null;
        }

        bank.Credit(sessionId, Wallet.Roubles, owed.Amount, output);
        escrow.Release(sessionId);
        await profiles.SaveAsync(sessionId);

        log.Info($"gave back {owed.Amount:N0} roubles from a match the server never finished [{sessionId}]");

        return $"A match was interrupted before it was settled. Your {owed.Amount:N0} has been returned.";
    }

    // ---- pushing ---------------------------------------------------------------------

    /// <summary>One object, broadcast to every human at the table. A push that does not arrive is Tuesday.</summary>
    private async Task PushAsync(FarkleTable table, string kind)
    {
        var message = JsonSerializer.Serialize(new FarkleMessage(table.Id, kind, MatchView.From(table.Match)));
        var everybody = table.Sessions.Select(id => new MongoId(id)).ToList();

        await socket.SendAsync(everybody, message);
    }

    // ---- odds and ends ---------------------------------------------------------------

    private static FarkleSummary Summarise(FarkleTable table) =>
        new(table.Id, table.HostName, table.Stake, table.Wallet.ToString(), table.VsBot, table.Match.Phase.ToString());

    private static void Touch(FarkleTable table, MongoId sessionId) =>
        table.LastSeenUtc[sessionId.ToString()] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private FarkleResponse View(FarkleTable table, MongoId sessionId) => new()
    {
        TableId = table.Id,
        YourSeat = table.SeatOf(sessionId),
        Match = MatchView.From(table.Match),
        Stake = table.Stake,
        Wallet = table.Wallet.ToString(),
        VsBot = table.VsBot,
        Settled = table.Settled,
        Balance = bank.GetBalance(sessionId, table.Wallet),
    };
}
