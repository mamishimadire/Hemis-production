namespace HemisAudit.ViewModels
{
    public class Rule53ValidationRequest
    {
        public int    ClientId   { get; set; }
        public int?   RunId      { get; set; }
        public string ValpacTable   { get; set; } = "dbo_CRSE";
        public string ValpacSubjCol { get; set; } = "_030";
        public string ProdTable     { get; set; } = "MT-audit-prod-CRSE";
        public string ProdSubjCol   { get; set; } = "IALSUBJ";
    }

    public class Rule53ValidationRow
    {
        public int    RowNumber        { get; set; }
        public string ControlType      { get; set; } = "";
        public string ValpacSubj       { get; set; } = "";
        public string ProdSubj         { get; set; } = "";
        public string ValidationResult { get; set; } = "PASS";
        public string ResultDetail     { get; set; } = "";
    }

    public class Rule53ControlSummary
    {
        public string  ControlType   { get; set; } = "";
        public string  ControlLabel  { get; set; } = "";
        public string  CriteriaText  { get; set; } = "";
        public int     TotalCount    { get; set; }
        public int     PassCount     { get; set; }
        public int     FailCount     { get; set; }
        public decimal ExceptionRate { get; set; }
        public string  Status        { get; set; } = "PASS";
    }

    public class Rule53ValidationSummary
    {
        public bool    Success    { get; set; }
        public string? Error      { get; set; }
        public string? Warning    { get; set; }
        public string  Status     { get; set; } = "";
        public string  Timestamp  { get; set; } = "";
        public int     ClientId   { get; set; }
        public int?    SavedRunId { get; set; }
        // config echo
        public string ValpacTable   { get; set; } = "dbo_CRSE";
        public string ValpacSubjCol { get; set; } = "_030";
        public string ProdTable     { get; set; } = "MT-audit-prod-CRSE";
        public string ProdSubjCol   { get; set; } = "IALSUBJ";
        // totals
        public int ValpacRecordCount { get; set; }
        public int ProdRecordCount   { get; set; }
        public int TotalValidated    { get; set; }
        public int PassCount         { get; set; }
        public int FailCount         { get; set; }
        public decimal ExceptionRate { get; set; }
        public bool IsPreviewOnly { get; set; }
        public int PreviewLimit   { get; set; }
        public string TableLinkageText { get; set; } = "";
        public string RuleModeText     { get; set; } = "";
        public List<string> ProcedureSteps { get; set; } = new();
        // controls
        public List<Rule53ControlSummary> ControlSummaries { get; set; } = new();
        public List<Rule53ValidationRow>  ValidationRows    { get; set; } = new();
    }

    public class Rule53WorkspaceStateViewModel
    {
        public int    ClientId { get; set; }
        public int?   RunId    { get; set; }
        public string ValpacTable   { get; set; } = "dbo_CRSE";
        public string ValpacSubjCol { get; set; } = "_030";
        public string ProdTable     { get; set; } = "MT-audit-prod-CRSE";
        public string ProdSubjCol   { get; set; } = "IALSUBJ";
        public string CurrentStatus             { get; set; } = "";
        public bool   HasDataAnalystSignoff     { get; set; }
        public bool   CurrentUserHasSignedOff   { get; set; }
        public string CurrentUserSignoffComment { get; set; } = "";
        public string CurrentUserEngagementRole { get; set; } = "";
        public bool   IsWorkspaceSaved          { get; set; }
        public bool   ResultsVisible            { get; set; }
        public string? LastEditedByUserName     { get; set; }
        public DateTime? LastEditedAt           { get; set; }
        public Rule53ValidationSummary? Summary { get; set; }
    }

    public class Rule53WorkspaceSaveResult
    {
        public bool   Success             { get; set; }
        public string? Error              { get; set; }
        public string? Message            { get; set; }
        public bool   SignoffsCleared     { get; set; }
        public int?   ClearedSignoffCount { get; set; }
        public Rule53WorkspaceStateViewModel? Workspace { get; set; }
    }

    public class Rule53RunReviewViewModel
    {
        public int    RunId          { get; set; }
        public int    ClientId       { get; set; }
        public bool   IsCurrentRun   { get; set; }
        public string EngagementName { get; set; } = "";
        public string MaconomyNumber { get; set; } = "";
        public string CurrentUserEngagementRole { get; set; } = "";
        public bool   HasDataAnalystSignoff     { get; set; }
        public bool   CurrentUserHasSignedOff   { get; set; }
        public bool   CanCurrentUserSignOff     { get; set; }
        public bool   CanCurrentUserRemoveSignoff => IsCurrentRun && CurrentUserHasSignedOff;
        public string? GeneratedSql             { get; set; }
        public Rule53ValidationSummary? Summary { get; set; }
        public List<RunSignoffViewModel> Signoffs { get; set; } = new();
    }

    public class Rule53WorkspaceSignoffInputModel
    {
        public int    ClientId { get; set; }
        public int?   RunId    { get; set; }
        public string Comment  { get; set; } = "";
    }

    public class Rule53RunSignoffInputModel
    {
        public int    RunId   { get; set; }
        public string Comment { get; set; } = "";
    }

    public class Rule53TableDiscoveryResult
    {
        public bool         Success         { get; set; }
        public List<string> Tables          { get; set; } = new();
        public string?      AutoValpacTable { get; set; }
        public string?      AutoProdTable   { get; set; }
        public string?      Error           { get; set; }
    }

    public class Rule53ColumnDiscoveryResult
    {
        public bool         Success      { get; set; }
        public List<string> Columns      { get; set; } = new();
        public string?      AutoSelected { get; set; }
        public string?      Error        { get; set; }
    }

    public class Rule53GetColumnsRequest
    {
        public int    ClientId  { get; set; }
        public string TableName { get; set; } = "";
        public string TableRole { get; set; } = "";
    }

    public class Rule53VerifyResult
    {
        public bool    Success           { get; set; }
        public int     ValpacRecordCount { get; set; }
        public int     ProdRecordCount   { get; set; }
        public string? Error             { get; set; }
    }

    public class Rule53SqlResult
    {
        public bool   Success { get; set; }
        public string Sql     { get; set; } = "";
        public string? Error  { get; set; }
    }
}
