using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Services.Manager;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// ClassProfile/RaceProfile are static, process-wide registries (see README §21, Known
/// Limitations). Unlike RuneWordService (see RuneEvaluatorTests), both expose a public
/// <c>All</c> dictionary, so each test here
/// snapshots the ambient state, swaps in a small controlled fixture, and restores the
/// original snapshot in `finally` — safe to run regardless of whether real classes.json/
/// races.json content happened to already be loaded elsewhere in the same test run.
/// </summary>
public class ClassManagerTests
{
    private static void WithClassProfiles(List<ClassProfile> profiles, Action test)
    {
        var snapshot = ClassProfile.All.Values.ToList();
        try
        {
            ClassProfile.Load(profiles);
            test();
        }
        finally
        {
            ClassProfile.Load(snapshot);
        }
    }

    private static void WithRaceProfiles(List<RaceProfile> profiles, Action test)
    {
        var snapshot = RaceProfile.All.Values.ToList();
        try
        {
            RaceProfile.Load(profiles);
            test();
        }
        finally
        {
            RaceProfile.Load(snapshot);
        }
    }

    [Fact]
    public void GetClassGroup_ReturnsRegisteredGroup_OrPhysicalDefaultForUnknownClass()
    {
        WithClassProfiles(new List<ClassProfile>
        {
            new() { Class = "ElementalMage", Group = "Mage" },
        }, () =>
        {
            Assert.Equal("Mage", ClassManager.GetClassGroup("ElementalMage"));
            Assert.Equal("Physical", ClassManager.GetClassGroup("SomeClassThatDoesNotExist"));
        });
    }

    [Fact]
    public void GrantClassXp_DefaultOverload_AccumulatesOnCharactersCurrentClass()
    {
        var character = TestHelpers.CreateCharacter();
        character.Class = "Fighter";

        ClassManager.GrantClassXp(character, 100);
        ClassManager.GrantClassXp(character, 50);

        Assert.Equal(150, ClassManager.GetClassXp(character, "Fighter"));
    }

    [Fact]
    public void GrantClassXp_IgnoresNonPositiveAmounts()
    {
        var character = TestHelpers.CreateCharacter();
        character.Class = "Fighter";

        ClassManager.GrantClassXp(character, 0);
        ClassManager.GrantClassXp(character, -50);

        Assert.Equal(0, ClassManager.GetClassXp(character, "Fighter"));
    }

    [Fact]
    public void ApplyDailyPenalty_ReducesInactiveClassXp_ButNeverBelowZero_AndSkipsActiveClass()
    {
        var originalPenalty = ClassManager.PenaltyPerDay;
        try
        {
            ClassManager.PenaltyPerDay = 500;

            WithClassProfiles(new List<ClassProfile>
            {
                new() { Class = "Fighter", Group = "Physical" },
                new() { Class = "Knight", Group = "Physical" },
            }, () =>
            {
                var character = TestHelpers.CreateCharacter();
                character.Class = "Fighter"; // active class — must be exempt from the penalty
                character.ClassXp["Fighter"] = 1000;
                character.ClassXp["Knight"] = 200; // inactive, less than the penalty

                ClassManager.ApplyDailyPenalty(character);

                Assert.Equal(1000, ClassManager.GetClassXp(character, "Fighter")); // active class untouched
                Assert.Equal(0, ClassManager.GetClassXp(character, "Knight"));      // floored at 0, not negative
            });
        }
        finally
        {
            ClassManager.PenaltyPerDay = originalPenalty;
        }
    }

    [Fact]
    public void ApplyDailyPenalty_OnlyAppliesOncePerUtcDay()
    {
        var originalPenalty = ClassManager.PenaltyPerDay;
        try
        {
            ClassManager.PenaltyPerDay = 100;

            WithClassProfiles(new List<ClassProfile>
            {
                new() { Class = "Fighter", Group = "Physical" },
                new() { Class = "Knight", Group = "Physical" },
            }, () =>
            {
                var character = TestHelpers.CreateCharacter();
                character.Class = "Fighter";
                character.ClassXp["Knight"] = 300;

                ClassManager.ApplyDailyPenalty(character);
                ClassManager.ApplyDailyPenalty(character); // same day — must be a no-op

                Assert.Equal(200, ClassManager.GetClassXp(character, "Knight")); // only reduced once
            });
        }
        finally
        {
            ClassManager.PenaltyPerDay = originalPenalty;
        }
    }

    [Fact]
    public void CanChangeClass_TrueForNeverChangedCharacter_FalseImmediatelyAfterASwitch()
    {
        var character = TestHelpers.CreateCharacter();
        Assert.True(ClassManager.CanChangeClass(character)); // LastClassChanged == DateTime.MinValue

        character.LastClassChanged = DateTime.UtcNow;
        Assert.False(ClassManager.CanChangeClass(character));
        Assert.True(ClassManager.GetClassChangeCooldownRemaining(character) > TimeSpan.FromDays(6));
    }

    [Fact]
    public void SetClass_Blocked_WhenClassIsForbiddenForCharactersRace()
    {
        WithRaceProfiles(new List<RaceProfile>
        {
            new() { Race = "Myralu", ForbiddenClasses = new(StringComparer.OrdinalIgnoreCase) { "Barbarian" } },
        }, () =>
        {
            var character = TestHelpers.CreateCharacter();
            character.Race = "Myralu";
            character.Class = "Fighter";

            bool changed = ClassManager.SetClass(character, "Barbarian");

            Assert.False(changed);
            Assert.Equal("Fighter", character.Class); // unchanged
        });
    }

    [Fact]
    public void SetClass_SameGroupSwitch_TransfersHalfOfAccumulatedClassXp()
    {
        // Empty RaceProfile registry -> IsClassAllowed short-circuits to true for any class,
        // so this test's outcome depends only on the ClassProfile fixture below, not on
        // whatever race data (if any) happens to already be loaded in this process.
        WithRaceProfiles(new List<RaceProfile>(), () =>
        WithClassProfiles(new List<ClassProfile>
        {
            new() { Class = "Fighter", Group = "Physical" },
            new() { Class = "Knight", Group = "Physical" }, // same group as Fighter
        }, () =>
        {
            var character = TestHelpers.CreateCharacter();
            character.Class = "Fighter";
            character.ClassXp["Fighter"] = 1000;

            bool changed = ClassManager.SetClass(character, "Knight");

            Assert.True(changed);
            Assert.Equal("Knight", character.Class);
            Assert.Equal(500, ClassManager.GetClassXp(character, "Knight")); // 50% of the old class's XP
        }));
    }
}
