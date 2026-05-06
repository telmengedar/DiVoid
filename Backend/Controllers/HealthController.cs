using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace apikeyservice.Controllers;

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
    public IActionResult CheckHealth()
    {
        return Ok(Assembly.GetExecutingAssembly().GetName().Name);
    }
}