using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ThriveDevCenter.Server.Controllers
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel.DataAnnotations;
    using System.Linq;
    using System.Threading.Tasks;
    using Hubs;
    using Microsoft.AspNetCore.SignalR;
    using Authorization;
    using BlazorPagination;
    using Filters;
    using Microsoft.EntityFrameworkCore;
    using Models;
    using Shared;
    using Shared.Forms;
    using Shared.Models;
    using Shared.Notifications;
    using Utilities;

    [ApiController]
    [Route("api/v1/[controller]")]
    public class UserManagementController : Controller
    {
        private readonly ILogger<UserManagementController> logger;
        private readonly NotificationsEnabledDb database;
        private readonly IHubContext<NotificationsHub, INotifications> notifications;

        public UserManagementController(ILogger<UserManagementController> logger,
            NotificationsEnabledDb database, IHubContext<NotificationsHub, INotifications> notifications)
        {
            this.logger = logger;
            this.database = database;
            this.notifications = notifications;
        }

        [AuthorizeRoleFilter(RequiredAccess = UserAccessLevel.Admin)]
        [HttpGet]
        public async Task<PagedResult<UserInfo>> Get([Required] string sortColumn,
            [Required] SortDirection sortDirection, [Required] [Range(1, int.MaxValue)] int page,
            [Required] [Range(1, 100)] int pageSize)
        {
            IQueryable<User> query;

            try
            {
                query = database.Users.OrderBy(sortColumn, sortDirection, new[] { "UserName" });
            }
            catch (ArgumentException e)
            {
                logger.LogWarning("Invalid requested order: {@E}", e);
                throw new HttpResponseException() { Value = "Invalid data selection or sort" };
            }

            var objects = await query.ToPagedResultAsync(page, pageSize);

            // TODO: create a separate UserInfo type to use for the list here
            return objects.ConvertResult(i => i.GetInfo(RecordAccessLevel.Admin));
        }

        [AuthorizeRoleFilter(RequiredAccess = UserAccessLevel.User)]
        [HttpGet("{id:long}")]
        public async Task<ActionResult<UserInfo>> GetUser([Required] long id)
        {
            bool admin =
                HttpContext.HasAuthenticatedUserWithAccess(UserAccessLevel.Admin, AuthenticationScopeRestriction.None);

            var user = await database.Users.FindAsync(id);

            if (user == null)
                return NotFound();

            // Has to be an admin or looking at their own data
            if (!admin && HttpContext.AuthenticatedUser()!.Id != user.Id)
                return NotFound();

            return user.GetInfo(admin ? RecordAccessLevel.Admin : RecordAccessLevel.Private);
        }

        // TODO: should this be allowed for non-logged in users so that the uploader names in public folder items
        // could be shown?
        [AuthorizeRoleFilter(RequiredAccess = UserAccessLevel.User)]
        [HttpPost("usernames")]
        public async Task<ActionResult<Dictionary<long, string>>> GetUsernames([Required] [FromBody] List<long> ids)
        {
            var users = await database.Users.Where(u => ids.Contains(u.Id))
                .Select(u => new Tuple<long, string>(u.Id, u.UserName)).ToListAsync();

            Dictionary<long, string> result = new();

            foreach (var (id, username) in users)
            {
                result.Add(id, username);
            }

            // Add missing text for users we didn't find
            foreach (var requestedId in ids)
            {
                if (!result.ContainsKey(requestedId))
                {
                    result.Add(requestedId, $"unknown user ({requestedId})");
                }
            }

            return result;
        }

        [AuthorizeRoleFilter(RequiredAccess = UserAccessLevel.Admin)]
        [HttpPut("{id:long}/association")]
        public async Task<IActionResult> UpdateUserAssociationStatus([Required] long id,
            [Required] [FromBody] AssociationStatusUpdateForm request)
        {
            if (request.BoardMember && !request.AssociationMember)
                return BadRequest("Can't be board member if not an association member");

            if (request.BoardMember && !request.HasBeenBoardMember)
                return BadRequest("If currently a board member must have been a board member");

            var performingUser = HttpContext.AuthenticatedUser()!;

            var user = await database.Users.FindAsync(id);

            if (user == null)
                return NotFound();

            if (user.AssociationMember == request.AssociationMember && user.BoardMember == request.BoardMember &&
                user.HasBeenBoardMember == request.HasBeenBoardMember)
            {
                return Ok("No changes required");
            }

            user.AssociationMember = request.AssociationMember;
            user.BoardMember = request.BoardMember;
            user.HasBeenBoardMember = request.HasBeenBoardMember;

            await database.AdminActions.AddAsync(new AdminAction()
            {
                Message = $"User association status changed: {user.AssociationMember}, board: {user.BoardMember}, " +
                    $"has been board member: {user.HasBeenBoardMember}",
                TargetUserId = user.Id,
                PerformedById = performingUser.Id,
            });

            await database.SaveChangesAsync();

            logger.LogInformation("User {Email1} association status is now: {AssociationMember}, " +
                "board member: {BoardMember}, has been board member: {HasBeenBoardMember}, performed by: {Email2}",
                user.Email, user.AssociationMember, user.BoardMember, user.HasBeenBoardMember, performingUser.Email);

            return Ok();
        }

        [AuthorizeRoleFilter(RequiredAccess = UserAccessLevel.User)]
        [HttpGet("{id:long}/sessions")]
        public async Task<ActionResult<PagedResult<SessionDTO>>> GetUserSessions([Required] long id,
            [Required] string sortColumn,
            [Required] SortDirection sortDirection, [Required] [Range(1, int.MaxValue)] int page,
            [Required] [Range(1, 50)] int pageSize)
        {
            bool admin =
                HttpContext.HasAuthenticatedUserWithAccess(UserAccessLevel.Admin, AuthenticationScopeRestriction.None);

            var user = await database.Users.FindAsync(id);

            if (user == null)
                return NotFound();

            // Has to be an admin or looking at their own data
            if (!admin && HttpContext.AuthenticatedUser()!.Id != user.Id)
                return NotFound();

            // If requestSession is null this request came with an API key and not a browser session
            var requestSession = HttpContext.AuthenticatedUserSession();

            IQueryable<Session> query;

            try
            {
                query = database.Sessions.Where(s => s.UserId == id).OrderBy(sortColumn, sortDirection);
            }
            catch (ArgumentException e)
            {
                logger.LogWarning("Invalid requested order: {@E}", e);
                throw new HttpResponseException() { Value = "Invalid data selection or sort" };
            }

            var objects = await query.ToPagedResultAsync(page, pageSize);

            return objects.ConvertResult(i => i.GetDTO(i.Id == requestSession?.Id));
        }

        [AuthorizeRoleFilter(RequiredAccess = UserAccessLevel.User)]
        [HttpDelete("{id:long}/sessions")]
        public async Task<ActionResult<PagedResult<SessionDTO>>> DeleteAllUserSessions([Required] long id)
        {
            bool admin =
                HttpContext.HasAuthenticatedUserWithAccess(UserAccessLevel.Admin, AuthenticationScopeRestriction.None);

            var user = await database.Users.FindAsync(id);
            var actingUser = HttpContext.AuthenticatedUser()!;

            if (user == null)
                return NotFound();

            // Has to be an admin or performing an action on their own data
            if (!admin && actingUser.Id != user.Id)
                return NotFound();

            var sessions = await database.Sessions.Where(s => s.UserId == id).ToListAsync();

            if (sessions.Count < 1)
                return Ok();

            if (admin && actingUser.Id != user.Id)
            {
                await database.AdminActions.AddAsync(new AdminAction()
                {
                    Message = "Forced logout",
                    PerformedById = actingUser.Id,
                    TargetUserId = user.Id,
                });
            }
            else
            {
                logger.LogInformation("User ({Email}) deleted their sessions from {RemoteIpAddress}", user.Email,
                    HttpContext.Connection.RemoteIpAddress);
            }

            database.Sessions.RemoveRange(sessions);
            await database.SaveChangesAsync();

            await InvalidateSessions(sessions.Select(s => s.Id));
            return Ok();
        }

        [AuthorizeRoleFilter(RequiredAccess = UserAccessLevel.User)]
        [HttpDelete("{id:long}/otherSessions")]
        public async Task<ActionResult<PagedResult<SessionDTO>>> DeleteOtherUserSessions([Required] long id)
        {
            var user = await database.Users.FindAsync(id);

            if (user == null)
                return NotFound();

            // It makes sense only for the current user to perform this action
            if (HttpContext.AuthenticatedUser()!.Id != user.Id)
                return NotFound();

            var requestSession = HttpContext.AuthenticatedUserSession();

            if (requestSession == null)
                return Forbid("This action can only be performed with an active session");

            var sessions = await database.Sessions.Where(s => s.UserId == id).ToListAsync();

            if (sessions.Count < 1)
                return Ok();

            sessions = sessions.Where(s => s.Id != requestSession.Id).ToList();

            database.Sessions.RemoveRange(sessions);
            await database.SaveChangesAsync();

            logger.LogInformation("User ({Email}) deleted their other sessions than {Id} from {RemoteIpAddress}",
                user.Email, requestSession.Id, HttpContext.Connection.RemoteIpAddress);

            await InvalidateSessions(sessions.Select(s => s.Id));
            return Ok();
        }

        [AuthorizeRoleFilter(RequiredAccess = UserAccessLevel.User)]
        [HttpDelete("{id:long}/sessions/{sessionId:long}")]
        public async Task<ActionResult<PagedResult<SessionDTO>>> DeleteSpecificUserSession([Required] long id,
            [Required] long sessionId)
        {
            bool admin =
                HttpContext.HasAuthenticatedUserWithAccess(UserAccessLevel.Admin, AuthenticationScopeRestriction.None);

            var user = await database.Users.FindAsync(id);
            var actingUser = HttpContext.AuthenticatedUser()!;

            if (user == null)
                return NotFound();

            // Has to be an admin or performing an action on their own data
            if (!admin && actingUser.Id != user.Id)
                return NotFound();

            var sessions = await database.Sessions.Where(s => s.UserId == id).ToListAsync();

            Session? session = null;

            // For safety we never give out the real ids of the session, so we need to here find the actual
            // target session
            foreach (var potentialSession in sessions)
            {
                if (potentialSession.GetDoubleHashedId() == sessionId)
                {
                    session = potentialSession;
                    break;
                }
            }

            if (session == null)
                return NotFound();

            if (admin && actingUser.Id != user.Id)
            {
                await database.AdminActions.AddAsync(new AdminAction()
                {
                    Message = $"Force ended session {session.Id}",
                    PerformedById = actingUser.Id,
                    TargetUserId = user.Id,
                });
            }
            else
            {
                logger.LogInformation("User ({Email}) deleted their session {Id} from {RemoteIpAddress}",
                    user.Email, session.Id, HttpContext.Connection.RemoteIpAddress);
            }

            database.Sessions.Remove(session);
            await database.SaveChangesAsync();

            await InvalidateSessions(new[] { session.Id });
            return Ok();
        }

        [NonAction]
        private async Task InvalidateSessions(IEnumerable<Guid> sessions)
        {
            // This per-user variant didn't end up being needed currently (this probably works, but is not tested)
            // await notifications.Clients.User(userId.ToString()).ReceiveSessionInvalidation();

            await notifications.Clients.Groups(sessions.Select(s => NotificationGroups.SessionImportantMessage + s))
                .ReceiveSessionInvalidation();

            // TODO: force close signalr connections for the user https://github.com/dotnet/aspnetcore/issues/5333
        }
    }
}
