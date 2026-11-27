using System.Text.Json;
using Myria.Lib.Core.Entities;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Services.Manager;
using Myria.Lib.Core.Systems.Enums;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// v0.3 race/class profiles: the playable races are the story's peoples, the only class
/// restriction is the Amato's (no inner magic), every class has the same growth budget, and
/// characters of the retired races (Zalu, Gavon, Gamato) are moved to a lore race on load.
/// </summary>
[Collection("GameData")]
public class RaceClassProfileTests
{
    public RaceClassProfileTests(GameDataFixture _) { }

    private static readonly string[] InnerMagicClasses = { "ElementalMage", "ArcanMage", "Druid", "SoulsKnight" };

    [Fact]
    public void ThePlayableRaces_AreTheStorysPeoples()
    {
        Assert.Equal(CharacterRace.AllBuiltIn.OrderBy(r => r), RaceProfile.All.Keys.OrderBy(r => r));
        Assert.DoesNotContain(CharacterRace.Zalu, RaceProfile.All.Keys);
        Assert.DoesNotContain(CharacterRace.Gavon, RaceProfile.All.Keys);
        Assert.DoesNotContain(CharacterRace.Gamato, RaceProfile.All.Keys);
    }

    [Fact]
    public void OnlyTheAmato_AreRestricted_FromTheInnerMagicClasses_AndAnyoneCanBeARunicMage()
    {
        foreach (var (race, profile) in RaceProfile.All)
        {
            Assert.True(ClassManager.IsClassAllowed(race, CharacterClass.RunicMage), $"{race} can't be a Runic Mage");
            if (race == CharacterRace.Amato)
                Assert.Equal(InnerMagicClasses.OrderBy(c => c), profile.ForbiddenClasses.OrderBy(c => c));
            else
                Assert.Empty(profile.ForbiddenClasses);
        }
        Assert.NotNull(ClassManager.GetRestrictionReason(CharacterRace.Amato, "Druid"));
        Assert.Null(ClassManager.GetRestrictionReason(CharacterRace.Faerie, "Druid"));
    }

    [Fact]
    public void EveryClass_HasTheSameGrowthBudget()
    {
        foreach (var (cls, p) in ClassProfile.All)
        {
            Assert.True(p.StatGrowth.Values.Sum() == 17, $"{cls} grows {p.StatGrowth.Values.Sum()} stat points per level");
            Assert.True(p.HpPerLevel + 2 * p.ManaPerLevel == 200, $"{cls}: HP {p.HpPerLevel} + 2×MP {p.ManaPerLevel} != 200");
        }
        // Races, too: same start total and growth total, so no race is simply stronger. And small
        // next to a class (v0.3): at most 1 point per stat and level, so a race flavours a class
        // but never grows a stat like the class does (a Rotuka Knight used to get as much STR per
        // level from its race as from its class).
        foreach (var (race, p) in RaceProfile.All)
        {
            Assert.True(p.StatGrowth.Values.Sum() == 3, $"{race} grows {p.StatGrowth.Values.Sum()}");
            Assert.All(p.StatGrowth.Values, g => Assert.InRange(g, 0, 1));
            Assert.True(p.BaseStatBonus.Values.Sum() == 10, $"{race} starts with {p.BaseStatBonus.Values.Sum()} bonus points");
            Assert.All(p.BaseStatBonus.Values, b => Assert.InRange(b, -2, 4));
        }
    }

