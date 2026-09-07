namespace Blackjack.Game;

/// <summary>
/// One table: the shoe, the dealer, and the boxes people play out of. Owns the
/// entire rule set, so the transport layer above it decides nothing about the
/// game -- it only converts a view to JSON and moves roubles.
///
/// **One shoe, one dealer hand, however many seats.** That is the whole point of
/// sitting together: cards deplete for everybody, and every seat lives or dies
/// against the same upcard. Seats with their own shoes and their own dealers would
/// not be a table, they would be people sitting side by side.
///
/// A table is one seat unless the caller says otherwise, and one seat behaves
/// exactly as it did before seats existed -- <see cref="Deal(int, double?)"/>,
/// <see cref="Hit()"/> and friends are the shorthand for it, and
/// <see cref="View"/> is still the one-seat snapshot. Those shorthands refuse to
/// answer once a second person sits down, for the reason spelled out on
/// <see cref="Solo"/>.
/// </summary>
public sealed class BlackjackTable
{
    private readonly Rules _rules;
    private readonly Shoe _shoe;
    private readonly List<BlackjackSeat> _seats = [];
    private Hand _dealer = new(0);

    /// <summary>
    /// The seat the table is waiting on. Past the last seat means nobody is, which
    /// only happens between rounds -- <see cref="Advance"/> finishes the round
    /// rather than leaving it pointing nowhere.
    /// </summary>
    private int _activeSeat;

    public BlackjackTable(
        Rules? rules = null,
        Random? rng = null,
        int seats = 1,
        IReadOnlyCollection<int>? occupiedSeats = null,
        IReadOnlyDictionary<int, string>? seatNames = null)
    {
        _rules = rules ?? new Rules();
        _shoe = new Shoe(_rules.DeckCount, rng);
        BuildSeats(seats, occupiedSeats, seatNames);
    }

    /// <summary>Test seam: run the table against a stacked shoe.</summary>
    /// <param name="occupiedSeats">
    /// Which boxes have somebody standing at them. Seat 0 alone by default, which is
    /// every caller that predates several people sharing a table.
    /// </param>
    /// <param name="seatNames">
    /// What to call the people, by seat. A seat with no name here is "You", which is
    /// right for the one-seat table and is why this can stay optional. Keyed by seat
    /// rather than given as a list parallel to <paramref name="occupiedSeats"/>,
    /// because two collections that have to agree on an order are two collections
    /// that will eventually disagree -- and the failure would be a player wearing
    /// somebody else's name over somebody else's cards.
    /// </param>
    public BlackjackTable(
        Rules rules,
        Shoe shoe,
        int seats = 1,
        IReadOnlyCollection<int>? occupiedSeats = null,
        IReadOnlyDictionary<int, string>? seatNames = null)
    {
        _rules = rules;
        _shoe = shoe;
        BuildSeats(seats, occupiedSeats, seatNames);
    }

    /// <summary>
    /// Where somebody sits when nobody said otherwise, and the seat the one-seat
    /// shorthands are about.
    /// </summary>
    public const int PlayerSeatIndex = 0;

    public Rules Rules => _rules;

    public RoundPhase Phase { get; private set; } = RoundPhase.AwaitingBet;

    /// <summary>
    /// Every box, empty ones included. Fixed for the life of the table: a five-box
    /// table that silently became four when somebody stood up would renumber
    /// everybody else's seat, and a client holding seat 3 would start acting on
    /// somebody else's cards.
    /// </summary>
    public IReadOnlyList<BlackjackSeat> Seats => _seats;

    /// <summary>Cards left before the shoe is reshuffled. Shared by every seat.</summary>
    public int ShoeRemaining => _shoe.Remaining;

    /// <summary>The seat to act, or null when nothing is waiting on a decision.</summary>
    public int? ActiveSeat =>
        Phase == RoundPhase.PlayerTurn && _activeSeat < _seats.Count ? _activeSeat : null;

