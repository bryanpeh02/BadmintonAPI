using BadmintonFYP.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BadmintonFYP.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly BadmintonDbContext _context;

        public AuthController(BadmintonDbContext context)
        {
            _context = context;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            if (string.IsNullOrEmpty(request.Phone) || string.IsNullOrEmpty(request.Password))
            {
                return BadRequest(new { Message = "Phone and Password are required." });
            }

            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Phone == request.Phone);

            if (user == null)
            {
                return Unauthorized(new { Message = "Invalid phone number or password." });
            }

            if (user.IsBlacklisted == true)
            {
                return StatusCode(403, new { Message = "Your account is blacklisted." });
            }

            // TODO: In production, use BCrypt or another secure hashing algorithm to compare passwords.
            // Example: if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            if (user.PasswordHash != request.Password)
            {
                return Unauthorized(new { Message = "Invalid phone number or password." });
            }

            return Ok(new
            {
                UserId = user.UserId,
                Username = user.Username,
                Role = user.Role,
                IsMonthlyMember = user.IsMonthlyMember
            });
        }
    }

    public class LoginRequest
    {
        public string Phone { get; set; } = null!;
        public string Password { get; set; } = null!;
    }
}
