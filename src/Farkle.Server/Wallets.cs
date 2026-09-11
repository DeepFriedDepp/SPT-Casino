using SPTarkov.Server.Core.Models.Common;

namespace Farkle.Server;

/// <summary>
/// What a match is played for. Roubles, and only roubles: decision #2 in the work order.
///
/// Kept as an enum rather than a constant so <see cref="Bank"/> keeps the shape the
/// other four banks have, which is what lets it be read side by side with them when
/// one of them turns up a bug.
/// </summary>
public enum Wallet
{
    Roubles,
}

/// <summary>A currency, and what one match may be staked in it.</summary>
public sealed record WalletInfo(Wallet Wallet, MongoId Tpl, string Sign, string Label, int MinStake, int MaxStake, int Step)
{
    private static readonly MongoId RoublesTpl = new("5449016a4bdc2d6f028b456f");

    /// <summary>
    /// Ten thousand to a million. The floor is where a match stops being a free game
    /// with a formality attached; the ceiling is two maximum rouble stacks paid to the
    /// winner, which is about the most a stash takes back without a trip to the mail.
    /// </summary>
    private static readonly Dictionary<Wallet, WalletInfo> Table = new()
    {
        [Wallet.Roubles] = new(Wallet.Roubles, RoublesTpl, "R", "Roubles", 10_000, 1_000_000, 10_000),
    };

    public static WalletInfo For(Wallet wallet) => Table[wallet];

    public static IEnumerable<WalletInfo> All => Table.Values;

    /// <summary>Any whole amount between the two ends. Checked here, not trusted from the panel.</summary>
    public static bool Allows(Wallet wallet, long stake)
    {
        var info = For(wallet);

        return stake >= info.MinStake && stake <= info.MaxStake;
    }
}
