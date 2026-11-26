using Myria.Lib.Core.Entities.Monsters;
using Myria.Lib.Core.Entities;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// RespawnSeconds (added for Myria.Mono's real-time world — see WorldMonster) has the same
/// "manual field-by-field Clone()" risk Skill.Range had: Clone() is what WorldMonster actually
/// spawns from (never the shared template), so a value left out of Clone() would silently make
/// every monster respawn as if RespawnSeconds were never set, rather than throwing.
/// </summary>
public class MonsterRespawnTests
{
    [Fact]
    public void DefaultsToAFastRespawn()
    {
        var monster = new Monster(1, "Goblin", new Stats { Strength = 8 }, "", 5);
        Assert.Equal(5f, monster.RespawnSeconds);
    }

    [Fact]
    public void CloneWithDeltasPreservesRespawnSeconds()
    {
        var monster = new Monster(1, "Goblin", new Stats { Strength = 8 }, "", 5) { RespawnSeconds = 7.3f };
        var clone = monster.Clone();
        Assert.Equal(7.3f, clone.RespawnSeconds);
    }
}
