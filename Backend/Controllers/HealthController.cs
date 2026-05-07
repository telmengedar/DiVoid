using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

/// <summary>
/// provides endpoints for health checks
/// </summary>
[Route("api/health")]
[ApiController]
public class HealthController : ControllerBase
{

    /// <summary>
    /// checks whether the service is responding
    /// </summary>
    /// <returns></returns>
    [ProducesResponseType(200)]
    [HttpGet]
    [AllowAnonymous]
    public IActionResult CheckHealth()
    {
        return Ok(Assembly.GetExecutingAssembly().GetName().Name);
    }
}