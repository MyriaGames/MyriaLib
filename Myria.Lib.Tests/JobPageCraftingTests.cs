using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Manager;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// Job-page crafting (v0.3): only jobs opted in via Job.AllowJobPageCrafting may craft from the
/// Jobs page, and they craft their master NPC's recipes. Uses the real jobs/npcs/recipes data, so
/// this also pins the shipped content: Herbalist is the only opted-in job, and its master crafts
/// healing potions from sunleaf.
/// </summary>
[Collection("GameData")]
public class JobPageCraftingTests
{
    public JobPageCraftingTests(GameDataFixture _) { }

    [Fact]
    public void Herbalist_IsOptedIn_AndResolvesToItsMasterNpc()
    {
        var npc = JobManager.GetJobPageCraftNpc("herbalist");

        Assert.NotNull(npc);
        Assert.Equal("herbalist_master", npc!.Id);
        Assert.NotEmpty(CraftingService.GetRecipes(npc.Id));
    }

    [Theory]
    [InlineData("blacksmith")]
    [InlineData("leathersmith")]
    [InlineData("tailor")]
    [InlineData("artificer")]
    public void NpcBoundCraftingJobs_AreNotOptedIn(string jobId)
    {
        // These must keep requiring their NPC - the plan's whole point is that this is opt-in.
        Assert.Null(JobManager.GetJobPageCraftNpc(jobId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not_a_job")]
    public void NoOrUnknownJob_ResolvesToNull(string? jobId)
    {
        Assert.Null(JobManager.GetJobPageCraftNpc(jobId));
    }

    [Fact]
    public void Herbalist_CraftsHealingPotionFromSunleaf()
    {
        var character = TestHelpers.CreateCharacter();
        character.Inventory.Items.Add(new MaterialItem { Id = "sunleaf", Name = "item.sunleaf", StackSize = 2 });
        var npc = JobManager.GetJobPageCraftNpc("herbalist")!;

        var outcome = CraftExecutionService.Craft(character, npc, "t1_healing_potion", 1);

        Assert.True(outcome.Success);
        Assert.Equal("herbalist", outcome.JobId);
        Assert.DoesNotContain(character.Inventory.Items, i => i.Id == "sunleaf");
        Assert.Contains(character.Inventory.Items, i => i.Id == "t1_healing_potion" && i is ConsumableItem);
    }
}
