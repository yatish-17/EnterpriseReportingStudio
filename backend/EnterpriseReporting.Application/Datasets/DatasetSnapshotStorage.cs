using System.Text.Json;

namespace EnterpriseReporting.Application.Datasets;

public static class DatasetSnapshotStorage
{
    private const string Prefix = "$ers-snapshot-v1$";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(DatasetSnapshot snapshot) =>
        Prefix + JsonSerializer.Serialize(snapshot, JsonOptions);

    public static bool TryDeserialize(string value, out DatasetSnapshot? snapshot)
    {
        snapshot = null;
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        snapshot = JsonSerializer.Deserialize<DatasetSnapshot>(value[Prefix.Length..], JsonOptions);
        return snapshot is not null;
    }
}
