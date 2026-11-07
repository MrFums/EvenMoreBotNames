using SPTarkov.Server.Core.Models.Spt.Mod;

namespace EvenMoreBotNames;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.fums.evenmorebotnames";
    public string Name { get; init; } = "EvenMoreBotNames";
    public string Author { get; init; } = "Fums";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; } = false;
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public bool? IsBundleMod { get; init; } = false;
    public string License { get; init; } = "MIT";
}
