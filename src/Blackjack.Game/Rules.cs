namespace Blackjack.Game;

/// <summary>
/// Table rules. Defaults are a standard 6-deck shoe game: dealer stands on all
/// 17s, blackjack pays 3:2. Every value here is exposed as mod config, so the
/// engine must never assume a default is in force.
/// </summary>
public sealed record Rules
{
    public int DeckCount { get; init; } = 6;

    /// <summary>When true the dealer draws to soft 17 instead of standing.</summary>
    public bool DealerHitsSoft17 { get; init; }

    /// <summary>
    /// Default profit multiplier on a natural: 1.5 is the usual 3:2.
    ///
    /// Deal takes an override, because the right answer depends on what is being
    /// staked. Roubles round within a unit nobody can see, so 3:2 is fine. An
    /// indivisible thing cannot pay it -- one bitcoin at 3:2 settles on two and a
    /// half -- so valuables are dealt at even money instead.
    /// </summary>
    public double BlackjackPayout { get; init; } = 1.5;

    public bool DoubleAfterSplit { get; init; } = true;

    /// <summary>Number of splits allowed, so 3 means up to four hands.</summary>
    public int MaxSplits { get; init; } = 3;

    /// <summary>
    /// Split aces normally receive exactly one card each and are then forced to
    /// stand. Turning this off makes split aces play like any other hand.
    /// </summary>
    public bool OneCardAfterAceSplit { get; init; } = true;

    public bool AllowResplitAces { get; init; }

    /// <summary>Fraction of the shoe dealt before it is reshuffled.</summary>
    public double ShufflePenetration { get; init; } = 0.75;

    public int MinBet { get; init; } = 1_000;

    public int MaxBet { get; init; } = 500_000;

    /// <summary>
    /// The most boxes a table can have. Seven is the real-world number and the shoe
    /// is sized for it: eight seats splitting and doubling out of one six-deck shoe
    /// is already close to drawing it dry inside a single round.
    ///
    /// A ceiling rather than a fixed count -- a table is one box unless the caller
    /// asks for more, which is what keeps a solo game exactly what it always was.
    /// </summary>
    public int MaxSeats { get; init; } = 7;
}
