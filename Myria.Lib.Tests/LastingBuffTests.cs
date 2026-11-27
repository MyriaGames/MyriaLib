using System.Text.Json;
using Myria.Lib.Core.Entities;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Entities.Skills;
using Myria.Lib.Core.Models.Dto;
using Myria.Lib.Core.Services;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// v0.3 lasting buffs (the Druid's Verdant Vigor): +% max HP/MP for real-clock minutes, across
/// fights, saved with the character, castable outside fights.
/// </summary>
[Collection("GameData")]
public class LastingBuffTests : IDisposable
{
    private DateTime _now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    public LastingBuffTests(GameDataFixture _) => LastingBuff.Clock = () => _now;
    public void Dispose() => LastingBuff.Clock = () => DateTime.UtcNow;

    private static Character Hero(string name = "Hero", string cls = "Druid")
    {
        var c = new Character(name, new Stats()) { Class = cls };
        c.CurrentHealth = c.MaxHealth;
        c.CurrentMana = c.MaxMana;
        return c;
    }

    private static Skill Vigor(float scaling = 1f) => new()
    {
        Id = "verdant_vigor", Name = "Verdant Vigor", ManaCost = 25, ScalingFactor = scaling,
        LastingBuff = new LastingBuffDefinition { HpPercent = 0.05f, MpPercent = 0.05f, DurationMinutes = 60 },
    };

    [Fact]
    public void TheBuff_RaisesMaxHpAndMp_ByItsPercentage_AndFillsItsShare()
    {
        var target = Hero();
        int hp = target.MaxHealth, mp = target.MaxMana;

        target.ApplyLastingBuff(LastingBuff.From(Vigor(), "Druid")!);

        Assert.InRange(target.MaxHealth, (int)(hp * 1.05) - 1, (int)(hp * 1.05) + 1);
        Assert.InRange(target.MaxMana, (int)(mp * 1.05) - 1, (int)(mp * 1.05) + 1);
        Assert.Equal(target.MaxHealth, target.CurrentHealth);
        Assert.Equal(target.MaxMana, target.CurrentMana);
    }

    [Fact]
    public void TheBuff_RunsOutAfter60Minutes_AndVitalsComeBackUnderTheMax()
    {
        var target = Hero();
        int hp = target.MaxHealth;
        target.ApplyLastingBuff(LastingBuff.From(Vigor(), "Druid")!);

        _now = _now.AddMinutes(59);
        Assert.True(target.MaxHealth > hp);

        _now = _now.AddMinutes(2);
        Assert.Equal(hp, target.MaxHealth);
        Assert.True(target.PruneLastingBuffs());
        Assert.Empty(target.LastingBuffs);
        Assert.Equal(hp, target.CurrentHealth);
    }

    [Fact]
    public void RecastingTheSameSkill_Replaces_NotStacks()
    {
        var target = Hero();
        target.ApplyLastingBuff(LastingBuff.From(Vigor(), "A")!);
        _now = _now.AddMinutes(30);
        target.ApplyLastingBuff(LastingBuff.From(Vigor(1.2f), "B")!);

        var buff = Assert.Single(target.LastingBuffs);
        Assert.Equal(0.06f, buff.HpPercent, 4);
        Assert.Equal(_now.AddMinutes(60), buff.ExpiresUtc);
    }

    [Fact]
    public void OutOfCombatCast_CostsMana_AndCountsAsAUse()
    {
        var druid = Hero("Druid");
        var ally = Hero("Ally", "Knight");
        int mana = druid.CurrentMana;

        var buff = LastingBuffService.TryCast(druid, Vigor(), ally, out var error);

        Assert.Null(error);
        Assert.NotNull(buff);
        Assert.Single(ally.LastingBuffs);
        Assert.Equal(mana - 25, druid.CurrentMana);
        Assert.Equal(1, SkillLevelingService.GetOrCreate(druid, "verdant_vigor").UsageCount);
    }

    [Fact]
    public void OutOfCombatCast_IsRefused_ForSkillsWithoutALastingBuff_OrWithoutMana()
    {
        var druid = Hero();
        Assert.Null(LastingBuffService.TryCast(druid, new Skill { Id = "nature_bolt", ManaCost = 1 }, druid, out var e1));
        Assert.Equal("skill.cast.notOutOfCombat", e1);

        druid.CurrentMana = 10;
        Assert.Null(LastingBuffService.TryCast(druid, Vigor(), druid, out var e2));
        Assert.Equal("skill.cast.noMana", e2);
        Assert.Empty(druid.LastingBuffs);
        Assert.Equal(10, druid.CurrentMana);
    }

    [Fact]
    public void TheBuff_SurvivesSaveAndLoad()
    {
        var c = Hero();
        c.ApplyLastingBuff(LastingBuff.From(Vigor(), "Druid")!);

        var loaded = CharacterService.DeserializeCharacter(CharacterService.SerializeCharacter(c))!;

        var buff = Assert.Single(loaded.LastingBuffs);
        Assert.Equal("verdant_vigor", buff.SkillId);
        Assert.Equal(_now.AddMinutes(60), buff.ExpiresUtc);
    }

    [Fact]
    public void ACharacterUpdatePush_ReplacesTheBuffList_BeforeTheVitals()
    {
        var c = Hero();
        int hp = c.MaxHealth;
        int buffedMax = (int)Math.Round(hp * 1.05);
        c.ApplyCharacterUpdate(new CharacterUpdateDto(null, null, buffedMax, buffedMax, null, null, null,
            LastingBuffs: new() { new LastingBuffSnapshot("verdant_vigor", "Verdant Vigor", "D", 0.05f, 0.05f, _now.AddMinutes(60)) }));

        Assert.Single(c.LastingBuffs);
        Assert.Equal(c.MaxHealth, c.CurrentHealth);
        Assert.True(c.CurrentHealth > hp);
    }

    [Fact]
    public void TheDruid_HasVerdantVigor_WithA60MinuteBuffOf5Percent()
    {
        var skill = Assert.Single(new[] { Myria.Lib.Core.Services.Builder.SkillFactory.GetSkill("verdant_vigor") }, s => s is not null);
        Assert.Equal("Druid", skill!.Class);
        Assert.Equal(0.05f, skill.LastingBuff!.HpPercent);
        Assert.Equal(0.05f, skill.LastingBuff.MpPercent);
        Assert.Equal(60, skill.LastingBuff.DurationMinutes);
    }
}
