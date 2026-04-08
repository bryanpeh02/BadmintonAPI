using BadmintonFYP.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BadmintonFYP.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AnnouncementsController : ControllerBase
    {
        private readonly BadmintonDbContext _context;

        public AnnouncementsController(BadmintonDbContext context)
        {
            _context = context;
        }

        // GET /api/announcements/active
        [HttpGet("active")]
        public async Task<IActionResult> GetActiveAnnouncements()
        {
            var active = await _context.Announcements
                .Where(a => a.IsActive)
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync();
            return Ok(active);
        }

        // GET /api/announcements
        [HttpGet]
        public async Task<IActionResult> GetAllAnnouncements([FromQuery] int adminId)
        {
            var admin = await _context.Users.FindAsync(adminId);
            if (admin == null || (admin.Role != "Admin" && admin.Role != "SuperAdmin"))
            {
                return StatusCode(403, new { Message = "Unauthorized." });
            }

            var all = await _context.Announcements
                .OrderByDescending(a => a.CreatedAt)
                .ToListAsync();
            return Ok(all);
        }

        // POST /api/announcements
        [HttpPost]
        public async Task<IActionResult> CreateAnnouncement([FromQuery] int adminId, [FromBody] AnnouncementRequest request)
        {
            var admin = await _context.Users.FindAsync(adminId);
            if (admin == null || (admin.Role != "Admin" && admin.Role != "SuperAdmin"))
            {
                return StatusCode(403, new { Message = "Unauthorized." });
            }

            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return BadRequest(new { Message = "Message is required." });
            }

            var announcement = new Announcement
            {
                Message = request.Message,
                IsActive = true,
                CreatedAt = DateTime.Now
            };

            _context.Announcements.Add(announcement);
            await _context.SaveChangesAsync();

            return Ok(new { Message = "Announcement created successfully.", Announcement = announcement });
        }

        // DELETE /api/announcements/{id}
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteAnnouncement(int id, [FromQuery] int adminId)
        {
            var admin = await _context.Users.FindAsync(adminId);
            if (admin == null || (admin.Role != "Admin" && admin.Role != "SuperAdmin"))
            {
                return StatusCode(403, new { Message = "Unauthorized." });
            }

            var announcement = await _context.Announcements.FindAsync(id);
            if (announcement == null)
            {
                return NotFound();
            }

            _context.Announcements.Remove(announcement);
            await _context.SaveChangesAsync();

            return Ok(new { Message = "Announcement deleted successfully." });
        }
    }

    public class AnnouncementRequest
    {
        public string Message { get; set; }
    }
}
