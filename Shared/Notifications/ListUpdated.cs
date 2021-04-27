namespace ThriveDevCenter.Shared.Notifications
{
    using Models;

    /// <summary>
    ///   Notification about a list being updated
    /// </summary>
    public abstract class ListUpdated<T> : SerializedNotification
        where T : class
    {
        public ListItemChangeType Type { get; init; } = ListItemChangeType.ItemUpdated;
        public T Item { get; init; }
    }

    // These separate class types are needed for JSON serialization to work

    public class LFSListUpdated : ListUpdated<LFSProjectInfo>
    {
    }

    public class UserListUpdated : ListUpdated<UserInfo>
    {
    }

    public class ProjectGitFileUpdated : ListUpdated<ProjectGitFileDTO>
    {
    }

    public class LfsObjectListUpdated : ListUpdated<LfsObjectDTO>
    {
    }

    public class PatronListUpdated : ListUpdated<PatronDTO>
    {
    }

    public class AccessKeyListUpdated : ListUpdated<AccessKeyDTO>
    {
    }

    public class LogEntriesUpdated : ListUpdated<LogEntryDTO>
    {
    }

    public class AdminActionsUpdated : ListUpdated<AdminActionDTO>
    {
    }

    public class LauncherLinkListUpdated : ListUpdated<LauncherLinkDTO>
    {
    }

    public class DevBuildListUpdated : ListUpdated<DevBuildDTO>
    {
    }
}
