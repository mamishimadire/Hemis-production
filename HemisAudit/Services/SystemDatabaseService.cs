using System.Security.Cryptography;
using Npgsql;
using Microsoft.AspNetCore.Identity;
using HemisAudit.Models;
using HemisAudit.ViewModels;

namespace HemisAudit.Services
{
    public interface ISystemDatabaseService
    {
        Task<string> EnsureUserMirrorAsync(ApplicationUser user, string role);
        Task EnsurePerformanceObjectsAsync();
        Task<int> GetClientCountAsync(ApplicationUser? user, string role, string scope = "all");
        Task<int> GetEngagementCountForFirmAsync(int firmId);
        Task<int> GetPendingApprovalCountAsync(ApplicationUser? user, string role);
        Task<int> GetAssignedClientCountAsync(ApplicationUser user, string role);
        Task<string?> GetSystemRoleAsync(ApplicationUser? user);
        Task<string?> GetEngagementRoleAsync(int clientId, ApplicationUser? user, string role);
        Task ToggleClientFavoriteAsync(int clientId, ApplicationUser user, string role);
        Task<int> GetValidationRunCountAsync(ApplicationUser? user, string role);
        Task<int> GetExceptionCountAsync(ApplicationUser? user, string role);
        Task NormalizeCompletedRunStatusesAsync();
        Task<List<ClientListViewModel>> GetClientsAsync(ApplicationUser? user, string role, bool approvedOnly = false, string? search = null, string scope = "all");
        Task<List<ValidationRunRow>> GetRecentRunsAsync(ApplicationUser? user, string role, int take = 10);
        Task<List<ValidationRunRow>> GetCurrentRunsAsync(ApplicationUser? user, string role);
        Task<bool> IsWorkspaceSavedAsync(int runId);
        Task<int> CreateClientAsync(CreateClientViewModel model, ApplicationUser creator, string role, int firmId);
        Task<ClientDetailViewModel?> GetClientDetailAsync(int clientId, ApplicationUser? user, string role);
        Task ApproveClientAsync(int clientId, ApplicationUser approver, string role);
        Task<bool> CanAccessClientModuleAsync(int clientId, ApplicationUser? user, string role);
        Task<bool> CanAccessClientResultsAsync(int clientId, ApplicationUser? user, string role);
        Task<ArchiveEligibilityViewModel> GetArchiveEligibilityAsync(int clientId);
        Task ArchiveClientAsync(int clientId, ApplicationUser archiver, string role);
        Task DeleteClientAsync(int clientId);
        Task AssignUserAsync(int clientId, ApplicationUser targetUser, string engagementRole, ApplicationUser assignedBy, string assignedByRole);
        Task RemoveAssignmentAsync(int clientUserId);
        Task DeleteUserMirrorAsync(ApplicationUser targetUser, ApplicationUser deletedBy, string deletedByRole);
        Task WriteAuditLogAsync(
            string action,
            string? details = null,
            string? userId = null,
            string? userName = null,
            string? entityType = null,
            int? entityId = null,
            string? oldValues = null,
            string? newValues = null,
            string? ipAddress = null);
        Task<List<AuditLogRowViewModel>> GetAuditLogsAsync(int take = 500);
        Task<int> GetUnreadMessageCountAsync(ApplicationUser? user, string role);
        Task<List<MessageSummaryViewModel>> GetInboxThreadsAsync(ApplicationUser? user, string role, int take = 20);
        Task<MessageThreadViewModel?> GetMessageThreadAsync(int threadId, ApplicationUser? user, string role);
        Task<List<MessageRecipientOptionViewModel>> GetMessageRecipientsAsync(ApplicationUser? user, string role, int? clientId = null);
        Task<int> CreateMessageThreadAsync(ApplicationUser sender, string senderRole, IEnumerable<string> recipientUserIds, string subject, string body, int? clientId = null, IEnumerable<MessageAttachmentInput>? attachments = null);
        Task<int> ReplyToThreadAsync(int threadId, ApplicationUser sender, string senderRole, string body, IEnumerable<MessageAttachmentInput>? attachments = null);
        Task UpdateThreadSubjectAsync(int threadId, ApplicationUser user, string role, string subject);
        Task DeleteThreadForUserAsync(int threadId, ApplicationUser user, string role);
        Task UpdateMessageAsync(int messageId, int threadId, ApplicationUser user, string role, string body);
        Task DeleteMessageAsync(int messageId, int threadId, ApplicationUser user, string role);
        Task MarkThreadReadAsync(int threadId, ApplicationUser user, string role);
        Task<HashSet<int>> GetEngagementScopeAsync(int clientId);
        Task SaveEngagementScopeAsync(int clientId, IEnumerable<int> ruleNumbers, ApplicationUser user);

        // ── Shared rule-engine persistence (replaces each Rule*Service's private, duplicated
        // SQL-Server-only OpenSystemConnectionAsync()/dbo.Users-keyed methods) ─────────────────
        Task<string?> GetRawEngagementRoleAsync(int clientId, string userId);
        Task EnsureClientNotArchivedAsync(int clientId);
        Task<int?> GetClientIdForRunAsync(int runId);
        Task<RuleValidationRunRow?> GetCurrentRuleRunAsync(int clientId, int ruleNumber);
        Task<RuleValidationRunRow?> GetRuleRunByIdAsync(int runId, int ruleNumber);
        // For rule engines that bundle several internal rule numbers behind one controller/service
        // (e.g. Rule10Service serving HEMIS Rules 1-10) and so don't know the exact rule number
        // for a runId ahead of time.
        Task<RuleValidationRunRow?> GetRuleRunByIdAsync(int runId);
        Task<string?> GetValidationRecordHashAsync(int runId);
        Task<string?> GetLatestValidationRunHashAsync(int clientId, int ruleNumber);
        Task MarkPreviousRuleRunsHistoricalAsync(int clientId, int ruleNumber);
        Task<int> ClearRuleSignoffsAndFlagForReviewAsync(int runId);
        Task<List<RunSignoffViewModel>> GetRuleRunSignoffsAsync(int runId, string? currentUserId);
        Task<bool> HasRuleSignoffRoleAsync(int runId, string signoffRole);
        Task UpdateRuleRunStatusFromSignoffsAsync(int runId);
        Task SaveRuleWorkspaceFieldsAsync(SaveRuleWorkspaceFieldsRequest request, string? editorDisplayName);
        Task MarkRuleWorkspaceEditStartedAsync(int runId, string? editorDisplayName);
        Task<int> SaveValidationRunAsync(SaveValidationRunRequest request, string? userEmail, string? userName);
        Task<bool> RuleWorkspaceReadyForSignoffAsync(int runId);

        // reviewerUserId / actorUserId are ApplicationUser.Id (text) — resolve via UserManager
        // before calling, exactly like every other tenant-scoped method in this service.
        Task AddOrUpdateRuleSignoffAsync(int runId, int clientId, string reviewerUserId, string signoffRole, string? comment);
        Task<RuleSignoffRemovalResult> RemoveRuleSignoffByReviewerAsync(int runId, string reviewerUserId);
        Task<RuleSignoffRemovalResult> RemoveRuleSignoffByRoleAsync(int runId, string signoffRole, string? actorDisplayName);
    }

    // Talks to the shared Supabase Postgres database that now hosts both ASP.NET Identity
    // (AspNetUsers, etc.) and the "system database" tables created by
    // Data/SystemDatabaseBootstrapper.cs (engagements, messaging, audit log, sign-offs).
    // There is no more int-keyed "Users" mirror table: every user reference in these tables
    // is a text foreign key straight to AspNetUsers("Id").
    public class SystemDatabaseService : ISystemDatabaseService
    {
        private static readonly SemaphoreSlim NormalizeStatusesLock = new(1, 1);
        private static readonly SemaphoreSlim PerformanceObjectsLock = new(1, 1);
        private static readonly TimeSpan NormalizeStatusesInterval = TimeSpan.FromSeconds(30);
        private static DateTimeOffset _lastNormalizedStatusesAt = DateTimeOffset.MinValue;
        private static bool _performanceObjectsReady;
        private readonly IConfiguration _configuration;
        private readonly UserManager<ApplicationUser> _userManager;

        public SystemDatabaseService(IConfiguration configuration, UserManager<ApplicationUser> userManager)
        {
            _configuration = configuration;
            _userManager = userManager;
        }

        // No more mirror row to create/update — the Identity user already exists by the time
        // this is called. Kept for interface/call-site compatibility.
        public Task<string> EnsureUserMirrorAsync(ApplicationUser user, string role)
        {
            return Task.FromResult(user.Id);
        }

        public async Task EnsurePerformanceObjectsAsync()
        {
            if (_performanceObjectsReady)
                return;

            await PerformanceObjectsLock.WaitAsync();
            try
            {
                if (_performanceObjectsReady)
                    return;

                await using var connection = await OpenConnectionAsync();
                await using var command = connection.CreateConfiguredCommand();
                command.CommandTimeout = 60;
                command.CommandText = @"
CREATE INDEX IF NOT EXISTS ""IX_ValidationRuns_ClientRuleTimestamp""
    ON ""ValidationRuns"" (""ClientID"", ""RuleNumber"", ""RunTimestamp"" DESC, ""RunID"" DESC)
    INCLUDE (""Status"", ""IsCurrent"", ""UserID"", ""TotalRecords"", ""PassCount"", ""FailCount"", ""ExceptionRate"", ""RunByUserName"", ""LastEditedByUserName"", ""LastEditedAt"", ""RecordHash"");

CREATE INDEX IF NOT EXISTS ""IX_ValidationRuns_ClientCurrentTimestamp""
    ON ""ValidationRuns"" (""ClientID"", ""IsCurrent"", ""RunTimestamp"" DESC, ""RunID"" DESC)
    INCLUDE (""RuleNumber"", ""Status"", ""UserID"", ""TotalRecords"", ""PassCount"", ""FailCount"", ""ExceptionRate"", ""RunByUserName"", ""LastEditedByUserName"", ""LastEditedAt"");

CREATE INDEX IF NOT EXISTS ""IX_ReviewSignoffs_RunRole""
    ON ""ReviewSignoffs"" (""RunID"", ""SignoffRole"")
    INCLUDE (""ReviewerID"", ""Comment"", ""SignedOffAt"");

CREATE INDEX IF NOT EXISTS ""IX_UserClientAssignments_ClientUser""
    ON ""UserClientAssignments"" (""ClientID"", ""UserID"")
    INCLUDE (""EngagementRole"");

CREATE INDEX IF NOT EXISTS ""IX_UserClientAssignments_UserClient""
    ON ""UserClientAssignments"" (""UserID"", ""ClientID"")
    INCLUDE (""EngagementRole"");

CREATE INDEX IF NOT EXISTS ""IX_ClientFavorites_UserClient""
    ON ""ClientFavorites"" (""UserID"", ""ClientID"");

CREATE INDEX IF NOT EXISTS ""IX_Clients_StatusCreated""
    ON ""Clients"" (""Status"", ""CreatedAt"" DESC, ""ClientID"" DESC)
    INCLUDE (""EngagementName"", ""MaconomyNumber"", ""Industry"", ""CreatedBy"", ""DirectorName"", ""ManagerName"");

CREATE INDEX IF NOT EXISTS ""IX_ValidationRuns_ClientTimestampDashboard""
    ON ""ValidationRuns"" (""ClientID"", ""RunTimestamp"" DESC, ""RunID"" DESC)
    INCLUDE (""RuleNumber"", ""RuleName"", ""Status"", ""TotalRecords"", ""PassCount"", ""FailCount"", ""ExceptionRate"", ""UserID"", ""RunByUserName"", ""LastEditedByUserName"", ""LastEditedAt"", ""IsCurrent"");

CREATE INDEX IF NOT EXISTS ""IX_ReviewSignoffs_RunSummary""
    ON ""ReviewSignoffs"" (""RunID"")
    INCLUDE (""SignoffRole"", ""ReviewerID"", ""SignedOffAt"", ""Comment"");

CREATE INDEX IF NOT EXISTS ""IX_Clients_StatusClient""
    ON ""Clients"" (""Status"", ""ClientID"")
    INCLUDE (""EngagementName"", ""MaconomyNumber"", ""CreatedAt"", ""CreatedBy"");";
                await command.ExecuteNonQueryAsync();
                _performanceObjectsReady = true;
            }
            finally
            {
                PerformanceObjectsLock.Release();
            }
        }

        public async Task<int> GetClientCountAsync(ApplicationUser? user, string role, string scope = "all")
        {
            var clients = await GetClientsCoreAsync(user, role, null, scope);
            return clients.Count;
        }

        public async Task<int> GetEngagementCountForFirmAsync(int firmId)
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = "SELECT COUNT(1) FROM \"Clients\" WHERE \"FirmID\" = @FirmID;";
            command.Parameters.AddWithValue("@FirmID", firmId);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public async Task<int> GetPendingApprovalCountAsync(ApplicationUser? user, string role)
        {
            var clients = await GetClientsCoreAsync(user, role);
            return clients.Count(c => string.Equals(c.Status, "Pending", StringComparison.OrdinalIgnoreCase));
        }

        public async Task<int> GetAssignedClientCountAsync(ApplicationUser user, string role)
        {
            var (userId, _) = await ResolveUserScopeAsync(user, role);
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = "SELECT COUNT(1) FROM \"UserClientAssignments\" WHERE \"UserID\" = @UserID;";
            command.Parameters.AddWithValue("@UserID", userId);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        // Ordered highest-to-lowest so a user holding multiple roles (e.g. the platform
        // owner, who is both "ServiceProvider" and their firm's "Admin") resolves to a
        // deterministic engagement-facing role instead of whatever AspNetUserRoles
        // happens to return first. ServiceProvider is a platform-level role, not an
        // engagement one, so it's intentionally lowest priority here.
        private static readonly string[] RolePriority = { "Admin", "Director", "Manager", "DataAnalyst", "Trainee", "ServiceProvider" };

        public async Task<string?> GetSystemRoleAsync(ApplicationUser? user)
        {
            if (user == null)
                return null;

            var roles = await _userManager.GetRolesAsync(user);
            foreach (var candidate in RolePriority)
            {
                if (roles.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    return candidate;
            }

            return roles.FirstOrDefault();
        }

        public async Task<string?> GetEngagementRoleAsync(int clientId, ApplicationUser? user, string role)
        {
            if (user == null)
                return null;

            await using var connection = await OpenConnectionAsync();

            // A user (including Admins) can only hold an engagement role on a client that
            // belongs to their own firm — prevents cross-tenant access via a guessed clientId.
            var clientFirmId = await GetClientFirmIdAsync(connection, clientId);
            if (clientFirmId == null || clientFirmId != user.FirmId)
                return null;

            if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
                return "Admin";

            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
SELECT ""EngagementRole""
FROM ""UserClientAssignments""
WHERE ""ClientID"" = @ClientID
  AND ""UserID"" = @UserID
LIMIT 1;";
            command.Parameters.AddWithValue("@ClientID", clientId);
            command.Parameters.AddWithValue("@UserID", user.Id);
            var value = await command.ExecuteScalarAsync();
            return value == null || value == DBNull.Value ? null : Convert.ToString(value);
        }

        private async Task<int?> GetClientFirmIdAsync(NpgsqlConnection connection, int clientId)
        {
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = "SELECT \"FirmID\" FROM \"Clients\" WHERE \"ClientID\" = @ClientID LIMIT 1;";
            command.Parameters.AddWithValue("@ClientID", clientId);
            var value = await command.ExecuteScalarAsync();
            return value == null || value == DBNull.Value ? null : Convert.ToInt32(value);
        }

        public async Task<int> GetValidationRunCountAsync(ApplicationUser? user, string role)
        {
            var (userId, isAdmin) = await ResolveUserScopeAsync(user, role);
            var isDataAnalyst = string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase);
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = isAdmin
                ? "SELECT COUNT(*) FROM \"ValidationRuns\";"
                : isDataAnalyst
                    ? @"SELECT COUNT(*)
                    FROM ""ValidationRuns"" vr
                    WHERE EXISTS (
                        SELECT 1
                        FROM ""UserClientAssignments"" a
                        WHERE a.""ClientID"" = vr.""ClientID"" AND a.""UserID"" = @UserID
                    ) OR vr.""UserID"" = @UserID;"
                    : @"SELECT COUNT(*)
                    FROM ""ValidationRuns"" vr
                    WHERE (
                        EXISTS (
                            SELECT 1
                            FROM ""UserClientAssignments"" a
                            WHERE a.""ClientID"" = vr.""ClientID"" AND a.""UserID"" = @UserID
                        ) OR vr.""UserID"" = @UserID
                    )
                      AND EXISTS (
                        SELECT 1
                        FROM ""ReviewSignoffs"" rs
                        WHERE rs.""RunID"" = vr.""RunID""
                          AND rs.""SignoffRole"" = 'DataAnalyst'
                    );";
            if (!isAdmin)
                command.Parameters.AddWithValue("@UserID", userId);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public async Task<int> GetExceptionCountAsync(ApplicationUser? user, string role)
        {
            var (userId, isAdmin) = await ResolveUserScopeAsync(user, role);
            var isDataAnalyst = string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase);
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = isAdmin
                ? "SELECT COALESCE(SUM(COALESCE(\"FailCount\",0)),0) FROM \"ValidationRuns\";"
                : isDataAnalyst
                    ? @"SELECT COALESCE(SUM(COALESCE(vr.""FailCount"",0)),0)
                    FROM ""ValidationRuns"" vr
                    WHERE EXISTS (
                        SELECT 1
                        FROM ""UserClientAssignments"" a
                        WHERE a.""ClientID"" = vr.""ClientID"" AND a.""UserID"" = @UserID
                    ) OR vr.""UserID"" = @UserID;"
                    : @"SELECT COALESCE(SUM(COALESCE(vr.""FailCount"",0)),0)
                    FROM ""ValidationRuns"" vr
                    WHERE (
                        EXISTS (
                            SELECT 1
                            FROM ""UserClientAssignments"" a
                            WHERE a.""ClientID"" = vr.""ClientID"" AND a.""UserID"" = @UserID
                        ) OR vr.""UserID"" = @UserID
                    )
                      AND EXISTS (
                        SELECT 1
                        FROM ""ReviewSignoffs"" rs
                        WHERE rs.""RunID"" = vr.""RunID""
                          AND rs.""SignoffRole"" = 'DataAnalyst'
                    );";
            if (!isAdmin)
                command.Parameters.AddWithValue("@UserID", userId);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public async Task NormalizeCompletedRunStatusesAsync()
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastNormalizedStatusesAt < NormalizeStatusesInterval)
                return;

            await NormalizeStatusesLock.WaitAsync();
            try
            {
                now = DateTimeOffset.UtcNow;
                if (now - _lastNormalizedStatusesAt < NormalizeStatusesInterval)
                    return;

            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandTimeout = 30;
            command.CommandText = @"
UPDATE ""ValidationRuns"" AS vr
SET ""Status"" = 'Reviewed and Completed'
WHERE vr.""Status"" <> 'Reviewed and Completed'
  AND EXISTS (
      SELECT 1 FROM ""ReviewSignoffs"" rs
      WHERE rs.""RunID"" = vr.""RunID"" AND rs.""SignoffRole"" = 'DataAnalyst'
  )
  AND EXISTS (
      SELECT 1 FROM ""ReviewSignoffs"" rs
      WHERE rs.""RunID"" = vr.""RunID"" AND rs.""SignoffRole"" = 'Manager'
  )
  AND EXISTS (
      SELECT 1 FROM ""ReviewSignoffs"" rs
      WHERE rs.""RunID"" = vr.""RunID"" AND rs.""SignoffRole"" = 'Director'
  );";
            await command.ExecuteNonQueryAsync();
                _lastNormalizedStatusesAt = DateTimeOffset.UtcNow;
            }
            finally
            {
                NormalizeStatusesLock.Release();
            }
        }

