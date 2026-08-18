namespace HemisAudit.ViewModels
{
    public class Rule66TableDiscoveryResult
    {
        public bool Success { get; set; }
        public List<string> Tables { get; set; } = new();
        public string? AutoStudTable { get; set; }
        public string? AutoCregTable { get; set; }
        public string? Error { get; set; }
    }

    public class Rule66ColumnDiscoveryResult
    {
        public bool Success { get; set; }
        public List<string> Columns { get; set; } = new();
        public string? AutoSelected { get; set; }
        public string? Error { get; set; }
    }

    public class Rule66GetColumnsRequest
    {
        public int ClientId { get; set; }
        public string TableName { get; set; } = "";
        public string TableRole { get; set; } = "";
    }

    public class Rule66DistinctValuesResult
    {
        public bool Success { get; set; }
        public List<string> Values { get; set; } = new();
        public string? Error { get; set; }
    }

    public class Rule66GetDistinctValuesRequest
    {
        public int ClientId { get; set; }
        public string TableName { get; set; } = "";
        public string ColumnName { get; set; } = "";
    }

    public class Rule66ValidationRequest
    {
        public int ClientId { get; set; }
        public int? RunId { get; set; }
        public string StudTable { get; set; } = "dbo_STUD_VALPAC";
        public string CregTable { get; set; } = "dbo_CREG";
        public string StudStudentNoCol { get; set; } = "_007";
        public string CregStudentNoCol { get; set; } = "_007";
        public string StudQualCol { get; set; } = "_001";
        public string CregQualCol { get; set; } = "_001";
        public string FundingSourceCol { get; set; } = "_019";
        public string FundingSourceValues { get; set; } = "NS";
    }

    public class Rule66VerifyResult
    {
        public bool Success { get; set; }
        public int StudRecordCount { get; set; }
        public int StudNsfasCount { get; set; }
        public int CregRecordCount { get; set; }
        public string? Error { get; set; }
    }

    public class Rule66ControlSummaryItemViewModel
    {
        public string ControlType { get; set; } = "";
        public string ControlLabel { get; set; } = "";
        public string CriteriaText { get; set; } = "";
        public int TotalCount { get; set; }
        public int PassCount { get; set; }
        public int FailCount { get; set; }
        public string Status { get; set; } = "";
    }

    public class Rule66ValidationRowRecord
    {
        public int ValidationNumber { get; set; }
        public string ControlType { get; set; } = "";
        public string ControlLabel { get; set; } = "";
        public string ValidationResult { get; set; } = "";
        public string ValidationExplanation { get; set; } = "";
        public Dictionary<string, string?> DisplayValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class Rule66ValidationSummary
    {
        public bool Success { get; set; }
        public int StudRecordCount { get; set; }
        public int StudNsfasCount { get; set; }
        public int CregRecordCount { get; set; }
        public int TotalValidated { get; set; }
        public int DisplayedCount { get; set; }
        public bool IsPreviewOnly { get; set; }
        public int PreviewLimit { get; set; }
        public int PassCount { get; set; }
        public int FailCount { get; set; }
        public decimal ExceptionRate { get; set; }
        public string Status { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public string StudTable { get; set; } = "dbo_STUD_VALPAC";
        public string CregTable { get; set; } = "dbo_CREG";
        public string StudStudentNoCol { get; set; } = "_007";
        public string CregStudentNoCol { get; set; } = "_007";
        public string StudQualCol { get; set; } = "_001";
        public string CregQualCol { get; set; } = "_001";
        public string FundingSourceCol { get; set; } = "_019";
        public string FundingSourceValues { get; set; } = "NS";
        public string TableLinkageText { get; set; } = "";
        public string RuleModeText { get; set; } = "";
        public List<string> ProcedureSteps { get; set; } = new();
        public int ClientId { get; set; }
        public int? SavedRunId { get; set; }
        public List<Rule66ControlSummaryItemViewModel> ControlSummaries { get; set; } = new();
        public List<Rule66ValidationRowRecord> ReviewRows { get; set; } = new();
        public string? Warning { get; set; }
        public string? Error { get; set; }
    }

    public class Rule66WorkspaceStateViewModel
    {
        public int ClientId { get; set; }
        public int? RunId { get; set; }
        public bool ResultsVisible { get; set; }
        public string StudTable { get; set; } = "dbo_STUD_VALPAC";
        public string CregTable { get; set; } = "dbo_CREG";
        public string StudStudentNoCol { get; set; } = "_007";
        public string CregStudentNoCol { get; set; } = "_007";
        public string StudQualCol { get; set; } = "_001";
        public string CregQualCol { get; set; } = "_001";
        public string FundingSourceCol { get; set; } = "_019";
        public string FundingSourceValues { get; set; } = "NS";
        public string CurrentUserEngagementRole { get; set; } = "";
        public bool HasDataAnalystSignoff { get; set; }
        public bool CurrentUserHasSignedOff { get; set; }
        public string CurrentUserSignoffComment { get; set; } = "";
        public string CurrentStatus { get; set; } = "";
        public string? LastEditedByUserName { get; set; }
        public DateTime? LastEditedAt { get; set; }
        public bool IsWorkspaceSaved { get; set; }
        public Rule66ValidationSummary? Summary { get; set; }
    }

    public class Rule66RunReviewViewModel
    {
        public int RunId { get; set; }
        public int ClientId { get; set; }
        public bool IsCurrentRun { get; set; }
        public string EngagementName { get; set; } = "";
        public string MaconomyNumber { get; set; } = "";
        public string GeneratedSql { get; set; } = "";
        public Rule66ValidationSummary Summary { get; set; } = new();
        public List<RunSignoffViewModel> Signoffs { get; set; } = new();
        public string CurrentUserEngagementRole { get; set; } = "";
        public bool HasDataAnalystSignoff { get; set; }
        public bool CurrentUserHasSignedOff =>
            Signoffs.Any(s => HemisAudit.Helpers.ValidationRunAccessPolicy.IsSignoffOwnedByEngagementRole(s.SignoffRole, CurrentUserEngagementRole));
        public bool CanCurrentUserSignOff => IsCurrentRun && HemisAudit.Helpers.ValidationRunAccessPolicy.CanAssignedUserSignOff(CurrentUserEngagementRole);
        public bool CanCurrentUserRemoveSignoff => IsCurrentRun && CurrentUserHasSignedOff;
        public bool CanCurrentUserDownload => HemisAudit.Helpers.ValidationRunAccessPolicy.CanAssignedUserDownload(CurrentUserEngagementRole);
    }

    public class Rule66WorkspaceSaveResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public bool SignoffsCleared { get; set; }
        public int? ClearedSignoffCount { get; set; }
        public Rule66WorkspaceStateViewModel? Workspace { get; set; }
        public string? Error { get; set; }
    }

    public class Rule66RunSignoffInputModel
    {
        public int RunId { get; set; }
        public string Comment { get; set; } = "";
    }

    public class Rule66WorkspaceSignoffInputModel
    {
        public int ClientId { get; set; }
        public int? RunId { get; set; }
        public string Comment { get; set; } = "";
    }

    public class Rule66SqlResult
    {
        public bool Success { get; set; }
        public string Sql { get; set; } = "";
        public string? Error { get; set; }
    }
}
