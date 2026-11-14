using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Imports;

/// <summary>Validates imported progress before writing a new character and its complete save in one transaction.</summary>
static class PathOfBuildingCharacters
{
    public sealed record Progress(CharacterClass Class, int Level, uint Experience, List<ushort> Passives,
        Dictionary<ushort, ushort> MasteryEffects, ushort ExtraPassivePoints, IReadOnlyList<string> Warnings,
        Dictionary<ushort, string>? Tattoos = null, Dictionary<ushort, ushort>? TattooKeystones = null)
    {
        public CharacterSaves.Save ToSave(PathOfBuildingItems.Result items) => new(items.Inventories,
            SkillBar: items.SkillBar, Experience: Experience, Passives: Passives, MasteryEffects: MasteryEffects,
            ExtraPassivePoints: ExtraPassivePoints, Waypoints: [Town], Visited: [Town],
            Tattoos: Tattoos is { Count: > 0 } ? Tattoos : null, TattooKeystones: TattooKeystones is { Count: > 0 } ? TattooKeystones : null);
    }

    const string Town = "1_1_town";

    public static Progress Prepare(PathOfBuildingBuild build, string folder = "data",
        Func<IReadOnlyDictionary<uint, uint[]>>? graphProvider = null)
    {
        if (!Enum.IsDefined(build.Class) || build.Level is < 1 or > 100)
            throw new ArgumentException("The build must use a supported character class and a level from 1 to 100.");
        var tree = build.Tree;
        var warnings = new List<string>();
        if (tree.ClassId != (int)build.Class)
            throw new ArgumentException("The build's class does not match its selected passive tree.");
        if (tree.Version is { Length: > 0 } version && version is not "3_29" and not "3.29")
            throw new ArgumentException($"The selected passive tree targets {version}; this server requires 3_29.");

        // Character integer IDs are the PoB class IDs, whereas PassiveSkills.Characters stores data-table rows.
        using var characters = ReadTable(folder, "Characters");
        int classRow = -1, row = 0;
        foreach (var character in characters.RootElement.EnumerateArray())
        {
            if (character.GetProperty("IntegerId").GetInt32() == (int)build.Class) classRow = row;
            row++;
        }
        if (classRow < 0) throw new ArgumentException($"The extracted Characters table does not contain {build.Class}.");

        using var experience = ReadTable(folder, "ExperienceLevels");
        uint? xp = null;
        foreach (var entry in experience.RootElement.EnumerateArray())
            if (entry.GetProperty("Level").GetInt32() == build.Level)
                xp = entry.GetProperty("Experience").GetUInt32();
        if (xp is null || build.Level > 1 && xp == 0)
            throw new ArgumentException($"The extracted ExperienceLevels table has no valid experience for level {build.Level}.");

        var passives = new List<ushort>();
        var masteries = new Dictionary<ushort, ushort>();
        if (tree.Version is null or "")
            warnings.Add("The build does not identify its passive-tree version; all selected node and mastery IDs were checked against 3.29 data.");
        var passiveTree = NativeData.Shared("passive-tree", () => new PassiveTree(folder));
        using var passiveData = ReadTable(folder, "PassiveSkills");
        var metadata = NativeData.Shared("passive-metadata", () =>
        {
            var nodes = new Dictionary<uint, JsonElement>();
            foreach (var node in passiveData.RootElement.EnumerateArray())
                if (node.GetProperty("PassiveSkillGraphId").TryGetUInt32(out uint id)) nodes.TryAdd(id, node);
            return nodes;
        });
        var seen = new HashSet<uint>();
        foreach (uint id in tree.Nodes)
        {
            if (!seen.Add(id)) throw new ArgumentException($"Passive {id} occurs more than once in the selected tree.");
            // Expansion IDs, ascendancies and Bloodlines are materialized separately, after their jewels.
            if (id > ushort.MaxValue) continue;
            if (!metadata.TryGetValue(id, out var raw) || passiveTree.Find((ushort)id) is not { } node)
                throw new ArgumentException($"Passive {id} is absent from this client's tree.");
            if (node.IsAscendancy) continue;
            if (Flag(raw, "IsExpansion") || Flag(raw, "IsProxyPassive") || Flag(raw, "IsAnointmentOnly")
                || raw.TryGetProperty("SkillType", out var skillType) && skillType.GetInt32() != 0
                || raw.TryGetProperty("DescendancyKey", out var descendancy) && descendancy.ValueKind == JsonValueKind.Number)
                continue;
            if (node.IsStart)
            {
                if (!raw.GetProperty("Characters").EnumerateArray().Any(c => c.GetInt32() == classRow))
                    throw new ArgumentException($"The selected tree contains another class's starting node ({id}).");
                // The native client supplies the class start implicitly; it spends no passive skill point.
                continue;
            }
            passives.Add((ushort)id);
        }
        foreach (var (nodeId, effectId) in tree.Masteries)
        {
            if (!passives.Contains(nodeId))
                throw new ArgumentException($"Mastery {effectId} has no allocated ordinary passive {nodeId}.");
            if (passiveTree.Find(nodeId) is not { MasteryGroup: >= 0 })
                throw new ArgumentException($"Mastery effect {effectId} is not valid for allocated passive {nodeId} in 3.29.");
            if (passiveTree.FindEffect(effectId) is not { } effect
                || effect.Hash != effectId || !passiveTree.IsEffectFor(nodeId, effect)
                || masteries.ContainsValue(effectId))
            {
                throw new ArgumentException($"Mastery passive {nodeId}: effect {effectId} is unavailable, incompatible or already selected.");
            }
            masteries.Add(nodeId, effectId);
        }
        bool Runegrafted(ushort nodeId) => tree.Overrides.Any(o => o.NodeId == nodeId && o.Name.StartsWith("Runegraft ", StringComparison.Ordinal));
        foreach (ushort nodeId in passives)
            if (passiveTree.Find(nodeId) is { MasteryGroup: >= 0 } && !masteries.ContainsKey(nodeId) && !Runegrafted(nodeId))
                throw new ArgumentException($"Mastery passive {nodeId}: no mastery effect was selected.");
        // Connectivity is validated by the Game module against the complete build, including Ascendant
        // starts and actual radius jewels. Pruning here would silently destroy valid detached allocations.
        var tattoos = PathOfBuildingTattoos.Prepare(tree.Overrides, passives, passiveTree, passiveData.RootElement, folder, warnings);

        int requiredQuestPoints = Math.Max(0, passives.Count - (build.Level - 1) - passiveTree.PointsGranted(passives) - NativeProgress.AscendancyPoints(build, folder));
        if (requiredQuestPoints > 24)
            throw new ArgumentException($"The selected tree requires {requiredQuestPoints} extra passive points; the supported quest budget is 24.");
        if (requiredQuestPoints > 0)
            warnings.Add($"Imported {requiredQuestPoints} extra passive point(s) needed by the selected tree; campaign quests were not completed.");
        return new(build.Class, build.Level, xp.Value, passives, masteries, (ushort)requiredQuestPoints, warnings,
            tattoos.Tattoos, tattoos.Keystones);
    }

    static NativeData.Lease ReadTable(string folder, string table)
    {
        string path = Path.Combine(folder, "tables", table + ".json");
        if (!NativeData.Exists(path)) throw new ArgumentException($"Missing game data: {path}. Run scripts/dev/extract-data.ps1 first.");
        var doc = NativeData.Read(path);
        if (doc.RootElement.ValueKind == JsonValueKind.Array) return doc;
        doc.Dispose();
        throw new ArgumentException($"The extracted {table} table is invalid; extract the game data again.");
    }

    static bool Flag(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    static bool NonDefault(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Equals("None", StringComparison.OrdinalIgnoreCase);
}
