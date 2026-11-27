using Myria.Lib.Core.Entities;
using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Entities.Monsters;
using Myria.Lib.Core.Systems;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// A group fight's drops go to its LootRecipient (a party's loot master in a dungeon) when set,
/// even when they aren't the first player - otherwise to the first living player, as before.
/// </summary>
[Collection("GameData")]
public class GroupLootRecipientTests
{
    public GroupLootRecipientTests(GameDataFixture _) { }

    private static Monster CertainDrop()
    {
        var m = new Monster(1, "Dummy", new Stats(), "", exp: 0)
        {
            UniqueLootTable = { new UniqueLootEntry { ItemId = "iron_sword", DropChance = 1f } },
        };
        m.ResetHealth();
        m.CurrentHealth = 1;
        return m;
    }

    private static Myria.Lib.Core.Entities.Characters.Character Tough(string name, int level)
    {
        var c = TestHelpers.CreateCharacter(name, level);
        c.Stats.BaseHealth = 1_000_000; c.CurrentHealth = c.MaxHealth;   // nobody dies before the drop
        return c;
    }

    private static int Swords(Myria.Lib.Core.Entities.Characters.Character c) =>
        c.Inventory.Items.Count(i => i.Id == "iron_sword");

    [Fact]
    public void Drops_GoToTheLootRecipient()
    {
        var first = Tough("First", 10);
        var master = Tough("Master", 10);
        var enc = new GroupCombatEncounter(new[] { first, master }, new List<Monster> { CertainDrop() })
        {
            LootRecipient = master,
        };

        while (!enc.IsFinished)
        {
            var who = enc.CurrentTurnCharacterName;
            Assert.True(enc.CharacterAttack(who, 0));
        }

        Assert.Equal(1, Swords(master));
        Assert.Equal(0, Swords(first));
    }

    [Fact]
    public void WithoutARecipient_TheFirstLivingPlayerGetsTheDrops()
    {
        var first = Tough("First", 10);
        var second = Tough("Second", 10);
        var enc = new GroupCombatEncounter(new[] { first, second }, new List<Monster> { CertainDrop() });

        var expected = enc.Characters[0];   // first in turn order (the encounter sorts by initiative)
        var other = expected == first ? second : first;
        while (!enc.IsFinished) enc.CharacterAttack(enc.CurrentTurnCharacterName, 0);

        Assert.Equal(1, Swords(expected));
        Assert.Equal(0, Swords(other));
    }
}
