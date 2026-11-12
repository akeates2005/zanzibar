namespace Poc.Application.Authorization.Schema;

public static class PlatformSchema
{
    public const string Type = "platform";

    /// <summary>The single platform object.</summary>
    public const string MainId = "main";

    public static ResourceRef Main => new(Type, MainId);

    public static class Relations
    {
        public const string Administrator = "administrator";
        public const string Support = "support";
    }

    public static class Permissions
    {
        public const string Admin = "admin";
        public const string SupportView = "support_view";
        public const string View = "view";
        public const string ManageAccess = "manage_access";
        public const string CreateCustomer = "create_customer";
        public const string CreateGroup = "create_group";
    }
}
