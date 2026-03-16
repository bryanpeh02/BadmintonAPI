using BadmintonFYP.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BadmintonFYP.Api.Services;

namespace BadmintonFYP.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UsersController : ControllerBase
    {
        private readonly BadmintonDbContext _context;
        private readonly IWhatsAppService _whatsAppService;

        public UsersController(BadmintonDbContext context, IWhatsAppService whatsAppService)
        {
            _context = context;
            _whatsAppService = whatsAppService;
        }

        // ── RBAC helpers ──────────────────────────────────────────────────────
        private static bool IsAdminOrSuperAdmin(User u) =>
            u.Role == "Admin" || u.Role == "SuperAdmin";

        private static bool IsPrivilegedTarget(User target) =>
            target.Role == "Admin" || target.Role == "SuperAdmin";

        /// <summary>
        /// Returns a 403 result if <paramref name="caller"/> is only an Admin
        /// but <paramref name="target"/> is an Admin/SuperAdmin. Returns null on success.
        /// </summary>
        private IActionResult? CheckSuperAdminRequired(User caller, User target)
        {
            if (IsPrivilegedTarget(target) && caller.Role != "SuperAdmin")
                return StatusCode(403, new { Message = "Only SuperAdmins can modify other admins." });
            return null;
        }
        // ─────────────────────────────────────────────────────────────────────

        // GET /api/users
        [HttpGet]
        public async Task<IActionResult> GetAllUsers()
        {
            var users = await _context.Users
                .Select(u => new 
                {
                    u.UserId,
                    u.Username,
                    u.Phone,
                    u.Role,
                    u.IsMonthlyMember,
                    u.ViolationCount,
                    u.IsBlacklisted
                })
                .ToListAsync();

            return Ok(users);
        }

        // POST /api/users/{userId}/penalize
        [HttpPost("{userId}/penalize")]
        public async Task<IActionResult> PenalizeUser(int userId, [FromQuery] int adminId)
        {
            var admin = await _context.Users.FindAsync(adminId);
            if (admin == null || !IsAdminOrSuperAdmin(admin))
                return StatusCode(403, new { Message = "Unauthorized. Only Admins can penalize users." });

            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                return NotFound(new { Message = "User not found." });

            var privilegeCheck = CheckSuperAdminRequired(admin, user);
            if (privilegeCheck != null) return privilegeCheck;

            // Increment violation count
            user.ViolationCount = (user.ViolationCount ?? 0) + 1;

            // Fetch dynamic MaxViolations threshold from SystemSettings
            var settings = await _context.SystemSettings.FirstOrDefaultAsync()
                           ?? new SystemSetting { MaxViolations = 3 };

            if (user.ViolationCount >= settings.MaxViolations)
            {
                user.IsBlacklisted = true;
            }

            _context.Users.Update(user);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                Message = user.IsBlacklisted == true ? "User has been blacklisted!" : "User penalized successfully.",
                UserId = user.UserId,
                Username = user.Username,
                ViolationCount = user.ViolationCount,
                IsBlacklisted = user.IsBlacklisted
            });
        }
        // POST /api/users/{userId}/unfreeze
        [HttpPost("{userId}/unfreeze")]
        public async Task<IActionResult> UnfreezeUser(int userId, [FromQuery] int adminId)
        {
            var admin = await _context.Users.FindAsync(adminId);
            if (admin == null || !IsAdminOrSuperAdmin(admin))
                return StatusCode(403, new { Message = "Unauthorized. Only Admins can unfreeze users." });

            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                return NotFound(new { Message = "User not found." });

            var privilegeCheck = CheckSuperAdminRequired(admin, user);
            if (privilegeCheck != null) return privilegeCheck;

            // Unfreeze logic
            user.IsBlacklisted = false;
            user.ViolationCount = 0;

            _context.Users.Update(user);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                Message = "User has been unfrozen and violations reset.",
                UserId = user.UserId,
                Username = user.Username,
                ViolationCount = user.ViolationCount,
                IsBlacklisted = user.IsBlacklisted
            });
        }

        // --- NEW ADMIN CRUD ENDPOINTS --- //

        // GET /api/users/{id}
        [HttpGet("{id}")]
        public async Task<IActionResult> GetUserById(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null)
            {
                return NotFound(new { Message = "User not found." });
            }

            return Ok(new 
            {
                user.UserId,
                user.Username,
                user.Phone,
                user.Role,
                user.IsMonthlyMember,
                user.ViolationCount,
                user.IsBlacklisted,
                user.CreatedAt
            });
        }

        // POST /api/users
        [HttpPost]
        public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Phone))
            {
                return BadRequest(new { Message = "Username and phone are required." });
            }

            if (await _context.Users.AnyAsync(u => u.Phone == request.Phone))
            {
                return BadRequest(new { Message = "Phone number is already registered." });
            }

            var newUser = new User
            {
                Username = request.Username,
                Phone = request.Phone,
                PasswordHash = string.IsNullOrWhiteSpace(request.Password) ? "123456" : request.Password,
                Role = string.IsNullOrWhiteSpace(request.Role) ? "Player" : request.Role,
                IsMonthlyMember = request.IsMonthlyMember,
                ViolationCount = 0,
                IsBlacklisted = false,
                CreatedAt = DateTime.Now
            };

            _context.Users.Add(newUser);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetUserById), new { id = newUser.UserId }, new 
            {
                newUser.UserId,
                newUser.Username,
                newUser.Phone,
                newUser.Role,
                newUser.IsMonthlyMember
            });
        }

        // PUT /api/users/{id}
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateUser(int id, [FromQuery] int adminId, [FromBody] UpdateUserRequest request)
        {
            var admin = await _context.Users.FindAsync(adminId);
            if (admin == null || !IsAdminOrSuperAdmin(admin))
                return StatusCode(403, new { Message = "Unauthorized. Only Admins can update users." });

            var user = await _context.Users.FindAsync(id);
            if (user == null)
                return NotFound(new { Message = "User not found." });

            var privilegeCheck = CheckSuperAdminRequired(admin, user);
            if (privilegeCheck != null) return privilegeCheck;

            if (!string.IsNullOrWhiteSpace(request.Username))
                user.Username = request.Username;

            if (!string.IsNullOrWhiteSpace(request.Phone))
            {
                // Check if new phone conflicts with an existing user
                if (await _context.Users.AnyAsync(u => u.Phone == request.Phone && u.UserId != id))
                {
                    return BadRequest(new { Message = "Phone number is already registered to another user." });
                }
                user.Phone = request.Phone;
            }

            if (!string.IsNullOrWhiteSpace(request.Role))
                user.Role = request.Role;

            if (request.IsMonthlyMember.HasValue)
                user.IsMonthlyMember = request.IsMonthlyMember.Value;

            _context.Users.Update(user);
            await _context.SaveChangesAsync();

            return Ok(new { Message = "User updated successfully.", user.UserId, user.Username, user.Phone, user.Role, user.IsMonthlyMember });
        }

        // PUT /api/users/{id}/reset-password
        [HttpPut("{id}/reset-password")]
        public async Task<IActionResult> ResetPassword(int id, [FromQuery] int adminId)
        {
            var admin = await _context.Users.FindAsync(adminId);
            if (admin == null || !IsAdminOrSuperAdmin(admin))
                return StatusCode(403, new { Message = "Unauthorized. Only Admins can reset passwords." });

            var user = await _context.Users.FindAsync(id);
            if (user == null)
                return NotFound(new { Message = "User not found." });

            var privilegeCheck = CheckSuperAdminRequired(admin, user);
            if (privilegeCheck != null) return privilegeCheck;

            user.PasswordHash = "123456";
            _context.Users.Update(user);
            await _context.SaveChangesAsync();

            return Ok(new { Message = $"Password for '{user.Username}' has been reset to the default." });
        }

        // PUT /api/users/{id}/change-password
        [HttpPut("{id}/change-password")]
        public async Task<IActionResult> ChangePassword(int id, [FromBody] ChangePasswordRequest request)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null)
            {
                return NotFound(new { Message = "User not found." });
            }

            if (user.PasswordHash != request.OldPassword)
            {
                return BadRequest(new { Message = "Incorrect current password." });
            }

            if (string.IsNullOrWhiteSpace(request.NewPassword))
            {
                return BadRequest(new { Message = "New password cannot be empty." });
            }

            user.PasswordHash = request.NewPassword;
            _context.Users.Update(user);
            await _context.SaveChangesAsync();

            return Ok(new { Message = "Password changed successfully." });
        }

        // DELETE /api/users/{id}
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteUser(int id, [FromQuery] int adminId)
        {
            var admin = await _context.Users.FindAsync(adminId);
            if (admin == null || !IsAdminOrSuperAdmin(admin))
                return StatusCode(403, new { Message = "Unauthorized. Only Admins can delete users." });

            var user = await _context.Users
                .Include(u => u.Reservations)
                .FirstOrDefaultAsync(u => u.UserId == id);

            if (user == null)
                return NotFound(new { Message = "User not found." });

            var privilegeCheck = CheckSuperAdminRequired(admin, user);
            if (privilegeCheck != null) return privilegeCheck;

            // Explictly remove all associated reservations to prevent Foreign Key Constraint deletion errors
            if (user.Reservations.Any())
            {
                _context.Reservations.RemoveRange(user.Reservations);
            }

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();

            return Ok(new { Message = "User deleted successfully." });
        }

        [HttpPost("broadcast")]
        public async Task<IActionResult> BroadcastMessage([FromBody] BroadcastRequest request)
        {
            if (request.UserIds == null || !request.UserIds.Any())
                return BadRequest(new { message = "No users selected." });

            if (string.IsNullOrEmpty(request.Message))
                return BadRequest(new { message = "Message content is empty." });

            foreach (var userId in request.UserIds)
            {
                var user = await _context.Users.FindAsync(userId);
                if (user != null && !string.IsNullOrEmpty(user.Phone))
                {
                    // Format the message with a professional header
                    string formattedMsg = $"📢 *OFFICIAL ANNOUNCEMENT*\n\n{request.Message}";
                    
                    // Call your existing WhatsAppService
                    await _whatsAppService.SendMessageAsync(user.Phone, formattedMsg);
                }
            }

            return Ok(new { message = "Broadcast sent successfully!" });
        }
    }

    public class BroadcastRequest
    {
        public List<int> UserIds { get; set; }
        public string Message { get; set; }
    }

    public class CreateUserRequest
    {
        public string Username { get; set; }
        public string Phone { get; set; }
        public string Role { get; set; }
        public bool IsMonthlyMember { get; set; }
        public string? Password { get; set; }
    }

    public class ChangePasswordRequest
    {
        public string OldPassword { get; set; }
        public string NewPassword { get; set; }
    }

    public class UpdateUserRequest
    {
        public string Username { get; set; }
        public string Phone { get; set; }
        public string Role { get; set; }
        public bool? IsMonthlyMember { get; set; }
    }
}
