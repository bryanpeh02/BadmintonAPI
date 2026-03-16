using BadmintonFYP.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BadmintonFYP.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EventsController : ControllerBase
    {
        private readonly BadmintonDbContext _context;

        public EventsController(BadmintonDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> GetActiveEvents()
        {
            var events = await _context.Events
                .Include(e => e.Reservations)
                    .ThenInclude(r => r.User)
                .Where(e => e.Status != "Cancelled" && e.Status != "Completed")
                .OrderBy(e => e.EventDate)
                .ToListAsync();

            var result = events.Select(e =>
            {
                var activeReservations = e.Reservations.Where(r => r.Status == "Active");
                var takenSlots = activeReservations.Sum(r => r.SlotsCount);
                int availableSlots = e.TotalSlots - takenSlots;

                if (availableSlots <= 0)
                {
                    e.Status = "Full";
                    // If you want to persist the "Full" status in the database, 
                    // you can uncomment the lines below:
                    // _context.Update(e);
                }

                return new
                {
                    e.EventId,
                    e.Title,
                    e.Description,
                    e.EventDate,
                    e.TotalSlots,
                    AvailableSlots = availableSlots,
                    e.Status,
                    e.CreatedAt,
                    Reservations = e.Reservations.Select(r => new
                    {
                        r.ReservationId,
                        r.UserId,
                        User = new
                        {
                            r.User.Username,
                            r.User.Phone,
                            r.User.Role,
                            r.User.ViolationCount,
                            r.User.IsBlacklisted,
                            r.User.IsMonthlyMember
                        },
                        r.SlotsCount,
                        r.Status,
                        r.QueuePosition,
                        r.IsPaid,
                        r.GuestNames,
                        r.CreatedAt
                    })
                };
            }).ToList();

            // Uncomment if you uncommented _context.Update(e) above
            // await _context.SaveChangesAsync();

            return Ok(result);
        }

        // --- NEW ADMIN CRUD ENDPOINTS --- //

        // POST /api/events
        [HttpPost]
        public async Task<IActionResult> CreateEvent([FromBody] CreateEventRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Title) || request.TotalSlots <= 0)
            {
                return BadRequest(new { Message = "Valid title and total slots are required." });
            }

            int preBookCount = request.PreSelectedMemberIds?.Count ?? 0;
            if (request.TotalSlots < preBookCount)
            {
                return BadRequest(new { Message = "Event capacity cannot be smaller than the number of pre-selected VIPs." });
            }

            var newEvent = new Event
            {
                Title = request.Title,
                Description = request.Description,
                EventDate = request.EventDate ?? DateTime.Now.AddDays(1),
                TotalSlots = request.TotalSlots,
                Price = request.Price ?? 0m,
                Status = "Open", // Default status as requested
                CreatedAt = DateTime.Now
            };

            _context.Events.Add(newEvent);
            await _context.SaveChangesAsync();
            
            if (request.PreSelectedMemberIds != null && request.PreSelectedMemberIds.Any())       
            {
                foreach (var userId in request.PreSelectedMemberIds)
                {
                    // Verify the user exists before binding to avoid orphaned Foreign Keys
                    var vipUser = await _context.Users.FindAsync(userId);
                    if (vipUser != null)
                    {
                        var reservation = new Reservation
                        {
                            EventId = newEvent.EventId,
                            UserId = userId,
                            SlotsCount = 1,
                            Status = "Active",
                            IsPaid = vipUser.IsMonthlyMember == true
                        };
                        _context.Reservations.Add(reservation);
                    }
                }
                await _context.SaveChangesAsync();
            }

            return Ok(new { Message = "Event created successfully.", EventId = newEvent.EventId });
        }

        // PUT /api/events/{id}
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateEvent(int id, [FromBody] UpdateEventRequest request)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var evt = await _context.Events
                    .Include(e => e.Reservations)
                    .FirstOrDefaultAsync(e => e.EventId == id);

                if (evt == null)
                {
                    return NotFound(new { Message = "Event not found." });
                }

                if (!string.IsNullOrWhiteSpace(request.Title))
                    evt.Title = request.Title;

                if (request.Description != null)
                    evt.Description = request.Description;

                if (request.EventDate.HasValue)
                    evt.EventDate = request.EventDate.Value;

                bool capacityIncreased = false;
                int addedSlots = 0;

                if (request.TotalSlots.HasValue && request.TotalSlots > 0)
                {
                    int oldTotal = evt.TotalSlots;
                    evt.TotalSlots = request.TotalSlots.Value;
                    int slotDifference = evt.TotalSlots - oldTotal;

                    if (slotDifference > 0)
                    {
                        capacityIncreased = true;
                        addedSlots = slotDifference;
                    }

                    // Auto-Status Fix (UX Improvement)
                    int occupiedSlots = evt.Reservations.Where(r => r.Status == "Active").Sum(r => r.SlotsCount);
                    int availableSlots = evt.TotalSlots - occupiedSlots;

                    if (availableSlots > 0) evt.Status = "Open";
                    else evt.Status = "Full";
                }

                if (request.Price.HasValue) evt.Price = request.Price.Value;
                if (!string.IsNullOrEmpty(request.Status))
                {
                    // Allow manual override if explicitly provided in request
                    evt.Status = request.Status;
                }

                _context.Events.Update(evt);
                await _context.SaveChangesAsync();

                // Auto-bump Logic for Capacity Increase
                if (capacityIncreased && addedSlots > 0)
                {
                    int occupiedSlots = evt.Reservations.Where(r => r.Status == "Active").Sum(r => r.SlotsCount);
                    int availableSlots = evt.TotalSlots - occupiedSlots;

                    if (availableSlots > 0)
                    {
                        var waitingList = evt.Reservations
                            .Where(r => r.Status == "Waiting" && r.QueuePosition != null)
                            .OrderBy(r => r.QueuePosition)
                            .ToList();

                        foreach (var waiting in waitingList)
                        {
                            if (availableSlots <= 0) break;

                            if (availableSlots >= waiting.SlotsCount)
                            {
                                // Bump entirely to Active
                                waiting.Status = "Active";
                                waiting.QueuePosition = null;
                                _context.Reservations.Update(waiting);
                                availableSlots -= waiting.SlotsCount;
                            }
                            else
                            {
                                // Partial bump: Create new active reservation, reduce waiting slots
                                var newlyActiveRes = new Reservation
                                {
                                    EventId = waiting.EventId,
                                    UserId = waiting.UserId,
                                    SlotsCount = availableSlots,
                                    Status = "Active",
                                    QueuePosition = null
                                };
                                _context.Reservations.Add(newlyActiveRes);

                                waiting.SlotsCount -= availableSlots;
                                _context.Reservations.Update(waiting);
                                availableSlots = 0;
                            }
                        }
                        await _context.SaveChangesAsync();
                        
                        // Re-sequence the Queue smoothly
                        var remainingWaitlist = evt.Reservations
                            .Where(r => r.Status == "Waiting" && r.QueuePosition != null)
                            .OrderBy(r => r.QueuePosition)
                            .ToList();

                        int newPos = 1;
                        foreach (var r in remainingWaitlist)
                        {
                            if (r.QueuePosition != newPos)
                            {
                                r.QueuePosition = newPos;
                                _context.Reservations.Update(r);
                            }
                            newPos++;
                        }
                        await _context.SaveChangesAsync();
                    }
                }

                await transaction.CommitAsync();
                return Ok(new { Message = "Event updated successfully.", EventId = evt.EventId });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { Message = "An error occurred while updating the event.", Details = ex.Message });
            }
        }

        // DELETE /api/events/{id}
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteEvent(int id)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var evt = await _context.Events
                    .Include(e => e.Reservations)
                    .FirstOrDefaultAsync(e => e.EventId == id);

                if (evt == null)
                {
                    return NotFound(new { Message = "Event not found." });
                }

                if (evt.Reservations.Any())
                {
                    // Specifically delete or cancel all associated reservations to uphold FK constraints
                    _context.Reservations.RemoveRange(evt.Reservations);
                }

                _context.Events.Remove(evt);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(new { Message = "Event and associated reservations deleted successfully." });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { Message = "An error occurred while deleting the event.", Details = ex.Message });
            }
        }
    }

    public class CreateEventRequest
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public DateTime? EventDate { get; set; }
        public int TotalSlots { get; set; }
        public decimal? Price { get; set; }
        public List<int>? PreSelectedMemberIds { get; set; }
    }

    public class UpdateEventRequest
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public DateTime? EventDate { get; set; }
        public int? TotalSlots { get; set; }
        public decimal? Price { get; set; }
        public string Status { get; set; }
    }
}
