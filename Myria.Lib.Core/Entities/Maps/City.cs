namespace Myria.Lib.Core.Entities.Maps
{
    public class City : MapArea
    {
        public bool IsBig { get; set; } = false;
        /// <summary>v0.3: a small settlement on a road (healer, trader, Adventurers' Guild house) rather than a city.</summary>
        public bool IsVillage { get; set; } = false;
    }
}