        public async Task<List<ClientListViewModel>> GetClientsAsync(ApplicationUser? user, string role, bool approvedOnly = false, string? search = null, string scope = "all")
        {
            var rows = await GetClientsCoreAsync(user, role, search, scope);
            if (approvedOnly)
                rows = rows.Where(r => r.IsActiveEngagement).ToList();
            return rows;
        }

        public async Task<List<ValidationRunRow>> GetRecentRunsAsync(ApplicationUser? user, string role, int take = 10)
        {
            var (userId, isAdmin) = await ResolveUserScopeAsync(user, role);
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = isAdmin
                ? $@"SELECT vr.""RunID"", vr.""ClientID"", COALESCE(c.""EngagementName"",'') AS ""ClientName"", vr.""RuleNumber"", vr.""RuleName"", vr.""Status"", vr.""TotalRecords"", vr.""PassCount"", vr.""FailCount"", vr.""ExceptionRate"", vr.""RunTimestamp"",
                            COALESCE(NULLIF(vr.""RunByUserName"",''), TRIM(COALESCE(u.""FirstName"",'') || ' ' || COALESCE(u.""LastName"",''))) AS ""RunByUserName"",
                            COALESCE(vr.""LastEditedByUserName"",'') AS ""LastEditedByUserName"",
                            vr.""LastEditedAt"",
                            vr.""IsCurrent"",
                            EXISTS (
                                SELECT 1 FROM ""ReviewSignoffs"" rs
                                WHERE rs.""RunID"" = vr.""RunID""
                                  AND rs.""SignoffRole"" = 'DataAnalyst'
                            ) AS ""HasDataAnalystSignoff"",
                            EXISTS (
                                SELECT 1 FROM ""ReviewSignoffs"" rs
                                WHERE rs.""RunID"" = vr.""RunID""
                                  AND rs.""SignoffRole"" = 'Manager'
                            ) AS ""HasManagerSignoff"",
                            EXISTS (
                                SELECT 1 FROM ""ReviewSignoffs"" rs
                                WHERE rs.""RunID"" = vr.""RunID""
                                  AND rs.""SignoffRole"" = 'Director'
                            ) AS ""HasDirectorSignoff""
                      FROM ""ValidationRuns"" vr
                      LEFT JOIN ""Clients"" c ON c.""ClientID"" = vr.""ClientID""
                      LEFT JOIN ""AspNetUsers"" u ON u.""Id"" = vr.""UserID""
                        ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC
                      LIMIT {take};"
                : $@"SELECT vr.""RunID"", vr.""ClientID"", COALESCE(c.""EngagementName"",'') AS ""ClientName"", vr.""RuleNumber"", vr.""RuleName"", vr.""Status"", vr.""TotalRecords"", vr.""PassCount"", vr.""FailCount"", vr.""ExceptionRate"", vr.""RunTimestamp"",
                            COALESCE(NULLIF(vr.""RunByUserName"",''), TRIM(COALESCE(u.""FirstName"",'') || ' ' || COALESCE(u.""LastName"",''))) AS ""RunByUserName"",
                            COALESCE(vr.""LastEditedByUserName"",'') AS ""LastEditedByUserName"",
                            vr.""LastEditedAt"",
                            vr.""IsCurrent"",
                            EXISTS (
                                SELECT 1 FROM ""ReviewSignoffs"" rs
                                WHERE rs.""RunID"" = vr.""RunID""
                                  AND rs.""SignoffRole"" = 'DataAnalyst'
                            ) AS ""HasDataAnalystSignoff"",
                            EXISTS (
                                SELECT 1 FROM ""ReviewSignoffs"" rs
                                WHERE rs.""RunID"" = vr.""RunID""
                                  AND rs.""SignoffRole"" = 'Manager'
                            ) AS ""HasManagerSignoff"",
                            EXISTS (
                                SELECT 1 FROM ""ReviewSignoffs"" rs
                                WHERE rs.""RunID"" = vr.""RunID""
                                  AND rs.""SignoffRole"" = 'Director'
                            ) AS ""HasDirectorSignoff""
                      FROM ""ValidationRuns"" vr
                      LEFT JOIN ""Clients"" c ON c.""ClientID"" = vr.""ClientID""
                      LEFT JOIN ""AspNetUsers"" u ON u.""Id"" = vr.""UserID""
                      WHERE vr.""WorkspaceSavedAt"" IS NOT NULL
                        AND (
                            EXISTS (
                                SELECT 1 FROM ""UserClientAssignments"" a
                                WHERE a.""ClientID"" = vr.""ClientID"" AND a.""UserID"" = @UserID
                            ) OR vr.""UserID"" = @UserID
                        )
                      ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC
                      LIMIT {take};";
            if (!isAdmin)
                command.Parameters.AddWithValue("@UserID", userId);

            await using var reader = await command.ExecuteReaderAsync();
            var list = new List<ValidationRunRow>();
            while (await reader.ReadAsync())
            {
                list.Add(new ValidationRunRow
                {
                    Id = reader.GetInt32(0),
                    ClientId = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    ClientName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    RuleNumber = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    RuleName = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    Status = reader.IsDBNull(5) ? "" : reader.GetString(5),
                    TotalValidated = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    PassCount = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                    FailCount = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                    ExceptionRate = reader.IsDBNull(9) ? 0 : reader.GetDecimal(9),
                    RunAt = reader.IsDBNull(10) ? DateTime.UtcNow : reader.GetDateTime(10),
                    RunByUserName = reader.IsDBNull(11) ? "" : reader.GetString(11),
                    LastEditedByUserName = reader.IsDBNull(12) ? null : reader.GetString(12),
                    LastEditedAt = reader.IsDBNull(13) ? null : reader.GetDateTime(13),
                    IsCurrent = !reader.IsDBNull(14) && reader.GetBoolean(14),
                    HasDataAnalystSignoff = !reader.IsDBNull(15) && reader.GetBoolean(15),
                    HasManagerSignoff = !reader.IsDBNull(16) && reader.GetBoolean(16),
                    HasDirectorSignoff = !reader.IsDBNull(17) && reader.GetBoolean(17)
                });
            }
            return list;
        }

        public async Task<List<ValidationRunRow>> GetCurrentRunsAsync(ApplicationUser? user, string role)
        {
            var (userId, isAdmin) = await ResolveUserScopeAsync(user, role);
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = isAdmin
                ? @"SELECT vr.""RunID"", vr.""ClientID"", COALESCE(c.""EngagementName"",'') AS ""ClientName"", vr.""RuleNumber"", vr.""RuleName"", vr.""Status"", vr.""TotalRecords"", vr.""PassCount"", vr.""FailCount"", vr.""ExceptionRate"", vr.""RunTimestamp"",
                            COALESCE(NULLIF(vr.""RunByUserName"",''), TRIM(COALESCE(u.""FirstName"",'') || ' ' || COALESCE(u.""LastName"",''))) AS ""RunByUserName"",
                            COALESCE(vr.""LastEditedByUserName"",'') AS ""LastEditedByUserName"",
                            vr.""LastEditedAt"",
                            vr.""IsCurrent"",
                            EXISTS (
                                SELECT 1 FROM ""ReviewSignoffs"" rs
                                WHERE rs.""RunID"" = vr.""RunID""
                                  AND rs.""SignoffRole"" = 'DataAnalyst'
                            ) AS ""HasDataAnalystSignoff"",
                            EXISTS (
                                SELECT 1 FROM ""ReviewSignoffs"" rs
                                WHERE rs.""RunID"" = vr.""RunID""
                                  AND rs.""SignoffRole"" = 'Manager'
                            ) AS ""HasManagerSignoff"",
                            EXISTS (
                                SELECT 1 FROM ""ReviewSignoffs"" rs
                                WHERE rs.""RunID"" = vr.""RunID""
                                  AND rs.""SignoffRole"" = 'Director'
                            ) AS ""HasDirectorSignoff""
                      FROM ""ValidationRuns"" vr
                      LEFT JOIN ""Clients"" c ON c.""ClientID"" = vr.""ClientID""
                      LEFT JOIN ""AspNetUsers"" u ON u.""Id"" = vr.""UserID""
                      WHERE vr.""IsCurrent""
                        ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC;"
                : @"SELECT vr.""RunID"", vr.""ClientID"", COALESCE(c.""EngagementName"",'') AS ""ClientName"", vr.""RuleNumber"", vr.""RuleName"", vr.""Status"", vr.""TotalRecords"", vr.""PassCount"", vr.""FailCount"", vr.""ExceptionRate"", vr.""RunTimestamp"",
                            COALESCE(NULLIF(vr.""RunByUserName"",''), TRIM(COALESCE(u.""FirstName"",'') || ' ' || COALESCE(u.""LastName"",''))) AS ""RunByUserName"",
                            COALESCE(vr.""LastEditedByUserName"",'') AS ""LastEditedByUserName"",
                            vr.""LastEditedAt"",
                            vr.""IsCurrent"",
                            EXISTS (
                                SELECT 1 FROM ""ReviewSignoffs"" rs
                                WHERE rs.""RunID"" = vr.""RunID""
                                  AND rs.""SignoffRole"" = 'DataAnalyst'
                            ) AS ""HasDataAnalystSignoff"",
                            EXISTS (
                                SELECT 1 FROM ""ReviewSignoffs"" rs
                                WHERE rs.""RunID"" = vr.""RunID""
                                  AND rs.""SignoffRole"" = 'Manager'
                            ) AS ""HasManagerSignoff"",
                            EXISTS (
                                SELECT 1 FROM ""ReviewSignoffs"" rs
                                WHERE rs.""RunID"" = vr.""RunID""
                                  AND rs.""SignoffRole"" = 'Director'
                            ) AS ""HasDirectorSignoff""
                      FROM ""ValidationRuns"" vr
                      LEFT JOIN ""Clients"" c ON c.""ClientID"" = vr.""ClientID""
                      LEFT JOIN ""AspNetUsers"" u ON u.""Id"" = vr.""UserID""
                      WHERE vr.""IsCurrent""
                        AND (
                            EXISTS (
                                SELECT 1 FROM ""UserClientAssignments"" a
                                WHERE a.""ClientID"" = vr.""ClientID"" AND a.""UserID"" = @UserID
                            ) OR vr.""UserID"" = @UserID
                        )
                      ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC;";
            if (!isAdmin)
                command.Parameters.AddWithValue("@UserID", userId);

            await using var reader = await command.ExecuteReaderAsync();
            var list = new List<ValidationRunRow>();
            while (await reader.ReadAsync())
            {
                list.Add(new ValidationRunRow
                {
                    Id = reader.GetInt32(0),
                    ClientId = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    ClientName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    RuleNumber = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    RuleName = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    Status = reader.IsDBNull(5) ? "" : reader.GetString(5),
                    TotalValidated = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    PassCount = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                    FailCount = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                    ExceptionRate = reader.IsDBNull(9) ? 0 : reader.GetDecimal(9),
                    RunAt = reader.IsDBNull(10) ? DateTime.UtcNow : reader.GetDateTime(10),
                    RunByUserName = reader.IsDBNull(11) ? "" : reader.GetString(11),
                    LastEditedByUserName = reader.IsDBNull(12) ? null : reader.GetString(12),
                    LastEditedAt = reader.IsDBNull(13) ? null : reader.GetDateTime(13),
                    IsCurrent = !reader.IsDBNull(14) && reader.GetBoolean(14),
                    HasDataAnalystSignoff = !reader.IsDBNull(15) && reader.GetBoolean(15),
                    HasManagerSignoff = !reader.IsDBNull(16) && reader.GetBoolean(16),
                    HasDirectorSignoff = !reader.IsDBNull(17) && reader.GetBoolean(17)
                });
            }

            return list;
        }

        public async Task<bool> IsWorkspaceSavedAsync(int runId)
        {
            if (runId <= 0)
                return false;

            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
SELECT EXISTS (
    SELECT 1
    FROM ""ValidationRuns""
    WHERE ""RunID"" = @RunID
      AND ""WorkspaceSavedAt"" IS NOT NULL
);";
            command.Parameters.AddWithValue("@RunID", runId);
            return Convert.ToBoolean(await command.ExecuteScalarAsync());
        }

        public async Task<int> CreateClientAsync(CreateClientViewModel model, ApplicationUser creator, string role, int firmId)
        {
            await using var connection = await OpenConnectionAsync();
            var creatorId = await EnsureUserMirrorAsync(creator, role);
            var autoApprove = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);

            await using var check = connection.CreateConfiguredCommand();
            check.CommandText = "SELECT COUNT(1) FROM \"Clients\" WHERE \"MaconomyNumber\" = @MaconomyNumber;";
            check.Parameters.AddWithValue("@MaconomyNumber", model.MaconomyNumber);
            if (Convert.ToInt32(await check.ExecuteScalarAsync()) > 0)
                throw new InvalidOperationException("A client with this Maconomy number already exists.");

            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
INSERT INTO ""Clients""
(""FirmID"", ""EngagementName"", ""MaconomyNumber"", ""Industry"", ""DirectorName"", ""DirectorEmail"", ""DirectorEmpCode"", ""ManagerName"", ""ManagerEmail"", ""ManagerEmpCode"", ""Status"", ""CreatedBy"", ""ApprovedBy"", ""ApprovedAt"", ""CreatedAt"")
VALUES
(@FirmID, @EngagementName, @MaconomyNumber, @Industry, @DirectorName, @DirectorEmail, @DirectorEmpCode, @ManagerName, @ManagerEmail, @ManagerEmpCode, @Status, @CreatedBy, @ApprovedBy, @ApprovedAt, now())
RETURNING ""ClientID"";";
            command.Parameters.AddWithValue("@FirmID", firmId);
            command.Parameters.AddWithValue("@EngagementName", model.EngagementName);
            command.Parameters.AddWithValue("@MaconomyNumber", model.MaconomyNumber);
            command.Parameters.AddWithValue("@Industry", model.Industry);
            command.Parameters.AddWithValue("@DirectorName", model.DirectorName);
            command.Parameters.AddWithValue("@DirectorEmail", model.DirectorEmail);
            command.Parameters.AddWithValue("@DirectorEmpCode", model.DirectorEmpCode);
            command.Parameters.AddWithValue("@ManagerName", model.ManagerName);
            command.Parameters.AddWithValue("@ManagerEmail", model.ManagerEmail);
            command.Parameters.AddWithValue("@ManagerEmpCode", model.ManagerEmpCode);
            command.Parameters.AddWithValue("@Status", autoApprove ? "Approved" : "Pending");
            command.Parameters.AddWithValue("@CreatedBy", creatorId);
            command.Parameters.AddWithValue("@ApprovedBy", autoApprove ? creatorId : (object)DBNull.Value);
            command.Parameters.AddWithValue("@ApprovedAt", autoApprove ? DateTime.UtcNow : (object)DBNull.Value);
            var created = await command.ExecuteScalarAsync();
            var clientId = Convert.ToInt32(created);

            if (string.Equals(role, "Director", StringComparison.OrdinalIgnoreCase))
            {
                await using var assignmentExists = connection.CreateConfiguredCommand();
                assignmentExists.CommandText = @"
SELECT COUNT(1)
FROM ""UserClientAssignments""
WHERE ""UserID"" = @UserID
  AND ""ClientID"" = @ClientID;";
                assignmentExists.Parameters.AddWithValue("@UserID", creatorId);
                assignmentExists.Parameters.AddWithValue("@ClientID", clientId);
                var hasAssignment = Convert.ToInt32(await assignmentExists.ExecuteScalarAsync()) > 0;

                await using var assignmentCommand = connection.CreateConfiguredCommand();
                assignmentCommand.CommandText = hasAssignment
                    ? @"
UPDATE ""UserClientAssignments""
SET ""EngagementRole"" = @EngagementRole,
    ""AssignedBy"" = @AssignedBy,
    ""AssignedAt"" = now()
WHERE ""UserID"" = @UserID
  AND ""ClientID"" = @ClientID;"
                    : @"
INSERT INTO ""UserClientAssignments"" (""UserID"", ""ClientID"", ""EngagementRole"", ""AssignedBy"", ""AssignedAt"")
VALUES (@UserID, @ClientID, @EngagementRole, @AssignedBy, now());";
                assignmentCommand.Parameters.AddWithValue("@UserID", creatorId);
                assignmentCommand.Parameters.AddWithValue("@ClientID", clientId);
                assignmentCommand.Parameters.AddWithValue("@EngagementRole", "Director");
                assignmentCommand.Parameters.AddWithValue("@AssignedBy", creatorId);
                await assignmentCommand.ExecuteNonQueryAsync();
            }

            return clientId;
        }

        public async Task<ClientDetailViewModel?> GetClientDetailAsync(int clientId, ApplicationUser? user, string role)
        {
            if (clientId <= 0)
                return null;

            var access = await GetClientResultsAccessAsync(clientId, user, role);
            if (!access.CanAccess)
                return null;

            await using var connection = await OpenConnectionAsync();
            ClientDetailViewModel? detail = null;

            await using (var command = connection.CreateConfiguredCommand())
            {
                command.CommandText = @"
SELECT c.""ClientID"", c.""EngagementName"", c.""MaconomyNumber"", c.""DirectorName"", c.""DirectorEmail"", c.""DirectorEmpCode"",
       c.""Industry"", c.""ManagerName"", c.""ManagerEmail"", c.""ManagerEmpCode"", c.""Status"", c.""CreatedAt"",
       COALESCE(u.""FirstName"" || ' ' || u.""LastName"", '') AS ""CreatedByName""
FROM ""Clients"" c
LEFT JOIN ""AspNetUsers"" u ON u.""Id"" = c.""CreatedBy""
WHERE c.""ClientID"" = @ClientID;";
                command.Parameters.AddWithValue("@ClientID", clientId);

                await using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                    return null;

                detail = new ClientDetailViewModel
                {
                    Id = reader.GetInt32(0),
                    EngagementName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    MaconomyNumber = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    DirectorName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    DirectorEmail = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    DirectorEmpCode = reader.IsDBNull(5) ? "" : reader.GetString(5),
                    Industry = reader.IsDBNull(6) || string.IsNullOrWhiteSpace(reader.GetString(6)) ? "Unspecified" : reader.GetString(6),
                    ManagerName = reader.IsDBNull(7) ? "" : reader.GetString(7),
                    ManagerEmail = reader.IsDBNull(8) ? "" : reader.GetString(8),
                    ManagerEmpCode = reader.IsDBNull(9) ? "" : reader.GetString(9),
                    Status = reader.IsDBNull(10) ? "" : reader.GetString(10),
                    CreatedAt = reader.IsDBNull(11) ? DateTime.UtcNow : reader.GetDateTime(11),
                    CreatedByName = reader.IsDBNull(12) ? "" : reader.GetString(12),
                    CurrentUserEngagementRole = access.CurrentUserEngagementRole
                };
            }

            detail.AssignedUsers = await GetAssignedUsersAsync(connection, clientId);
            detail.ValidationRuns = await GetValidationRunsForClientAsync(connection, clientId);
            detail.ScopeRuleNumbers = await GetEngagementScopeAsync(clientId);
            return detail;
        }

