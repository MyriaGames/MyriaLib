using Myria.Lib.Core.Entities.Maps;
using Myria.Lib.Core.Entities.NPCs;
using Myria.Lib.Core.Models.Dto;
using Myria.Lib.Core.Services;
using Myria.Lib.Core.Services.Builder;
using Myria.Lib.Core.Systems.Enums;

namespace Myria.Lib.Core.Systems.World
{
    // ── v0.3 quest rework, part 2: dynamic quests generated from the world state ──────────────

    public enum GrantKind { Fetch, Kill }

    /// <summary>All dynamic-quest numbers in one place. Every value is a placeholder pending balance.</summary>
    public static class DynamicQuestRules
    {
        /// <summary>A gap is split into grants of about remaining/SplitFactor, clamped to the
        /// kind's min/max; below the minimum, one grant takes whatever is left.</summary>
        public const int SplitFactor = 3;
        public const int FetchMinGrant = 3, FetchMaxGrant = 10;
        public const int KillMinGrant  = 5, KillMaxGrant  = 15;

        /// <summary>An unfinished grant stops counting as "promised" after this - the NPC/room then
        /// offers that share again to someone else. The holder's copy stays completable.</summary>
        public static TimeSpan StaleAfter { get; set; } = TimeSpan.FromDays(7);

        /// <summary>Fetch reward per item, as a multiple of the item's shop price (at least 1 gold).</summary>
        public const double FetchGoldPerItemFactor = 1.25;
        public const int FetchXpPerItem = 4;
        public const int KillGoldPerKill = 8;
        /// <summary>Bounty XP per kill on top of the kill's own XP, as a fraction of the monster's XP (at least 1).</summary>
        public const double KillXpBonusFraction = 0.5;

        /// <summary>The Adventurers' Guild house NPC - one template, placed in every city/village.</summary>
        public const string GuildNpcId = "adventurers_guild";
        /// <summary>A guild house offers bounties for overworld rooms within this many exits.</summary>
        public const int GuildRadius = 3;
        /// <summary>Most bounties a guild house lists at once: the most overrun rooms first, then the
        /// nearest. (Every overworld room can be overrun, so the full list ran to 30+ near Royas.)</summary>
        public const int MaxBountiesShown = 6;
    }

    /// <summary>
    /// One player's copy of a dynamic quest: how much of a specific gap (an NPC instance's stock,
    /// or a room's population of one monster type) they've promised to close.
    /// </summary>
    public sealed class QuestGrant
    {
        public required string Id { get; init; }
        public required GrantKind Kind { get; init; }
        /// <summary>Fetch: the NPC's room. Kill: the overrun room.</summary>
        public required int RoomId { get; init; }
        public string? NpcId { get; init; }
        public string? ItemId { get; init; }
        public int? MonsterId { get; init; }
        public required int Target { get; init; }
        public required int RewardXp { get; init; }
        public required int RewardGold { get; init; }
        public required string HolderName { get; init; }
        public required DateTime AcceptedUtc { get; init; }
        public bool Completed { get; set; }

        public string QuestId => DynamicQuests.QuestIdPrefix + Id;
        public StockKey? StockKey => Kind == GrantKind.Fetch ? new StockKey(RoomId, NpcId!, ItemId!) : null;
        public PopulationKey? PopulationKey => Kind == GrantKind.Kill ? new PopulationKey(RoomId, MonsterId!.Value) : null;

        /// <summary>Whether this grant still counts toward closing its gap: not done, and not stale.</summary>
        public bool IsOutstanding(DateTime now) => !Completed && now - AcceptedUtc < DynamicQuestRules.StaleAfter;
    }

    public static class DynamicQuests
    {
        public const string QuestIdPrefix = "dyn.";

        public static bool IsDynamicQuestId(string questId) => questId.StartsWith(QuestIdPrefix, StringComparison.Ordinal);
        public static string GrantIdOf(string questId) => questId[QuestIdPrefix.Length..];

        // ── Sizing and rewards ────────────────────────────────────────────────

        /// <summary>How big the next grant against a remaining gap is: the gap split into roughly
        /// <see cref="DynamicQuestRules.SplitFactor"/> parts (more players get a share), clamped to
        /// the kind's min/max - and if the gap is already below the minimum, all of it.</summary>
        public static int GrantSize(int remaining, GrantKind kind)
        {
            if (remaining <= 0) return 0;
            var (min, max) = kind == GrantKind.Fetch
                ? (DynamicQuestRules.FetchMinGrant, DynamicQuestRules.FetchMaxGrant)
                : (DynamicQuestRules.KillMinGrant, DynamicQuestRules.KillMaxGrant);
            if (remaining <= min) return remaining;
            int share = (int)Math.Ceiling(remaining / (double)DynamicQuestRules.SplitFactor);
            return Math.Min(remaining, Math.Clamp(share, min, max));
        }