    /// <summary>
    /// True when this seat is the one the table is waiting on.
    ///
    /// The question a client actually has, and the only one that stays answerable
    /// with several people at the table. A seat off the table throws rather than
    /// answering false: a box that is never anybody's turn is indistinguishable from
    /// one that is merely waiting, and the caller would sit forever.
    /// </summary>
    public bool IsTurnFor(int seatIndex)
    {
        RequireSeat(seatIndex);

        return ActiveSeat == seatIndex;
    }

    /// <summary>
    /// The one person at the table.
    ///
    /// Refuses to answer once there is more than one, or when the single person is
    /// not at <see cref="PlayerSeatIndex"/>. Returning seat 0 regardless would be
    /// the dangerous reading: every caller of this asks about somebody's cards or
    /// somebody's money, and handing seat 0's to whoever happened to ask is a bug
    /// that no test of a one-seat table can see.
    /// </summary>
    private BlackjackSeat Solo =>
        _seats[PlayerSeatIndex].IsOccupied && _seats.Count(seat => seat.IsOccupied) == 1
            ? _seats[PlayerSeatIndex]
            : throw new InvalidOperationException(
                "This table seats people at "
                + string.Join(", ", _seats.Where(seat => seat.IsOccupied).Select(seat => seat.Index))
                + ", so \"the player\" is ambiguous. Ask about a seat: PlaceBet(index, wager), "
                + "Hit(index), AvailableActions(index), ViewTable().Seats[index].");

    /// <summary>
    /// Which of the one player's hands is being played. See <see cref="Solo"/> for
    /// why this refuses to answer on a shared table.
    /// </summary>
    public int ActiveHandIndex => Solo.ActiveHandIndex;

    /// <summary>
    /// Total currently at risk. The caller debits the difference between this
    /// before and after an action, which is how doubling and splitting collect
    /// their extra stake without the engine knowing what a rouble is.
    ///
    /// One player's, and it throws rather than summing a shared table -- see
    /// <see cref="Solo"/>. Per-seat money is on <see cref="BlackjackSeat"/>.
    /// </summary>
    public int TotalWagered => Solo.TotalWagered;

    public int TotalReturned => Solo.TotalReturned;

    /// <summary>
    /// Puts a bet up for the round that has not started yet, and fixes what a
    /// natural pays this seat.
    ///
    /// Betting is a phase, not an instant: everybody bets, then somebody deals. The
    /// phase has no name of its own because <see cref="RoundPhase.AwaitingBet"/> and
    /// <see cref="RoundPhase.Settled"/> already mean "between rounds", and inventing
    /// a third value would have gone over the wire to clients that do not know it.
    ///
    /// <paramref name="blackjackPayout"/> overrides the table default for this seat
    /// and this round only -- the caller varies it by what is being staked, and one
    /// shoe serves every currency, so it cannot live on the table.
    /// </summary>
    public TableView PlaceBet(int seatIndex, int wager, double? blackjackPayout = null)
    {
        RequireSeat(seatIndex);
        PlaceBetCore(_seats[seatIndex], wager, blackjackPayout);

        return ViewTable();
    }

    /// <summary>
    /// Deals the round to whoever bet.
    ///
    /// **A seat that has not bet sits this round out.** It is dealt nothing, takes no
    /// turn and settles nothing. Blackjack has no fold, so sitting out is the
    /// legitimate state a quiet or disconnected seat falls into -- and because a bet
    /// never carries over, that happens on its own from the next round.
    /// </summary>
    public TableView StartRound()
    {
        StartRoundCore();

        return ViewTable();
    }

    /// <summary>
    /// Deals a round to the one player. Bet and deal in one call, which is what a
    /// table with nobody else at it means by "deal".
    ///
    /// Unchanged, and still the whole of the single-player API. See
    /// <see cref="Solo"/> for why it refuses once somebody else is seated.
    /// </summary>
    public RoundView Deal(int wager, double? blackjackPayout = null)
    {
        var seat = Solo;

        PlaceBetCore(seat, wager, blackjackPayout);
        StartRoundCore();

        return View();
    }

