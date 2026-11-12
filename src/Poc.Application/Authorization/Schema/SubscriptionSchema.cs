namespace Poc.Application.Authorization.Schema;

public static class SubscriptionSchema
{
    public const string Type = "subscription";

    public static class Relations
    {
        public const string Parent = "parent";
        public const string Administrator = "administrator";
        public const string Operator = "operator";
        public const string Viewer = "viewer";
    }

    public static class Permissions
    {
        public const string Admin = "admin";
        public const string SupportView = "support_view";
        public const string ChangeQuantity = "change_quantity";
        public const string View = "view";
        public const string Cancel = "cancel";
        public const string Delete = "delete";
        public const string ManageAccess = "manage_access";
    }
}
