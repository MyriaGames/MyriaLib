using Myria.Lib.Core.Entities.Maps;
using Myria.Lib.Core.Entities.Characters;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Services.Manager;
using Myria.Lib.Core.Systems;
using Myria.Lib.Core.Systems.Enums;

namespace Myria.Lib.Core.Services
{
    /// <summary>
    /// Outcome of a single <see cref="GatherService.Gather"/> attempt. ItemId/Amount/JobId/
    /// XpGranted are only meaningful when Result is <see cref="GatherResult.Success"/>.
    /// </summary>
    public readonly record struct GatherOutcome(
        GatherResult Result,
        string? ItemId,
        int Amount,
        string? JobId,
        long XpGranted);

    public static class GatherService
    {
        /// <summary>
        /// Attempts a single gather action in <paramref name="room"/> for <paramref name="character"/>.
        /// Tries each of the room's gathering spots in order, skipping any the character lacks the
        /// required tool for, and uses the first workable one. Consumes one gather charge (taking
        /// the character's job-knowledge bonus into account) and adds the item to inventory.
        /// This is the single implementation both single-player and multiplayer hosts should call —
        /// previously each had its own independent, hand-duplicated copy of this exact logic.
        /// </summary>
        public static GatherOutcome Gather(Character character, Room room)
        {
            if (room.GatheringSpots.Count == 0)
                return new GatherOutcome(GatherResult.NoSpots, null, 0, null, 0);

            int bonus = JobManager.GetGatherKnowledgeBonus(character);
            if (room.GathersRemaining + bonus <= 0)
                return new GatherOutcome(GatherResult.Depleted, null, 0, null, 0);

            var spot = PickSpot(character, room, out var failure);
            if (spot is null)
                return new GatherOutcome(failure, null, 0, null, 0);

            if (!ItemFactory.TryCreateItem(spot.GatheredItemId, out var item))
                return new GatherOutcome(GatherResult.NoSpots, null, 0, null, 0);

            string jobId = JobFor(spot.Type);

            long xpGranted = 0;
            if (!string.IsNullOrEmpty(jobId))
            {
                item.StackSize = JobManager.GetGatherAmount(character, jobId);
                xpGranted = 10;
            }
            if (item.StackSize <= 0)
                item.StackSize = 1;

            // Capture before AddItem — it decrements StackSize during stack-merging.
            int gatheredAmount = item.StackSize;

            if (!character.Inventory.AddItem(item, character, "gather"))
                return new GatherOutcome(GatherResult.InventoryFull, null, 0, null, 0);

            room.GathersRemaining--;
            if (!string.IsNullOrEmpty(jobId))
                JobManager.GrantSkillXp(character, jobId, xpGranted);

            DayCycleManager.AddTicks(GameTick.Gather);
            return new GatherOutcome(GatherResult.Success, item.Id, gatheredAmount, jobId, xpGranted);
        }

        /// <summary>The gathering job a spot type trains ("" for none).</summary>
        public static string JobFor(string spotType) => spotType switch
        {
            GatheringType.Ore  => "miner",
            GatheringType.Tree => "woodcutter",
            GatheringType.Herb => "herbalist",
            _                  => ""
        };

        /// <summary>Whether the character has the spot's tool (in the bag or equipped as weapon).</summary>
        public static bool HasTool(Character character, GatheringSpot spot) =>
            string.IsNullOrEmpty(spot.RequiredToolId)
            || character.Inventory.Items.Any(i => i.Id == spot.RequiredToolId)
            || character.WeaponSlot?.Id == spot.RequiredToolId;

        /// <summary>Whether the character's job skill is high enough for the spot (v0.3).</summary>
        public static bool HasSkill(Character character, GatheringSpot spot)
        {
            if (spot.RequiredSkillLevel <= 0) return true;
            string jobId = JobFor(spot.Type);
            return string.IsNullOrEmpty(jobId)
                || JobXpService.GetLevel(JobManager.GetOrAdd(character, jobId).SkillXp) >= spot.RequiredSkillLevel;
        }

        /// <summary>
        /// The spot one gather works: a random one among those the character has the tool and the
        /// skill for, so a room with several resources yields all of them (it used to always take
        /// the first workable spot). Null with the reason when none is workable - SkillTooLow if a
        /// tool was there but the skill wasn't, else NoTool. Shared with the realm server.
        /// </summary>
        public static GatheringSpot? PickSpot(Character character, Room room, out GatherResult failure)
        {
            var withTool = room.GatheringSpots.Where(s => HasTool(character, s)).ToList();
            var workable = withTool.Where(s => HasSkill(character, s)).ToList();
            failure = workable.Count > 0 ? GatherResult.Success
                    : withTool.Count > 0 ? GatherResult.SkillTooLow
                    : GatherResult.NoTool;
            return workable.Count == 0 ? null : workable[Random.Shared.Next(workable.Count)];
        }
    }
}