    /// <summary>
    /// A person sits down at a box.
    ///
    /// **Between rounds only.** Seating somebody mid-round would deal them into a
    /// hand they have not paid into, out of a shoe the table has already dealt from.
    /// That is also ordinary blackjack: you wait for the round to finish.
    /// </summary>
    public void TakeSeat(int seatIndex, string name)
    {
        RequireBetweenRounds("sit down at");

        var seat = SeatAt(seatIndex);

        if (seat.IsOccupied)
        {
            throw new InvalidOperationException(
                $"Seat {seatIndex} is already {seat.Name}'s. Two people cannot share a box.");
        }

        seat.IsOccupied = true;
        seat.Name = name;
        seat.PendingBet = 0;
        seat.ClearForNewRound();
    }

    /// <summary>
    /// A person leaves, and the box goes empty.
    ///
    /// Empty rather than handed to a bot, which is where this parts company with
    /// hold'em: blackjack has no bots, and an empty box changes nothing for anybody
    /// still at the table -- it is skipped, and every remaining seat still plays the
    /// same one dealer hand out of the same one shoe.
    ///
    /// Between rounds only, for the same reason as <see cref="TakeSeat"/>. The
    /// caller settles first: the seat's hands go with it, so a player standing up
    /// before their winnings have been paid takes the record of them along.
    /// </summary>
    public void VacateSeat(int seatIndex)
    {
        RequireBetweenRounds("stand up from");

        var seat = SeatAt(seatIndex);

        if (!seat.IsOccupied)
        {
            throw new InvalidOperationException($"Seat {seatIndex} is empty already.");
        }

        if (_seats.Count(occupant => occupant.IsOccupied) == 1)
        {
            throw new InvalidOperationException(
                "That is the last person at the table. A table with nobody at it has nobody to deal "
                + "to, so the caller closes the table instead of emptying it.");
        }

        seat.IsOccupied = false;
        seat.Name = DefaultName(seatIndex);
        seat.PendingBet = 0;
        seat.ClearForNewRound();
    }

    public TableView Hit(int seatIndex)
    {
        var hand = RequireActionable(seatIndex, PlayerAction.Hit);
        hand.Add(_shoe.Draw());

        // Stand on 21 automatically -- hitting it is never correct, and leaving the
        // hand active invites a client to send a drawing action that busts it.
        if (hand.Status == HandStatus.Active && hand.Value == 21)
        {
            hand.Status = HandStatus.Stood;
        }

        Advance();
        return ViewTable();
    }

    public TableView Stand(int seatIndex)
    {
        var hand = RequireActionable(seatIndex, PlayerAction.Stand);
        hand.Status = HandStatus.Stood;

        Advance();
        return ViewTable();
    }

    public TableView Double(int seatIndex)
    {
        var hand = RequireActionable(seatIndex, PlayerAction.Double);
        hand.DoubleWager();
        hand.Add(_shoe.Draw());

        // Add() flips the status to Bust on its own; only a surviving hand stands.
        if (hand.Status == HandStatus.Active)
        {
            hand.Status = HandStatus.Doubled;
        }

        Advance();
        return ViewTable();
    }

    public TableView Split(int seatIndex)
    {
        var seat = _seats[RequireSeat(seatIndex)];
        var hand = RequireActionable(seatIndex, PlayerAction.Split);

        var moved = hand.RemoveSecondCard();
        var splitAces = moved.IsAce;

        var newHand = new Hand(hand.Wager, fromSplit: true);
        newHand.Add(moved);

        // The original hand is a split hand too now -- without this, a ten landing
        // on it would score as a natural and pay 3:2.
        hand.IsFromSplit = true;
        seat.HandList.Insert(seat.ActiveHandIndex + 1, newHand);
        seat.SplitsUsed++;

        hand.Add(_shoe.Draw());
        newHand.Add(_shoe.Draw());

        if (splitAces && _rules.OneCardAfterAceSplit)
        {
            StandUnlessResplittable(seat, hand);
            StandUnlessResplittable(seat, newHand);
        }

        Advance();
        return ViewTable();
    }

