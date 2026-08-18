using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace HemisAudit.Controllers
{
    // Catch-all target for both UseStatusCodePagesWithReExecute (routing 404s, and every
    // controller action's `return NotFound()`/`return Forbid()`) and UseExceptionHandler (any
    // unhandled exception that somehow reaches the pipeline outside FriendlyExceptionFilter's
    // reach). Without this, those responses fell through to a bare, bodyless status code, which
    // browsers render as their own generic "this page can't be found" chrome instead of anything
    // from the app - allow-anonymous since the failure can happen before/without authentication.
    [AllowAnonymous]
    [Route("Error")]
    public class ErrorController : Controller
    {
        [Route("{statusCode:int?}")]
        public IActionResult Index(int? statusCode)
        {
            var exceptionFeature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
            var effectiveStatusCode = statusCode ?? (exceptionFeature != null ? 500 : 404);

            Response.StatusCode = effectiveStatusCode;
            ViewBag.StatusCode = effectiveStatusCode;
            ViewBag.IsAuthenticated = User.Identity?.IsAuthenticated == true;
            return View();
        }
    }
}
