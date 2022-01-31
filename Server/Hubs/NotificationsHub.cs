namespace ThriveDevCenter.Server.Hubs
{
    using System;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Authorization;
    using Microsoft.AspNetCore.SignalR;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Primitives;
    using Models;
    using Services;
    using Shared;
    using Shared.Models;
    using Shared.Models.Enums;
    using Shared.Notifications;
    using Utilities;

    public class NotificationsHub : Hub<INotifications>
    {
        private readonly ILogger<NotificationsHub> logger;
        private readonly ITokenVerifier csrfVerifier;
        private readonly ApplicationDbContext database;

        public NotificationsHub(ILogger<NotificationsHub> logger, ITokenVerifier csrfVerifier,
            ApplicationDbContext database)
        {
            this.logger = logger;
            this.csrfVerifier = csrfVerifier;
            this.database = database;
        }

        public override async Task OnConnectedAsync()
        {
            var http = Context.GetHttpContext();
            User? connectedUser = null;
            Session? session = null;

            if (http != null)
            {
                var queryParams = http.Request.Query;

                if (!queryParams.TryGetValue("minorVersion", out StringValues minorStr) ||
                    !queryParams.TryGetValue("majorVersion", out StringValues majorStr))
                {
                    throw new HubException("invalid connection parameters");
                }

                if (minorStr.Count < 1 || majorStr.Count < 1)
                    throw new HubException("invalid connection parameters");

                string csrf;

                if (!queryParams.TryGetValue("access_token", out StringValues accessToken))
                {
                    // In release mode (at least I saw this happen once) the access token is in a header
                    if (http.Request.Headers.TryGetValue("Authorization", out StringValues header) &&
                        header.Count > 0 && header[0].StartsWith("Bearer "))
                    {
                        // In format "Bearer TOKEN"
                        csrf = header[0].Split(' ').Last();
                    }
                    else
                    {
                        throw new HubException("invalid connection parameters");
                    }
                }
                else
                {
                    if (accessToken.Count < 1)
                        throw new HubException("invalid connection parameters");

                    csrf = accessToken[0];
                }

                try
                {
                    session = await http.Request.Cookies.GetSession(database);
                    connectedUser = await
                        UserFromCookiesHelper.GetUserFromSession(session, database, true,
                            http.Connection.RemoteIpAddress);
                }
                catch (ArgumentException)
                {
                    throw new HubException("invalid session cookie");
                }

                if (!csrfVerifier.IsValidCSRFToken(csrf, connectedUser))
                    throw new HubException("invalid CSRF token");

                Context.Items["Session"] = session;

                bool invalidVersion = false;

                try
                {
                    var major = Convert.ToInt32(majorStr[0]);
                    var minor = Convert.ToInt32(minorStr[0]);

                    if (major != AppInfo.Major || minor != AppInfo.Minor)
                        invalidVersion = true;
                }
                catch (Exception)
                {
                    throw new HubException("invalid connection parameters");
                }

                if (invalidVersion)
                    await Clients.Caller.ReceiveVersionMismatch();
            }

            if (connectedUser != null && session == null)
                throw new Exception("Logic error! user is not null but session is null");

            Context.Items["User"] = connectedUser;

            await base.OnConnectedAsync();

            if (connectedUser == null)
            {
                await Clients.Caller.ReceiveOwnUserInfo(null);
            }
            else
            {
                if (session == null)
                    throw new Exception("logic error, session is null when connected user is not null");

                // All sessions listen to notifications about them
                await Groups.AddToGroupAsync(Context.ConnectionId,
                    NotificationGroups.SessionImportantMessage + session.Id);

                await Clients.Caller.ReceiveOwnUserInfo(connectedUser.GetInfo(
                    connectedUser.HasAccessLevel(UserAccessLevel.Admin) ?
                        RecordAccessLevel.Admin :
                        RecordAccessLevel.Private));

                // Could send some user specific notices here
                // await Clients.Caller.ReceiveSiteNotice(SiteNoticeType.Primary, "hey you connected");
            }
        }

        public async Task JoinGroup(string groupName)
        {
            var user = Context.Items["User"] as User;
            var session = Context.Items["Session"] as Session;

            // Special group joining has its own rules so it is done first
            if (await HandleSpecialGroupJoin(groupName, user, session))
                return;

            if (!await IsUserAllowedInGroup(groupName, user))
            {
                logger.LogWarning("Client failed to join group: {GroupName}", groupName);
                throw new HubException("You don't have access to the specified group");
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        }

        public async Task LeaveGroup(string groupName)
        {
            if (await HandleSpecialGroupLeave(groupName))
                return;

            // TODO: does this need also group checking?
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        }

        public Task WhoAmI()
        {
            // TODO: reload from Db at some interval
            var user = Context.Items["User"] as User;

            var accessLevel = RequireAccessLevel(UserAccessLevel.Admin, user) ?
                RecordAccessLevel.Admin :
                RecordAccessLevel.Private;

            return Clients.Caller.ReceiveOwnUserInfo(
                user?.GetInfo(accessLevel));
        }

        private async Task<bool> HandleSpecialGroupJoin(string groupName, User? user, Session? session)
        {
            _ = user;

            // Special joins for only server-known groups
            switch (groupName)
            {
                case NotificationGroups.InProgressCLASignatureUpdated:
                {
                    if (session != null)
                    {
                        await Groups.AddToGroupAsync(Context.ConnectionId,
                            NotificationGroups.InProgressCLASignatureUpdated + session.Id);
                        return true;
                    }

                    break;
                }
            }

            return false;
        }

        /// <summary>
        ///   Special handling for groups joined by HandleSpecialGroupJoin
        /// </summary>
        /// <param name="groupName">
        ///   The group name the client provided (if handled this isn't the real group name)
        /// </param>
        /// <returns>True if the leave is handled and no further actions are required</returns>
        private async Task<bool> HandleSpecialGroupLeave(string groupName)
        {
            switch (groupName)
            {
                case NotificationGroups.InProgressCLASignatureUpdated:
                {
                    if (Context.Items["Session"] is Session session)
                    {
                        await Groups.RemoveFromGroupAsync(Context.ConnectionId,
                            NotificationGroups.InProgressCLASignatureUpdated + session.Id);
                        return true;
                    }

                    break;
                }
            }

            return false;
        }

        private async Task<bool> IsUserAllowedInGroup(string groupName, User? user)
        {
            // First check explicitly named groups
            switch (groupName)
            {
                case NotificationGroups.UserListUpdated:
                case NotificationGroups.PatronListUpdated:
                case NotificationGroups.AccessKeyListUpdated:
                case NotificationGroups.ControlledServerListUpdated:
                case NotificationGroups.ExternalServerListUpdated:
                case NotificationGroups.CLAListUpdated:
                case NotificationGroups.GithubAutoCommentListUpdated:
                case NotificationGroups.SentBulkEmailListUpdated:
                    return RequireAccessLevel(UserAccessLevel.Admin, user);
                case NotificationGroups.PrivateLFSUpdated:
                case NotificationGroups.PrivateCIProjectUpdated:
                case NotificationGroups.CrashReportListUpdatedPrivate:
                case NotificationGroups.SymbolListUpdated:
                    return RequireAccessLevel(UserAccessLevel.Developer, user);
                case NotificationGroups.DevBuildsListUpdated:
                    return RequireAccessLevel(UserAccessLevel.User, user);
                case NotificationGroups.LFSListUpdated:
                case NotificationGroups.CIProjectListUpdated:
                case NotificationGroups.CrashReportListUpdatedPublic:
                    return RequireAccessLevel(UserAccessLevel.NotLoggedIn, user);
            }

            // Then check prefixes
            if (groupName.StartsWith(NotificationGroups.UserUpdatedPrefix) ||
                groupName.StartsWith(NotificationGroups.UserSessionsUpdatedPrefix))
            {
                if (!GetIDPartFromGroup(groupName, out long id))
                    return false;

                // Early return if the user is not an admin and not looking at themselves, this prevents user id
                // enumeration from this endpoint
                if (user?.Id != id && !RequireAccessLevel(UserAccessLevel.Admin, user))
                    return false;

                // Can't join non-existent user groups
                if (!GetTargetModelFromGroup(groupName, database.Users, out var item))
                    return false;

                // Admins can see all user info
                if (RequireAccessLevel(UserAccessLevel.Admin, user))
                    return true;

                // People can see their own info
                return item!.Id == user?.Id;
            }

            // TODO: refactor this with the same code that's after this
            if (groupName.StartsWith(NotificationGroups.LFSItemUpdatedPrefix))
            {
                if (!GetTargetModelFromGroup(groupName, database.LfsProjects, out var item))
                    return false;

                if (RequireAccessLevel(UserAccessLevel.Admin, user))
                    return true;

                // Only admins see deleted items
                if (item!.Deleted)
                    return false;

                // Everyone sees public projects
                if (item.Public)
                    return true;

                return RequireAccessLevel(UserAccessLevel.Developer, user);
            }

            if (groupName.StartsWith(NotificationGroups.CIProjectUpdatedPrefix) ||
                groupName.StartsWith(NotificationGroups.CIProjectBuildsUpdatedPrefix))
            {
                if (!GetTargetModelFromGroup(groupName, database.CiProjects, out var item))
                    return false;

                if (RequireAccessLevel(UserAccessLevel.Admin, user))
                    return true;

                // Only admins see deleted items
                // This doesn't really apply to deleted projects, but for code simplicity admin access is allowed to
                // builds list even when the project is deleted
                if (item!.Deleted)
                    return false;

                // Everyone sees public projects
                if (item.Public)
                    return true;

                return RequireAccessLevel(UserAccessLevel.Developer, user);
            }

            if (groupName.StartsWith(NotificationGroups.CIProjectsBuildUpdatedPrefix) ||
                groupName.StartsWith(NotificationGroups.CIProjectBuildJobsUpdatedPrefix))
            {
                if (!GetCompositeIDPartFromGroup(groupName, out var ids) || ids!.Length != 2)
                    return false;

                var item = await database.CiBuilds.Include(b => b.CiProject)
                    .FirstOrDefaultAsync(b => b.CiProjectId == ids[0] && b.CiBuildId == ids[1]);

                if (item == null)
                    return false;

                if (item.CiProject == null)
                    throw new NotLoadedModelNavigationException();

                // Everyone sees public projects' builds
                if (item.CiProject.Public)
                    return true;

                return RequireAccessLevel(UserAccessLevel.Developer, user);
            }

            if (groupName.StartsWith(NotificationGroups.CIProjectsBuildsJobUpdatedPrefix) ||
                groupName.StartsWith(NotificationGroups.CIProjectBuildJobSectionsUpdatedPrefix) ||
                groupName.StartsWith(NotificationGroups.CIProjectsBuildsJobRealtimeOutputPrefix))
            {
                if (!GetCompositeIDPartFromGroup(groupName, out var ids) || ids!.Length != 3)
                    return false;

                var item = await database.CiJobs.Include(j => j.Build!).ThenInclude(b => b.CiProject)
                    .FirstOrDefaultAsync(b => b.CiProjectId == ids[0] && b.CiBuildId == ids[1] && b.CiJobId == ids[2]);

                if (item == null)
                    return false;

                if (item.Build?.CiProject == null)
                    throw new NotLoadedModelNavigationException();

                // Everyone sees public projects' builds' jobs (and output sections)
                if (item.Build.CiProject.Public)
                    return true;

                return RequireAccessLevel(UserAccessLevel.Developer, user);
            }

            if (groupName.StartsWith(NotificationGroups.CIProjectSecretsUpdatedPrefix))
            {
                if (!GetTargetModelFromGroup(groupName, database.CiProjects, out _))
                    return false;

                // Only admins see secrets
                return RequireAccessLevel(UserAccessLevel.Admin, user);
            }

            if (groupName.StartsWith(NotificationGroups.UserLauncherLinksUpdatedPrefix))
            {
                if (!GetTargetModelFromGroup(groupName, database.Users, out var item))
                    return false;

                // Admin can view other people's launcher links
                if (RequireAccessLevel(UserAccessLevel.Admin, user))
                    return true;

                // Users can see their own links
                return item!.Id == user?.Id;
            }

            if (groupName.StartsWith(NotificationGroups.DevBuildUpdatedPrefix))
            {
                if (!GetTargetModelFromGroup(groupName, database.DevBuilds, out _))
                    return false;

                return RequireAccessLevel(UserAccessLevel.User, user);
            }

            if (groupName.StartsWith(NotificationGroups.StorageItemUpdatedPrefix))
            {
                if (!GetTargetModelFromGroup(groupName, database.StorageItems, out var item))
                    return false;

                return item!.IsReadableBy(user);
            }

            if (groupName.StartsWith(NotificationGroups.FolderContentsUpdatedPublicPrefix))
            {
                if (!GetTargetFolderFromGroup(groupName, database.StorageItems, out var item))
                    return false;

                return CheckFolderContentsAccess(user, UserAccessLevel.NotLoggedIn, item);
            }

            if (groupName.StartsWith(NotificationGroups.FolderContentsUpdatedUserPrefix))
            {
                if (!GetTargetFolderFromGroup(groupName, database.StorageItems, out var item))
                    return false;

                return CheckFolderContentsAccess(user, UserAccessLevel.User, item);
            }

            if (groupName.StartsWith(NotificationGroups.FolderContentsUpdatedDeveloperPrefix))
            {
                if (!GetTargetFolderFromGroup(groupName, database.StorageItems, out var item))
                    return false;

                return CheckFolderContentsAccess(user, UserAccessLevel.Developer, item);
            }

            if (groupName.StartsWith(NotificationGroups.MeetingUpdatedPrefix) ||
                groupName.StartsWith(NotificationGroups.MeetingPollListUpdatedPrefix))
            {
                if (!GetTargetModelFromGroup(groupName, database.Meetings, out var item))
                    return false;

                if (RequireAccessLevel(UserAccessLevel.Admin, user))
                    return true;

                if (user == null)
                    return item!.ReadAccess == AssociationResourceAccess.Public;

                return user.ComputeAssociationAccessLevel() >= item!.ReadAccess;
            }

            if (groupName.StartsWith(NotificationGroups.FolderContentsUpdatedOwnerPrefix))
            {
                // Anonymous users can't be the owners of any folders
                if (user == null)
                    return false;

                if (!GetTargetFolderFromGroup(groupName, database.StorageItems, out StorageItem? item))
                    return false;

                // Admins can act like the owner of any folder for listening to it
                if (RequireAccessLevel(UserAccessLevel.Admin, user))
                    return true;

                // Base folder can't be owned by anyone. Only admins can join this group (see above)
                // ReSharper disable once UseNullPropagation
                if (item == null)
                    return false;

                // Folders with no owner can't be listened to by normal users
                if (item.OwnerId == null)
                    return false;

                // Only owner can join this group
                return user.Id == item.OwnerId;
            }

            if (groupName.StartsWith(NotificationGroups.CLAUpdatedPrefix))
            {
                if (!GetTargetModelFromGroup(groupName, database.Clas, out var item))
                    return false;

                // Everyone sees active CLA data
                if (item!.Active)
                    return true;

                // Only admins see other CLA data
                return RequireAccessLevel(UserAccessLevel.Admin, user);
            }

            if (groupName.StartsWith(NotificationGroups.CrashReportUpdatedPrefix))
            {
                if (!GetTargetModelFromGroup(groupName, database.CrashReports, out var item))
                    return false;

                if (RequireAccessLevel(UserAccessLevel.Developer, user))
                    return true;

                return item!.Public;
            }

            // Only admins see this
            if (groupName.StartsWith(NotificationGroups.UserUpdatedPrefixAdminInfo))
                return RequireAccessLevel(UserAccessLevel.Admin, user);

            // Unknown groups are not allowed
            return false;
        }

        private static bool GetTargetModelFromGroup<T>(string groupName, DbSet<T> existingItems, out T? item)
            where T : class
        {
            if (!GetIDPartFromGroup(groupName, out long id))
            {
                item = null;
                return false;
            }

            // This lookup probably can timing attack leak the IDs of objects
            item = existingItems.Find(id);

            return item != null;
        }

        private static bool GetTargetModelFromGroupCompositeId<T>(string groupName, DbSet<T> existingItems, out T? item)
            where T : class
        {
            if (!GetCompositeIDPartFromGroup(groupName, out var ids))
            {
                item = null;
                return false;
            }

            // This lookup probably can timing attack leak the IDs of objects
            item = existingItems.Find(ids);

            return item != null;
        }

        private static bool GetTargetFolderFromGroup(string groupName, DbSet<StorageItem> existingItems,
            out StorageItem? item)
        {
            var idRaw = groupName.Split('_').Last();

            if (long.TryParse(idRaw, out long id))
            {
                // This lookup probably can timing attack leak the IDs of objects
                item = existingItems.Find(id);
                return item != null;
            }

            item = null;

            // Raw may be also "root" to listen for root folder items
            if (idRaw == "root")
            {
                return true;
            }

            return false;
        }

        private static bool CheckFolderContentsAccess(User? user, UserAccessLevel baseAccessLevel, StorageItem? folder)
        {
            if (!RequireAccessLevel(baseAccessLevel, user))
                return false;

            // Base folder is null, and it has public read by default
            if (folder == null)
                return true;

            return folder.IsReadableBy(user);
        }

        private static bool GetIDPartFromGroup(string groupName, out long id)
        {
            var idRaw = groupName.Split('_').Last();

            if (!long.TryParse(idRaw, out id))
            {
                return false;
            }

            return true;
        }

        private static bool GetCompositeIDPartFromGroup(string groupName, out long[]? id)
        {
            try
            {
                id = groupName.Split('_').Skip(1).Select(long.Parse).ToArray();
                return true;
            }
            catch (Exception)
            {
                id = null;
                return false;
            }
        }

        private static bool RequireAccessLevel(UserAccessLevel level, User? user)
        {
            // All site visitors have the not logged in access level
            if (level == UserAccessLevel.NotLoggedIn)
                return true;

            // All other access levels require a user
            if (user == null)
                return false;

            return user.HasAccessLevel(level);
        }
    }

    public interface INotifications
    {
        // These are general connection related things sent
        Task ReceiveSiteNotice(SiteNoticeType type, string message);
        Task ReceiveSessionInvalidation();
        Task ReceiveVersionMismatch();
        Task ReceiveOwnUserInfo(UserInfo? user);

        // Directly sending SerializedNotification doesn't work so we hack around that by manually serializing it
        // to a string before sending
        Task ReceiveNotificationJSON(string json);
    }

    public static class NotificationHelpers
    {
        private static readonly NotificationJsonConverter Converter = new NotificationJsonConverter();

        /// <summary>
        ///   Send all SerializedNotification derived classes through this extension method
        /// </summary>
        public static Task ReceiveNotification(this INotifications receiver, SerializedNotification notification)
        {
            // TODO: unify with the startup code
            var serialized =
                JsonSerializer.Serialize(notification, new JsonSerializerOptions() { Converters = { Converter } });

            return receiver.ReceiveNotificationJSON(serialized);
        }
    }
}
