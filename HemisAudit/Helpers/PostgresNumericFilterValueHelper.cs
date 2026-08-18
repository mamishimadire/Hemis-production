namespace HemisAudit.Helpers
{
    // Postgres-syntax counterpart to NumericFilterValueHelper.BuildNormalizedSqlExpression, which
    // is T-SQL only (PATINDEX/LEN/bracket-wildcard LIKE) and stays untouched since it's still used
    // by rules that haven't migrated off a live SQL Server connection. ParseValues and
    // NormalizeNumericLikeValue are pure C# string logic with no SQL dialect involved, so rules
    // reading uploaded Postgres data keep calling those directly on NumericFilterValueHelper.
    public static class PostgresNumericFilterValueHelper
    {
        public static string BuildNormalizedSqlExpression(string trimmedExpression) =>
            $@"CASE
    WHEN {trimmedExpression} = '' THEN ''
    WHEN {trimmedExpression} ~ '[^0-9]' THEN {trimmedExpression}
    WHEN {trimmedExpression} ~ '^0*$' THEN '0'
    ELSE regexp_replace({trimmedExpression}, '^0+', '')
END";
    }
}
