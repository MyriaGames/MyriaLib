using System.Text.Json;
using System.Text.Json.Serialization;
using Myria.Lib.Core.Models;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Systems.Mods;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// Integrity checks over the authored skill upgrade content in skills.json (v0.3: every skill has
/// a 3-tier upgrade set - L1 power/efficiency, L6 mastery, L11 power II + a signature effect).
/// Pure data checks, so a typo'd effect id or an unreachable tier fails here instead of silently
/// doing nothing in game.
/// </summary>
[Collection("GameData")]
public class SkillUpgradeContentTests
{
    private static readonly int MaxSkillLevel = SkillLevelingService.LevelBreakpoints.Max(b => b.Level);

    private readonly List<SkillData> _skills;

    public SkillUpgradeContentTests(GameDataFixture _)
    {
        var json = File.ReadAllText(ModLoader.ResolvePath("Data/common/skills.json"));
        _skills = JsonSerializer.Deserialize<List<SkillData>>(json,
            new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!;
    }

    [Fact]
    public void EverySkill_HasUpgradeOptions_WithAllThreeTiers()
    {
        foreach (var s in _skills)
        {
            Assert.True(s.UpgradeOptions.Count > 0, $"{s.Id} has no upgrade options");
            var levels = s.UpgradeOptions.Select(o => o.RequiredLevel).ToHashSet();
            Assert.True(levels.Contains(1) && levels.Contains(6) && levels.Contains(11),
                $"{s.Id} is missing a tier (has levels {string.Join(",", levels.Order())})");
        }
    }

    [Fact]
    public void UpgradeIds_AreUniqueAcrossAllSkills()
    {
        // Purchases are stored by upgrade id (CharacterSkillProgressUpgrade); a duplicate would make
        // one skill's purchase resolve against another's option.
        var dupes = _skills.SelectMany(s => s.UpgradeOptions).GroupBy(o => o.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dupes);
    }

    [Fact]
    public void AddedEffects_ReferenceRealEffects_AndDontDuplicateTheSkillsOwn()
    {
        foreach (var s in _skills)
        foreach (var o in s.UpgradeOptions)
        foreach (var e in o.AddedEffects)
        {
            Assert.True(EffectFactory.GetDefinition(e.EffectId) != null, $"{o.Id}: unknown effect '{e.EffectId}'");
            Assert.DoesNotContain(s.Effects ?? new(), own => own.EffectId == e.EffectId);
        }
    }

    [Fact]
    public void EveryTier_IsReachable()
    {
        foreach (var o in _skills.SelectMany(s => s.UpgradeOptions))
            Assert.InRange(o.RequiredLevel, 1, MaxSkillLevel);
    }

    [Fact]
    public void CostAndCooldownReductions_AreCapped_AndCantZeroTheSkill()
    {
        // Repeatable (MaxPurchases 0, no effects) means unlimited - fine for power, but an
        // uncapped mana/cooldown cut would make a skill free or spammable after a few points.
        foreach (var s in _skills)
        {
            int manaCut = 0, cooldownCut = 0;
            foreach (var o in s.UpgradeOptions.Where(o => o.ManaCostDelta < 0 || o.CooldownDelta < 0))
            {
                int max = SkillLevelingService.GetEffectiveMaxPurchases(o);
                Assert.True(max > 0, $"{o.Id} reduces cost/cooldown but is uncapped");
                manaCut += -o.ManaCostDelta * max;
                cooldownCut += -o.CooldownDelta * max;
            }
            Assert.True(s.ManaCost == 0 || manaCut < s.ManaCost, $"{s.Id}: upgrades can cut mana {s.ManaCost} by {manaCut}");
            Assert.True(cooldownCut == 0 || cooldownCut < s.Cooldown, $"{s.Id}: upgrades can cut cooldown {s.Cooldown} by {cooldownCut}");
        }
    }
}
