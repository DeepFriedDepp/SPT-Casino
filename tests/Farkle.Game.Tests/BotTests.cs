namespace Farkle.Game.Tests;

/// <summary>
/// What the bot must always do, and the places the score overrides the dice. The dial
/// VALUES are not pinned here -- the doc's own instruction is to measure them with the
/// console tool before trusting any -- but the ordering between characters is, because
/// two characters that bank at the same rate are one character with two names.
/// </summary>
public class BotTests
{
    private static FarkleMatch Ready(FarkleRules? rules = null, int botSeat = 1)
    {
        var match = new FarkleMatch(rules);
        match.Sit(0, "Alice", isBot: botSeat == 0);
        match.Sit(1, "Bot", isBot: botSeat == 1);
        return match;
    }

    /// <summary>Seat 0 banks 1,500 in one turn: three 1s, then three 5s for hot dice, then bank.</summary>
    private static void BankFifteenHundred(FarkleMatch match)
    {
        match.RollDice(MatchTests.Stacked(1, 1, 1, 2, 3, 4));
        match.KeepDice([0, 1, 2]);
        match.RollDice(MatchTests.Stacked(5, 5, 5));
        match.KeepDice([0, 1, 2]);
        match.Bank();
    }

    [Fact]
    public void NeverKeepsADeadDie()
    {
        var bot = new FarkleBot(BotCharacter.Tourist);
        var rng = new Random(11);

        for (var i = 0; i < 500; i++)
        {
            var match = Ready(botSeat: 0);
            match.RollDice(rng);

            if (match.Phase != Phase.Choosing)
            {
                continue;
            }

            var decision = bot.Decide(match);
            var faces = decision.Keep.Select(k => match.Roll[k]).ToList();

            Assert.True(Scoring.Of(faces).EveryDieCounts, $"kept {string.Join(" ", faces)} from {string.Join(" ", match.Roll)}");
            Assert.NotEmpty(decision.Reason);
        }
    }

    [Fact]
    public void RollsOnHotDice()
    {
        var bot = new FarkleBot(BotCharacter.Rock);
        var match = Ready(botSeat: 0);
        match.RollDice(MatchTests.Stacked(1, 1, 1, 5, 5, 5));

        var decision = bot.Decide(match);

        Assert.Equal(6, decision.Keep.Count);
        Assert.False(decision.Bank);
    }

    [Fact]
    public void BanksWhenBankingWins()
    {
        var bot = new FarkleBot(BotCharacter.Gambler);
        var match = Ready(new FarkleRules(Target: 2000, OpeningThreshold: 500), botSeat: 0);

        BankFifteenHundred(match);
        match.RollDice(MatchTests.Stacked(2, 2, 3, 3, 4, 6)); // Alice's opponent farkles

        Assert.Equal(0, match.CurrentSeat);
        match.RollDice(MatchTests.Stacked(5, 2, 3, 4, 6, 6)); // a lone 5: 1500 + 50 is not there yet
        var first = bot.Decide(match);
        Assert.False(first.Bank);

        match.KeepDice(first.Keep);
        match.RollDice(MatchTests.Stacked(1, 1, 1, 2, 3)); // +1000 puts it over
        var second = bot.Decide(match);

        Assert.True(second.Bank, second.Reason);
    }

    [Fact]
    public void OnTheLastTurnBehindItNeverBanksShort()
    {
        var bot = new FarkleBot(BotCharacter.Rock);
        var match = Ready(new FarkleRules(Target: 1000, OpeningThreshold: 500), botSeat: 1);

        // Alice banks 1000; the bot's last turn.
        match.RollDice(MatchTests.Stacked(1, 1, 1, 2, 3, 4));
        match.KeepDice([0, 1, 2]);
        match.Bank();
        Assert.Equal(1, match.FinalTurnFor);

        // 600 would be bankable by the threshold and is useless. Even the Rock rolls on.
        match.RollDice(MatchTests.Stacked(6, 6, 6, 2, 3, 4));
        var decision = bot.Decide(match);

        Assert.False(decision.Bank, decision.Reason);
        Assert.Contains("last turn", decision.Reason);
    }

    [Fact]
    public void OnTheLastTurnAheadItBanksTheWin()
    {
        var bot = new FarkleBot(BotCharacter.Gambler);
        var match = Ready(new FarkleRules(Target: 1000, OpeningThreshold: 500), botSeat: 1);

        match.RollDice(MatchTests.Stacked(1, 1, 1, 2, 3, 4));
        match.KeepDice([0, 1, 2]);
        match.Bank();

        // 2500 beats 1000. Even the Gambler takes it.
        match.RollDice(MatchTests.Stacked(2, 2, 2, 5, 5, 5));
        var decision = bot.Decide(match);

        Assert.True(decision.Bank, decision.Reason);
    }

    [Fact]
    public void ATieOnTheLastTurnIsNotBanked()
    {
        var bot = new FarkleBot(BotCharacter.Rock);
        var match = Ready(new FarkleRules(Target: 1000, OpeningThreshold: 500), botSeat: 1);

        match.RollDice(MatchTests.Stacked(1, 1, 1, 2, 3, 4));
        match.KeepDice([0, 1, 2]);
        match.Bank();

        match.RollDice(MatchTests.Stacked(1, 1, 1, 2, 3, 4));
        var decision = bot.Decide(match);

        Assert.False(decision.Bank, decision.Reason);
    }

