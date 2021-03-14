namespace ThriveDevCenter.Shared.Notifications
{
    /// <summary>
    ///   Lists the names of notification groups (or in case of dynamically generated ones, the name prefix)
    /// </summary>
    public static class NotificationGroups
    {
        public const string LFSListUpdated = "LFS";
        public const string PrivateLFSUpdated = "LFS_Developer";
        public const string LFSItemUpdatedPrefix = "SLFS_";

        public const string UserListUpdated = "Users";
        public const string UserUpdatedPrefix = "SUser_";
    }
}