    /// <summary>What this seat may legally do. Empty when it is not its turn.</summary>
    public IReadOnlyList<PlayerAction> AvailableActions(int seatIndex)
    {
        RequireSeat(seatIndex);

        if (Phase != RoundPhase.PlayerTurn || _activeSeat != seatIndex)
        {
            return [];
        }

        var seat = _seats[seatIndex];
        if (seat.ActiveHandIndex >= seat.Hands.Count)
        {
            return [];
        }

        var hand = seat.Hands[seat.ActiveHandIndex];
        if (hand.Status != HandStatus.Active)
        {
            return [];
        }

        var actions = new List<PlayerAction> { PlayerAction.Stand };

        if (hand.Value < 21)
        {
            actions.Add(PlayerAction.Hit);
        }

        // Doubling and splitting are first-decision-only; both need a pristine
        // two-card hand.
        if (hand.Cards.Count == 2 && (!hand.IsFromSplit || _rules.DoubleAfterSplit))
        {
            actions.Add(PlayerAction.Double);
        }

        if (CanSplitHand(seat, hand))
        {
            actions.Add(PlayerAction.Split);
        }

        return actions;
    }

    // The one-seat shorthands. Each is the seat-addressed call aimed at the only
    // person there, and each throws rather than guessing once there is more than
    // one -- letting them guess would let one player hit another's hand, which is
    // the worst bug this file could grow.

    public RoundView Hit()
    {
        Hit(Solo.Index);
        return View();
    }

    public RoundView Stand()
    {
        Stand(Solo.Index);
        return View();
    }

    public RoundView Double()
    {
        Double(Solo.Index);
        return View();
    }

    public RoundView Split()
    {
        Split(Solo.Index);
        return View();
    }

    public IReadOnlyList<PlayerAction> AvailableActions() => AvailableActions(Solo.Index);

    /// <summary>
    /// The whole table as everybody sees it: every box, the one dealer hand, the one
    /// shoe. Nothing is filtered per viewer -- see <see cref="SeatView"/> for why
    /// blackjack needs no such thing and hold'em did.
    ///
    /// The dealer's hole card is absent until the dealer's turn, hidden from every
    /// seat equally.
    /// </summary>
    public TableView ViewTable()
    {
        var revealDealer = Phase is RoundPhase.DealerTurn or RoundPhase.Settled;

        return new TableView(
            Phase,
            _seats.Select(ToView).ToList(),
            DealerView(revealDealer),
            ActiveSeat,
            _shoe.Remaining);
    }

    /// <summary>
    /// The one-seat snapshot, unchanged: this player's hands, this player's money.
    ///
    /// Throws on a shared table rather than describing seat 0 -- see
    /// <see cref="Solo"/>. A shared table calls <see cref="ViewTable"/>, which
    /// carries the same state with the money attached to the seat that owns it.
    ///
    /// The dealer's hole card is omitted from the payload entirely during the
    /// player's turn rather than blanked out. Anything sent to the client is
    /// knowable by the client.
    /// </summary>
    public RoundView View()
    {
        var seat = Solo;
        var revealDealer = Phase is RoundPhase.DealerTurn or RoundPhase.Settled;

        return new RoundView(
            Phase,
            seat.Hands.Select(ToView).ToList(),
            DealerView(revealDealer),
            seat.ActiveHandIndex,
            AvailableActions(seat.Index),
            seat.TotalWagered,
            seat.TotalReturned,
            _shoe.Remaining);
    }