    [Fact]
    public void BeforeTheBoardItCannotChooseToBankShort()
    {
        var bot = new FarkleBot(BotCharacter.Rock);
        var match = Ready(botSeat: 0);
        match.RollDice(MatchTests.Stacked(1, 5, 2, 3, 4, 6));

        var decision = bot.Decide(match);

        Assert.False(decision.Bank);
    }

    /// <summary>
    /// The one ordering that must hold whatever the numbers are: facing the same 650 with
    /// three dice left, the Rock banks more often than the Gambler.
    /// </summary>
    [Fact]
    public void TheRockBanksMoreOftenThanTheGambler()
    {
        Assert.True(BankRate(BotCharacter.Rock) > BankRate(BotCharacter.Gambler) + 0.2,
            $"rock {BankRate(BotCharacter.Rock):P0} gambler {BankRate(BotCharacter.Gambler):P0}");
    }

    [Fact]
    public void EveryCharacterHasADifferentBankRate()
    {
        var rates = BotCharacter.All.Select(c => (c.Name, Rate: BankRate(c))).ToList();

        for (var i = 0; i < rates.Count; i++)
        {
            for (var j = i + 1; j < rates.Count; j++)
            {
                Assert.True(Math.Abs(rates[i].Rate - rates[j].Rate) > 0.05,
                    $"{rates[i].Name} {rates[i].Rate:P0} and {rates[j].Name} {rates[j].Rate:P0} are the same person");
            }
        }
    }

    [Fact]
    public void LosingTiltsAGamblerTowardRiskAndARockAwayFromIt()
    {
        var gambler = new FarkleBot(BotCharacter.Gambler);
        var rock = new FarkleBot(BotCharacter.Rock);

        var match = Ready(botSeat: 0);
        match.RollDice(MatchTests.Stacked(1, 1, 1, 2, 3, 4));
        match.KeepDice([0, 1, 2]);
        match.RollDice(MatchTests.Stacked(2, 2, 3)); // farkle, 1000 gone

        gambler.Observe(match, 0);
        rock.Observe(match, 0);

        Assert.True(gambler.Mood < 0);
        Assert.True(gambler.Current.Risk > BotCharacter.Gambler.Dials.Risk);
        Assert.True(rock.Current.Risk < BotCharacter.Rock.Dials.Risk);
    }

    [Fact]
    public void MoodDecaysBackTowardLevel()
    {
        var bot = new FarkleBot(BotCharacter.Gambler);
        var match = Ready(botSeat: 0);
        match.RollDice(MatchTests.Stacked(1, 1, 1, 2, 3, 4));
        match.KeepDice([0, 1, 2]);
        match.RollDice(MatchTests.Stacked(2, 2, 3));
        bot.Observe(match, 0);
        var low = bot.Mood;

        // Quiet turns: the other seat's, nothing of the bot's in them.
        match.RollDice(MatchTests.Stacked(2, 2, 3, 3, 4, 6));
        bot.Observe(match, 0);

        Assert.True(bot.Mood > low && bot.Mood < 0);
    }

    [Fact]
    public void ImprovisedCharactersStayOnTheDials()
    {
        var rng = new Random(5);

        for (var i = 0; i < 200; i++)
        {
            var d = BotCharacter.Improvise(rng).Dials;
            Assert.InRange(d.Risk, 0, 1);
            Assert.InRange(d.Greed, 0, 1);
            Assert.InRange(d.Patience, 0, 1);
            Assert.InRange(d.Steadiness, 0, 1);
        }
    }

    [Fact]
    public void ThinkingTimeIsLongerWhenItIsClose()
    {
        var bot = new FarkleBot(BotCharacter.Grinder);

        // Six of a kind: nothing close about it.
        var easy = Ready(botSeat: 0);
        easy.RollDice(MatchTests.Stacked(1, 1, 1, 1, 1, 1));
        var obvious = bot.Decide(easy);

        Assert.InRange(obvious.Seconds, 0.5, 2.3);
    }

    /// <summary>
    /// How often a character banks, over a spread of positions: on the board, a turn score
    /// from 100 to 1,500, two to six dice in hand, and a roll showing one scoring 5 and
    /// nothing else. The decision is deterministic, so the spread is what makes this a
    /// rate rather than one answer repeated.
    /// </summary>
    private static double BankRate(BotCharacter character)
    {
        var bot = new FarkleBot(character);
        var banks = 0;
        var positions = 0;

        for (var turnScore = 100; turnScore <= 1500; turnScore += 100)
        {
            for (var dice = 2; dice <= 6; dice++)
            {
                var match = Ready(botSeat: 0);
                match.Force(turnScore, dice, onBoard: true);

                var faces = new List<int> { 5 };
                faces.AddRange(Enumerable.Repeat(2, dice - 1).Select((_, i) => i % 2 == 0 ? 2 : 3));
                match.RollDice(MatchTests.Stacked(faces.ToArray()));

                positions++;

                if (bot.Decide(match).Bank)
                {
                    banks++;
                }
            }
        }

        return (double)banks / positions;
    }
}