        /// <summary>Rewards scale with the grant's size (a "kill 15" pays three times a "kill 5").</summary>
        public static (int Xp, int Gold) Rewards(GrantKind kind, int target, string? itemId, int? monsterId)
        {
            if (kind == GrantKind.Fetch)
            {
                int price = itemId != null && ItemFactory.TryCreateItem(itemId, out var item) && item != null ? item.BuyPrice : 10;
                int goldPerItem = Math.Max(1, (int)Math.Round(price * DynamicQuestRules.FetchGoldPerItemFactor));
                return (DynamicQuestRules.FetchXpPerItem * target, goldPerItem * target);
            }
            long exp = monsterId is int id && MonsterService.GetMonsterById(id) is { } m ? m.Exp : 10;
            int xpPerKill = Math.Max(1, (int)Math.Round(exp * DynamicQuestRules.KillXpBonusFraction));
            return (xpPerKill * target, DynamicQuestRules.KillGoldPerKill * target);
        }

        /// <summary>Total still promised against one gap - what new grants must not exceed.</summary>
        public static int Promised(IEnumerable<QuestGrant> grants, StockKey key, DateTime now) =>
            grants.Where(g => g.StockKey == key && g.IsOutstanding(now)).Sum(g => g.Target);

        public static int Promised(IEnumerable<QuestGrant> grants, PopulationKey key, DateTime now) =>
            grants.Where(g => g.PopulationKey == key && g.IsOutstanding(now)).Sum(g => g.Target);

        // ── Offer tokens (what a client sends back to accept an offer) ────────

        public static string Token(StockKey k) => $"fetch|{k.RoomId}|{k.NpcId}|{k.ItemId}";
        public static string Token(PopulationKey k) => $"kill|{k.RoomId}|{k.MonsterId}";

        public static bool TryParseToken(string token, out StockKey? stock, out PopulationKey? population)
        {
            stock = null; population = null;
            var p = token.Split('|');
            if (p.Length == 4 && p[0] == "fetch" && int.TryParse(p[1], out var room))
            {
                stock = new StockKey(room, p[2], p[3]);
                return true;
            }
            if (p.Length == 3 && p[0] == "kill" && int.TryParse(p[1], out var kRoom) && int.TryParse(p[2], out var monster))
            {
                population = new PopulationKey(kRoom, monster);
                return true;
            }
            return false;
        }

        // ── Building the quest players see ────────────────────────────────────

        /// <summary>The Quest a grant is presented and tracked as. Fetch quests are handed in at
        /// the same NPC instance; kill quests count kills in the overrun room only and are handed in
        /// at any Adventurers' Guild house.</summary>
        public static Quest BuildQuest(QuestGrant g)
        {
            var quest = new Quest
            {
                Id = g.QuestId,
                IsDynamic = true,
                RequiredLevel = 1,
                RewardXp = g.RewardXp,
                RewardGold = g.RewardGold,
                Category = QuestCategory.Side,
            };
            string roomName = RoomService.AllRooms.FirstOrDefault(r => r.Id == g.RoomId)?.Name ?? "";

            if (g.Kind == GrantKind.Fetch)
            {
                string itemName = ItemFactory.TryCreateItem(g.ItemId!, out var item) && item != null ? item.Name : g.ItemId!;
                string npcName = NpcService.TryGet(g.NpcId!, out var npc) && npc != null ? npc.NameKey : g.NpcId!;
                quest.Name = "quest.dyn.fetch.name";
                quest.NameArgs = new() { g.Target.ToString(), itemName };
                quest.Description = "quest.dyn.fetch.desc";
                quest.DescriptionArgs = new() { g.Target.ToString(), itemName, npcName, roomName };
                quest.GiverNpcId = g.NpcId!;
                quest.ReturnNpcId = g.NpcId!;
                quest.ReturnRoomId = g.RoomId;
                quest.RequiredItems = new() { [g.ItemId!] = g.Target };
            }
            else
            {
                string monsterName = MonsterService.GetMonsterById(g.MonsterId!.Value)?.Name ?? g.MonsterId.ToString()!;
                quest.Name = "quest.dyn.kill.name";
                // The room is part of the name - bounties for the same monster in neighbouring rooms
                // looked identical in the list ("Bounty: 5 Marsh Vipers" several times).
                quest.NameArgs = new() { g.Target.ToString(), monsterName, roomName };
                quest.Description = "quest.dyn.kill.desc";
                quest.DescriptionArgs = new() { g.Target.ToString(), monsterName, roomName };
                quest.GiverNpcId = DynamicQuestRules.GuildNpcId;
                quest.ReturnNpcId = DynamicQuestRules.GuildNpcId;
                quest.RequiredRoomId = g.RoomId;
                quest.RequiredKills = new() { [g.MonsterId.Value] = g.Target };
            }
            return quest;
        }