    [Fact]
    public void TheArcher_IsTheStrengthArcher_AndTheHunter_TheDexterityOne()
    {
        var archer = ClassProfile.All["Archer"].StatGrowth;
        var hunter = ClassProfile.All["Hunter"].StatGrowth;
        Assert.True(archer["STR"] > archer["DEX"]);
        Assert.True(hunter["DEX"] > hunter["STR"]);

        // ATK is Strength-based (2×STR + END), so STR and ATK both count as Strength scaling.
        var skills = JsonDocument.Parse(File.ReadAllText(Path.Combine("Data", "common", "skills.json"))).RootElement.EnumerateArray()
            .Select(s => (Class: s.GetProperty("Class").GetString(), Stat: s.GetProperty("StatToScaleFrom").GetString())).ToList();
        Assert.All(skills.Where(s => s.Class == "Archer"), s => Assert.Contains(s.Stat, new[] { "STR", "ATK" }));
        Assert.All(skills.Where(s => s.Class == "Hunter"), s => Assert.Equal("DEX", s.Stat));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void EveryRaceAndClass_HasANameAndDescription(string lang)
    {
        var text = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine("Data", "locales", $"{lang}.json")))!;
        var keys = RaceProfile.All.Keys.SelectMany(r => new[] { $"race.{r}", $"race.{r}.desc" })
            .Concat(ClassProfile.All.Keys.SelectMany(c => new[] { $"class.{c}", $"class.{c}.desc" }))
            .Append("race.Amato.restriction");
        Assert.All(keys, k => Assert.True(text.ContainsKey(k), $"{lang} is missing {k}"));
    }

    // ── Retired races ─────────────────────────────────────────────────────

    /// <summary>A level-<paramref name="level"/> character of <paramref name="race"/> with the given
    /// start bonuses and growth, built the way creation + level-ups build one.</summary>
    private static Character Built(string race, int level, int[] bonus, int hp, int mp, int[] growth, int hpL, int mpL)
    {
        int ups = level - 1;
        var stats = new Stats
        {
            Strength = 10 + bonus[0] + growth[0] * ups, Dexterity = 10 + bonus[1] + growth[1] * ups,
            Endurance = 10 + bonus[2] + growth[2] * ups, Intelligence = 10 + bonus[3] + growth[3] * ups,
            Spirit = 10 + bonus[4] + growth[4] * ups,
            BaseHealth = 30 + hp + hpL * ups, BaseMana = 30 + mp + mpL * ups,
        };
        return new Character("Test", stats) { Race = race, Level = level, Class = "Fighter" };
    }

    private static Character BuiltAs(string race, int level)
    {
        var p = RaceProfile.All[race];
        int[] V(Dictionary<string, int> d) => new[] { d["STR"], d["DEX"], d["END"], d["INT"], d["SPR"] };
        return Built(race, level, V(p.BaseStatBonus), p.BaseHpBonus, p.BaseManaBonus, V(p.StatGrowth), p.HpPerLevel, p.ManaPerLevel);
    }

    [Theory]
    [InlineData("Gavon", "Faerie")]
    [InlineData("Zalu", "Aoyu")]
    [InlineData("Gamato", "Elf")]
    public void ARetiredRaceCharacter_EndsUpAsIfMadeWithItsLoreRace(string retired, string target)
    {
        var old = retired switch
        {
            "Gavon"  => Built(retired, 7, new[] { -3, 6, 1, 8, 6 }, 10, 30, new[] { 0, 2, 1, 3, 2 }, 3, 6),
            "Zalu"   => Built(retired, 7, new[] { 6, 1, 7, 2, 3 }, 25, 10, new[] { 2, 1, 2, 1, 2 }, 5, 2),
            _        => Built(retired, 7, new[] { 3, 4, 4, 4, 3 }, 15, 25, new[] { 1, 2, 1, 2, 2 }, 5, 3),
        };
        var expected = BuiltAs(target, 7);

        Assert.True(RaceMigration.Apply(old));

        Assert.Equal(target, old.Race);
        foreach (var id in new[] { "STR", "DEX", "END", "INT", "SPR" })
            Assert.Equal(expected.Stats.GetBase(id), old.Stats.GetBase(id));
        Assert.Equal(expected.Stats.BaseHealth, old.Stats.BaseHealth);
        Assert.Equal(expected.Stats.BaseMana, old.Stats.BaseMana);
        Assert.False(RaceMigration.Apply(old));                  // once only
    }

    [Theory]
    [InlineData("Rotuka", new[] { 8, 5, 5, -1, 1 }, new[] { 3, 2, 2, 0, 1 })]
    [InlineData("Myralu", new[] { 4, 1, 6, 4, 3 }, new[] { 1, 1, 2, 2, 2 })]
    [InlineData("Faerie", new[] { -3, 7, 0, 6, 8 }, new[] { 0, 2, 1, 2, 3 })]
    public void ACharacterBuiltWithTheOldRaceBalance_IsMovedToTheNewOne(string race, int[] oldBase, int[] oldGrowth)
    {
        var old = Built(race, 12, oldBase, 0, 0, oldGrowth, 0, 0);
        old.Stats.StrengthBonus = 4;                                // points the player spent stay
        int hp = old.Stats.BaseHealth;
        var expected = BuiltAs(race, 12);

        Assert.True(RaceMigration.Apply(old));

        foreach (var id in new[] { "STR", "DEX", "END", "INT", "SPR" })
            Assert.Equal(expected.Stats.GetBase(id), old.Stats.GetBase(id));
        Assert.Equal(4, old.Stats.StrengthBonus);
        Assert.Equal(hp, old.Stats.BaseHealth);
        Assert.False(RaceMigration.Apply(old));                  // once only
    }

    [Fact]
    public void HandEditedStats_AreNotRebalanced()
    {
        var c = Built("Rotuka", 12, new[] { 8, 5, 5, -1, 1 }, 0, 0, new[] { 3, 2, 2, 0, 1 }, 0, 0);
        c.Stats.Strength += 1;
        int str = c.Stats.Strength;
        Assert.False(RaceMigration.Apply(c));
        Assert.Equal(str, c.Stats.Strength);
    }

    [Fact]
    public void LoreRaceCharacters_AreLeftAlone()
    {
        var c = BuiltAs(CharacterRace.Iymva, 3);
        int str = c.Stats.Strength;
        Assert.False(RaceMigration.Apply(c));
        Assert.Equal(CharacterRace.Iymva, c.Race);
        Assert.Equal(str, c.Stats.Strength);
    }
}
