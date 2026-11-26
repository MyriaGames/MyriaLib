using Myria.Lib.Core.Entities.NPCs;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Manager;
using Myria.Lib.Core.Systems;
using Myria.Lib.Core.Systems.Enums;
using Myria.Lib.Core.Systems.World;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// v0.3 quest rework, part 2: grant sizing, staleness, tokens, the quests grants are presented as,
/// the Adventurers' Guild reach, and the room restrictions dynamic quests add to quest progress.
/// Real data: room 1 (Royas) has smith_default; room 2 is an overworld room next to it with monster 33.
/// </summary>
[Collection("GameData")]
public class DynamicQuestTests
{
    public DynamicQuestTests(GameDataFixture _) { }

    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    private static QuestGrant Grant(GrantKind kind, int target, DateTime accepted, bool completed = false) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Kind = kind, RoomId = kind == GrantKind.Fetch ? 1 : 2,
        NpcId = kind == GrantKind.Fetch ? "smith_default" : null, ItemId = kind == GrantKind.Fetch ? "iron_ore" : null,
        MonsterId = kind == GrantKind.Kill ? 33 : null, Target = target, RewardXp = 1, RewardGold = 1,
        HolderName = "Someone", AcceptedUtc = accepted, Completed = completed,
    };

    // ── Sizing ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(30, GrantKind.Kill, 10)]  // split into ~3 parts
    [InlineData(12, GrantKind.Kill, 5)]   // clamped up to the minimum
    [InlineData(90, GrantKind.Kill, 15)]  // clamped down to the maximum
    [InlineData(4, GrantKind.Kill, 4)]    // below the minimum: all of it, one grant
    [InlineData(0, GrantKind.Kill, 0)]
    [InlineData(20, GrantKind.Fetch, 7)]
    [InlineData(2, GrantKind.Fetch, 2)]
    public void GrantSize_SplitsBigGaps_DownToTheMinimum(int remaining, GrantKind kind, int expected)
    {
        Assert.Equal(expected, DynamicQuests.GrantSize(remaining, kind));
    }

    [Fact]
    public void Rewards_ScaleWithTheGrantSize()
    {
        var small = DynamicQuests.Rewards(GrantKind.Kill, 5, null, 33);
        var big   = DynamicQuests.Rewards(GrantKind.Kill, 15, null, 33);

        Assert.Equal(small.Xp * 3, big.Xp);
        Assert.Equal(small.Gold * 3, big.Gold);
        Assert.True(DynamicQuests.Rewards(GrantKind.Fetch, 3, "iron_ore", null).Gold > 0);
    }

    // ── What counts as promised ───────────────────────────────────────────

    [Fact]
    public void Promised_CountsOnlyOutstandingGrants_StaleAndCompletedOnesDontCount()
    {
        var key = new StockKey(1, "smith_default", "iron_ore");
        var grants = new[]
        {
            Grant(GrantKind.Fetch, 5, Now.AddDays(-1)),
            Grant(GrantKind.Fetch, 4, Now.AddDays(-8)),             // stale: a week passed
            Grant(GrantKind.Fetch, 3, Now.AddHours(-1), completed: true),
        };

        Assert.Equal(5, DynamicQuests.Promised(grants, key, Now));
        Assert.Equal(0, DynamicQuests.Promised(grants, new StockKey(1, "smith_default", "other"), Now));
    }

    // ── Tokens and the quest a grant becomes ──────────────────────────────

    [Fact]
    public void Tokens_RoundTrip()
    {
        var stock = new StockKey(1, "smith_default", "iron_ore");
        var pop = new PopulationKey(2, 33);

        Assert.True(DynamicQuests.TryParseToken(DynamicQuests.Token(stock), out var s, out _));
        Assert.Equal(stock, s);
        Assert.True(DynamicQuests.TryParseToken(DynamicQuests.Token(pop), out _, out var p));
        Assert.Equal(pop, p);
        Assert.False(DynamicQuests.TryParseToken("garbage", out _, out _));
    }

    [Fact]
    public void FetchQuest_IsHandedInAtTheSameNpcInstance()
    {
        var q = DynamicQuests.BuildQuest(Grant(GrantKind.Fetch, 6, Now));

        Assert.True(q.IsDynamic);
        Assert.StartsWith(DynamicQuests.QuestIdPrefix, q.Id);
        Assert.Equal(6, q.RequiredItems["iron_ore"]);
        Assert.Equal("smith_default", q.ReturnNpcIdResolved);
        Assert.Equal(1, q.ReturnRoomId);
        Assert.Null(q.RequiredRoomId);
    }

    [Fact]
    public void KillQuest_CountsInItsRoom_AndIsHandedInAtAnyGuildHouse()
    {
        var q = DynamicQuests.BuildQuest(Grant(GrantKind.Kill, 8, Now));

        Assert.Equal(8, q.RequiredKills[33]);
        Assert.Equal(2, q.RequiredRoomId);
        Assert.Equal(DynamicQuestRules.GuildNpcId, q.ReturnNpcIdResolved);
        Assert.Null(q.ReturnRoomId);
    }

    [Fact]
    public void Dto_RoundTripsTheQuestDefinition()
    {
        var q = DynamicQuests.BuildQuest(Grant(GrantKind.Kill, 8, Now));
        var back = DynamicQuests.FromDto(DynamicQuests.ToDto(q));

        Assert.Equal(q.Id, back.Id);
        Assert.Equal(q.RequiredKills, back.RequiredKills);
        Assert.Equal(q.RequiredRoomId, back.RequiredRoomId);
        Assert.Equal(q.NameArgs, back.NameArgs);
        Assert.Equal(q.RewardGold, back.RewardGold);
        Assert.True(back.IsDynamic);
    }

    [Fact]
    public void QuestText_FormatsDynamicQuests_AndLeavesAuthoredOnesAsBefore()
    {
        Localization.Load("en"); // no other Lib test depends on localization; runs aren't parallel
        var q = DynamicQuests.BuildQuest(Grant(GrantKind.Fetch, 6, Now));

        var name = QuestText.Name(q);
        Assert.Contains("6", name);
        Assert.Contains(Localization.T("item.iron_ore"), name);
        Assert.DoesNotContain("[", name);              // every key resolved
        Assert.Equal("Plain text", QuestText.Name(new Quest { Name = "Plain text" }));
    }

    // ── Guild reach ───────────────────────────────────────────────────────

    [Fact]
    public void RoomsWithin_FollowsExits_UpToTheRadius()
    {
        var zero = DynamicQuests.RoomsWithin(1, 0);
        var one  = DynamicQuests.RoomsWithin(1, 1);

        Assert.Equal(new[] { 1 }, zero);
        Assert.Contains(2, one);                                        // Royas' north exit
        Assert.Superset(one, DynamicQuests.RoomsWithin(1, 3));
    }

    // ── Room restrictions on quest progress ───────────────────────────────

    [Fact]
    public void RoomBoundKillObjective_OnlyCountsKillsInThatRoom()
    {
        var character = TestHelpers.CreateCharacter();
        var quest = QuestManager.StartQuest(character, DynamicQuests.BuildQuest(Grant(GrantKind.Kill, 2, Now)));

        character.CurrentRoomId = 1;
        QuestManager.UpdateKillProgress(character, 33);
        Assert.False(quest.KillProgress.TryGetValue(33, out var none) && none > 0);

        character.CurrentRoomId = 2;
        QuestManager.UpdateKillProgress(character, 33);
        Assert.Equal(1, quest.KillProgress[33]);
    }

    [Fact]
    public void RoomBoundReturn_IsOnlyOfferedInThatRoom()
    {
        var character = TestHelpers.CreateCharacter();
        var quest = QuestManager.StartQuest(character, DynamicQuests.BuildQuest(Grant(GrantKind.Fetch, 1, Now)));
        quest.Status = QuestStatus.Completed;
        character.Inventory.Items.Add(new Myria.Lib.Core.Entities.Items.MaterialItem { Id = "iron_ore", Name = "item.iron_ore", StackSize = 1 });

        character.CurrentRoomId = 9;   // a different city's smith_default
        Assert.Empty(QuestManager.GetReturnableForNpc(character, "smith_default"));

        character.CurrentRoomId = 1;
        Assert.Single(QuestManager.GetReturnableForNpc(character, "smith_default"));
    }

    [Fact]
    public void RegisteredDynamicQuests_ResolveById()
    {
        var q = DynamicQuests.BuildQuest(Grant(GrantKind.Kill, 5, Now));
        QuestManager.RegisterDynamic(q);
        try { Assert.Same(q, QuestManager.GetQuestById(q.Id)); }
        finally { QuestManager.UnregisterDynamic(q.Id); }
        Assert.Null(QuestManager.GetQuestById(q.Id));
    }
}
