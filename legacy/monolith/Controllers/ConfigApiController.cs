using LoginAndReg.Models.Api;
using LoginAndReg.Models.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LoginAndReg.Controllers
{
    /// <summary>
    /// Public client configuration for the SPA (step 3c.4): the upload
    /// limit and the extensions the SPA accepts. The SPA reads it once and
    /// validates files locally before sending, so an over-limit or wrong
    /// file never reaches the network. Public on purpose: the payload is a
    /// size limit and an extension list — no sensitive data.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("api/config")]
    public class ConfigApiController : ControllerBase
    {
        private readonly UploadOptions _uploadOptions;

        public ConfigApiController(IOptions<UploadOptions> uploadOptions)
        {
            _uploadOptions = uploadOptions.Value;
        }

        /// <summary>
        /// GET /api/config — upload limits and accepted extensions for the SPA.
        /// The extension list is the same single source of truth the server
        /// side validates against (FileStorageService, step 4.0): the SPA and
        /// the server can never disagree.
        /// </summary>
        [HttpGet]
        [ProducesResponseType(typeof(ConfigDto), StatusCodes.Status200OK)]
        public IActionResult Get()
        {
            return Ok(new ConfigDto
            {
                MaxUploadBytes = _uploadOptions.MaxBytes,
                AllowedUploadExtensions = _uploadOptions.AllowedExtensions.ToArray()
            });
        }
    }
}
