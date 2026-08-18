using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Npgsql;

namespace HemisAudit.Filters
{
    // Registered globally (Program.cs) so no controller — across all 61+ audit-rule modules —
    // has to remember to catch exceptions itself. Without this, an unhandled exception (e.g. a
    // transient DNS/network blip talking to Supabase) rendered as a raw .NET stack trace
    // straight into the page, since the app runs with ASPNETCORE_ENVIRONMENT=Development and
    // therefore skips the production UseExceptionHandler branch in Program.cs. This filter runs
    // inside the MVC pipeline, before that middleware would ever see the exception, so it
    // applies the same friendly behavior in both Development and Production.
    public class FriendlyExceptionFilter : IExceptionFilter
    {
        private readonly ILogger<FriendlyExceptionFilter> _logger;

        public FriendlyExceptionFilter(ILogger<FriendlyExceptionFilter> logger)
        {
            _logger = logger;
        }

        public void OnException(ExceptionContext context)
        {
            _logger.LogError(context.Exception, "Unhandled exception in {Path}", context.HttpContext.Request.Path);

            var message = DescribeForUser(context.Exception);
            var isAjax = string.Equals(context.HttpContext.Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(context.HttpContext.Request.Headers["Accept"], "application/json", StringComparison.OrdinalIgnoreCase)
                || context.HttpContext.Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true;

            if (isAjax)
            {
                context.Result = new JsonResult(new { success = false, error = message })
                {
                    StatusCode = IsTransient(context.Exception) ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError
                };
            }
            else
            {
                var tempDataFactory = context.HttpContext.RequestServices.GetService(typeof(ITempDataDictionaryFactory)) as ITempDataDictionaryFactory;
                if (tempDataFactory != null)
                {
                    var tempData = tempDataFactory.GetTempData(context.HttpContext);
                    tempData["Error"] = message;
                }
                context.Result = new RedirectToActionResult("Index", "Dashboard", null);
            }

            context.ExceptionHandled = true;
        }

        private static bool IsTransient(Exception ex) =>
            ex is NpgsqlException
            || ex is System.Net.Sockets.SocketException
            || ex is TimeoutException
            || ex is System.IO.IOException
            || (ex.InnerException != null && IsTransient(ex.InnerException));

        private static string DescribeForUser(Exception ex) =>
            IsTransient(ex)
                ? "Temporarily unable to reach the database. Please try again in a moment."
                : "Something went wrong processing that request. Please try again, and contact support if this keeps happening.";
    }
}
