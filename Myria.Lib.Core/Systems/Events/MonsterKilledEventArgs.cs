namespace Myria.Lib.Core.Systems.Events
{
    public class MonsterKilledEventArgs : EventArgs
    {
        public int MonsterId { get; }

        /// <summary>The instance that died (dungeon runs remove exactly this one from their room).</summary>
        public Myria.Lib.Core.Entities.Monsters.Monster? Monster { get; }

        public MonsterKilledEventArgs(int monsterId, Myria.Lib.Core.Entities.Monsters.Monster? monster = null)
        {
            MonsterId = monsterId;
            Monster = monster;
        }
    }
}