        public async Task ApproveClientAsync(int clientId, ApplicationUser approver, string role)
        {
            if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only admins can approve engagements.");

            await using var connection = await OpenConnectionAsync();
            await EnsureClientNotArchivedAsync(connection, clientId);
            var approverId = await EnsureUserMirrorAsync(approver, role);

            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
UPDATE ""Clients""
SET ""Status"" = 'Approved',
    ""ApprovedBy"" = @ApprovedBy,
    ""ApprovedAt"" = now()
WHERE ""ClientID"" = @ClientID
  AND ""Status"" <> 'Approved';";
            command.Parameters.AddWithValue("@ClientID", clientId);
            command.Parameters.AddWithValue("@ApprovedBy", approverId);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<bool> CanAccessClientModuleAsync(int clientId, ApplicationUser? user, string role)
        {
            if (user == null)
                return false;

            await using var connection = await OpenConnectionAsync();
            var isAdmin = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);
            var userId = "";

            if (!isAdmin)
                userId = await ResolveExistingUserScopeIdAsync(user, role);

            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = isAdmin
                ? @"SELECT COUNT(1)
                    FROM ""Clients""
                    WHERE ""ClientID"" = @ClientID
                      AND ""Status"" IN ('Approved', 'Active')
                      AND ""FirmID"" = @FirmID;"
                : @"SELECT COUNT(1)
                    FROM ""Clients"" c
                    INNER JOIN ""UserClientAssignments"" a
                        ON a.""ClientID"" = c.""ClientID""
                       AND a.""UserID"" = @UserID
                    WHERE c.""ClientID"" = @ClientID
                      AND c.""Status"" IN ('Approved', 'Active')
                      AND c.""FirmID"" = @FirmID;";
            command.Parameters.AddWithValue("@ClientID", clientId);
            command.Parameters.AddWithValue("@FirmID", (object?)user.FirmId ?? DBNull.Value);
            if (!isAdmin)
                command.Parameters.AddWithValue("@UserID", userId);

            return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
        }

        public async Task<bool> CanAccessClientResultsAsync(int clientId, ApplicationUser? user, string role)
        {
            if (clientId <= 0)
                return false;

            var access = await GetClientResultsAccessAsync(clientId, user, role);
            return access.CanAccess;
        }

