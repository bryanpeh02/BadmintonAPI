using BadmintonFYP.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BadmintonFYP.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class SettingsController : ControllerBase
    {
        private readonly BadmintonDbContext _context;

        public SettingsController(BadmintonDbContext context)
        {
            _context = context;
        }

        // GET /api/settings
        [HttpGet]
        public async Task<IActionResult> GetSettings()
        {
            var settings = await _context.SystemSettings.FirstOrDefaultAsync();
            if (settings == null)
            {
                // Should not happen due to seeding, but safety first
                settings = new SystemSetting { Id = 1, LateCancelHours = 3, MaxViolations = 3 };
                _context.SystemSettings.Add(settings);
                await _context.SaveChangesAsync();
            }
            return Ok(settings);
        }

        // PUT /api/settings
        [HttpPut]
        public async Task<IActionResult> UpdateSettings([FromQuery] int adminId, [FromBody] SystemSetting request)
        {
            var admin = await _context.Users.FindAsync(adminId);
            if (admin == null || admin.Role != "Admin")
            {
                return StatusCode(403, new { Message = "Unauthorized. Only Admins can update system settings." });
            }

            var settings = await _context.SystemSettings.FirstOrDefaultAsync();
            if (settings == null)
            {
                settings = new SystemSetting { Id = 1 };
                _context.SystemSettings.Add(settings);
            }

            settings.LateCancelHours = request.LateCancelHours;
            settings.MaxViolations = request.MaxViolations;

            _context.SystemSettings.Update(settings);
            await _context.SaveChangesAsync();

            return Ok(new { Message = "System settings updated successfully.", Settings = settings });
        }
    }
}