    private void BuildSeats(
        int seats,
        IReadOnlyCollection<int>? occupiedSeats,
        IReadOnlyDictionary<int, string>? seatNames)
    {
        if (seats < 1 || seats > _rules.MaxSeats)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seats), seats, $"A blackjack table has 1 to {_rules.MaxSeats} boxes.");
        }

        var occupied = (occupiedSeats ?? [PlayerSeatIndex]).ToList();

        if (occupied.Count == 0)
        {
            throw new ArgumentException(
                "A table with nobody at it has nothing to deal to; give it at least one occupied seat.",
                nameof(occupiedSeats));
        }

        if (occupied.Any(index => index < 0 || index >= seats))
        {
            throw new ArgumentOutOfRangeException(
                nameof(occupiedSeats),
                string.Join(", ", occupied),
                $"A {seats}-box table has seats 0 to {seats - 1}.");
        }

        if (occupied.Distinct().Count() != occupied.Count)
        {
            throw new ArgumentException(
                "Two people cannot share seat "
                + occupied.GroupBy(index => index).First(group => group.Count() > 1).Key + ".",
                nameof(occupiedSeats));
        }

        for (var index = 0; index < seats; index++)
        {
            var isOccupied = occupied.Contains(index);
            var name = seatNames is not null && seatNames.TryGetValue(index, out var given)
                ? given
                : isOccupied ? "You" : DefaultName(index);

            _seats.Add(new BlackjackSeat(index, isOccupied, name));
        }
    }

    private static string DefaultName(int index) => $"Seat {index}";

    private void PlaceBetCore(BlackjackSeat seat, int wager, double? blackjackPayout)
    {
        // Phase before anything else, so dealing twice reads as "a round is already
        // in progress" rather than as a complaint about a perfectly good wager.
        if (Phase is not (RoundPhase.AwaitingBet or RoundPhase.Settled))
        {
            throw new InvalidOperationException("A round is already in progress.");
        }

        if (!seat.IsOccupied)
        {
            throw new InvalidOperationException($"Seat {seat.Index} is empty; nobody can bet from it.");
        }

        if (wager < _rules.MinBet || wager > _rules.MaxBet)
        {
            throw new ArgumentOutOfRangeException(
                nameof(wager),
                wager,
                $"Wager must be between {_rules.MinBet} and {_rules.MaxBet}.");
        }

        seat.PendingBet = wager;
        seat.BlackjackPayout = blackjackPayout ?? _rules.BlackjackPayout;
    }

    private void StartRoundCore()
    {
        if (Phase is not (RoundPhase.AwaitingBet or RoundPhase.Settled))
        {
            throw new InvalidOperationException("A round is already in progress.");
        }

        // Read before anything is cleared: ClearForNewRound wipes the previous
        // round's hands, and the bets that decide who plays this one are still on
        // the seats at that moment.
        var playing = _seats.Where(seat => seat.IsOccupied && seat.PendingBet > 0).ToList();

        if (playing.Count == 0)
        {
            throw new InvalidOperationException("Nobody has bet, so there is nothing to deal.");
        }

        foreach (var seat in _seats)
        {
            seat.ClearForNewRound();
        }

        _dealer = new Hand(0);
        _activeSeat = 0;

        // Only ever reshuffle between rounds. Doing it mid-round would change the
        // composition of a shoe the players have already seen cards from -- and with
        // several of them watching, they have seen a great deal more of it.
        if (_shoe.NeedsShuffle(_rules.ShufflePenetration))
        {
            _shoe.Shuffle();
        }

        foreach (var seat in playing)
        {
            seat.IsInRound = true;
            seat.HandList.Add(new Hand(seat.PendingBet));
            seat.PendingBet = 0;
        }

        Phase = RoundPhase.PlayerTurn;

        // Real dealing order: one card to each box in turn, the dealer's upcard, a
        // second card to each box, then the hole card. With one box that is exactly
        // player-dealer-player-dealer, which is what it always was.
        foreach (var seat in playing)
        {
            seat.HandList[0].Add(_shoe.Draw());
        }

        _dealer.Add(_shoe.Draw());

        foreach (var seat in playing)
        {
            seat.HandList[0].Add(_shoe.Draw());
        }

        _dealer.Add(_shoe.Draw());

        // The dealer peeks on a ten or ace showing. Resolving here means nobody
        // doubles or splits into a round that was already lost.
        if (_dealer.IsBlackjack)
        {
            foreach (var seat in playing)
            {
                var hand = seat.HandList[0];
                hand.Status = hand.IsBlackjack ? HandStatus.Blackjack : HandStatus.Stood;
            }

            FinishRound();
            return;
        }

        foreach (var seat in playing)
        {
            var hand = seat.HandList[0];
            if (hand.IsBlackjack)
            {
                hand.Status = HandStatus.Blackjack;
            }
        }

        // Not an unconditional "seat 0 acts": a natural is already finished, so the
        // turn has to walk to the first seat that actually has a decision -- and if
        // every seat was dealt one, straight through to the dealer.
        Advance();
    }

    private bool CanSplitHand(BlackjackSeat seat, Hand hand)
    {
        if (!hand.CanSplit || seat.SplitsUsed >= _rules.MaxSplits)
        {
            return false;
        }

        // Re-splitting aces is a separate permission from splitting them once.
        return !hand.Cards[0].IsAce || !hand.IsFromSplit || _rules.AllowResplitAces;
    }

    private void StandUnlessResplittable(BlackjackSeat seat, Hand hand)
    {
        if (!CanSplitHand(seat, hand))
        {
            hand.Status = HandStatus.Stood;
        }
    }

    private int RequireSeat(int seatIndex) =>
        seatIndex >= 0 && seatIndex < _seats.Count
            ? seatIndex
            : throw new ArgumentOutOfRangeException(
                nameof(seatIndex), seatIndex, $"This table has seats 0 to {_seats.Count - 1}.");

    private BlackjackSeat SeatAt(int seatIndex) => _seats[RequireSeat(seatIndex)];

    private void RequireBetweenRounds(string what)
    {
        if (Phase is not (RoundPhase.AwaitingBet or RoundPhase.Settled))
        {
            throw new InvalidOperationException(
                $"Nobody can {what} a table in the middle of a round; it is {Phase}.");
        }
    }

    private Hand RequireActionable(int seatIndex, PlayerAction action)
    {
        RequireSeat(seatIndex);

        if (Phase != RoundPhase.PlayerTurn)
        {
            throw new InvalidOperationException($"Cannot {action} while the round is {Phase}.");
        }

        // The seat is passed in rather than read off whose turn it is, because
        // inferring it means a client whose action arrives a moment late acts for
        // whoever the table has moved on to. Refusing the stale action is the entire
        // point of the argument.
        if (_activeSeat != seatIndex)
        {
            throw new InvalidOperationException(
                $"It is seat {_activeSeat}'s turn ({_seats[_activeSeat].Name}), not seat {seatIndex}'s.");
        }

        if (!AvailableActions(seatIndex).Contains(action))
        {
            throw new InvalidOperationException($"{action} is not legal on the current hand.");
        }

        var seat = _seats[seatIndex];
        return seat.HandList[seat.ActiveHandIndex];
    }

    /// <summary>
    /// Walks the turn on: through this seat's hands first, then seat by seat in
    /// ascending order, then the dealer. Empty boxes and seats that did not bet are
    /// skipped, which is the whole of what sitting a round out costs the table.
    /// </summary>
    private void Advance()
    {
        while (_activeSeat < _seats.Count)
        {
            var seat = _seats[_activeSeat];

            if (seat.IsInRound)
            {
                while (seat.ActiveHandIndex < seat.Hands.Count
                    && seat.Hands[seat.ActiveHandIndex].Status != HandStatus.Active)
                {
                    seat.ActiveHandIndex++;
                }

                if (seat.ActiveHandIndex < seat.Hands.Count)
                {
                    return;
                }
            }

            _activeSeat++;
        }

        FinishRound();
    }

    private void FinishRound()
    {
        Phase = RoundPhase.DealerTurn;

        // The dealer only draws when a live hand somewhere at the table can still be
        // beaten. If every seat busted or won outright with a natural, the house has
        // nothing to play for -- and because there is one dealer hand, one seat still
        // standing is enough to make it draw for everybody.
        var anyLive = _seats.Any(seat =>
            seat.IsInRound && seat.Hands.Any(hand => hand.Status is HandStatus.Stood or HandStatus.Doubled));

        if (anyLive && !_dealer.IsBlackjack)
        {
            PlayDealer();
        }

        Settle();
        Phase = RoundPhase.Settled;
    }

    private void PlayDealer()
    {
        while (true)
        {
            var value = _dealer.Value;
            if (value < 17 || (value == 17 && _dealer.IsSoft && _rules.DealerHitsSoft17))
            {
                _dealer.Add(_shoe.Draw());
                continue;
            }

            break;
        }

        _dealer.Status = _dealer.IsBust ? HandStatus.Bust : HandStatus.Stood;
    }

    private void Settle()
    {
        var dealerNatural = _dealer.IsBlackjack;
        var dealerValue = _dealer.Value;

        // Every seat settles against the same dealer hand, and against nothing else.
        // Hands are adjudicated one at a time and pay into the seat that holds them,
        // which is the only thing keeping two people's money apart.
        foreach (var seat in _seats.Where(seat => seat.IsInRound))
        {
            foreach (var hand in seat.Hands)
            {
                if (hand.Status == HandStatus.Bust)
                {
                    hand.Outcome = HandOutcome.Bust;
                    hand.Returned = 0;
                    continue;
                }

                if (dealerNatural)
                {
                    // A natural beats a non-natural 21, so this cannot fall through to
                    // the numeric comparison below -- that would score it a push.
                    hand.Outcome = hand.Status == HandStatus.Blackjack ? HandOutcome.Push : HandOutcome.Lose;
                    hand.Returned = hand.Outcome == HandOutcome.Push ? hand.Wager : 0;
                    continue;
                }

                if (hand.Status == HandStatus.Blackjack)
                {
                    hand.Outcome = HandOutcome.Blackjack;
                    hand.Returned = hand.Wager
                        + (int)Math.Round(hand.Wager * seat.BlackjackPayout, MidpointRounding.AwayFromZero);
                    continue;
                }

                if (_dealer.IsBust || hand.Value > dealerValue)
                {
                    hand.Outcome = HandOutcome.Win;
                    hand.Returned = hand.Wager * 2;
                }
                else if (hand.Value == dealerValue)
                {
                    hand.Outcome = HandOutcome.Push;
                    hand.Returned = hand.Wager;
                }
                else
                {
                    hand.Outcome = HandOutcome.Lose;
                    hand.Returned = 0;
                }
            }
        }
    }

    private static HandView ToView(Hand hand) => new(
        hand.Cards.Select(card => card.Code).ToList(),
        hand.Value,
        hand.IsSoft,
        hand.Wager,
        hand.Status,
        hand.Outcome,
        hand.Returned);

    private SeatView ToView(BlackjackSeat seat) => new(
        seat.Index,
        seat.Name,
        seat.IsOccupied,
        seat.IsInRound,
        seat.PendingBet,
        seat.Hands.Select(ToView).ToList(),
        seat.ActiveHandIndex,
        AvailableActions(seat.Index),
        seat.TotalWagered,
        seat.TotalReturned);

    private HandView DealerView(bool reveal)
    {
        // Before the first deal there is no dealer hand to describe. The client asks
        // for state the moment the panel opens, so this path is hit on every visit.
        if (reveal || _dealer.Cards.Count == 0)
        {
            return ToView(_dealer);
        }

        // The hole card is omitted from the payload entirely rather than blanked
        // out. Anything sent to the client is knowable by the client.
        var upcard = _dealer.Cards[0];
        return new HandView(
            [upcard.Code],
            upcard.IsAce ? 11 : upcard.BaseValue,
            upcard.IsAce,
            0,
            HandStatus.Active,
            HandOutcome.Pending,
            0);
    }
}
