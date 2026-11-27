using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Builder;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>MONO-007 (v0.3): a character round-trips through a string with the same load path as a save file.</summary>
[Collection("GameData")]
public class CharacterSerializationTests
{
    public CharacterSerializationTests(GameDataFixture _) { }

    [Fact]
    public void Character_RoundTripsThroughAString()
    {
        var c = TestHelpers.CreateCharacter("Stringy", 9);
        c.Class = "Fighter";
        c.Inventory.AddItem(ItemFactory.CreateItem("iron_sword")!, c);
        c.Experience = 123;

        string json = CharacterService.SerializeCharacter(c);
        Assert.Contains("\"SaveVersion\"", json);

        var back = CharacterService.DeserializeCharacter(json)!;
        Assert.Equal("Stringy", back.Name);
        Assert.Equal(9, back.Level);
        Assert.Equal("Fighter", back.Class);
        Assert.Equal(123, back.Experience);
        Assert.Equal(c.Stats.Strength, back.Stats.Strength);
        Assert.Contains(back.Inventory.Items, i => i.Id == "iron_sword");
    }

    [Fact]
    public void OldBareCharacterJson_StillLoads()
    {
        var c = TestHelpers.CreateCharacter("Bare", 3);
        string wrapped = CharacterService.SerializeCharacter(c);
        string bare = System.Text.Json.Nodes.JsonNode.Parse(wrapped)!["Character"]!.ToJsonString();
        Assert.Equal("Bare", CharacterService.DeserializeCharacter(bare)!.Name);
    }
}
