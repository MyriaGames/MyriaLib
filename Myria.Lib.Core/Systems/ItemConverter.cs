using System.Text.Json;
using System.Text.Json.Serialization;
using Myria.Lib.Core.Entities.Items;
using Myria.Lib.Core.Services.Builder;

namespace Myria.Lib.Core.Systems
{
    public class ItemConverter : JsonConverter<Item>
    {
        public override Item Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            if (!root.TryGetProperty("Id", out var idProp))
                throw new JsonException("Missing 'Id' field in item.");

            string id = idProp.GetString()!;

            int stackSize = root.TryGetProperty("StackSize", out var stackProp)
                ? stackProp.GetInt32()
                : 1;

            var item = ItemFactory.CreateItem(id, stackSize);
            if (item == null)
            {
                LoadWarnings.Track(id);
                return null!; // caller must strip nulls from any List<Item> after deserialization
            }

            // Re-apply craft quality and upgrade level so upgraded/crafted gear isn't reset on load
            if (item is EquipmentItem equipment)
            {
                float quality = root.TryGetProperty("CraftQuality", out var qualityProp) ? qualityProp.GetSingle() : 1f;
                int level = root.TryGetProperty("UpgradeLevel", out var upgradeProp) ? upgradeProp.GetInt32() : 0;
                equipment.RestoreUpgradeState(level, quality);
            }

            return item;
        }

        public override void Write(Utf8JsonWriter writer, Item value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, (object)value, value.GetType(), options);
        }
    }
}
