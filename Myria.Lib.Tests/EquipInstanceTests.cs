using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Services.Builder;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// Several items can share an id (a +4 and a +10 blade): equipping must use the exact instance the
/// player chose - by id alone it took whichever came first in the bag.
/// </summary>
[Collection("GameData")]
public class EquipInstanceTests
{
    public EquipInstanceTests(GameDataFixture _) { }

    private static EquipmentItem Blade(int level)
    {
        var e = (EquipmentItem)ItemFactory.CreateItem("iron_sword", 1, level, 1f)!;
        e.AllowedClasses.Clear();
        return e;
    }

    [Fact]
    public void SwapEquipment_WithAnInstance_EquipsThatInstance()
    {
        var ch = TestHelpers.CreateCharacter(level: 10);
        var b4 = Blade(4); var b6 = Blade(6); var b10 = Blade(10);
        ch.Inventory.Items.AddRange(new[] { b4, b6, b10 });

        Assert.True(ch.Inventory.SwapEquipment(b6, ch, out _));
        Assert.Same(b6, ch.WeaponSlot);
        Assert.Contains(b4, ch.Inventory.Items);
        Assert.Contains(b10, ch.Inventory.Items);
        Assert.DoesNotContain(b6, ch.Inventory.Items);

        Assert.True(ch.Inventory.SwapEquipment(b10, ch, out _));
        Assert.Same(b10, ch.WeaponSlot);
        Assert.Contains(b6, ch.Inventory.Items);
    }

    [Fact]
    public void SwapEquipment_InstanceNotInTheBag_Fails()
    {
        var ch = TestHelpers.CreateCharacter(level: 10);
        Assert.False(ch.Inventory.SwapEquipment(Blade(4), ch, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void FindEquipment_MatchesIdAndUpgradeState()
    {
        var ch = TestHelpers.CreateCharacter(level: 10);
        var b4 = Blade(4); var b6 = Blade(6);
        ch.Inventory.Items.AddRange(new[] { b4, b6 });

        Assert.Same(b6, ch.Inventory.FindEquipment("iron_sword", 6, 1f));
        Assert.Same(b4, ch.Inventory.FindEquipment("IRON_SWORD", 4, 1f));
        Assert.Null(ch.Inventory.FindEquipment("iron_sword", 6, 1.5f));
    }
}
