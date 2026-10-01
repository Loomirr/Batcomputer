using System.Text.Json;
using System.Text.RegularExpressions;

namespace Batcomputer;

/// <summary>Organization only: named groups never add motion layers or alter bone transforms.</summary>
internal static class AnimationBoneGroupService
{
    internal static void Validate(JsonElement draft, IReadOnlyCollection<string> nativeBones)
    {
        if (!draft.TryGetProperty("boneGroups", out var value)) return;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("schema", out var schema) ||
            schema.ValueKind != JsonValueKind.String || schema.GetString() != "batcomputer.bone-groups.v1" ||
            !value.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array || groups.GetArrayLength() > 32)
            throw new InvalidDataException("Invalid bone group metadata.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                !Regex.IsMatch(id.GetString()!, "\\A[a-zA-Z0-9_-]{1,64}\\z") || !ids.Add(id.GetString()!) ||
                !group.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Length > 48 ||
                !group.TryGetProperty("bones", out var bones) || bones.ValueKind != JsonValueKind.Array || bones.GetArrayLength() > nativeBones.Count)
                throw new InvalidDataException("Bone groups need unique IDs, names, and native bone members.");
            foreach (var bone in bones.EnumerateArray())
                if (bone.ValueKind != JsonValueKind.String || !nativeBones.Contains(bone.GetString()!, StringComparer.Ordinal) || !members.Add(bone.GetString()!))
                    throw new InvalidDataException("Unknown or repeated bone group member. Each bone can belong to one group only.");
        }
    }
}
