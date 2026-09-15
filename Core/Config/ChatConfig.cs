using System.Text.Json;
using System.Text.Json.Serialization;

namespace MnmDamageParser.Core.Config;

/// <summary>One chat category = a window in the split Chat view, plus the
/// regexes that route a line to it.</summary>
public sealed class ChatCategoryConfig
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>.NET regexes (case-sensitive). A line joins this category
    /// if ANY of them matches its tag-stripped text. Categories are tested
    /// in file order; first hit wins.</summary>
    [JsonPropertyName("match")] public List<string> Match { get; set; } = new();
}

/// <summary><c>config/chat.json</c>: how the Chat window splits non-combat
/// chat into per-category windows. A missing file, an empty
/// <c>categories</c> list, or a parse error all mean "one combined Chat
/// window" -- the feature degrades, it never breaks the app.</summary>
public sealed class ChatConfig
{
    [JsonPropertyName("_readme")] public string? Readme { get; set; }

    /// <summary>Regexes that drop a line from every Chat window -- for
    /// combat the parser can't shape (damageless / absorbed hits) that
    /// leaks past the combat filter because it carries no combat anchor.</summary>
    [JsonPropertyName("deny")] public List<string> Deny { get; set; } = new();

    [JsonPropertyName("categories")] public List<ChatCategoryConfig> Categories { get; set; } = new();

    public static ChatConfig Load(string path)
    {
        if (!File.Exists(path)) return new ChatConfig();
        try
        {
            return JsonSerializer.Deserialize<ChatConfig>(File.ReadAllText(path), JsonOpts.Default)
                   ?? new ChatConfig();
        }
        catch
        {
            return new ChatConfig();
        }
    }
}
