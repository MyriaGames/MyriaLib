using Myria.Lib.Core.Entities;
using Myria.Lib.Core.Entities.Effects;
using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Entities.Monsters;
using Myria.Lib.Core.Entities.NPCs;
using Myria.Lib.Core.Models.Dto;
using Myria.Lib.Core.Services.Manager;
using Myria.Lib.Core.Systems;
using Myria.Lib.Core.Systems.Enums;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// Two reported bugs (2026-11-26):
/// 1. A monster killed by poison on its own turn was skipped instead of dying: no XP/loot/quest credit, and a
///    fight whose last monster died that way never ended (fleeing was the only way out).
/// 2. Quest counters and status drifted apart: the client took the server's counters but never the status
///    (5/5 shown, yet not returnable); items already carried at accept time weren't counted; items sold
///    afterwards still counted.
/// </summary>
[Collection("GameData")]
public class PoisonKillAndQuestProgressTests
{
    public PoisonKillAndQuestProgressTests(GameDataFixture _) { }

    // A monster no normal hit can kill, so only the poison can.
    private static Monster ToughMonster(int id = 42) =>
        new(id, "Goblin", new Stats { BaseHealth = 1_000_000 }, "", 1);

    private static void Poison(Monster monster) =>
        monster.ActiveEffects.Add(new ActiveEffect
        {
            DefinitionId = "test_poison", Name = "Poison", Type = EffectType.Poison,
            TurnsRemaining = 3, Magnitude = 10_000_000
        });

    private static Quest KillQuest(int monsterId, int kills) => new()
    {
        Id = "test_kill_quest", Name = "Test", Description = "", Status = QuestStatus.InProgress,
        GiverNpcId = "npc_a", RequiredKills = new() { [monsterId] = kills }
    };

    private static Quest ItemQuest(string itemId, int count) => new()
    {
        Id = "test_item_quest", Name = "Test", Description = "", GiverNpcId = "npc_a",
        RequiredItems = new() { [itemId] = count }
    };

    // Let the character act until the monsters have had their turn (bonus actions can come first).
    private static void ActUntilMonstersActed(GroupCombatEncounter encounter, string name)
    {
        for (int i = 0; i < 6 && !encounter.IsFinished; i++)
            encounter.CharacterAttack(name, 0);
    }

    // ── Bug 1 ────────────────────────────────────────────────────────────────

    [Fact]
    public void LastMonsterDyingOfPoison_EndsTheFight_AsAWin()
    {
        var character = TestHelpers.CreateCharacter();
        var monster = ToughMonster();
        var encounter = new GroupCombatEncounter(new[] { character }, new[] { monster });
        Poison(monster);
        int killedEvents = 0;
        encounter.MonsterKilled += (_, _) => killedEvents++;

        ActUntilMonstersActed(encounter, character.Name);

        Assert.False(monster.IsAlive);
        Assert.True(encounter.IsFinished);
        Assert.True(encounter.CharactersWon);
        Assert.Equal(1, killedEvents);
        Assert.True(encounter.XpGrantedByCharacter.GetValueOrDefault(character) > 0);
    }

    [Fact]
    public void PoisonKill_CountsForQuests()
    {
        var character = TestHelpers.CreateCharacter();
        var quest = KillQuest(42, 1);
        character.ActiveQuests.Add(quest);
        var monster = ToughMonster();
        var encounter = new GroupCombatEncounter(new[] { character }, new[] { monster });
        Poison(monster);

        ActUntilMonstersActed(encounter, character.Name);

        Assert.Equal(1, quest.KillProgress[42]);
        Assert.Equal(QuestStatus.Completed, quest.Status);
    }

    [Fact]
    public void PoisonKill_WithAnotherMonsterLeft_CreditsTheKill_AndTheFightGoesOn()
    {
        var character = TestHelpers.CreateCharacter();
        var poisoned = ToughMonster(42);
        var other = ToughMonster(43);
        var encounter = new GroupCombatEncounter(new[] { character }, new[] { other, poisoned });
        Poison(poisoned);
        int killedEvents = 0;
        encounter.MonsterKilled += (_, _) => killedEvents++;

        ActUntilMonstersActed(encounter, character.Name);

        Assert.False(poisoned.IsAlive);
        Assert.Equal(1, killedEvents);
        Assert.False(encounter.IsFinished);
    }

    // ── Bug 2 ────────────────────────────────────────────────────────────────

    [Fact]
    public void ServerCounters_ThatMeetTheGoal_MakeTheQuestReturnable()
    {
        var character = TestHelpers.CreateCharacter();
        var quest = KillQuest(42, 5);
        character.ActiveQuests.Add(quest);
        Assert.Empty(QuestManager.GetReturnableForNpc(character, "npc_a"));

        // What the realm sends after a fight online (the kills happened on the server).
        character.ApplySyncedQuestProgress([new QuestProgressState("test_kill_quest", new() { [42] = 5 }, new())]);

        Assert.Equal(QuestStatus.Completed, quest.Status);
        Assert.Single(QuestManager.GetReturnableForNpc(character, "npc_a"));
    }

    [Fact]
    public void ServerCounters_BelowTheGoal_PutAQuestBackInProgress()
    {
        var character = TestHelpers.CreateCharacter();
        var quest = KillQuest(42, 5);
        quest.Status = QuestStatus.Completed;     // the client had (wrongly) completed it
        character.ActiveQuests.Add(quest);

        character.ApplySyncedQuestProgress([new QuestProgressState("test_kill_quest", new() { [42] = 3 }, new())]);

        Assert.Equal(QuestStatus.InProgress, quest.Status);
    }

    [Fact]
    public void AcceptingAQuest_CountsItemsAlreadyCarried()
    {
        var character = TestHelpers.CreateCharacter();
        character.Inventory.Items.Add(new MaterialItem { Id = "iron_ore", Name = "Iron Ore", StackSize = 4 });

        var started = QuestManager.StartQuest(character, ItemQuest("iron_ore", 3));

        Assert.Equal(3, started.ItemProgress["iron_ore"]);
        Assert.Equal(QuestStatus.Completed, started.Status);
        Assert.Contains(started, character.ActiveQuests);
    }

    [Fact]
    public void ItemsGoneAfterwards_CountDown_AndTheQuestIsNoLongerReturnable()
    {
        var character = TestHelpers.CreateCharacter();
        var ore = new MaterialItem { Id = "iron_ore", Name = "Iron Ore", StackSize = 3 };
        character.Inventory.Items.Add(ore);
        var quest = QuestManager.StartQuest(character, ItemQuest("iron_ore", 3));
        Assert.Equal(QuestStatus.Completed, quest.Status);

        character.Inventory.Items.Remove(ore);     // sold / traded / used up

        Assert.Empty(QuestManager.GetReturnableForNpc(character, "npc_a"));
        Assert.Equal(0, quest.ItemProgress["iron_ore"]);
        Assert.Equal(QuestStatus.InProgress, quest.Status);
    }

    [Fact]
    public void TalkOnlyQuest_IsCompleteRightAway()
    {
        var character = TestHelpers.CreateCharacter();
        var started = QuestManager.StartQuest(character, new Quest { Id = "talk", Name = "Talk", Description = "" });
        Assert.Equal(QuestStatus.Completed, started.Status);
    }

    [Fact]
    public void ReturnedQuests_AreNeverTouchedByRefreshStatus()
    {
        var quest = KillQuest(42, 1);
        quest.Status = QuestStatus.Returned;
        QuestManager.RefreshStatus(quest);
        Assert.Equal(QuestStatus.Returned, quest.Status);
    }
}
