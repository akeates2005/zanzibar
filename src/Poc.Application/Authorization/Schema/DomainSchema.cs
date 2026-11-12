namespace Poc.Application.Authorization.Schema;

public static class DomainSchema
{
    public const string Type = "domain";

    public static class Relations
    {
        public const string Parent = "parent";
        public const string Administrator = "administrator";
        public const string Viewer = "viewer";
    }

    public static class Permissions
    {
        public const string Admin = "admin";
        public const string SupportView = "support_view";
        public const string View = "view";
        public const string Edit = "edit";
        public const string Delete = "delete";
        public const string ManageAccess = "manage_access";
        public const string CreateWallet = "create_wallet";
    }
}