        public static DynamicQuestDto ToDto(Quest q) => new(
            q.Id, q.Name, new List<string>(q.NameArgs), q.Description, new List<string>(q.DescriptionArgs),
            q.GiverNpcId, q.ReturnNpcId ?? "", q.ReturnRoomId, q.RequiredRoomId,
            new Dictionary<int, int>(q.RequiredKills), new Dictionary<string, int>(q.RequiredItems),
            q.RewardXp, q.RewardGold);

        public static Quest FromDto(DynamicQuestDto d) => new()
        {
            Id = d.Id, IsDynamic = true, RequiredLevel = 1, Category = QuestCategory.Side,
            Name = d.Name, NameArgs = new List<string>(d.NameArgs),
            Description = d.Description, DescriptionArgs = new List<string>(d.DescriptionArgs),
            GiverNpcId = d.GiverNpcId, ReturnNpcId = string.IsNullOrEmpty(d.ReturnNpcId) ? null : d.ReturnNpcId,
            ReturnRoomId = d.ReturnRoomId, RequiredRoomId = d.RequiredRoomId,
            RequiredKills = new Dictionary<int, int>(d.RequiredKills),
            RequiredItems = new Dictionary<string, int>(d.RequiredItems),
            RewardXp = d.RewardXp, RewardGold = d.RewardGold,
        };

        // ── Adventurers' Guild reach ──────────────────────────────────────────

        /// <summary>Rooms within <paramref name="maxDistance"/> exits of <paramref name="originRoomId"/>
        /// (breadth-first over room exits), including the origin itself.</summary>
        /// <summary>Exit distance from <paramref name="originRoomId"/> to every room within
        /// <paramref name="maxDistance"/> exits (the origin itself at 0).</summary>
        public static Dictionary<int, int> DistancesWithin(int originRoomId, int maxDistance)
        {
            var dist = new Dictionary<int, int> { [originRoomId] = 0 };
            var frontier = new List<int> { originRoomId };
            for (int d = 1; d <= maxDistance && frontier.Count > 0; d++)
            {
                var next = new List<int>();
                foreach (var id in frontier)
                {
                    var room = RoomService.AllRooms.FirstOrDefault(r => r.Id == id);
                    if (room == null) continue;
                    foreach (var exit in room.ExitIds.Values)
                        if (dist.TryAdd(exit, d)) next.Add(exit);
                }
                frontier = next;
            }
            return dist;
        }

        public static HashSet<int> RoomsWithin(int originRoomId, int maxDistance)
        {
            var seen = new HashSet<int> { originRoomId };
            var frontier = new List<int> { originRoomId };
            for (int d = 0; d < maxDistance && frontier.Count > 0; d++)
            {
                var next = new List<int>();
                foreach (var id in frontier)
                {
                    var room = RoomService.AllRooms.FirstOrDefault(r => r.Id == id);
                    if (room == null) continue;
                    foreach (var exit in room.ExitIds.Values)
                        if (seen.Add(exit)) next.Add(exit);
                }
                frontier = next;
            }
            return seen;
        }
    }

    /// <summary>
    /// Display text for quests. Authored quests keep working as before (a "quest." key is
    /// localized, anything else shown as-is); dynamic quests format their key with their args,
    /// localizing args that are themselves keys (item./monster./room./game.npc.).
    /// </summary>
    public static class QuestText
    {
        public static string Name(Quest q) => Format(q.Name, q.NameArgs);
        public static string Description(Quest q) => Format(q.Description, q.DescriptionArgs);

        private static readonly string[] KeyPrefixes = { "item.", "monster.", "room.", "game.npc." };

        private static string Format(string text, List<string> args)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (args.Count == 0)
                return text.StartsWith("quest.", StringComparison.OrdinalIgnoreCase) ? Localization.T(text) : text;
            var localized = args.Select(a => KeyPrefixes.Any(p => a.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                ? (object)Localization.T(a) : a).ToArray();
            return Localization.T(text, localized);
        }
    }
}
