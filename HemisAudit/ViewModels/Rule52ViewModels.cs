namespace HemisAudit.ViewModels
{
    public class Rule52ValidationRequest
    {
        public int    ClientId   { get; set; }
        public int?   RunId      { get; set; }
        public string ValpacTable          { get; set; } = "dbo_QUAL";
        public string ValpacSubjCol        { get; set; } = "_001";
        public string ApprovalStatusCol    { get; set; } = "_004";
        public string ApprovalStatusValues { get; set; } = "A";
        public string ProdTable            { get; set; } = "MT-audit-prod-QUAL";
        public string ProdSubjCol          { get; set; } = "IAIQUAL";
    }

    public class Rule52ValidationRow
    {
        public int    RowNumber        { get; set; }
        public string ControlType      { get; set; } = "";
        public string ValpacSubj       { get; set; } = "";
        public string ProdSubj         { get; set; } = "";
        public string ApprovalStatus   { get; set; } = "";
        public string ValidationResult { get; set; } = "PASS";
        public string ResultDetail     { get; set; } = "";
    }

    public class Rule52ControlSummary
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

    public class Rule52ValidationSummary
    {
        public bool    Success    { get; set; }
        public string? Error      { get; set; }
        public string? Warning    { get; set; }
        public string  Status     { get; set; } = "";
        public string  Timestamp  { get; set; } = "";
        public int     ClientId   { get; set; }
        public int?    SavedRunId { get; set; }
        // config echo
        public string ValpacTable          { get; set; } = "dbo_QUAL";
        public string ValpacSubjCol        { get; set; } = "_001";
        public string ApprovalStatusCol    { get; set; } = "_004";
        public string ApprovalStatusValues { get; set; } = "A";
        public string ProdTable            { get; set; } = "MT-audit-prod-QUAL";
        public string ProdSubjCol          { get; set; } = "IAIQUAL";
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
        public List<Rule52ControlSummary> ControlSummaries { get; set; } = new();
        public List<Rule52ValidationRow>  ValidationRows    { get; set; } = new();
    }

    public class Rule52WorkspaceStateViewModel
    {
        public int    ClientId { get; set; }
        public int?   RunId    { get; set; }
        public string ValpacTable          { get; set; } = "dbo_QUAL";
        public string ValpacSubjCol        { get; set; } = "_001";
        public string ApprovalStatusCol    { get; set; } = "_004";
        public string ApprovalStatusValues { get; set; } = "A";
        public string ProdTable            { get; set; } = "MT-audit-prod-QUAL";
        public string ProdSubjCol          { get; set; } = "IAIQUAL";
        public string CurrentStatus             { get; set; } = "";
        public bool   HasDataAnalystSignoff     { get; set; }
        public bool   CurrentUserHasSignedOff   { get; set; }
        public string CurrentUserSignoffComment { get; set; } = "";
        public string CurrentUserEngagementRole { get; set; } = "";
        public bool   IsWorkspaceSaved          { get; set; }
        public bool   ResultsVisible            { get; set; }
        public string? LastEditedByUserName     { get; set; }
        public DateTime? LastEditedAt           { get; set; }
        public Rule52ValidationSummary? Summary { get; set; }
    }

    public class Rule52WorkspaceSaveResult
    {
        public bool   Success             { get; set; }
        public string? Error              { get; set; }
        public string? Message            { get; set; }
        public bool   SignoffsCleared     { get; set; }
        public int?   ClearedSignoffCount { get; set; }
        public Rule52WorkspaceStateViewModel? Workspace { get; set; }
    }

    public class Rule52RunReviewViewModel
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
        public Rule52ValidationSummary? Summary { get; set; }
        public List<RunSignoffViewModel> Signoffs { get; set; } = new();
    }

    public class Rule52WorkspaceSignoffInputModel
    {
        public int    ClientId { get; set; }
        public int?   RunId    { get; set; }
        public string Comment  { get; set; } = "";
    }

    public class Rule52RunSignoffInputModel
    {
        public int    RunId   { get; set; }
        public string Comment { get; set; } = "";
    }

    public class Rule52TableDiscoveryResult
    {
        public bool         Success         { get; set; }
        public List<string> Tables          { get; set; } = new();
        public string?      AutoValpacTable { get; set; }
        public string?      AutoProdTable   { get; set; }
        public string?      Error           { get; set; }
    }

    public class Rule52ColumnDiscoveryResult
    {
        public bool         Success      { get; set; }
        public List<string> Columns      { get; set; } = new();
        public string?      AutoSelected { get; set; }
        public string?      Error        { get; set; }
    }

    public class Rule52GetColumnsRequest
    {
        public int    ClientId  { get; set; }
        public string TableName { get; set; } = "";
        public string TableRole { get; set; } = "";
    }

    public class Rule52VerifyResult
    {
        public bool    Success           { get; set; }
        public int     ValpacRecordCount { get; set; }
        public int     ProdRecordCount   { get; set; }
        public string? Error             { get; set; }
    }

    public class Rule52SqlResult
    {
        public bool   Success { get; set; }
        public string Sql     { get; set; } = "";
        public string? Error  { get; set; }
    }
}
