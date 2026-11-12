namespace Poc.Application.Authorization.Schema;

public static class WalletSchema
{
    public const string Type = "wallet";

    public static class Relations
    {
        public const string Parent = "parent";
        public const string Administrator = "administrator";
        public const string Spender = "spender";
        public const string Viewer = "viewer";
    }

    public static class Permissions
    {
        public const string Admin = "admin";
        public const string SupportView = "support_view";
        public const string Spend = "spend";
        public const string View = "view";
        public const string Edit = "edit";
        public const string TopUp = "top_up";
        public const string Move = "move";
        public const string Delete = "delete";
        public const string ManageAccess = "manage_access";
        public const string CreateSubscription = "create_subscription";
    }
}
