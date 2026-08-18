using HemisAudit.Helpers;

namespace HemisAudit.ViewModels
{
    public class Rule36GetColumnsRequest
    {
        public int ClientId { get; set; }
        public string TableName { get; set; } = "";
        public bool IsStudTable { get; set; }
    }

    public class Rule36ColumnSelectionResult
    {
        public bool Success { get; set; }
        public List<string> Columns { get; set; } = new();
        public string? AutoSelected { get; set; }
        public string? Error { get; set; }
    }

    public class Rule36VerifyRequest
    {
        public int ClientId { get; set; }
        public string StudTable { get; set; } = "";
        public string DeceasedTable { get; set; } = "";
        public string StudColumn { get; set; } = "";
        public string DeceasedColumn { get; set; } = "";
    }

    public class Rule36VerifyResult
    {
        public bool Success { get; set; }
        public int StudTotal { get; set; }
        public int DeceasedTotal { get; set; }
        public int MatchingRecords { get; set; }
        public string? Error { get; set; }
    }

    public class Rule36ValidationRequest
    {
        public int ClientId { get; set; }
        public int? RunId { get; set; }
        public string StudTable { get; set; } = "";
        public string DeceasedTable { get; set; } = "";
        public string StudColumn { get; set; } = "";
        public string DeceasedColumn { get; set; } = "";
    }

    public class Rule36ValidationRowRecord
    {
        public int ValidationNumber { get; set; }
        public string ValidationResult { get; set; } = "";
        public string? ExceptionReason { get; set; }
        public string StudentId { get; set; } = "";
        public Dictionary<string, string?> AdditionalColumns { get; set; } = new();
    }

    public class Rule36ExceptionRecord
    {
        public int ValidationNumber { get; set; }
        public string StudentId { get; set; } = "";
        public string ExceptionReason { get; set; } = "";
        public string ValidationResult { get; set; } = "";
        public Dictionary<string, string?> AdditionalColumns { get; set; } = new();
    }

    public class Rule36ValidationSummary
    {
        public bool Success { get; set; }
        public int TotalValidated { get; set; }
        public int PassCount { get; set; }
        public int FailCount { get; set; }
        public decimal ExceptionRate { get; set; }
        public string Status { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public string StudTable { get; set; } = "";
        public string DeceasedTable { get; set; } = "";
        public string StudColumn { get; set; } = "";
        public string DeceasedColumn { get; set; } = "";
        public int ClientId { get; set; }
        public int? SavedRunId { get; set; }
        public bool RowsTruncated { get; set; }
        public List<Rule36ValidationRowRecord> ValidationRows { get; set; } = new();
        public List<Rule36ExceptionRecord> Exceptions { get; set; } = new();
        public string? Warning { get; set; }
        public string? Error { get; set; }
    }

    public class Rule36WorkspaceSaveResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public bool SignoffsCleared { get; set; }
        public int? ClearedSignoffCount { get; set; }
        public Rule36WorkspaceStateViewModel? Workspace { get; set; }
        public string? Error { get; set; }
    }
}
