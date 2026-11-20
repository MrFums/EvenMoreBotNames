using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Bot;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services.Bot;
using SPTarkov.Server.Core.Services.Ragfair;
using Path = System.IO.Path;

namespace EvenMoreBotNames;

public class ModConfig
{
    public bool Enabled { get; set; } = true;
    public bool AddUsecNames { get; set; } = true;
    public bool AddBearNames { get; set; } = true;
    public int UsecNamesToLoad { get; set; }
    public int BearNamesToLoad { get; set; }
    public int NameSeed { get; set; } = 1;
    public bool ReplaceExistingNames { get; set; }
    public bool FixScavProfileNameCopy { get; set; } = true;
    public bool RenameExistingFleaSellers { get; set; } = true;
    public bool LogMemoryUsage { get; set; } = true;
}

public class NameFile
{
    public List<string> Names { get; set; } = [];
}

[Injectable(TypePriority = OnLoadOrder.PostLoad + 10)]
public class NameLoader(
    ISptLogger<NameLoader> logger,
    BotTable botTable,
    ModHelper modHelper,
    BotHelper botHelper,
    BotConfig botConfig,
    RagfairOfferService ragfairOfferService,
    SaveServer saveServer) : IOnLoad
{
    private const string Tag = "[EvenMoreBotNames]";
    private const string ConfigFile = "config.jsonc";

    private static readonly JsonSerializerOptions Lenient = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Load();
        }
        catch (Exception exception)
        {
            logger.Error($"{Tag} Failed to load names: {exception.Message}", exception);
        }

        return Task.CompletedTask;
    }

    private void Load()
    {
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config = LoadConfig(modPath);
        if (!config.Enabled)
        {
            return;
        }

        var memoryBefore = config.LogMemoryUsage ? GC.GetTotalMemory(true) : 0;
        var timer = Stopwatch.StartNew();

        var usec = config.AddUsecNames ? AddNames(modPath, "usec", config.UsecNamesToLoad, config) : 0;
        var bear = config.AddBearNames ? AddNames(modPath, "bear", config.BearNamesToLoad, config) : 0;

        // The flea fills SPT's PMC name cache before this runs, so clear it or the old names stick.
        var renamed = ClearNameCache() && config.RenameExistingFleaSellers ? RenameFleaSellers() : 0;

        if (config.FixScavProfileNameCopy)
        {
            RandomPmcNamePatch.Table = botTable;
            new RandomPmcNamePatch().Enable();
        }

        var memory = config.LogMemoryUsage
            ? $", {(GC.GetTotalMemory(true) - memoryBefore) / 1048576.0:F1} MB"
            : "";
        logger.Success($"{Tag} Added {usec} USEC and {bear} BEAR names, renamed {renamed} flea sellers ({timer.ElapsedMilliseconds} ms{memory})");
    }

    private int AddNames(string modPath, string side, int limit, ModConfig config)
    {
        if (!botTable.Types.TryGetValue(side, out var botType) || botType is null)
        {
            return 0;
        }

        var file = Path.Combine(modPath, "names", $"{side}.json");
        if (!File.Exists(file))
        {
            logger.Warning($"{Tag} names/{side}.json is missing");
            return 0;
        }

        List<string> names;
        using (var stream = File.OpenRead(file))
        {
            names = JsonSerializer.Deserialize<NameFile>(stream, Lenient)?.Names ?? [];
        }

        if (limit > 0 && names.Count > limit)
        {
            // Pick by a stable hash so every start gets the same names. Mods like PBT build a
            // profile per name, so a new random pick each start makes them redo it every time.
            // Raising the limit keeps the names already picked and adds more.
            names = names
                .Select(name => (Name: name, Rank: StableHash(name, config.NameSeed)))
                .OrderBy(entry => entry.Rank)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .Take(limit)
                .Select(entry => entry.Name)
                .ToList();
        }

        var firstNames = botType.FirstNames;
        if (config.ReplaceExistingNames)
        {
            firstNames.Clear();
        }

        var seen = new HashSet<string>(firstNames, StringComparer.OrdinalIgnoreCase);
        firstNames.EnsureCapacity(firstNames.Count + names.Count);

        var added = 0;
        foreach (var name in names)
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
            {
                firstNames.Add(name);
                added++;
            }
        }

        return added;
    }

    // FNV-1a with a SplitMix64 finish. string.GetHashCode changes every process, so it can't be used here.
    private static ulong StableHash(string value, int seed)
    {
        var hash = 14695981039346656037UL ^ (uint)seed;
        foreach (var c in value)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        hash = (hash ^ (hash >> 30)) * 0xBF58476D1CE4E5B9UL;
        hash = (hash ^ (hash >> 27)) * 0x94D049BB133111EBUL;
        return hash ^ (hash >> 31);
    }

    private bool ClearNameCache()
    {
        var field = typeof(BotHelper).GetField("_pmcNameCache", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(botHelper) is System.Collections.IDictionary cache)
        {
            cache.Clear();
            return true;
        }

        logger.Warning($"{Tag} Couldn't clear SPT's PMC name cache");
        return false;
    }

    private int RenameFleaSellers()
    {
        var playerIds = new HashSet<MongoId>(saveServer.GetProfiles().Keys);
        var renamed = 0;
        foreach (var offer in ragfairOfferService.GetOffers())
        {
            var user = offer.User;
            if (user is null
                || user.MemberType == MemberCategory.Trader
                || string.IsNullOrEmpty(user.Nickname)
                || playerIds.Contains(user.Id))
            {
                continue;
            }

            user.Nickname = botHelper.GetPmcNicknameOfMaxLength(botConfig.BotNameLengthLimit);
            renamed++;
        }

        return renamed;
    }

    private ModConfig LoadConfig(string modPath)
    {
        var folder = Path.Combine(modPath, "configs");
        var file = Path.Combine(folder, ConfigFile);
        try
        {
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(folder);
                using var stream = typeof(NameLoader).Assembly.GetManifestResourceStream($"EvenMoreBotNames.configs.{ConfigFile}");
                using var output = File.Create(file);
                stream?.CopyTo(output);
                logger.Warning($"{Tag} {ConfigFile} was missing, wrote the default one.");
            }

            return JsonSerializer.Deserialize<ModConfig>(File.ReadAllText(file), Lenient) ?? new ModConfig();
        }
        catch (Exception exception)
        {
            logger.Error($"{Tag} Couldn't read configs/{ConfigFile}, using defaults: {exception.Message}");
            return new ModConfig();
        }
    }
}

// SPT copies both PMC name lists every time it names a player scav. This picks one without the copy.
[IgnoreAutoPatch]
public class RandomPmcNamePatch : AbstractPatch
{
    internal static BotTable? Table;

    protected override MethodBase? GetTargetMethod()
    {
        return typeof(BotNameService).GetMethod("GetRandomPmcName", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
    }

    [PatchPrefix]
    public static bool Prefix(ref string __result)
    {
        if (Table?.Types is not { } types
            || !types.TryGetValue("usec", out var usecType) || usecType?.FirstNames is not { } usec
            || !types.TryGetValue("bear", out var bearType) || bearType?.FirstNames is not { } bear
            || usec.Count + bear.Count == 0)
        {
            return true;
        }

        var i = Random.Shared.Next(usec.Count + bear.Count);
        __result = i < usec.Count ? usec[i] : bear[i - usec.Count];
        return false;
    }
}
