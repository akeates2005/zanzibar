using System.Text.RegularExpressions;

namespace Poc.Application.Authorization;

/// <summary>
/// The engine's own rule for a resource's object id, for objects of any team's type. Observed on SpiceDB v1.57.1
/// (DECISIONS.md, 2026-11-18): it accepts <c>[a-zA-Z0-9/_|\-=+]</c>, 1 to 1024 bytes, and refuses <c>*</c> as a resource.
/// The API proto states the same rule (authzed/api, <c>authzed/api/v1/core.proto</c>, <c>ObjectReference.object_id</c>).
/// None of the tuple separators <c>:</c>, <c>#</c> or <c>@</c> is in the set. This application's own ids keep the
/// stricter <c>EntityId</c> rule.
/// </summary>
public static partial class EngineObjectId
{
    /// <summary>The engine's limit is in bytes; every allowed character is ASCII, so it is also the limit in characters.</summary>
    public const int MaxLength = 1024;

    public static bool IsValid(string? id) => !string.IsNullOrEmpty(id) && id.Length <= MaxLength && IdRegex().IsMatch(id);

    // \z, not $: $ also matches before a trailing newline.
    [GeneratedRegex(@"^[a-zA-Z0-9/_|=+-]+\z")]
    private static partial Regex IdRegex();
}