        public async Task<ArchiveEligibilityViewModel> GetArchiveEligibilityAsync(int clientId)
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
SELECT c.""Status""
FROM ""Clients"" c
WHERE c.""ClientID"" = @ClientID;";
            command.Parameters.AddWithValue("@ClientID", clientId);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return new ArchiveEligibilityViewModel
                {
                    CanArchive = false,
                    Message = "Engagement was not found."
                };
            }

            var status = reader.IsDBNull(0) ? "" : reader.GetString(0);

            if (string.Equals(status, "Archived", StringComparison.OrdinalIgnoreCase))
            {
                return new ArchiveEligibilityViewModel
                {
                    CanArchive = false,
                    Message = "This engagement is already archived."
                };
            }

            if (!string.Equals(status, "Approved", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase))
            {
                return new ArchiveEligibilityViewModel
                {
                    CanArchive = false,
                    Message = "Only active approved engagements can be archived."
                };
            }

            await reader.DisposeAsync();
            var scope = await GetEngagementScopeAsync(clientId);
            var validationRuns = await GetValidationRunsForClientAsync(connection, clientId);
            var currentRuns = validationRuns
                .Where(run => run.IsCurrent)
                .OrderBy(run => run.RuleNumber)
                .ThenByDescending(run => run.RunAt)
                .ThenByDescending(run => run.Id)
                .ToList();

            // When a scope is defined, only in-scope rules must be completed before archiving.
            var runsInScope = scope.Count > 0
                ? currentRuns.Where(run => scope.Contains(run.RuleNumber)).ToList()
                : currentRuns;

            if (runsInScope.Count == 0)
            {
                var noRunsMsg = scope.Count > 0
                    ? "No current results exist for the in-scope rules. Run the selected validation modules and complete the reviews before archiving."
                    : "No current results are available yet. Run the validation modules and complete the reviews before archiving.";
                return new ArchiveEligibilityViewModel
                {
                    CanArchive = false,
                    Message = noRunsMsg
                };
            }

            var incompleteCurrentRuns = runsInScope
                .Where(run => !string.Equals(run.Status, "Reviewed and Completed", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var latestCurrentRun = runsInScope
                .OrderByDescending(run => run.RunAt)
                .ThenByDescending(run => run.Id)
                .FirstOrDefault();
            var canArchive = incompleteCurrentRuns.Count == 0;
            return new ArchiveEligibilityViewModel
            {
                CanArchive = canArchive,
                CurrentRunId = latestCurrentRun?.Id,
                CurrentRunRuleNumber = latestCurrentRun?.RuleNumber,
                Message = canArchive
                    ? "All in-scope results are reviewed and completed. The engagement is ready to be archived."
                    : BuildArchiveEligibilityMessage(incompleteCurrentRuns)
            };
        }

        public async Task ArchiveClientAsync(int clientId, ApplicationUser archiver, string role)
        {
            if (!string.Equals(role, "Director", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only a director can archive an engagement.");

            var eligibility = await GetArchiveEligibilityAsync(clientId);
            if (!eligibility.CanArchive)
                throw new InvalidOperationException(eligibility.Message);

            await using var connection = await OpenConnectionAsync();
            await EnsureClientNotArchivedAsync(connection, clientId);
            var archiverId = await EnsureUserMirrorAsync(archiver, role);

            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
UPDATE ""Clients""
SET ""Status"" = 'Archived',
    ""ArchivedBy"" = @ArchivedBy,
    ""ArchivedAt"" = now()
WHERE ""ClientID"" = @ClientID
  AND ""Status"" <> 'Archived';";
            command.Parameters.AddWithValue("@ClientID", clientId);
            command.Parameters.AddWithValue("@ArchivedBy", archiverId);
            await command.ExecuteNonQueryAsync();
        }

        public async Task DeleteClientAsync(int clientId)
        {
            await using var connection = await OpenConnectionAsync();
            await EnsureClientNotArchivedAsync(connection, clientId);
            await using var command = connection.CreateConfiguredCommand();
            // Every table below has a foreign key on ClientID with no ON DELETE CASCADE, so each
            // one that has ever gained a row for this client (a rule selection, a favorite, an
            // uploaded dataset, a message thread) blocks the final DELETE FROM "Clients" with a
            // foreign-key violation. Any real, actively-used engagement is virtually guaranteed to
            // have rows in at least EngagementRuleScope, so this 500'd for any such client - not
            // just this one. ThreadMessages/ThreadUserStates and their own children reference
            // MessageThreads.ThreadID rather than ClientID directly, so they're cleared via the
            // client's thread IDs first.
            command.CommandText = @"
DELETE FROM ""ThreadMessageAttachments"" WHERE ""MessageID"" IN (
    SELECT ""MessageID"" FROM ""ThreadMessages"" WHERE ""ThreadID"" IN (
        SELECT ""ThreadID"" FROM ""MessageThreads"" WHERE ""ClientID"" = @ClientID));
DELETE FROM ""ThreadMessageRecipients"" WHERE ""MessageID"" IN (
    SELECT ""MessageID"" FROM ""ThreadMessages"" WHERE ""ThreadID"" IN (
        SELECT ""ThreadID"" FROM ""MessageThreads"" WHERE ""ClientID"" = @ClientID));
DELETE FROM ""ThreadMessages"" WHERE ""ThreadID"" IN (
    SELECT ""ThreadID"" FROM ""MessageThreads"" WHERE ""ClientID"" = @ClientID);
DELETE FROM ""ThreadUserStates"" WHERE ""ThreadID"" IN (
    SELECT ""ThreadID"" FROM ""MessageThreads"" WHERE ""ClientID"" = @ClientID);
DELETE FROM ""MessageThreads"" WHERE ""ClientID"" = @ClientID;
DELETE FROM ""ClientFavorites"" WHERE ""ClientID"" = @ClientID;
DELETE FROM ""EngagementRuleScope"" WHERE ""ClientID"" = @ClientID;
DELETE FROM ""DatasetUploadJobs"" WHERE ""ClientID"" = @ClientID;
DELETE FROM ""EngagementDatabases"" WHERE ""ClientID"" = @ClientID;
DELETE FROM ""UserClientAssignments"" WHERE ""ClientID"" = @ClientID;
DELETE FROM ""ReviewSignoffs"" WHERE ""ClientID"" = @ClientID;
DELETE FROM ""ValidationRuns"" WHERE ""ClientID"" = @ClientID;
DELETE FROM ""Clients"" WHERE ""ClientID"" = @ClientID;";
            command.Parameters.AddWithValue("@ClientID", clientId);
            await command.ExecuteNonQueryAsync();
        }

        public async Task AssignUserAsync(int clientId, ApplicationUser targetUser, string engagementRole, ApplicationUser assignedBy, string assignedByRole)
        {
            await using var connection = await OpenConnectionAsync();
            await EnsureClientNotArchivedAsync(connection, clientId);
            var targetUserId = await EnsureUserMirrorAsync(targetUser, engagementRole);
            var assignedById = await EnsureUserMirrorAsync(assignedBy, assignedByRole);

            await using var exists = connection.CreateConfiguredCommand();
            exists.CommandText = "SELECT COUNT(1) FROM \"UserClientAssignments\" WHERE \"UserID\" = @UserID AND \"ClientID\" = @ClientID;";
            exists.Parameters.AddWithValue("@UserID", targetUserId);
            exists.Parameters.AddWithValue("@ClientID", clientId);
            var hasRow = Convert.ToInt32(await exists.ExecuteScalarAsync()) > 0;

            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = hasRow
                ? @"UPDATE ""UserClientAssignments""
                    SET ""EngagementRole"" = @EngagementRole, ""AssignedBy"" = @AssignedBy, ""AssignedAt"" = now()
                    WHERE ""UserID"" = @UserID AND ""ClientID"" = @ClientID;"
                : @"INSERT INTO ""UserClientAssignments"" (""UserID"", ""ClientID"", ""EngagementRole"", ""AssignedBy"", ""AssignedAt"")
                    VALUES (@UserID, @ClientID, @EngagementRole, @AssignedBy, now());";
            command.Parameters.AddWithValue("@UserID", targetUserId);
            command.Parameters.AddWithValue("@ClientID", clientId);
            command.Parameters.AddWithValue("@EngagementRole", engagementRole);
            command.Parameters.AddWithValue("@AssignedBy", assignedById);
            await command.ExecuteNonQueryAsync();
        }

        public async Task RemoveAssignmentAsync(int clientUserId)
        {
            await using var connection = await OpenConnectionAsync();
            await EnsureAssignmentClientNotArchivedAsync(connection, clientUserId);
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = "DELETE FROM \"UserClientAssignments\" WHERE \"AssignmentID\" = @Id;";
            command.Parameters.AddWithValue("@Id", clientUserId);
            await command.ExecuteNonQueryAsync();
        }

        // No more mirror row to delete. What matters here is reassigning/clearing this user's
        // ownership references across the operational tables before their Identity row is
        // removed by the caller — that part of the original logic is preserved below.
        public async Task DeleteUserMirrorAsync(ApplicationUser targetUser, ApplicationUser deletedBy, string deletedByRole)
        {
            var targetUserId = targetUser.Id;
            if (string.IsNullOrWhiteSpace(targetUserId))
                return;

            var replacementUserId = deletedBy.Id;

            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
UPDATE ""Clients""
SET ""CreatedBy"" = @ReplacementUserID
WHERE ""CreatedBy"" = @TargetUserID;

UPDATE ""Clients""
SET ""ApprovedBy"" = @ReplacementUserID
WHERE ""ApprovedBy"" = @TargetUserID;

UPDATE ""Clients""
SET ""ArchivedBy"" = @ReplacementUserID
WHERE ""ArchivedBy"" = @TargetUserID;

UPDATE ""UserClientAssignments""
SET ""AssignedBy"" = @ReplacementUserID
WHERE ""AssignedBy"" = @TargetUserID;

DELETE FROM ""UserClientAssignments""
WHERE ""UserID"" = @TargetUserID;

DELETE FROM ""ReviewSignoffs""
WHERE ""ReviewerID"" = @TargetUserID;

UPDATE ""ValidationRuns""
SET ""UserID"" = @ReplacementUserID
WHERE ""UserID"" = @TargetUserID;

UPDATE ""AuditLog""
SET ""UserID"" = NULL
WHERE ""UserID"" = @TargetUserID;

DELETE FROM ""PasswordResetTokens""
WHERE ""UserID"" = @TargetUserID;

DELETE FROM ""ImpersonationLog""
WHERE ""AdminUserID"" = @TargetUserID
   OR ""ImpersonatedUserID"" = @TargetUserID;

UPDATE ""MessageThreads""
SET ""CreatedByUserID"" = @ReplacementUserID
WHERE ""CreatedByUserID"" = @TargetUserID;

UPDATE ""ThreadMessages""
SET ""SenderUserID"" = @ReplacementUserID
WHERE ""SenderUserID"" = @TargetUserID;

DELETE FROM ""ThreadMessageRecipients""
WHERE ""UserID"" = @TargetUserID;

DELETE FROM ""ThreadUserStates""
WHERE ""UserID"" = @TargetUserID;

DELETE FROM ""ClientFavorites""
WHERE ""UserID"" = @TargetUserID;

UPDATE ""EngagementRuleScope""
SET ""AddedByUserID"" = @ReplacementUserID
WHERE ""AddedByUserID"" = @TargetUserID;";
            command.Parameters.AddWithValue("@ReplacementUserID", replacementUserId);
            command.Parameters.AddWithValue("@TargetUserID", targetUserId);
            await command.ExecuteNonQueryAsync();
        }

        public async Task WriteAuditLogAsync(
            string action,
            string? details = null,
            string? userId = null,
            string? userName = null,
            string? entityType = null,
            int? entityId = null,
            string? oldValues = null,
            string? newValues = null,
            string? ipAddress = null)
        {
            await using var connection = await OpenConnectionAsync();
            var actorUserId = await ResolveAuditUserIdAsync(connection, userId, userName);
            var timestamp = DateTime.UtcNow;
            var previousHash = await GetLatestHashAsync(connection, "AuditLog");

            await using var insert = connection.CreateConfiguredCommand();
            insert.CommandText = @"
INSERT INTO ""AuditLog""
(""UserID"", ""Action"", ""EntityType"", ""EntityID"", ""OldValues"", ""NewValues"", ""IPAddress"", ""Timestamp"", ""PreviousHash"", ""RecordHash"")
VALUES
(@UserID, @Action, @EntityType, @EntityID, @OldValues, @NewValues, @IPAddress, @Timestamp, @PreviousHash, NULL)
RETURNING ""LogID"";";
            insert.Parameters.AddWithValue("@UserID", (object?)actorUserId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@Action", action);
            insert.Parameters.AddWithValue("@EntityType", (object?)entityType ?? DBNull.Value);
            insert.Parameters.AddWithValue("@EntityID", (object?)entityId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@OldValues", (object?)oldValues ?? DBNull.Value);
            insert.Parameters.AddWithValue("@NewValues", (object?)newValues ?? DBNull.Value);
            insert.Parameters.AddWithValue("@IPAddress", (object?)ipAddress ?? DBNull.Value);
            insert.Parameters.AddWithValue("@Timestamp", timestamp);
            insert.Parameters.AddWithValue("@PreviousHash", (object?)previousHash ?? DBNull.Value);
            var logId = Convert.ToInt32(await insert.ExecuteScalarAsync());

            var recordHash = ComputeHash($@"AuditLog|{logId}|{actorUserId}|{action}|{entityType}|{entityId}|{oldValues}|{newValues}|{ipAddress}|{timestamp:o}|{previousHash}");
            await using var update = connection.CreateConfiguredCommand();
            update.CommandText = "UPDATE \"AuditLog\" SET \"RecordHash\" = @RecordHash WHERE \"LogID\" = @LogID;";
            update.Parameters.AddWithValue("@RecordHash", recordHash);
            update.Parameters.AddWithValue("@LogID", logId);
            await update.ExecuteNonQueryAsync();
        }

        public async Task<List<AuditLogRowViewModel>> GetAuditLogsAsync(int take = 500)
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = $@"
SELECT
       al.""LogID"",
       al.""Timestamp"",
       TRIM(COALESCE(u.""FirstName"",'') || ' ' || COALESCE(u.""LastName"",'')) AS ""UserName"",
       al.""Action"",
       al.""EntityType"",
       al.""EntityID"",
       al.""OldValues"",
       al.""NewValues"",
       al.""IPAddress"",
       al.""PreviousHash"",
       al.""RecordHash""
FROM ""AuditLog"" al
LEFT JOIN ""AspNetUsers"" u ON u.""Id"" = al.""UserID""
ORDER BY al.""Timestamp"" DESC, al.""LogID"" DESC
LIMIT {take};";
            await using var reader = await command.ExecuteReaderAsync();
            var list = new List<AuditLogRowViewModel>();
            while (await reader.ReadAsync())
            {
                list.Add(new AuditLogRowViewModel
                {
                    LogId = reader.GetInt32(0),
                    Timestamp = reader.IsDBNull(1) ? DateTime.UtcNow : reader.GetDateTime(1),
                    UserName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Action = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    EntityType = reader.IsDBNull(4) ? null : reader.GetString(4),
                    EntityId = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    OldValues = reader.IsDBNull(6) ? null : reader.GetString(6),
                    NewValues = reader.IsDBNull(7) ? null : reader.GetString(7),
                    IpAddress = reader.IsDBNull(8) ? null : reader.GetString(8),
                    PreviousHash = reader.IsDBNull(9) ? null : reader.GetString(9),
                    RecordHash = reader.IsDBNull(10) ? null : reader.GetString(10)
                });
            }

            return list;
        }

        public async Task<int> GetUnreadMessageCountAsync(ApplicationUser? user, string role)
        {
            var (userId, _) = await ResolveUserScopeAsync(user, role);
            if (string.IsNullOrEmpty(userId))
                return 0;

            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"SELECT COUNT(1)
                                    FROM ""ThreadMessageRecipients"" r
                                    INNER JOIN ""ThreadMessages"" tm ON tm.""MessageID"" = r.""MessageID""
                                    LEFT JOIN ""ThreadUserStates"" tus ON tus.""ThreadID"" = tm.""ThreadID"" AND tus.""UserID"" = @UserID
                                    WHERE r.""UserID"" = @UserID
                                      AND r.""IsRead"" = false
                                      AND COALESCE(tm.""IsDeleted"", false) = false
                                      AND COALESCE(tus.""IsDeleted"", false) = false;";
            command.Parameters.AddWithValue("@UserID", userId);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public async Task<List<MessageSummaryViewModel>> GetInboxThreadsAsync(ApplicationUser? user, string role, int take = 20)
        {
            var (userId, isAdmin) = await ResolveUserScopeAsync(user, role);
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = isAdmin
                ? $@"
SELECT
       th.""ThreadID"",
       COALESCE(th.""Subject"",'') AS ""Subject"",
       COALESCE(c.""EngagementName"",'General') AS ""ClientName"",
       CASE
           WHEN lastMsg.""MessageID"" IS NULL THEN ''
           WHEN COALESCE(lastMsg.""IsDeleted"", false) THEN 'Message deleted'
           WHEN NULLIF(COALESCE(lastMsg.""Body"",''), '') IS NOT NULL THEN lastMsg.""Body""
           WHEN EXISTS (
               SELECT 1 FROM ""ThreadMessageAttachments"" att
               WHERE att.""MessageID"" = lastMsg.""MessageID""
           ) THEN '[Attachment]'
           ELSE ''
       END AS ""Preview"",
       COALESCE(lastMsg.""SentAt"", th.""LastMessageAt"") AS ""LastMessageAt"",
       COALESCE(sender.""FirstName"" || ' ' || sender.""LastName"", '') AS ""LastSenderName"",
       (
         SELECT COUNT(1)
         FROM ""ThreadMessages"" tm
         INNER JOIN ""ThreadMessageRecipients"" r ON r.""MessageID"" = tm.""MessageID""
         WHERE tm.""ThreadID"" = th.""ThreadID""
           AND r.""UserID"" = @UserID
           AND r.""IsRead"" = false
           AND COALESCE(tm.""IsDeleted"", false) = false
       ) AS ""UnreadCount""
FROM ""MessageThreads"" th
LEFT JOIN ""Clients"" c ON c.""ClientID"" = th.""ClientID""
LEFT JOIN ""ThreadMessages"" lastMsg ON lastMsg.""MessageID"" = (
    SELECT tm.""MessageID""
    FROM ""ThreadMessages"" tm
    WHERE tm.""ThreadID"" = th.""ThreadID""
    ORDER BY tm.""SentAt"" DESC, tm.""MessageID"" DESC
    LIMIT 1
)
LEFT JOIN ""AspNetUsers"" sender ON sender.""Id"" = lastMsg.""SenderUserID""
WHERE (
    th.""CreatedByUserID"" = @UserID
    OR EXISTS (
        SELECT 1
        FROM ""ThreadMessages"" tm
        INNER JOIN ""ThreadMessageRecipients"" r ON r.""MessageID"" = tm.""MessageID""
        WHERE tm.""ThreadID"" = th.""ThreadID""
          AND r.""UserID"" = @UserID
    )
    OR EXISTS (
        SELECT 1
        FROM ""ThreadMessages"" tm
        WHERE tm.""ThreadID"" = th.""ThreadID""
          AND tm.""SenderUserID"" = @UserID
    )
)
AND NOT EXISTS (
    SELECT 1
    FROM ""ThreadUserStates"" tus
    WHERE tus.""ThreadID"" = th.""ThreadID""
      AND tus.""UserID"" = @UserID
      AND tus.""IsDeleted""
)
ORDER BY th.""LastMessageAt"" DESC, th.""ThreadID"" DESC
LIMIT {take};"
                : $@"
SELECT
       th.""ThreadID"",
       COALESCE(th.""Subject"",'') AS ""Subject"",
       COALESCE(c.""EngagementName"",'General') AS ""ClientName"",
       CASE
           WHEN lastMsg.""MessageID"" IS NULL THEN ''
           WHEN COALESCE(lastMsg.""IsDeleted"", false) THEN 'Message deleted'
           WHEN NULLIF(COALESCE(lastMsg.""Body"",''), '') IS NOT NULL THEN lastMsg.""Body""
           WHEN EXISTS (
               SELECT 1 FROM ""ThreadMessageAttachments"" att
               WHERE att.""MessageID"" = lastMsg.""MessageID""
           ) THEN '[Attachment]'
           ELSE ''
       END AS ""Preview"",
       COALESCE(lastMsg.""SentAt"", th.""LastMessageAt"") AS ""LastMessageAt"",
       COALESCE(sender.""FirstName"" || ' ' || sender.""LastName"", '') AS ""LastSenderName"",
       (
         SELECT COUNT(1)
         FROM ""ThreadMessages"" tm
         INNER JOIN ""ThreadMessageRecipients"" r ON r.""MessageID"" = tm.""MessageID""
         WHERE tm.""ThreadID"" = th.""ThreadID""
           AND r.""UserID"" = @UserID
           AND r.""IsRead"" = false
           AND COALESCE(tm.""IsDeleted"", false) = false
       ) AS ""UnreadCount""
FROM ""MessageThreads"" th
LEFT JOIN ""Clients"" c ON c.""ClientID"" = th.""ClientID""
LEFT JOIN ""ThreadMessages"" lastMsg ON lastMsg.""MessageID"" = (
    SELECT tm.""MessageID""
    FROM ""ThreadMessages"" tm
    WHERE tm.""ThreadID"" = th.""ThreadID""
    ORDER BY tm.""SentAt"" DESC, tm.""MessageID"" DESC
    LIMIT 1
)
LEFT JOIN ""AspNetUsers"" sender ON sender.""Id"" = lastMsg.""SenderUserID""
WHERE (
    EXISTS (
        SELECT 1
        FROM ""ThreadMessages"" tm
        INNER JOIN ""ThreadMessageRecipients"" r ON r.""MessageID"" = tm.""MessageID""
        WHERE tm.""ThreadID"" = th.""ThreadID""
          AND r.""UserID"" = @UserID
    )
    OR EXISTS (
        SELECT 1
        FROM ""ThreadMessages"" tm
        WHERE tm.""ThreadID"" = th.""ThreadID""
          AND tm.""SenderUserID"" = @UserID
    )
)
AND NOT EXISTS (
    SELECT 1
    FROM ""ThreadUserStates"" tus
    WHERE tus.""ThreadID"" = th.""ThreadID""
      AND tus.""UserID"" = @UserID
      AND tus.""IsDeleted""
)
ORDER BY th.""LastMessageAt"" DESC, th.""ThreadID"" DESC
LIMIT {take};";
            command.Parameters.AddWithValue("@UserID", userId);

            await using var reader = await command.ExecuteReaderAsync();
            var list = new List<MessageSummaryViewModel>();
            while (await reader.ReadAsync())
            {
                list.Add(new MessageSummaryViewModel
                {
                    ThreadId = reader.GetInt32(0),
                    Subject = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    ClientName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Preview = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    LastMessageAt = reader.IsDBNull(4) ? DateTime.UtcNow : reader.GetDateTime(4),
                    LastSenderName = reader.IsDBNull(5) ? "" : reader.GetString(5),
                    UnreadCount = reader.FieldCount > 6 && !reader.IsDBNull(6) ? reader.GetInt32(6) : 0
                });
            }

            return list;
        }

        public async Task<MessageThreadViewModel?> GetMessageThreadAsync(int threadId, ApplicationUser? user, string role)
        {
            var (userId, _) = await ResolveUserScopeAsync(user, role);
            await using var connection = await OpenConnectionAsync();
            await using var check = connection.CreateConfiguredCommand();
            check.CommandText = @"SELECT COUNT(1)
                    FROM ""MessageThreads"" th
                    WHERE th.""ThreadID"" = @ThreadID
                      AND NOT EXISTS (
                          SELECT 1
                          FROM ""ThreadUserStates"" tus
                          WHERE tus.""ThreadID"" = th.""ThreadID""
                            AND tus.""UserID"" = @UserID
                            AND tus.""IsDeleted""
                      )
                      AND (
                          EXISTS (
                              SELECT 1 FROM ""ThreadMessages"" tm
                              INNER JOIN ""ThreadMessageRecipients"" r ON r.""MessageID"" = tm.""MessageID""
                              WHERE tm.""ThreadID"" = th.""ThreadID"" AND r.""UserID"" = @UserID
                          )
                          OR EXISTS (
                              SELECT 1 FROM ""ThreadMessages"" tm
                              WHERE tm.""ThreadID"" = th.""ThreadID"" AND tm.""SenderUserID"" = @UserID
                          )
                      );";
            check.Parameters.AddWithValue("@ThreadID", threadId);
            check.Parameters.AddWithValue("@UserID", userId);
            if (Convert.ToInt32(await check.ExecuteScalarAsync()) == 0)
                return null;

            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
SELECT th.""ThreadID"", th.""ClientID"", COALESCE(th.""Subject"",'') AS ""Subject"", COALESCE(c.""EngagementName"",'General') AS ""ClientName"",
       COALESCE(sender.""FirstName"" || ' ' || sender.""LastName"", '') AS ""CreatedByName"", th.""CreatedAt"", th.""LastMessageAt""
FROM ""MessageThreads"" th
LEFT JOIN ""Clients"" c ON c.""ClientID"" = th.""ClientID""
LEFT JOIN ""AspNetUsers"" sender ON sender.""Id"" = th.""CreatedByUserID""
WHERE th.""ThreadID"" = @ThreadID;";
            command.Parameters.AddWithValue("@ThreadID", threadId);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return null;

            var thread = new MessageThreadViewModel
            {
                ThreadId = reader.GetInt32(0),
                ClientId = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                Subject = reader.IsDBNull(2) ? "" : reader.GetString(2),
                ClientName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                CreatedByName = reader.IsDBNull(4) ? "" : reader.GetString(4),
                CreatedAt = reader.IsDBNull(5) ? DateTime.UtcNow : reader.GetDateTime(5),
                LastMessageAt = reader.IsDBNull(6) ? DateTime.UtcNow : reader.GetDateTime(6),
                CanEdit = true,
                CanDelete = true
            };
            await reader.CloseAsync();

            await using var participants = connection.CreateConfiguredCommand();
            participants.CommandText = @"
SELECT DISTINCT participant.""FullName""
FROM (
    SELECT TRIM(COALESCE(sender.""FirstName"",'') || ' ' || COALESCE(sender.""LastName"",'')) AS ""FullName""
    FROM ""ThreadMessages"" tm
    INNER JOIN ""AspNetUsers"" sender ON sender.""Id"" = tm.""SenderUserID""
    WHERE tm.""ThreadID"" = @ThreadID

    UNION

    SELECT TRIM(COALESCE(recipient.""FirstName"",'') || ' ' || COALESCE(recipient.""LastName"",'')) AS ""FullName""
    FROM ""ThreadMessages"" tm
    INNER JOIN ""ThreadMessageRecipients"" r ON r.""MessageID"" = tm.""MessageID""
    INNER JOIN ""AspNetUsers"" recipient ON recipient.""Id"" = r.""UserID""
    WHERE tm.""ThreadID"" = @ThreadID
) participant
WHERE NULLIF(participant.""FullName"", '') IS NOT NULL
ORDER BY participant.""FullName"";";
            participants.Parameters.AddWithValue("@ThreadID", threadId);
            await using (var participantReader = await participants.ExecuteReaderAsync())
            {
                while (await participantReader.ReadAsync())
                {
                    thread.Participants.Add(participantReader.IsDBNull(0) ? "" : participantReader.GetString(0));
                }
            }

            thread.Messages = await GetThreadMessagesAsync(connection, threadId, userId);
            return thread;
        }

        public async Task<List<MessageRecipientOptionViewModel>> GetMessageRecipientsAsync(ApplicationUser? user, string role, int? clientId = null)
        {
            var (userId, isAdmin) = await ResolveUserScopeAsync(user, role);
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = isAdmin
                ? @"SELECT u.""Id"",
                           TRIM(COALESCE(u.""FirstName"",'') || ' ' || COALESCE(u.""LastName"",'')) AS ""FullName"",
                           COALESCE(u.""Email"",'') AS ""Email"",
                           COALESCE((
                               SELECT r.""Name""
                               FROM ""AspNetUserRoles"" ur
                               INNER JOIN ""AspNetRoles"" r ON r.""Id"" = ur.""RoleId""
                               WHERE ur.""UserId"" = u.""Id""
                               LIMIT 1
                           ), '') AS ""RoleName""
                    FROM ""AspNetUsers"" u
                    WHERE u.""IsActive""
                      AND u.""Id"" <> @UserID
                      AND u.""FirmId"" = @FirmID
                    ORDER BY ""FullName"";"
                : clientId.HasValue
                    ? @"SELECT DISTINCT u.""Id"", TRIM(COALESCE(u.""FirstName"",'') || ' ' || COALESCE(u.""LastName"",'')) AS ""FullName"", COALESCE(u.""Email"",'') AS ""Email"", COALESCE(a.""EngagementRole"",'') AS ""RoleName""
                        FROM ""AspNetUsers"" u
                        INNER JOIN ""UserClientAssignments"" a ON a.""UserID"" = u.""Id"" AND a.""ClientID"" = @ClientID
                        WHERE u.""IsActive""
                          AND u.""Id"" <> @UserID
                          AND u.""FirmId"" = @FirmID
                        ORDER BY ""FullName"";"
                    : @"SELECT DISTINCT u.""Id"", TRIM(COALESCE(u.""FirstName"",'') || ' ' || COALESCE(u.""LastName"",'')) AS ""FullName"", COALESCE(u.""Email"",'') AS ""Email"", COALESCE(a.""EngagementRole"",'') AS ""RoleName""
                        FROM ""AspNetUsers"" u
                        INNER JOIN ""UserClientAssignments"" a ON a.""UserID"" = u.""Id""
                        WHERE u.""IsActive""
                          AND u.""Id"" <> @UserID
                          AND u.""FirmId"" = @FirmID
                          AND a.""ClientID"" IN (
                              SELECT DISTINCT ""ClientID""
                              FROM ""UserClientAssignments""
                              WHERE ""UserID"" = @UserID
                          )
                        ORDER BY ""FullName"";";
            command.Parameters.AddWithValue("@UserID", userId);
            command.Parameters.AddWithValue("@FirmID", (object?)user?.FirmId ?? DBNull.Value);
            if (clientId.HasValue)
                command.Parameters.AddWithValue("@ClientID", clientId.Value);

            await using var reader = await command.ExecuteReaderAsync();
            var options = new List<MessageRecipientOptionViewModel>();
            while (await reader.ReadAsync())
            {
                options.Add(new MessageRecipientOptionViewModel
                {
                    UserId = reader.GetString(0),
                    FullName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Email = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Role = reader.IsDBNull(3) ? "" : reader.GetString(3)
                });
            }
            return options;
        }

        public async Task<int> CreateMessageThreadAsync(ApplicationUser sender, string senderRole, IEnumerable<string> recipientUserIds, string subject, string body, int? clientId = null, IEnumerable<MessageAttachmentInput>? attachments = null)
        {
            await using var connection = await OpenConnectionAsync();
            var senderUserId = await EnsureUserMirrorAsync(sender, senderRole);
            var timestamp = DateTime.UtcNow;
            var threadPrevHash = await GetLatestHashAsync(connection, "MessageThreads");

            await using var threadCommand = connection.CreateConfiguredCommand();
            threadCommand.CommandText = @"
INSERT INTO ""MessageThreads"" (""ClientID"", ""Subject"", ""CreatedByUserID"", ""CreatedAt"", ""LastMessageAt"", ""PreviousHash"", ""RecordHash"")
VALUES (@ClientID, @Subject, @CreatedByUserID, @CreatedAt, @LastMessageAt, @PreviousHash, NULL)
RETURNING ""ThreadID"";";
            threadCommand.Parameters.AddWithValue("@ClientID", (object?)clientId ?? DBNull.Value);
            threadCommand.Parameters.AddWithValue("@Subject", subject);
            threadCommand.Parameters.AddWithValue("@CreatedByUserID", senderUserId);
            threadCommand.Parameters.AddWithValue("@CreatedAt", timestamp);
            threadCommand.Parameters.AddWithValue("@LastMessageAt", timestamp);
            threadCommand.Parameters.AddWithValue("@PreviousHash", (object?)threadPrevHash ?? DBNull.Value);
            var threadId = Convert.ToInt32(await threadCommand.ExecuteScalarAsync());

            var threadRecordHash = ComputeHash($@"MessageThread|{threadId}|{clientId}|{senderUserId}|{subject}|{timestamp:o}|{threadPrevHash}");
            await using (var updateThread = connection.CreateConfiguredCommand())
            {
                updateThread.CommandText = "UPDATE \"MessageThreads\" SET \"RecordHash\" = @RecordHash WHERE \"ThreadID\" = @ThreadID;";
                updateThread.Parameters.AddWithValue("@RecordHash", threadRecordHash);
                updateThread.Parameters.AddWithValue("@ThreadID", threadId);
                await updateThread.ExecuteNonQueryAsync();
            }

            var participantIds = recipientUserIds
                .Append(senderUserId)
                .Distinct()
                .ToList();

            await RestoreThreadForUsersAsync(connection, threadId, participantIds);
            await InsertThreadMessageAsync(connection, threadId, senderUserId, body, null, recipientUserIds, timestamp, attachments);
            return threadId;
        }

        public async Task<int> ReplyToThreadAsync(int threadId, ApplicationUser sender, string senderRole, string body, IEnumerable<MessageAttachmentInput>? attachments = null)
        {
            await using var connection = await OpenConnectionAsync();
            var senderUserId = await EnsureUserMirrorAsync(sender, senderRole);

            if (!await CanAccessThreadAsync(connection, threadId, senderUserId))
                throw new InvalidOperationException("You cannot reply to this chat.");

            var participants = await GetThreadParticipantIdsAsync(connection, threadId);
            await RestoreThreadForUsersAsync(connection, threadId, participants.Append(senderUserId));
            participants.Remove(senderUserId);
            await InsertThreadMessageAsync(connection, threadId, senderUserId, body, null, participants, DateTime.UtcNow, attachments);

            await using var update = connection.CreateConfiguredCommand();
            update.CommandText = "UPDATE \"MessageThreads\" SET \"LastMessageAt\" = now() WHERE \"ThreadID\" = @ThreadID;";
            update.Parameters.AddWithValue("@ThreadID", threadId);
            await update.ExecuteNonQueryAsync();
            return threadId;
        }

        public async Task UpdateThreadSubjectAsync(int threadId, ApplicationUser user, string role, string subject)
        {
            var userId = await EnsureUserMirrorAsync(user, role);
            await using var connection = await OpenConnectionAsync();

            if (!await CanAccessThreadAsync(connection, threadId, userId))
                throw new InvalidOperationException("You cannot edit this chat.");

            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
UPDATE ""MessageThreads""
SET ""Subject"" = @Subject
WHERE ""ThreadID"" = @ThreadID;";
            command.Parameters.AddWithValue("@Subject", subject);
            command.Parameters.AddWithValue("@ThreadID", threadId);
            await command.ExecuteNonQueryAsync();
        }

        public async Task DeleteThreadForUserAsync(int threadId, ApplicationUser user, string role)
        {
            var userId = await EnsureUserMirrorAsync(user, role);
            await using var connection = await OpenConnectionAsync();

            if (!await CanAccessThreadAsync(connection, threadId, userId))
                throw new InvalidOperationException("You cannot delete this chat.");

            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
INSERT INTO ""ThreadUserStates"" (""ThreadID"", ""UserID"", ""IsDeleted"", ""DeletedAt"")
VALUES (@ThreadID, @UserID, true, now())
ON CONFLICT (""ThreadID"", ""UserID"") DO UPDATE
SET ""IsDeleted"" = EXCLUDED.""IsDeleted"",
    ""DeletedAt"" = EXCLUDED.""DeletedAt"";";
            command.Parameters.AddWithValue("@ThreadID", threadId);
            command.Parameters.AddWithValue("@UserID", userId);
            await command.ExecuteNonQueryAsync();
        }

        public async Task UpdateMessageAsync(int messageId, int threadId, ApplicationUser user, string role, string body)
        {
            var userId = await EnsureUserMirrorAsync(user, role);
            await using var connection = await OpenConnectionAsync();

            if (!await CanAccessMessageAsync(connection, messageId, threadId, userId))
                throw new InvalidOperationException("You can only edit your own messages.");

            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
UPDATE ""ThreadMessages""
SET ""Body"" = @Body,
    ""EditedAt"" = now()
WHERE ""MessageID"" = @MessageID
  AND ""ThreadID"" = @ThreadID;";
            command.Parameters.AddWithValue("@Body", body);
            command.Parameters.AddWithValue("@MessageID", messageId);
            command.Parameters.AddWithValue("@ThreadID", threadId);
            await command.ExecuteNonQueryAsync();
        }

        public async Task DeleteMessageAsync(int messageId, int threadId, ApplicationUser user, string role)
        {
            var userId = await EnsureUserMirrorAsync(user, role);
            await using var connection = await OpenConnectionAsync();

            if (!await CanAccessMessageAsync(connection, messageId, threadId, userId))
                throw new InvalidOperationException("You can only delete your own messages.");

            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
UPDATE ""ThreadMessages""
SET ""Body"" = '',
    ""IsDeleted"" = true,
    ""DeletedAt"" = now(),
    ""EditedAt"" = now()
WHERE ""MessageID"" = @MessageID
  AND ""ThreadID"" = @ThreadID;";
            command.Parameters.AddWithValue("@MessageID", messageId);
            command.Parameters.AddWithValue("@ThreadID", threadId);
            await command.ExecuteNonQueryAsync();
        }

        public async Task MarkThreadReadAsync(int threadId, ApplicationUser user, string role)
        {
            var userId = await EnsureUserMirrorAsync(user, role);
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
UPDATE ""ThreadMessageRecipients"" r
SET ""IsRead"" = true,
    ""ReadAt"" = now()
FROM ""ThreadMessages"" tm
WHERE tm.""MessageID"" = r.""MessageID""
  AND tm.""ThreadID"" = @ThreadID
  AND r.""UserID"" = @UserID
  AND r.""IsRead"" = false;";
            command.Parameters.AddWithValue("@ThreadID", threadId);
            command.Parameters.AddWithValue("@UserID", userId);
            await command.ExecuteNonQueryAsync();
        }

        public async Task ToggleClientFavoriteAsync(int clientId, ApplicationUser user, string role)
        {
            var clients = await GetClientsCoreAsync(user, role, null, "all");
            if (!clients.Any(c => c.Id == clientId))
                throw new InvalidOperationException("You cannot favorite this engagement.");

            var (userId, _) = await ResolveUserScopeAsync(user, role);
            await using var connection = await OpenConnectionAsync();

            await using var check = connection.CreateConfiguredCommand();
            check.CommandText = @"
SELECT COUNT(1)
FROM ""ClientFavorites""
WHERE ""UserID"" = @UserID AND ""ClientID"" = @ClientID;";
            check.Parameters.AddWithValue("@UserID", userId);
            check.Parameters.AddWithValue("@ClientID", clientId);
            var exists = Convert.ToInt32(await check.ExecuteScalarAsync()) > 0;

            if (exists)
            {
                await using var delete = connection.CreateConfiguredCommand();
                delete.CommandText = @"
DELETE FROM ""ClientFavorites""
WHERE ""UserID"" = @UserID AND ""ClientID"" = @ClientID;";
                delete.Parameters.AddWithValue("@UserID", userId);
                delete.Parameters.AddWithValue("@ClientID", clientId);
                await delete.ExecuteNonQueryAsync();
            }
            else
            {
                await using var insert = connection.CreateConfiguredCommand();
                insert.CommandText = @"
INSERT INTO ""ClientFavorites"" (""UserID"", ""ClientID"")
VALUES (@UserID, @ClientID);";
                insert.Parameters.AddWithValue("@UserID", userId);
                insert.Parameters.AddWithValue("@ClientID", clientId);
                await insert.ExecuteNonQueryAsync();
            }
        }

        private async Task<List<ClientListViewModel>> GetClientsCoreAsync(ApplicationUser? user, string role, string? search = null, string scope = "all")
        {
            var (userId, isAdmin) = await ResolveUserScopeAsync(user, role);
            var isDataAnalyst = string.Equals(role, "DataAnalyst", StringComparison.OrdinalIgnoreCase);
            var normalizedSearch = string.IsNullOrWhiteSpace(search)
                ? null
                : $"%{search.Trim().ToLowerInvariant()}%";
            var normalizedScope = string.IsNullOrWhiteSpace(scope)
                ? "all"
                : scope.Trim().ToLowerInvariant();
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandTimeout = 60;
            command.CommandText = isAdmin
                ? @"
SELECT c.""ClientID"", c.""EngagementName"", c.""MaconomyNumber"", COALESCE(c.""Industry"", '') AS ""Industry"", c.""Status"", c.""CreatedAt"",
       COALESCE(u.""FirstName"" || ' ' || u.""LastName"", '') AS ""CreatedByName"",
       (SELECT COUNT(1) FROM ""UserClientAssignments"" a WHERE a.""ClientID"" = c.""ClientID"") AS ""AssignedUsersCount"",
       (SELECT COUNT(1) FROM ""ValidationRuns"" vr WHERE vr.""ClientID"" = c.""ClientID"") AS ""ValidationRunsCount"",
       (SELECT COUNT(1)
        FROM ""ValidationRuns"" vr
        WHERE vr.""ClientID"" = c.""ClientID""
          AND EXISTS (
              SELECT 1 FROM ""ReviewSignoffs"" rs
              WHERE rs.""RunID"" = vr.""RunID"" AND rs.""SignoffRole"" = 'DataAnalyst'
          )) AS ""SignedOffValidationRunsCount"",
       (SELECT vr.""RunID"" FROM ""ValidationRuns"" vr WHERE vr.""ClientID"" = c.""ClientID"" ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC LIMIT 1) AS ""LatestRunId"",
       (SELECT vr.""RuleNumber"" FROM ""ValidationRuns"" vr WHERE vr.""ClientID"" = c.""ClientID"" ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC LIMIT 1) AS ""LatestRunRuleNumber"",
       (SELECT vr.""RunID""
        FROM ""ValidationRuns"" vr
        WHERE vr.""ClientID"" = c.""ClientID""
          AND EXISTS (
              SELECT 1 FROM ""ReviewSignoffs"" rs
              WHERE rs.""RunID"" = vr.""RunID"" AND rs.""SignoffRole"" = 'DataAnalyst'
          )
        ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC LIMIT 1) AS ""LatestSignedOffRunId"",
       (SELECT vr.""RuleNumber""
        FROM ""ValidationRuns"" vr
        WHERE vr.""ClientID"" = c.""ClientID""
          AND EXISTS (
              SELECT 1 FROM ""ReviewSignoffs"" rs
              WHERE rs.""RunID"" = vr.""RunID"" AND rs.""SignoffRole"" = 'DataAnalyst'
          )
        ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC LIMIT 1) AS ""LatestSignedOffRunRuleNumber"",
       (SELECT vr.""Status"" FROM ""ValidationRuns"" vr WHERE vr.""ClientID"" = c.""ClientID"" ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC LIMIT 1) AS ""LastRunStatus"",
       (SELECT vr.""RunTimestamp"" FROM ""ValidationRuns"" vr WHERE vr.""ClientID"" = c.""ClientID"" ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC LIMIT 1) AS ""LastRunAt"",
       (SELECT vr.""Status""
        FROM ""ValidationRuns"" vr
        WHERE vr.""ClientID"" = c.""ClientID""
          AND EXISTS (
              SELECT 1 FROM ""ReviewSignoffs"" rs
              WHERE rs.""RunID"" = vr.""RunID"" AND rs.""SignoffRole"" = 'DataAnalyst'
          )
        ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC LIMIT 1) AS ""LatestSignedOffStatus"",
       (SELECT vr.""RunTimestamp""
        FROM ""ValidationRuns"" vr
        WHERE vr.""ClientID"" = c.""ClientID""
          AND EXISTS (
              SELECT 1 FROM ""ReviewSignoffs"" rs
              WHERE rs.""RunID"" = vr.""RunID"" AND rs.""SignoffRole"" = 'DataAnalyst'
          )
        ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC LIMIT 1) AS ""LatestSignedOffAt"",
       EXISTS (
           SELECT 1 FROM ""ClientFavorites"" f
           WHERE f.""ClientID"" = c.""ClientID"" AND f.""UserID"" = @UserID
       ) AS ""IsFavorite"",
       'Admin' AS ""CurrentUserEngagementRole""
FROM ""Clients"" c
LEFT JOIN ""AspNetUsers"" u ON u.""Id"" = c.""CreatedBy""
WHERE c.""FirmID"" = @FirmID"
                    + (normalizedSearch != null
                        ? @"
  AND (
        LOWER(c.""EngagementName"") LIKE @Search
        OR LOWER(c.""MaconomyNumber"") LIKE @Search
        OR LOWER(COALESCE(c.""Industry"", '')) LIKE @Search
        OR LOWER(COALESCE(c.""Status"", '')) LIKE @Search
        OR LOWER(COALESCE(c.""DirectorName"", '')) LIKE @Search
        OR LOWER(COALESCE(c.""ManagerName"", '')) LIKE @Search
        OR LOWER(COALESCE(u.""FirstName"", '') || ' ' || COALESCE(u.""LastName"", '')) LIKE @Search
    )"
                        : "") + @"
  AND (
        @Scope = 'all'
        OR (@Scope = 'active' AND c.""Status"" IN ('Approved', 'Active'))
        OR (@Scope = 'archived' AND c.""Status"" = 'Archived')
        OR (@Scope = 'favorites' AND EXISTS (
            SELECT 1 FROM ""ClientFavorites"" f
            WHERE f.""ClientID"" = c.""ClientID"" AND f.""UserID"" = @UserID
        ))
    )
ORDER BY c.""CreatedAt"" DESC, c.""ClientID"" DESC;"
                : @"
SELECT c.""ClientID"", c.""EngagementName"", c.""MaconomyNumber"", COALESCE(c.""Industry"", '') AS ""Industry"", c.""Status"", c.""CreatedAt"",
       COALESCE(u.""FirstName"" || ' ' || u.""LastName"", '') AS ""CreatedByName"",
       (SELECT COUNT(1) FROM ""UserClientAssignments"" a WHERE a.""ClientID"" = c.""ClientID"") AS ""AssignedUsersCount"",
       (SELECT COUNT(1) FROM ""ValidationRuns"" vr WHERE vr.""ClientID"" = c.""ClientID"") AS ""ValidationRunsCount"",
       (SELECT COUNT(1)
        FROM ""ValidationRuns"" vr
        WHERE vr.""ClientID"" = c.""ClientID""
          AND EXISTS (
              SELECT 1 FROM ""ReviewSignoffs"" rs
              WHERE rs.""RunID"" = vr.""RunID"" AND rs.""SignoffRole"" = 'DataAnalyst'
          )) AS ""SignedOffValidationRunsCount"",
       (SELECT vr.""RunID"" FROM ""ValidationRuns"" vr WHERE vr.""ClientID"" = c.""ClientID"" ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC LIMIT 1) AS ""LatestRunId"",
       (SELECT vr.""RuleNumber"" FROM ""ValidationRuns"" vr WHERE vr.""ClientID"" = c.""ClientID"" ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC LIMIT 1) AS ""LatestRunRuleNumber"",
       (SELECT vr.""RunID""
        FROM ""ValidationRuns"" vr
        WHERE vr.""ClientID"" = c.""ClientID""
          AND EXISTS (
              SELECT 1 FROM ""ReviewSignoffs"" rs
              WHERE rs.""RunID"" = vr.""RunID"" AND rs.""SignoffRole"" = 'DataAnalyst'
          )
        ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC LIMIT 1) AS ""LatestSignedOffRunId"",
       (SELECT vr.""RuleNumber""
        FROM ""ValidationRuns"" vr
        WHERE vr.""ClientID"" = c.""ClientID""
          AND EXISTS (
              SELECT 1 FROM ""ReviewSignoffs"" rs
              WHERE rs.""RunID"" = vr.""RunID"" AND rs.""SignoffRole"" = 'DataAnalyst'
          )
        ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC LIMIT 1) AS ""LatestSignedOffRunRuleNumber"",
       (SELECT vr.""Status"" FROM ""ValidationRuns"" vr WHERE vr.""ClientID"" = c.""ClientID"" ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC LIMIT 1) AS ""LastRunStatus"",
       (SELECT vr.""RunTimestamp"" FROM ""ValidationRuns"" vr WHERE vr.""ClientID"" = c.""ClientID"" ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC LIMIT 1) AS ""LastRunAt"",
       (SELECT vr.""Status""
        FROM ""ValidationRuns"" vr
        WHERE vr.""ClientID"" = c.""ClientID""
          AND EXISTS (
              SELECT 1 FROM ""ReviewSignoffs"" rs
              WHERE rs.""RunID"" = vr.""RunID"" AND rs.""SignoffRole"" = 'DataAnalyst'
          )
        ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC LIMIT 1) AS ""LatestSignedOffStatus"",
       (SELECT vr.""RunTimestamp""
        FROM ""ValidationRuns"" vr
        WHERE vr.""ClientID"" = c.""ClientID""
          AND EXISTS (
              SELECT 1 FROM ""ReviewSignoffs"" rs
              WHERE rs.""RunID"" = vr.""RunID"" AND rs.""SignoffRole"" = 'DataAnalyst'
          )
        ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC LIMIT 1) AS ""LatestSignedOffAt"",
       EXISTS (
           SELECT 1 FROM ""ClientFavorites"" f
           WHERE f.""ClientID"" = c.""ClientID"" AND f.""UserID"" = @UserID
       ) AS ""IsFavorite"",
       COALESCE((SELECT a.""EngagementRole"" FROM ""UserClientAssignments"" a WHERE a.""ClientID"" = c.""ClientID"" AND a.""UserID"" = @UserID LIMIT 1), '') AS ""CurrentUserEngagementRole""
FROM ""Clients"" c
LEFT JOIN ""AspNetUsers"" u ON u.""Id"" = c.""CreatedBy""
WHERE c.""FirmID"" = @FirmID
   AND (
       EXISTS (
           SELECT 1 FROM ""UserClientAssignments"" a
           WHERE a.""ClientID"" = c.""ClientID"" AND a.""UserID"" = @UserID
       )
   )"
                    + (normalizedSearch != null
                        ? @"
  AND (
        LOWER(c.""EngagementName"") LIKE @Search
        OR LOWER(c.""MaconomyNumber"") LIKE @Search
        OR LOWER(COALESCE(c.""Industry"", '')) LIKE @Search
        OR LOWER(COALESCE(c.""Status"", '')) LIKE @Search
        OR LOWER(COALESCE(c.""DirectorName"", '')) LIKE @Search
        OR LOWER(COALESCE(c.""ManagerName"", '')) LIKE @Search
        OR LOWER(COALESCE(u.""FirstName"", '') || ' ' || COALESCE(u.""LastName"", '')) LIKE @Search
    )"
                        : "") + @"
  AND (
        @Scope = 'all'
        OR (@Scope = 'active' AND c.""Status"" IN ('Approved', 'Active'))
        OR (@Scope = 'archived' AND c.""Status"" = 'Archived')
        OR (@Scope = 'favorites' AND EXISTS (
            SELECT 1 FROM ""ClientFavorites"" f
            WHERE f.""ClientID"" = c.""ClientID"" AND f.""UserID"" = @UserID
        ))
    )
ORDER BY c.""CreatedAt"" DESC, c.""ClientID"" DESC;";
            command.Parameters.AddWithValue("@UserID", userId);
            command.Parameters.AddWithValue("@FirmID", (object?)user?.FirmId ?? DBNull.Value);
            command.Parameters.AddWithValue("@Scope", normalizedScope);
            if (normalizedSearch != null)
                command.Parameters.AddWithValue("@Search", normalizedSearch);

            await using var reader = await command.ExecuteReaderAsync();
            var list = new List<ClientListViewModel>();
            while (await reader.ReadAsync())
            {
                var engagementName = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var maconomyNumber = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var industry = reader.IsDBNull(3) || string.IsNullOrWhiteSpace(reader.GetString(3))
                    ? "Unspecified"
                    : reader.GetString(3);
                var status = reader.IsDBNull(4) ? "" : reader.GetString(4);
                var createdAt = reader.IsDBNull(5) ? DateTime.UtcNow : reader.GetDateTime(5);
                var createdByName = reader.IsDBNull(6) ? "" : reader.GetString(6);
                var assignedUsersCount = reader.IsDBNull(7) ? 0 : reader.GetInt32(7);
                var validationRunsCount = reader.IsDBNull(8) ? 0 : reader.GetInt32(8);
                var signedOffValidationRunsCount = reader.IsDBNull(9) ? 0 : reader.GetInt32(9);
                var latestRunId = reader.IsDBNull(10) ? (int?)null : reader.GetInt32(10);
                var latestRunRuleNumber = reader.IsDBNull(11) ? (int?)null : reader.GetInt32(11);
                var latestSignedOffRunId = reader.IsDBNull(12) ? (int?)null : reader.GetInt32(12);
                var latestSignedOffRunRuleNumber = reader.IsDBNull(13) ? (int?)null : reader.GetInt32(13);
                var lastRunStatus = reader.IsDBNull(14) ? null : reader.GetString(14);
                DateTime? lastRunAt = reader.IsDBNull(15) ? (DateTime?)null : reader.GetDateTime(15);
                var latestSignedOffStatus = reader.IsDBNull(16) ? null : reader.GetString(16);
                DateTime? latestSignedOffAt = reader.IsDBNull(17) ? (DateTime?)null : reader.GetDateTime(17);
                var isFavorite = !reader.IsDBNull(18) && reader.GetBoolean(18);
                var currentUserEngagementRole = reader.IsDBNull(19) ? "" : reader.GetString(19);
                list.Add(new ClientListViewModel
                {
                    Id = reader.GetInt32(0),
                    Name = engagementName,
                    FiscalYear = maconomyNumber,
                    EngagementName = engagementName,
                    MaconomyNumber = maconomyNumber,
                    Industry = industry,
                    Status = status,
                    CreatedAt = createdAt,
                    CreatedByName = createdByName,
                    AssignedUsersCount = assignedUsersCount,
                    ValidationRunsCount = validationRunsCount,
                    LatestRunId = latestRunId,
                    LatestRunRuleNumber = latestRunRuleNumber,
                    LatestSignedOffRunId = latestSignedOffRunId,
                    LatestSignedOffRunRuleNumber = latestSignedOffRunRuleNumber,
                    LastRunStatus = lastRunStatus,
                    LastRunAt = lastRunAt,
                    LatestSignedOffStatus = latestSignedOffStatus,
                    LatestSignedOffAt = latestSignedOffAt,
                    CurrentUserEngagementRole = currentUserEngagementRole
                    ,
                    IsFavorite = isFavorite
                });
            }

            return list;
        }

        private async Task<List<ClientUserRow>> GetAssignedUsersAsync(NpgsqlConnection connection, int clientId)
        {
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
SELECT a.""AssignmentID"", u.""Id"", COALESCE(u.""FirstName"",'') || CASE WHEN COALESCE(u.""LastName"",'') = '' THEN '' ELSE ' ' || u.""LastName"" END AS ""FullName"",
       COALESCE(u.""Email"",'') AS ""Email"", COALESCE(a.""EngagementRole"",'DataAnalyst') AS ""EngagementRole"", COALESCE(a.""AssignedBy"",'') AS ""AssignedBy"", a.""AssignedAt""
FROM ""UserClientAssignments"" a
INNER JOIN ""AspNetUsers"" u ON u.""Id"" = a.""UserID""
WHERE a.""ClientID"" = @ClientID
ORDER BY u.""FirstName"", u.""LastName"";";
            command.Parameters.AddWithValue("@ClientID", clientId);
            await using var reader = await command.ExecuteReaderAsync();
            var list = new List<ClientUserRow>();
            while (await reader.ReadAsync())
            {
                list.Add(new ClientUserRow
                {
                    ClientUserId = reader.GetInt32(0),
                    UserId = reader.GetString(1),
                    FullName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Email = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    EngagementRole = reader.IsDBNull(4) ? "DataAnalyst" : reader.GetString(4),
                    AssignedAt = reader.IsDBNull(6) ? DateTime.UtcNow : reader.GetDateTime(6),
                    IsActive = true
                });
            }
            return list;
        }

        private async Task<List<ValidationRunRow>> GetValidationRunsForClientAsync(NpgsqlConnection connection, int clientId)
        {
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
WITH SignoffSummary AS
(
    SELECT
        rs.""RunID"",
        COUNT(1) AS ""SignoffCount"",
        bool_or(COALESCE(rs.""SignoffRole"", '') = 'DataAnalyst') AS ""HasDataAnalystSignoff"",
        bool_or(COALESCE(rs.""SignoffRole"", '') = 'Manager') AS ""HasManagerSignoff"",
        bool_or(COALESCE(rs.""SignoffRole"", '') = 'Director') AS ""HasDirectorSignoff""
    FROM ""ReviewSignoffs"" rs
    GROUP BY rs.""RunID""
)
SELECT vr.""RunID"", vr.""RuleNumber"", vr.""RuleName"", vr.""Status"", vr.""TotalRecords"", vr.""PassCount"", vr.""FailCount"", vr.""ExceptionRate"", vr.""RunTimestamp"",
       COALESCE(NULLIF(vr.""RunByUserName"",''), TRIM(COALESCE(u.""FirstName"",'') || ' ' || COALESCE(u.""LastName"",''))) AS ""RunByUserName"",
       COALESCE(vr.""LastEditedByUserName"",'') AS ""LastEditedByUserName"",
       vr.""LastEditedAt"",
       vr.""IsCurrent"",
       COALESCE(ss.""SignoffCount"", 0) AS ""SignoffCount"",
       COALESCE(ss.""HasDataAnalystSignoff"", false) AS ""HasDataAnalystSignoff"",
       COALESCE(ss.""HasManagerSignoff"", false) AS ""HasManagerSignoff"",
       COALESCE(ss.""HasDirectorSignoff"", false) AS ""HasDirectorSignoff""
FROM ""ValidationRuns"" vr
LEFT JOIN ""AspNetUsers"" u ON u.""Id"" = vr.""UserID""
LEFT JOIN SignoffSummary ss ON ss.""RunID"" = vr.""RunID""
WHERE vr.""ClientID"" = @ClientID
ORDER BY vr.""RunTimestamp"" DESC, vr.""RunID"" DESC;";
            command.Parameters.AddWithValue("@ClientID", clientId);
            await using var reader = await command.ExecuteReaderAsync();
            var list = new List<ValidationRunRow>();
            while (await reader.ReadAsync())
            {
                list.Add(new ValidationRunRow
                {
                    Id = reader.GetInt32(0),
                    RuleNumber = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    RuleName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Status = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    TotalValidated = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    PassCount = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    FailCount = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    ExceptionRate = reader.IsDBNull(7) ? 0 : reader.GetDecimal(7),
                    RunAt = reader.IsDBNull(8) ? DateTime.UtcNow : reader.GetDateTime(8),
                    RunByUserName = reader.IsDBNull(9) ? "" : reader.GetString(9),
                    LastEditedByUserName = reader.IsDBNull(10) ? null : reader.GetString(10),
                    LastEditedAt = reader.IsDBNull(11) ? null : reader.GetDateTime(11),
                    IsCurrent = !reader.IsDBNull(12) && reader.GetBoolean(12),
                    SignoffCount = reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
                    HasDataAnalystSignoff = !reader.IsDBNull(14) && reader.GetBoolean(14),
                    HasManagerSignoff = !reader.IsDBNull(15) && reader.GetBoolean(15),
                    HasDirectorSignoff = !reader.IsDBNull(16) && reader.GetBoolean(16)
                });
            }
            return list;
        }

        private async Task<List<MessageItemViewModel>> GetThreadMessagesAsync(NpgsqlConnection connection, int threadId, string currentUserId)
        {
            var attachmentsByMessage = await GetMessageAttachmentsAsync(connection, threadId);
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
SELECT tm.""MessageID"",
       tm.""ThreadID"",
       TRIM(COALESCE(u.""FirstName"",'') || ' ' || COALESCE(u.""LastName"",'')) AS ""SenderName"",
       COALESCE(u.""Email"",'') AS ""SenderEmail"",
       tm.""Body"",
       tm.""SentAt"",
       (tm.""SenderUserID"" = @CurrentUserID) AS ""IsCurrentUser"",
       EXISTS (
            SELECT 1
            FROM ""ThreadMessageRecipients"" r
            WHERE r.""MessageID"" = tm.""MessageID""
              AND r.""UserID"" = @CurrentUserID
              AND r.""IsRead""
       ) AS ""IsRead"",
       (
            SELECT r.""ReadAt""
            FROM ""ThreadMessageRecipients"" r
            WHERE r.""MessageID"" = tm.""MessageID""
              AND r.""UserID"" = @CurrentUserID
            LIMIT 1
       ) AS ""ReadAt""
       ,
       (
            SELECT COUNT(1)
            FROM ""ThreadMessageRecipients"" r
            WHERE r.""MessageID"" = tm.""MessageID""
       ) AS ""RecipientCount"",
       (
            SELECT COUNT(1)
            FROM ""ThreadMessageRecipients"" r
            WHERE r.""MessageID"" = tm.""MessageID""
              AND r.""IsRead""
       ) AS ""ReadCount"",
       (
            SELECT MIN(r.""ReadAt"")
            FROM ""ThreadMessageRecipients"" r
            WHERE r.""MessageID"" = tm.""MessageID""
              AND r.""IsRead""
       ) AS ""FirstReadAt"",
       (
            SELECT MAX(r.""ReadAt"")
            FROM ""ThreadMessageRecipients"" r
            WHERE r.""MessageID"" = tm.""MessageID""
              AND r.""IsRead""
       ) AS ""LastReadAt"",
       (tm.""SenderUserID"" = @CurrentUserID AND COALESCE(tm.""IsDeleted"", false) = false) AS ""CanEdit"",
       (tm.""SenderUserID"" = @CurrentUserID AND COALESCE(tm.""IsDeleted"", false) = false) AS ""CanDelete"",
       (tm.""EditedAt"" IS NOT NULL) AS ""IsEdited"",
       tm.""EditedAt"",
       COALESCE(tm.""IsDeleted"", false) AS ""IsDeleted"",
       tm.""DeletedAt""
FROM ""ThreadMessages"" tm
INNER JOIN ""AspNetUsers"" u ON u.""Id"" = tm.""SenderUserID""
WHERE tm.""ThreadID"" = @ThreadID
ORDER BY tm.""SentAt"" ASC, tm.""MessageID"" ASC;";
            command.Parameters.AddWithValue("@ThreadID", threadId);
            command.Parameters.AddWithValue("@CurrentUserID", currentUserId);
            await using var reader = await command.ExecuteReaderAsync();
            var messages = new List<MessageItemViewModel>();
            while (await reader.ReadAsync())
            {
                messages.Add(new MessageItemViewModel
                {
                    MessageId = reader.GetInt32(0),
                    ThreadId = reader.GetInt32(1),
                    SenderName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    SenderEmail = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Body = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    SentAt = reader.IsDBNull(5) ? DateTime.UtcNow : reader.GetDateTime(5),
                    IsCurrentUser = !reader.IsDBNull(6) && reader.GetBoolean(6),
                    IsRead = !reader.IsDBNull(7) && reader.GetBoolean(7),
                    ReadAt = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                    RecipientCount = reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetInt32(9) : 0,
                    ReadCount = reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetInt32(10) : 0,
                    FirstReadAt = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetDateTime(11) : null,
                    LastReadAt = reader.FieldCount > 12 && !reader.IsDBNull(12) ? reader.GetDateTime(12) : null,
                    CanEdit = reader.FieldCount > 13 && !reader.IsDBNull(13) && reader.GetBoolean(13),
                    CanDelete = reader.FieldCount > 14 && !reader.IsDBNull(14) && reader.GetBoolean(14),
                    IsEdited = reader.FieldCount > 15 && !reader.IsDBNull(15) && reader.GetBoolean(15),
                    EditedAt = reader.FieldCount > 16 && !reader.IsDBNull(16) ? reader.GetDateTime(16) : null,
                    IsDeleted = reader.FieldCount > 17 && !reader.IsDBNull(17) && reader.GetBoolean(17),
                    DeletedAt = reader.FieldCount > 18 && !reader.IsDBNull(18) ? reader.GetDateTime(18) : null,
                    Attachments = attachmentsByMessage.TryGetValue(reader.GetInt32(0), out var messageAttachments)
                        ? messageAttachments
                        : new List<MessageAttachmentViewModel>()
                });
            }

            return messages;
        }

        private async Task<List<string>> GetThreadParticipantIdsAsync(NpgsqlConnection connection, int threadId)
        {
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
SELECT DISTINCT participant.""UserID""
FROM (
    SELECT tm.""SenderUserID"" AS ""UserID""
    FROM ""ThreadMessages"" tm
    WHERE tm.""ThreadID"" = @ThreadID
    UNION
    SELECT r.""UserID""
    FROM ""ThreadMessages"" tm
    INNER JOIN ""ThreadMessageRecipients"" r ON r.""MessageID"" = tm.""MessageID""
    WHERE tm.""ThreadID"" = @ThreadID
) participant;";
            command.Parameters.AddWithValue("@ThreadID", threadId);
            await using var reader = await command.ExecuteReaderAsync();
            var list = new List<string>();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0))
                    list.Add(reader.GetString(0));
            }

            return list;
        }

        private async Task<Dictionary<int, List<MessageAttachmentViewModel>>> GetMessageAttachmentsAsync(NpgsqlConnection connection, int threadId)
        {
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
SELECT att.""AttachmentID"",
       att.""MessageID"",
       att.""FileName"",
       att.""FilePath"",
       att.""ContentType"",
       att.""FileSize"",
       att.""AttachmentKind""
FROM ""ThreadMessageAttachments"" att
INNER JOIN ""ThreadMessages"" tm ON tm.""MessageID"" = att.""MessageID""
WHERE tm.""ThreadID"" = @ThreadID
ORDER BY att.""AttachmentID"" ASC;";
            command.Parameters.AddWithValue("@ThreadID", threadId);

            await using var reader = await command.ExecuteReaderAsync();
            var attachments = new Dictionary<int, List<MessageAttachmentViewModel>>();
            while (await reader.ReadAsync())
            {
                var messageId = reader.GetInt32(1);
                if (!attachments.TryGetValue(messageId, out var list))
                {
                    list = new List<MessageAttachmentViewModel>();
                    attachments[messageId] = list;
                }

                list.Add(new MessageAttachmentViewModel
                {
                    AttachmentId = reader.GetInt32(0),
                    MessageId = messageId,
                    FileName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    FilePath = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    ContentType = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    FileSize = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                    AttachmentKind = reader.IsDBNull(6) ? "file" : reader.GetString(6)
                });
            }

            return attachments;
        }

        private async Task<bool> CanAccessThreadAsync(NpgsqlConnection connection, int threadId, string userId)
        {
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
SELECT COUNT(1)
FROM ""MessageThreads"" th
WHERE th.""ThreadID"" = @ThreadID
  AND NOT EXISTS (
      SELECT 1
      FROM ""ThreadUserStates"" tus
      WHERE tus.""ThreadID"" = th.""ThreadID""
        AND tus.""UserID"" = @UserID
        AND tus.""IsDeleted""
  )
  AND (
      th.""CreatedByUserID"" = @UserID
      OR EXISTS (
          SELECT 1
          FROM ""ThreadMessages"" tm
          INNER JOIN ""ThreadMessageRecipients"" r ON r.""MessageID"" = tm.""MessageID""
          WHERE tm.""ThreadID"" = th.""ThreadID""
            AND r.""UserID"" = @UserID
      )
      OR EXISTS (
          SELECT 1
          FROM ""ThreadMessages"" tm
          WHERE tm.""ThreadID"" = th.""ThreadID""
            AND tm.""SenderUserID"" = @UserID
      )
  );";
            command.Parameters.AddWithValue("@ThreadID", threadId);
            command.Parameters.AddWithValue("@UserID", userId);
            return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
        }

        private async Task<bool> CanAccessMessageAsync(NpgsqlConnection connection, int messageId, int threadId, string userId)
        {
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
SELECT COUNT(1)
FROM ""ThreadMessages"" tm
WHERE tm.""MessageID"" = @MessageID
  AND tm.""ThreadID"" = @ThreadID
  AND COALESCE(tm.""IsDeleted"", false) = false
  AND tm.""SenderUserID"" = @UserID;";
            command.Parameters.AddWithValue("@MessageID", messageId);
            command.Parameters.AddWithValue("@ThreadID", threadId);
            command.Parameters.AddWithValue("@UserID", userId);
            return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
        }

        private async Task RestoreThreadForUsersAsync(NpgsqlConnection connection, int threadId, IEnumerable<string> userIds)
        {
            foreach (var participantId in userIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct())
            {
                await using var command = connection.CreateConfiguredCommand();
                command.CommandText = @"
INSERT INTO ""ThreadUserStates"" (""ThreadID"", ""UserID"", ""IsDeleted"", ""DeletedAt"")
VALUES (@ThreadID, @UserID, false, NULL)
ON CONFLICT (""ThreadID"", ""UserID"") DO UPDATE
SET ""IsDeleted"" = EXCLUDED.""IsDeleted"",
    ""DeletedAt"" = EXCLUDED.""DeletedAt"";";
                command.Parameters.AddWithValue("@ThreadID", threadId);
                command.Parameters.AddWithValue("@UserID", participantId);
                await command.ExecuteNonQueryAsync();
            }
        }

        private async Task<int> InsertThreadMessageAsync(
            NpgsqlConnection connection,
            int threadId,
            string senderUserId,
            string body,
            int? replyToMessageId,
            IEnumerable<string> recipientUserIds,
            DateTime sentAt,
            IEnumerable<MessageAttachmentInput>? attachments = null)
        {
            var previousHash = await GetLatestHashAsync(connection, "ThreadMessages");

            await using var insert = connection.CreateConfiguredCommand();
            insert.CommandText = @"
INSERT INTO ""ThreadMessages"" (""ThreadID"", ""SenderUserID"", ""Body"", ""ReplyToMessageID"", ""SentAt"", ""PreviousHash"", ""RecordHash"", ""EditedAt"", ""IsDeleted"", ""DeletedAt"")
VALUES (@ThreadID, @SenderUserID, @Body, @ReplyToMessageID, @SentAt, @PreviousHash, NULL, NULL, false, NULL)
RETURNING ""MessageID"";";
            insert.Parameters.AddWithValue("@ThreadID", threadId);
            insert.Parameters.AddWithValue("@SenderUserID", senderUserId);
            insert.Parameters.AddWithValue("@Body", body);
            insert.Parameters.AddWithValue("@ReplyToMessageID", (object?)replyToMessageId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@SentAt", sentAt);
            insert.Parameters.AddWithValue("@PreviousHash", (object?)previousHash ?? DBNull.Value);
            var messageId = Convert.ToInt32(await insert.ExecuteScalarAsync());

            var recordHash = ComputeHash($@"ThreadMessage|{messageId}|{threadId}|{senderUserId}|{body}|{sentAt:o}|{replyToMessageId}|{previousHash}");
            await using (var update = connection.CreateConfiguredCommand())
            {
                update.CommandText = "UPDATE \"ThreadMessages\" SET \"RecordHash\" = @RecordHash WHERE \"MessageID\" = @MessageID;";
                update.Parameters.AddWithValue("@RecordHash", recordHash);
                update.Parameters.AddWithValue("@MessageID", messageId);
                await update.ExecuteNonQueryAsync();
            }

            foreach (var recipientId in recipientUserIds.Distinct())
            {
                await using var recipient = connection.CreateConfiguredCommand();
                recipient.CommandText = @"
INSERT INTO ""ThreadMessageRecipients"" (""MessageID"", ""UserID"", ""IsRead"", ""ReadAt"")
VALUES (@MessageID, @UserID, false, NULL);";
                recipient.Parameters.AddWithValue("@MessageID", messageId);
                recipient.Parameters.AddWithValue("@UserID", recipientId);
                await recipient.ExecuteNonQueryAsync();
            }

            if (attachments != null)
            {
                foreach (var attachment in attachments.Where(item => item != null))
                {
                    await using var attachmentCommand = connection.CreateConfiguredCommand();
                    attachmentCommand.CommandText = @"
INSERT INTO ""ThreadMessageAttachments"" (""MessageID"", ""FileName"", ""FilePath"", ""ContentType"", ""FileSize"", ""AttachmentKind"", ""CreatedAt"")
VALUES (@MessageID, @FileName, @FilePath, @ContentType, @FileSize, @AttachmentKind, @CreatedAt);";
                    attachmentCommand.Parameters.AddWithValue("@MessageID", messageId);
                    attachmentCommand.Parameters.AddWithValue("@FileName", attachment.FileName);
                    attachmentCommand.Parameters.AddWithValue("@FilePath", attachment.FilePath);
                    attachmentCommand.Parameters.AddWithValue("@ContentType", attachment.ContentType);
                    attachmentCommand.Parameters.AddWithValue("@FileSize", attachment.FileSize);
                    attachmentCommand.Parameters.AddWithValue("@AttachmentKind", attachment.AttachmentKind);
                    attachmentCommand.Parameters.AddWithValue("@CreatedAt", sentAt);
                    await attachmentCommand.ExecuteNonQueryAsync();
                }
            }

            return messageId;
        }

        // Callers pass the actor's real Identity id directly (e.g. admin.Id) as userId now that
        // there is no int mirror id to translate. userName historically carried the actor's
        // email as a fallback lookup path — still needed for callers that only know the email.
        private async Task<string?> ResolveAuditUserIdAsync(NpgsqlConnection connection, string? userId, string? userName)
        {
            if (!string.IsNullOrWhiteSpace(userId))
                return userId;

            if (!string.IsNullOrWhiteSpace(userName))
                return await GetUserIdByEmailAsync(connection, userName);

            return null;
        }

        private async Task<string?> GetLatestHashAsync(NpgsqlConnection connection, string tableName)
        {
            var sql = tableName switch
            {
                "AuditLog" => "SELECT \"RecordHash\" FROM \"AuditLog\" WHERE \"RecordHash\" IS NOT NULL ORDER BY \"LogID\" DESC LIMIT 1;",
                "ValidationRuns" => "SELECT \"RecordHash\" FROM \"ValidationRuns\" WHERE \"RecordHash\" IS NOT NULL ORDER BY \"RunID\" DESC LIMIT 1;",
                "MessageThreads" => "SELECT \"RecordHash\" FROM \"MessageThreads\" WHERE \"RecordHash\" IS NOT NULL ORDER BY \"ThreadID\" DESC LIMIT 1;",
                "ThreadMessages" => "SELECT \"RecordHash\" FROM \"ThreadMessages\" WHERE \"RecordHash\" IS NOT NULL ORDER BY \"MessageID\" DESC LIMIT 1;",
                _ => $"SELECT \"RecordHash\" FROM \"{tableName}\" WHERE \"RecordHash\" IS NOT NULL LIMIT 1;"
            };

            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = sql;
            var value = await command.ExecuteScalarAsync();
            return value == null || value == DBNull.Value ? null : Convert.ToString(value);
        }

        private static string ComputeHash(string input)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes);
        }

        private async Task EnsureClientNotArchivedAsync(NpgsqlConnection connection, int clientId)
        {
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
SELECT ""Status""
FROM ""Clients""
WHERE ""ClientID"" = @ClientID
LIMIT 1;";
            command.Parameters.AddWithValue("@ClientID", clientId);
            var status = Convert.ToString(await command.ExecuteScalarAsync());
            if (string.Equals(status, "Archived", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Archived engagements are read-only.");
        }

        private async Task EnsureAssignmentClientNotArchivedAsync(NpgsqlConnection connection, int clientUserId)
        {
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
SELECT c.""Status""
FROM ""UserClientAssignments"" a
INNER JOIN ""Clients"" c ON c.""ClientID"" = a.""ClientID""
WHERE a.""AssignmentID"" = @AssignmentID
LIMIT 1;";
            command.Parameters.AddWithValue("@AssignmentID", clientUserId);
            var status = Convert.ToString(await command.ExecuteScalarAsync());
            if (string.Equals(status, "Archived", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Archived engagements are read-only.");
        }

        private static string FormatMissingSignoffMessage(IReadOnlyList<string> missing)
        {
            if (missing.Count == 0)
                return "review signoffs";

            if (missing.Count == 1)
                return $"{missing[0]} signoff";

            if (missing.Count == 2)
                return $"{missing[0]} and {missing[1]} signoffs";

            return $"{string.Join(", ", missing.Take(missing.Count - 1))}, and {missing[^1]} signoffs";
        }

        private static string BuildArchiveEligibilityMessage(IReadOnlyList<ValidationRunRow> incompleteCurrentRuns)
        {
            if (incompleteCurrentRuns.Count == 0)
                return "All current results must be reviewed and completed before archiving.";

            var labels = incompleteCurrentRuns
                .Select(run => $"Rule {run.RuleNumber} ({run.Status})")
                .ToList();

            return $"Archive is locked until every current result shows Reviewed and Completed. Outstanding rules: {string.Join(", ", labels)}.";
        }

        private Task<(string UserId, bool IsAdmin)> ResolveUserScopeAsync(ApplicationUser? user, string role)
        {
            if (user == null)
                return Task.FromResult(("", false));

            return Task.FromResult((user.Id, string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)));
        }

        // No more mirror table to fall back to — the current user's Identity id is already known.
        private Task<string> ResolveExistingUserScopeIdAsync(ApplicationUser user, string role)
        {
            return Task.FromResult(user.Id);
        }

        private async Task<string?> GetUserIdByEmailAsync(NpgsqlConnection connection, string email)
        {
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = "SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = @Email LIMIT 1;";
            command.Parameters.AddWithValue("@Email", email.ToUpperInvariant());
            var value = await command.ExecuteScalarAsync();
            return value == null || value == DBNull.Value ? null : Convert.ToString(value);
        }

        private async Task<string?> ResolveUserIdByEmailAsync(string email)
        {
            await using var connection = await OpenConnectionAsync();
            return await GetUserIdByEmailAsync(connection, email);
        }

        private async Task<NpgsqlConnection> OpenConnectionAsync()
        {
            var connectionString = HemisAudit.Data.PostgresConnectionStringHelper.WithResiliencyDefaults(
                _configuration.GetConnectionString("Postgres")
                    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured."));

            // This path is raw ADO.NET (not EF), so it doesn't get EF's EnableRetryOnFailure.
            // Retry here guards against Supabase's pooler having closed a pooled connection
            // server-side between the time Npgsql handed it out and the time we open it.
            const int maxAttempts = 3;
            for (var attempt = 1; ; attempt++)
            {
                var connection = new NpgsqlConnection(connectionString);
                try
                {
                    await connection.OpenAsync();
                    return connection;
                }
                catch (Exception ex) when (attempt < maxAttempts && IsTransientConnectionFailure(ex))
                {
                    await connection.DisposeAsync();
                    await Task.Delay(200 * attempt);
                }
            }
        }

        private static bool IsTransientConnectionFailure(Exception ex) =>
            ex is NpgsqlException
            || ex is System.Net.Sockets.SocketException
            || ex is System.IO.IOException
            || ex is TimeoutException;

        private async Task<(bool CanAccess, string CurrentUserEngagementRole)> GetClientResultsAccessAsync(int clientId, ApplicationUser? user, string role)
        {
            if (user == null)
                return (false, "");

            var isAdmin = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);
            var userId = "";

            if (!isAdmin)
                userId = await ResolveExistingUserScopeIdAsync(user, role);

            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandTimeout = 30;
            command.CommandText = isAdmin
                ? @"
SELECT
    EXISTS (
        SELECT 1
        FROM ""Clients"" c
        WHERE c.""ClientID"" = @ClientID
          AND c.""Status"" IN ('Approved', 'Active', 'Archived')
          AND c.""FirmID"" = @FirmID
    ) AS ""CanAccess"",
    'Admin' AS ""CurrentUserEngagementRole"";"
                : @"
SELECT
    (c.""ClientID"" IS NOT NULL AND a.""UserID"" IS NOT NULL) AS ""CanAccess"",
    COALESCE(a.""EngagementRole"", '') AS ""CurrentUserEngagementRole""
FROM (SELECT @ClientID AS ""ClientID"") seed
LEFT JOIN ""Clients"" c
    ON c.""ClientID"" = seed.""ClientID""
   AND c.""Status"" IN ('Approved', 'Active', 'Archived')
   AND c.""FirmID"" = @FirmID
LEFT JOIN ""UserClientAssignments"" a
    ON a.""ClientID"" = seed.""ClientID""
   AND a.""UserID"" = @UserID;";
            command.Parameters.AddWithValue("@ClientID", clientId);
            command.Parameters.AddWithValue("@FirmID", (object?)user.FirmId ?? DBNull.Value);
            if (!isAdmin)
                command.Parameters.AddWithValue("@UserID", userId);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return (false, "");

            return (!reader.IsDBNull(0) && reader.GetBoolean(0), reader.IsDBNull(1) ? "" : reader.GetString(1));
        }

        public async Task<HashSet<int>> GetEngagementScopeAsync(int clientId)
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
SELECT ""RuleNumber""
FROM ""EngagementRuleScope""
WHERE ""ClientID"" = @ClientID;";
            command.Parameters.AddWithValue("@ClientID", clientId);
            var result = new HashSet<int>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                result.Add(reader.GetInt32(0));
            return result;
        }

        public async Task SaveEngagementScopeAsync(int clientId, IEnumerable<int> ruleNumbers, ApplicationUser user)
        {
            var userId = await EnsureUserMirrorAsync(user, "DataAnalyst");
            var userName = $"{user.FirstName} {user.LastName}".Trim();
            var selected = ruleNumbers.Distinct().ToList();

            await using var connection = await OpenConnectionAsync();

            // Remove rules no longer selected
            await using var del = connection.CreateConfiguredCommand();
            del.CommandText = "DELETE FROM \"EngagementRuleScope\" WHERE \"ClientID\" = @ClientID;";
            del.Parameters.AddWithValue("@ClientID", clientId);
            await del.ExecuteNonQueryAsync();

            // Insert newly selected rules
            foreach (var ruleNumber in selected)
            {
                await using var ins = connection.CreateConfiguredCommand();
                ins.CommandText = @"
INSERT INTO ""EngagementRuleScope"" (""ClientID"", ""RuleNumber"", ""AddedAt"", ""AddedByUserID"", ""AddedByUserName"")
VALUES (@ClientID, @RuleNumber, now(), @UserID, @UserName);";
                ins.Parameters.AddWithValue("@ClientID", clientId);
                ins.Parameters.AddWithValue("@RuleNumber", ruleNumber);
                ins.Parameters.AddWithValue("@UserID", userId);
                ins.Parameters.AddWithValue("@UserName", userName);
                await ins.ExecuteNonQueryAsync();
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════
        // Shared rule-engine persistence. Every Rule*Service.cs used to duplicate its own copy
        // of these methods against Microsoft.Data.SqlClient / a "SystemDatabase" SQL Server
        // config that no longer exists in this app's architecture (see appsettings.json —
        // that section is a pre-Supabase relic). These are the Postgres-native, text-UserID
        // equivalents; each rule now calls into these instead of maintaining its own copy.
        // The live SQL Server connection to the *audited institution* is untouched — this only
        // covers where/how a rule's results get saved.
        // ════════════════════════════════════════════════════════════════════════════════════

        // Direct (int clientId, text userId) -> EngagementRole lookup, matching exactly what each
        // rule's own private GetEngagementRoleAsync did against dbo.UserClientAssignments — no
        // Admin short-circuit or other business logic layered in, to avoid changing rule behavior
        // while porting the connection/dialect. Use GetEngagementRoleAsync(ApplicationUser, role)
        // instead where that fuller semantic (used by the rest of the app) is actually wanted.
        public async Task<string?> GetRawEngagementRoleAsync(int clientId, string userId)
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
SELECT ""EngagementRole"" FROM ""UserClientAssignments""
WHERE ""ClientID"" = @ClientID AND ""UserID"" = @UserID
LIMIT 1;";
            command.Parameters.AddWithValue("@ClientID", clientId);
            command.Parameters.AddWithValue("@UserID", userId);
            var value = await command.ExecuteScalarAsync();
            return value == null || value == DBNull.Value ? null : Convert.ToString(value);
        }

        public async Task EnsureClientNotArchivedAsync(int clientId)
        {
            await using var connection = await OpenConnectionAsync();
            await EnsureClientNotArchivedAsync(connection, clientId);
        }

        public async Task<int?> GetClientIdForRunAsync(int runId)
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = "SELECT \"ClientID\" FROM \"ValidationRuns\" WHERE \"RunID\" = @RunID LIMIT 1;";
            command.Parameters.AddWithValue("@RunID", runId);
            var value = await command.ExecuteScalarAsync();
            return value == null || value == DBNull.Value ? null : Convert.ToInt32(value);
        }

        public async Task<RuleValidationRunRow?> GetCurrentRuleRunAsync(int clientId, int ruleNumber)
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
SELECT ""RunID"", ""ClientID"", ""IsCurrent"",
       COALESCE(""HemisServer"", ''), COALESCE(""AuditDatabase"", ''),
       COALESCE(""StudTable"", ''), COALESCE(""DeceasedTable"", ''),
       COALESCE(""StudColumn"", ''), COALESCE(""DeceasedColumn"", ''),
       COALESCE(""Status"", ''), ""LastEditedByUserName"", ""LastEditedAt"", ""ResultsJSON"", ""ExceptionsJSON""
FROM ""ValidationRuns""
WHERE ""ClientID"" = @ClientID AND ""RuleNumber"" = @RuleNumber AND ""IsCurrent"" = true
ORDER BY ""RunTimestamp"" DESC, ""RunID"" DESC
LIMIT 1;";
            command.Parameters.AddWithValue("@ClientID", clientId);
            command.Parameters.AddWithValue("@RuleNumber", ruleNumber);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            var row = ReadRuleValidationRunRow(reader);
            row.RuleNumber = ruleNumber;
            return row;
        }

        public async Task<RuleValidationRunRow?> GetRuleRunByIdAsync(int runId, int ruleNumber)
        {
            var row = await GetRuleRunByIdAsync(runId);
            return row != null && row.RuleNumber == ruleNumber ? row : null;
        }

        public async Task<RuleValidationRunRow?> GetRuleRunByIdAsync(int runId)
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
SELECT vr.""RunID"", vr.""ClientID"", vr.""IsCurrent"",
       COALESCE(vr.""HemisServer"", ''), COALESCE(vr.""AuditDatabase"", ''),
       COALESCE(vr.""StudTable"", ''), COALESCE(vr.""DeceasedTable"", ''),
       COALESCE(vr.""StudColumn"", ''), COALESCE(vr.""DeceasedColumn"", ''),
       COALESCE(vr.""Status"", ''), vr.""LastEditedByUserName"", vr.""LastEditedAt"", vr.""ResultsJSON"", vr.""ExceptionsJSON"",
       c.""EngagementName"", c.""MaconomyNumber"", vr.""RuleNumber""
FROM ""ValidationRuns"" vr
INNER JOIN ""Clients"" c ON c.""ClientID"" = vr.""ClientID""
WHERE vr.""RunID"" = @RunID;";
            command.Parameters.AddWithValue("@RunID", runId);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            var row = ReadRuleValidationRunRow(reader);
            row.EngagementName = reader.IsDBNull(14) ? "" : reader.GetString(14);
            row.MaconomyNumber = reader.IsDBNull(15) ? "" : reader.GetString(15);
            row.RuleNumber = reader.GetInt32(16);
            return row;
        }

        private static RuleValidationRunRow ReadRuleValidationRunRow(NpgsqlDataReader reader) => new()
        {
            RunId = reader.GetInt32(0),
            ClientId = reader.GetInt32(1),
            IsCurrent = !reader.IsDBNull(2) && reader.GetBoolean(2),
            HemisServer = reader.GetString(3),
            AuditDatabase = reader.GetString(4),
            StudTable = reader.GetString(5),
            DeceasedTable = reader.GetString(6),
            StudColumn = reader.GetString(7),
            DeceasedColumn = reader.GetString(8),
            Status = reader.GetString(9),
            LastEditedByUserName = reader.IsDBNull(10) ? null : reader.GetString(10),
            LastEditedAt = reader.IsDBNull(11) ? null : reader.GetDateTime(11),
            ResultsJSON = reader.IsDBNull(12) ? null : reader.GetString(12),
            ExceptionsJSON = reader.IsDBNull(13) ? null : reader.GetString(13)
        };

        public async Task<string?> GetValidationRecordHashAsync(int runId)
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = "SELECT \"RecordHash\" FROM \"ValidationRuns\" WHERE \"RunID\" = @RunID LIMIT 1;";
            command.Parameters.AddWithValue("@RunID", runId);
            var value = await command.ExecuteScalarAsync();
            return value == null || value == DBNull.Value ? null : Convert.ToString(value);
        }

        public async Task<string?> GetLatestValidationRunHashAsync(int clientId, int ruleNumber)
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
SELECT ""RecordHash"" FROM ""ValidationRuns""
WHERE ""ClientID"" = @ClientID AND ""RuleNumber"" = @RuleNumber AND ""RecordHash"" IS NOT NULL
ORDER BY ""RunTimestamp"" DESC, ""RunID"" DESC
LIMIT 1;";
            command.Parameters.AddWithValue("@ClientID", clientId);
            command.Parameters.AddWithValue("@RuleNumber", ruleNumber);
            var value = await command.ExecuteScalarAsync();
            return value == null || value == DBNull.Value ? null : Convert.ToString(value);
        }

        public async Task MarkPreviousRuleRunsHistoricalAsync(int clientId, int ruleNumber)
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
UPDATE ""ValidationRuns""
SET ""IsCurrent"" = false
WHERE ""ClientID"" = @ClientID AND ""RuleNumber"" = @RuleNumber AND ""IsCurrent"" = true;";
            command.Parameters.AddWithValue("@ClientID", clientId);
            command.Parameters.AddWithValue("@RuleNumber", ruleNumber);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<int> ClearRuleSignoffsAndFlagForReviewAsync(int runId)
        {
            await using var connection = await OpenConnectionAsync();

            await using var countCommand = connection.CreateConfiguredCommand();
            countCommand.CommandText = "SELECT COUNT(1) FROM \"ReviewSignoffs\" WHERE \"RunID\" = @RunID;";
            countCommand.Parameters.AddWithValue("@RunID", runId);
            var existingCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync());

            await using var deleteCommand = connection.CreateConfiguredCommand();
            deleteCommand.CommandText = "DELETE FROM \"ReviewSignoffs\" WHERE \"RunID\" = @RunID;";
            deleteCommand.Parameters.AddWithValue("@RunID", runId);
            await deleteCommand.ExecuteNonQueryAsync();

            await using var updateCommand = connection.CreateConfiguredCommand();
            updateCommand.CommandText = "UPDATE \"ValidationRuns\" SET \"Status\" = 'Needs Review' WHERE \"RunID\" = @RunID;";
            updateCommand.Parameters.AddWithValue("@RunID", runId);
            await updateCommand.ExecuteNonQueryAsync();

            return existingCount;
        }

        public async Task<List<RunSignoffViewModel>> GetRuleRunSignoffsAsync(int runId, string? currentUserId)
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
SELECT rs.""SignoffID"",
       COALESCE(rs.""SignoffRole"", ''),
       TRIM(COALESCE(u.""FirstName"", '') || ' ' || COALESCE(u.""LastName"", '')),
       COALESCE(u.""Email"", ''),
       COALESCE(rs.""Comment"", ''),
       rs.""SignedOffAt"",
       CASE WHEN @CurrentUserID IS NOT NULL AND rs.""ReviewerID"" = @CurrentUserID THEN true ELSE false END
