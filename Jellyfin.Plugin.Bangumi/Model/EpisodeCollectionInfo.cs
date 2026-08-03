namespace Jellyfin.Plugin.Bangumi.Model;

public class EpisodeCollectionInfo
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Episode? Episode { get; set; }

    public EpisodeCollectionType Type { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("updated_at")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long UpdatedAt { get; set; }
}
