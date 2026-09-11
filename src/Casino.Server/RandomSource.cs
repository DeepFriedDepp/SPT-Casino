using SPTarkov.DI.Annotations;

namespace Casino.Server;

/// <summary>
/// Where a table gets its randomness.
///
/// An interface so a test can seed it. The alternative -- letting a service call
/// `new Random()` -- makes every money test depend on which pocket the ball found or
/// which faces the dice showed, which is the one thing a money test must not care about.
///
/// ## Why this lives here and not in a table
///
/// Roulette and Slots each carry an identical copy of this pair in their own
/// namespaces, and Poker and Blackjack build a `Random` inline. Farkle would have been
/// the third copy. `CLAUDE.md`'s rule is to extract at the second case, and the second
/// case had already happened, so the shared one is here beside the gates. The two
/// existing copies are left alone until somebody is in those files for another reason;
/// they are in different namespaces and nothing collides.
/// </summary>
public interface IRandomSource
{
    Random Create();
}

/// <summary>
/// `Random.Shared` rather than a field: it is thread-safe, which a shared `new Random()`
/// is not, and this is a singleton reached from request threads.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class RandomSource : IRandomSource
{
    public Random Create() => Random.Shared;
}