FROM ""ReviewSignoffs"" rs
INNER JOIN ""AspNetUsers"" u ON u.""Id"" = rs.""ReviewerID""
WHERE rs.""RunID"" = @RunID
ORDER BY CASE COALESCE(rs.""SignoffRole"", '')
            WHEN 'DataAnalyst' THEN 1
            WHEN 'Manager' THEN 2
            WHEN 'Director' THEN 3
            ELSE 4
         END,
         rs.""SignedOffAt"" DESC;";
            command.Parameters.AddWithValue("@RunID", runId);
            command.Parameters.AddWithValue("@CurrentUserID", (object?)currentUserId ?? DBNull.Value);

            await using var reader = await command.ExecuteReaderAsync();
            var signoffs = new List<RunSignoffViewModel>();
            while (await reader.ReadAsync())
            {
                signoffs.Add(new RunSignoffViewModel
                {
                    Id = reader.GetInt32(0),
                    SignoffRole = reader.GetString(1),
                    ReviewerName = reader.GetString(2),
                    ReviewerEmail = reader.GetString(3),
                    Comment = reader.GetString(4),
                    SignedOffAt = reader.IsDBNull(5) ? DateTime.UtcNow : reader.GetDateTime(5),
                    IsCurrentUser = !reader.IsDBNull(6) && reader.GetBoolean(6)
                });
            }
            return signoffs;
        }

        public async Task<bool> HasRuleSignoffRoleAsync(int runId, string signoffRole)
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM \"ReviewSignoffs\" WHERE \"RunID\" = @RunID AND \"SignoffRole\" = @SignoffRole);";
            command.Parameters.AddWithValue("@RunID", runId);
            command.Parameters.AddWithValue("@SignoffRole", signoffRole);
            return Convert.ToBoolean(await command.ExecuteScalarAsync());
        }

        public async Task<bool> RuleWorkspaceReadyForSignoffAsync(int runId)
        {
            if (await IsWorkspaceSavedAsync(runId)) return true;
            return await HasRuleSignoffRoleAsync(runId, "DataAnalyst");
        }

        public async Task UpdateRuleRunStatusFromSignoffsAsync(int runId)
        {
            await using var connection = await OpenConnectionAsync();
            var hasAll = await HasAllRequiredRuleSignoffsAsync(connection, runId);
            await SetRuleRunStatusAsync(connection, runId, hasAll ? "Reviewed and Completed" : "Needs Review");
        }

        private static async Task<bool> HasAllRequiredRuleSignoffsAsync(NpgsqlConnection connection, int runId)
        {
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
SELECT
    EXISTS(SELECT 1 FROM ""ReviewSignoffs"" WHERE ""RunID"" = @RunID AND ""SignoffRole"" = 'DataAnalyst'),
    EXISTS(SELECT 1 FROM ""ReviewSignoffs"" WHERE ""RunID"" = @RunID AND ""SignoffRole"" = 'Manager'),
    EXISTS(SELECT 1 FROM ""ReviewSignoffs"" WHERE ""RunID"" = @RunID AND ""SignoffRole"" = 'Director');";
            command.Parameters.AddWithValue("@RunID", runId);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return false;
            return reader.GetBoolean(0) && reader.GetBoolean(1) && reader.GetBoolean(2);
        }

        private static async Task SetRuleRunStatusAsync(NpgsqlConnection connection, int runId, string status, NpgsqlTransaction? transaction = null)
        {
            await using var command = connection.CreateConfiguredCommand();
            if (transaction != null) command.Transaction = transaction;
            command.CommandText = "UPDATE \"ValidationRuns\" SET \"Status\" = @Status WHERE \"RunID\" = @RunID;";
            command.Parameters.AddWithValue("@RunID", runId);
            command.Parameters.AddWithValue("@Status", status);
            await command.ExecuteNonQueryAsync();
        }

        public async Task SaveRuleWorkspaceFieldsAsync(SaveRuleWorkspaceFieldsRequest request, string? editorDisplayName)
        {
            await using var connection = await OpenConnectionAsync();
            var previousHash = await GetValidationRecordHashAsync(request.RunId);
            var recordHash = ComputeHash($@"WorkspaceSave|{request.RunId}|{request.ClientId}|{request.HemisServer}|{request.AuditDatabase}|{request.StudTable}|{request.DeceasedTable}|{request.StudColumn}|{request.DeceasedColumn}|{editorDisplayName}|{DateTime.UtcNow:o}|{previousHash}");

            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
UPDATE ""ValidationRuns""
SET ""HemisServer"" = @HemisServer,
    ""AuditDatabase"" = @AuditDatabase,
    ""StudTable"" = @StudTable,
    ""DeceasedTable"" = @DeceasedTable,
    ""StudColumn"" = @StudColumn,
    ""DeceasedColumn"" = @DeceasedColumn,
    ""LastEditedByUserName"" = @LastEditedByUserName,
    ""LastEditedAt"" = now(),
    ""WorkspaceSavedAt"" = now(),
    ""PreviousHash"" = @PreviousHash,
    ""RecordHash"" = @RecordHash,
    ""Status"" = 'Needs Review'
WHERE ""RunID"" = @RunID AND ""ClientID"" = @ClientID;";
            command.Parameters.AddWithValue("@RunID", request.RunId);
            command.Parameters.AddWithValue("@ClientID", request.ClientId);
            command.Parameters.AddWithValue("@HemisServer", request.HemisServer);
            command.Parameters.AddWithValue("@AuditDatabase", request.AuditDatabase);
            command.Parameters.AddWithValue("@StudTable", request.StudTable);
            command.Parameters.AddWithValue("@DeceasedTable", request.DeceasedTable);
            command.Parameters.AddWithValue("@StudColumn", request.StudColumn);
            command.Parameters.AddWithValue("@DeceasedColumn", request.DeceasedColumn);
            command.Parameters.AddWithValue("@LastEditedByUserName", (object?)editorDisplayName ?? DBNull.Value);
            command.Parameters.AddWithValue("@PreviousHash", (object?)previousHash ?? DBNull.Value);
            command.Parameters.AddWithValue("@RecordHash", recordHash);
            await command.ExecuteNonQueryAsync();
        }

        public async Task MarkRuleWorkspaceEditStartedAsync(int runId, string? editorDisplayName)
        {
            await using var connection = await OpenConnectionAsync();
            var previousHash = await GetValidationRecordHashAsync(runId);
            var recordHash = ComputeHash($@"BeginWorkspaceEdit|{runId}|{editorDisplayName}|{DateTime.UtcNow:o}|{previousHash}");

            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
UPDATE ""ValidationRuns""
SET ""LastEditedByUserName"" = @LastEditedByUserName,
    ""LastEditedAt"" = now(),
    ""WorkspaceSavedAt"" = NULL,
    ""PreviousHash"" = @PreviousHash,
    ""RecordHash"" = @RecordHash,
    ""Status"" = 'Needs Review'
WHERE ""RunID"" = @RunID;";
            command.Parameters.AddWithValue("@RunID", runId);
            command.Parameters.AddWithValue("@LastEditedByUserName", (object?)editorDisplayName ?? DBNull.Value);
            command.Parameters.AddWithValue("@PreviousHash", (object?)previousHash ?? DBNull.Value);
            command.Parameters.AddWithValue("@RecordHash", recordHash);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<int> SaveValidationRunAsync(SaveValidationRunRequest request, string? userEmail, string? userName)
        {
            var user = string.IsNullOrWhiteSpace(userEmail) ? null : await _userManager.FindByEmailAsync(userEmail);
            if (user == null)
                throw new InvalidOperationException("The current analyst could not be resolved in the system database.");

            await using var connection = await OpenConnectionAsync();
            await EnsureClientNotArchivedAsync(connection, request.ClientId);

            var previousHash = await GetLatestValidationRunHashAsync(request.ClientId, request.RuleNumber);

            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = @"
INSERT INTO ""ValidationRuns""
(""ClientID"", ""UserID"", ""RuleNumber"", ""RuleName"", ""Status"", ""TotalRecords"", ""PassCount"", ""FailCount"", ""ExceptionRate"", ""RunTimestamp"",
 ""HemisServer"", ""AuditDatabase"", ""StudTable"", ""DeceasedTable"", ""StudColumn"", ""DeceasedColumn"",
 ""ExceptionsJSON"", ""ResultsJSON"", ""RunByUserName"", ""LastEditedByUserName"", ""LastEditedAt"", ""PreviousHash"", ""RecordHash"", ""IsCurrent"")
VALUES
(@ClientID, @UserID, @RuleNumber, @RuleName, @Status, @TotalRecords, @PassCount, @FailCount, @ExceptionRate, now(),
 @HemisServer, @AuditDatabase, @StudTable, @DeceasedTable, @StudColumn, @DeceasedColumn,
 @ExceptionsJSON, @ResultsJSON, @RunByUserName, NULL, NULL, @PreviousHash, NULL, true)
RETURNING ""RunID"";";
            command.Parameters.AddWithValue("@ClientID", request.ClientId);
            command.Parameters.AddWithValue("@UserID", user.Id);
            command.Parameters.AddWithValue("@RuleNumber", request.RuleNumber);
            command.Parameters.AddWithValue("@RuleName", request.RuleName);
            command.Parameters.AddWithValue("@Status", request.Status);
            command.Parameters.AddWithValue("@TotalRecords", request.TotalRecords);
            command.Parameters.AddWithValue("@PassCount", request.PassCount);
            command.Parameters.AddWithValue("@FailCount", request.FailCount);
            command.Parameters.AddWithValue("@ExceptionRate", request.ExceptionRate);
            command.Parameters.AddWithValue("@HemisServer", request.HemisServer);
            command.Parameters.AddWithValue("@AuditDatabase", request.AuditDatabase);
            command.Parameters.AddWithValue("@StudTable", request.StudTable);
            command.Parameters.AddWithValue("@DeceasedTable", request.DeceasedTable);
            command.Parameters.AddWithValue("@StudColumn", request.StudColumn);
            command.Parameters.AddWithValue("@DeceasedColumn", request.DeceasedColumn);
            command.Parameters.AddWithValue("@ExceptionsJSON", (object?)request.ExceptionsJSON ?? DBNull.Value);
            command.Parameters.AddWithValue("@ResultsJSON", (object?)request.ResultsJSON ?? DBNull.Value);
            command.Parameters.AddWithValue("@RunByUserName", (object?)userName ?? (object?)userEmail ?? DBNull.Value);
            command.Parameters.AddWithValue("@PreviousHash", (object?)previousHash ?? DBNull.Value);

            var runId = Convert.ToInt32(await command.ExecuteScalarAsync());

            var recordHash = ComputeHash($@"ValidationRun|{request.RuleNumber}|{runId}|{request.ClientId}|{user.Id}|{request.Status}|{request.TotalRecords}|{request.FailCount}|{request.ExceptionRate}|{DateTime.UtcNow:o}|{previousHash}");
            await using var hashCommand = connection.CreateConfiguredCommand();
            hashCommand.CommandText = "UPDATE \"ValidationRuns\" SET \"RecordHash\" = @RecordHash WHERE \"RunID\" = @RunID;";
            hashCommand.Parameters.AddWithValue("@RunID", runId);
            hashCommand.Parameters.AddWithValue("@RecordHash", recordHash);
            await hashCommand.ExecuteNonQueryAsync();

            return runId;
        }

        public async Task AddOrUpdateRuleSignoffAsync(int runId, int clientId, string reviewerUserId, string signoffRole, string? comment)
        {
            await using var connection = await OpenConnectionAsync();

            await using var deleteExisting = connection.CreateConfiguredCommand();
            deleteExisting.CommandText = "DELETE FROM \"ReviewSignoffs\" WHERE \"RunID\" = @RunID AND \"ReviewerID\" = @ReviewerID;";
            deleteExisting.Parameters.AddWithValue("@RunID", runId);
            deleteExisting.Parameters.AddWithValue("@ReviewerID", reviewerUserId);
            await deleteExisting.ExecuteNonQueryAsync();

            await using var insert = connection.CreateConfiguredCommand();
            insert.CommandText = @"
INSERT INTO ""ReviewSignoffs"" (""ClientID"", ""RunID"", ""ReviewerID"", ""SignoffRole"", ""ReviewType"", ""Comment"", ""SignedOffAt"")
VALUES (@ClientID, @RunID, @ReviewerID, @SignoffRole, 'Final', @Comment, now());";
            insert.Parameters.AddWithValue("@ClientID", clientId);
            insert.Parameters.AddWithValue("@RunID", runId);
            insert.Parameters.AddWithValue("@ReviewerID", reviewerUserId);
            insert.Parameters.AddWithValue("@SignoffRole", signoffRole);
            insert.Parameters.AddWithValue("@Comment", string.IsNullOrWhiteSpace(comment) ? DBNull.Value : comment.Trim());
            await insert.ExecuteNonQueryAsync();

            await UpdateRuleRunStatusFromSignoffsAsync(runId);
        }

        // ── Signoff removal with run versioning (Postgres port of the SQL-Server-only,
        // shared ReviewSignoffSqlHelper.cs — same branching, same hash-chain-on-clone logic) ──

        private sealed record RuleSignoffReviewerSnapshot(string SignoffRole, bool IsCurrent, string? RecordHash);
        private sealed record RuleSignoffRoleSnapshot(string ReviewerId, string SignoffRole, bool IsCurrent, string? RecordHash);

        private static int GetSignoffRoleRank(string? signoffRole) => signoffRole switch
        {
            "DataAnalyst" => 1,
            "Manager" => 2,
            "Director" => 3,
            _ => 99
        };

        public async Task<RuleSignoffRemovalResult> RemoveRuleSignoffByReviewerAsync(int runId, string reviewerUserId)
        {
            await using var connection = await OpenConnectionAsync();

            await using var snapshotCommand = connection.CreateConfiguredCommand();
            snapshotCommand.CommandText = @"
SELECT COALESCE(rs.""SignoffRole"", ''), COALESCE(vr.""IsCurrent"", false), vr.""RecordHash""
FROM ""ReviewSignoffs"" rs
INNER JOIN ""ValidationRuns"" vr ON vr.""RunID"" = rs.""RunID""
WHERE rs.""RunID"" = @RunID AND rs.""ReviewerID"" = @ReviewerID
LIMIT 1;";
            snapshotCommand.Parameters.AddWithValue("@RunID", runId);
            snapshotCommand.Parameters.AddWithValue("@ReviewerID", reviewerUserId);

            RuleSignoffReviewerSnapshot? snapshot = null;
            await using (var reader = await snapshotCommand.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                    snapshot = new RuleSignoffReviewerSnapshot(reader.GetString(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetString(2));
            }
            if (snapshot == null)
                return new RuleSignoffRemovalResult { RemovedCount = 0, HistoricalRunId = runId };

            if (!snapshot.IsCurrent)
            {
                var removedCount = await DeleteReviewerSignoffAsync(connection, runId, reviewerUserId);
                if (removedCount > 0)
                    await UpdateRuleRunStatusFromSignoffsAsync(runId);
                return new RuleSignoffRemovalResult { RemovedCount = removedCount, SignoffRole = snapshot.SignoffRole, HistoricalRunId = runId };
            }

            return await RemoveCurrentRunSignoffWithVersioningAsync(connection, runId, snapshot.SignoffRole, snapshot.RecordHash, actorDisplayName: null, reviewerUserId: reviewerUserId);
        }

        public async Task<RuleSignoffRemovalResult> RemoveRuleSignoffByRoleAsync(int runId, string signoffRole, string? actorDisplayName)
        {
            await using var connection = await OpenConnectionAsync();

            await using var snapshotCommand = connection.CreateConfiguredCommand();
            snapshotCommand.CommandText = @"
SELECT rs.""ReviewerID"", COALESCE(rs.""SignoffRole"", ''), COALESCE(vr.""IsCurrent"", false), vr.""RecordHash""
FROM ""ReviewSignoffs"" rs
INNER JOIN ""ValidationRuns"" vr ON vr.""RunID"" = rs.""RunID""
WHERE rs.""RunID"" = @RunID AND rs.""SignoffRole"" = @SignoffRole
LIMIT 1;";
            snapshotCommand.Parameters.AddWithValue("@RunID", runId);
            snapshotCommand.Parameters.AddWithValue("@SignoffRole", signoffRole);

            RuleSignoffRoleSnapshot? snapshot = null;
            await using (var reader = await snapshotCommand.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                    snapshot = new RuleSignoffRoleSnapshot(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3));
            }
            if (snapshot == null)
                return new RuleSignoffRemovalResult { RemovedCount = 0, HistoricalRunId = runId };

            if (!snapshot.IsCurrent)
            {
                var removedCount = await DeleteRoleSignoffAsync(connection, runId, snapshot.SignoffRole);
                if (removedCount > 0)
                    await UpdateRuleRunStatusFromSignoffsAsync(runId);
                return new RuleSignoffRemovalResult { RemovedCount = removedCount, SignoffRole = snapshot.SignoffRole, HistoricalRunId = runId };
            }

            return await RemoveCurrentRunSignoffWithVersioningAsync(connection, runId, snapshot.SignoffRole, snapshot.RecordHash, actorDisplayName, reviewerUserId: null);
        }

        private async Task<RuleSignoffRemovalResult> RemoveCurrentRunSignoffWithVersioningAsync(
            NpgsqlConnection connection, int runId, string signoffRole, string? previousHash, string? actorDisplayName, string? reviewerUserId)
        {
            var removedRank = GetSignoffRoleRank(signoffRole);

            if (removedRank > 1)
            {
                await using var transaction = await connection.BeginTransactionAsync();
                try
                {
                    var removedCount = await RemoveRoleAndHigherSignoffsAsync(connection, transaction, runId, removedRank);
                    if (removedCount > 0)
                        await SetRuleRunStatusAsync(connection, runId, "Needs Review", transaction);

                    await transaction.CommitAsync();
                    return new RuleSignoffRemovalResult { RemovedCount = removedCount, SignoffRole = signoffRole, HistoricalRunId = runId };
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }

            await using var cloneTransaction = await connection.BeginTransactionAsync();
            try
            {
                await SetRunCurrentStateAsync(connection, cloneTransaction, runId, false);
                var newRunId = await CloneValidationRunAsync(connection, cloneTransaction, runId, previousHash, actorDisplayName);
                await NormalizeSingleCurrentRunAsync(connection, cloneTransaction, runId, newRunId);
                await SetRuleRunStatusAsync(connection, newRunId, "Needs Review", cloneTransaction);

                var actorId = reviewerUserId ?? "";
                var newHash = ComputeHash($"SignoffRemovalVersion|{runId}|{newRunId}|{actorId}|{signoffRole}|{DateTime.UtcNow:o}|{previousHash}");
                await using (var hashCommand = connection.CreateConfiguredCommand())
                {
                    hashCommand.Transaction = cloneTransaction;
                    hashCommand.CommandText = "UPDATE \"ValidationRuns\" SET \"RecordHash\" = @RecordHash WHERE \"RunID\" = @RunID;";
                    hashCommand.Parameters.AddWithValue("@RunID", newRunId);
                    hashCommand.Parameters.AddWithValue("@RecordHash", newHash);
                    await hashCommand.ExecuteNonQueryAsync();
                }

                await cloneTransaction.CommitAsync();
                return new RuleSignoffRemovalResult
                {
                    RemovedCount = 1,
                    SignoffRole = signoffRole,
                    HistoricalSnapshotPreserved = true,
                    HistoricalRunId = runId,
                    NewCurrentRunId = newRunId
                };
            }
            catch
            {
                await cloneTransaction.RollbackAsync();
                throw;
            }
        }

        private static async Task<int> DeleteReviewerSignoffAsync(NpgsqlConnection connection, int runId, string reviewerId)
        {
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = "DELETE FROM \"ReviewSignoffs\" WHERE \"RunID\" = @RunID AND \"ReviewerID\" = @ReviewerID;";
            command.Parameters.AddWithValue("@RunID", runId);
            command.Parameters.AddWithValue("@ReviewerID", reviewerId);
            return await command.ExecuteNonQueryAsync();
        }

        private static async Task<int> DeleteRoleSignoffAsync(NpgsqlConnection connection, int runId, string signoffRole)
        {
            await using var command = connection.CreateConfiguredCommand();
            command.CommandText = "DELETE FROM \"ReviewSignoffs\" WHERE \"RunID\" = @RunID AND \"SignoffRole\" = @SignoffRole;";
            command.Parameters.AddWithValue("@RunID", runId);
            command.Parameters.AddWithValue("@SignoffRole", signoffRole);
            return await command.ExecuteNonQueryAsync();
        }

        private static async Task<int> RemoveRoleAndHigherSignoffsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int runId, int removedRank)
        {
            await using var command = connection.CreateConfiguredCommand();
            command.Transaction = transaction;
            command.CommandText = @"
DELETE FROM ""ReviewSignoffs""
WHERE ""RunID"" = @RunID
  AND CASE COALESCE(""SignoffRole"", '')
        WHEN 'DataAnalyst' THEN 1
        WHEN 'Manager' THEN 2
        WHEN 'Director' THEN 3
        ELSE 99
      END >= @RemovedRank;";
            command.Parameters.AddWithValue("@RunID", runId);
            command.Parameters.AddWithValue("@RemovedRank", removedRank);
            return await command.ExecuteNonQueryAsync();
        }

        private static async Task SetRunCurrentStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int runId, bool isCurrent)
        {
            await using var command = connection.CreateConfiguredCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE \"ValidationRuns\" SET \"IsCurrent\" = @IsCurrent WHERE \"RunID\" = @RunID;";
            command.Parameters.AddWithValue("@RunID", runId);
            command.Parameters.AddWithValue("@IsCurrent", isCurrent);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task<int> CloneValidationRunAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int sourceRunId, string? previousHash, string? reviewerDisplayName)
        {
            await using var command = connection.CreateConfiguredCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO ""ValidationRuns""
(""ClientID"", ""UserID"", ""HemisServer"", ""AuditDatabase"", ""StudTable"", ""DeceasedTable"", ""StudColumn"", ""DeceasedColumn"",
 ""RuleNumber"", ""RuleName"", ""Status"", ""RunTimestamp"", ""TotalRecords"", ""PassCount"", ""FailCount"", ""ExceptionRate"",
 ""ExceptionsJSON"", ""ResultsJSON"", ""RunByUserName"", ""LastEditedByUserName"", ""LastEditedAt"", ""PreviousHash"", ""RecordHash"", ""IsCurrent"")
SELECT
    ""ClientID"", ""UserID"", ""HemisServer"", ""AuditDatabase"", ""StudTable"", ""DeceasedTable"", ""StudColumn"", ""DeceasedColumn"",
    ""RuleNumber"", ""RuleName"", 'Needs Review', now(), ""TotalRecords"", ""PassCount"", ""FailCount"", ""ExceptionRate"",
    ""ExceptionsJSON"", ""ResultsJSON"", ""RunByUserName"", COALESCE(@ReviewerDisplayName, ""LastEditedByUserName""), now(), @PreviousHash, NULL, true
FROM ""ValidationRuns""
WHERE ""RunID"" = @SourceRunID
RETURNING ""RunID"";";
            command.Parameters.AddWithValue("@SourceRunID", sourceRunId);
            command.Parameters.AddWithValue("@PreviousHash", (object?)previousHash ?? DBNull.Value);
            command.Parameters.AddWithValue("@ReviewerDisplayName", (object?)reviewerDisplayName ?? DBNull.Value);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        private static async Task NormalizeSingleCurrentRunAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int sourceRunId, int newCurrentRunId)
        {
            await using var command = connection.CreateConfiguredCommand();
            command.Transaction = transaction;
            command.CommandText = @"
UPDATE ""ValidationRuns"" vr
SET ""IsCurrent"" = CASE WHEN vr.""RunID"" = @NewCurrentRunID THEN true ELSE false END
FROM ""ValidationRuns"" src
WHERE src.""RunID"" = @SourceRunID
  AND vr.""ClientID"" = src.""ClientID""
  AND vr.""RuleNumber"" = src.""RuleNumber"";";
            command.Parameters.AddWithValue("@SourceRunID", sourceRunId);
            command.Parameters.AddWithValue("@NewCurrentRunID", newCurrentRunId);
            await command.ExecuteNonQueryAsync();
        }
    }
}
