namespace Poc.Application.Authorization.Schema;

public static class GroupSchema
{
    public const string Type = "group";

    public static class Relations
    {
        public const string Owner = "owner";
        public const string Member = "member";
    }

    public static class Permissions
    {
        public const string Manage = "manage";
        public const string View = "view";
    }
}
