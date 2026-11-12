using Poc.Application.Authorization;

namespace Poc.Application.Common;

/// <summary>
/// Who is performing a command. <see cref="System"/> exists only for the seeder and the default-grant policy: it skips
/// the engine permission check (there is no signed-in user) but still goes through every service rule and the audit.
/// The API never builds one; user ids come from validated tokens via <see cref="ForUser"/>.
/// </summary>
public readonly record struct Actor(string UserId, bool IsSystem = false)
{
    public const string SystemUserId = "system";

    public static Actor System => new(SystemUserId, true);

    public SubjectRef Subject => SubjectRef.User(UserId);

    public static Actor ForUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("A user id is required.", nameof(userId));
        // A token must never be able to claim the system identity by naming itself "system".
        if (userId == SystemUserId) throw new ArgumentException("'system' is reserved and cannot be a user id.", nameof(userId));
        return new Actor(userId);
    }
}
