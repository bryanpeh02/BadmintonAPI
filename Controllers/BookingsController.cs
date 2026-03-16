using BadmintonFYP.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BadmintonFYP.Api.Services;

namespace BadmintonFYP.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class BookingsController : ControllerBase
    {
        private readonly BadmintonDbContext _context;
        private readonly IWhatsAppService _whatsAppService;

        public BookingsController(BadmintonDbContext context, IWhatsAppService whatsAppService)
        {
            _context = context;
            _whatsAppService = whatsAppService;
        }

        [HttpPost]
        public async Task<IActionResult> CreateBooking([FromBody] BookingRequest request)
        {
            if (request.SlotsRequested <= 0)
            {
                return BadRequest(new { Message = "Slots requested must be greater than 0." });
            }

            var user = await _context.Users.FindAsync(request.UserId);
            if (user == null)
            {
                return NotFound(new { Message = "User not found." });
            }

            if (user.IsBlacklisted == true)
            {
                return StatusCode(403, new { Message = "Your account is blacklisted." });
            }

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var evt = await _context.Events
                    .Include(e => e.Reservations)
                    .FirstOrDefaultAsync(e => e.EventId == request.EventId);

                if (evt == null)
                {
                    return NotFound(new { Message = "Event not found." });
                }

                if (evt.Status == "Cancelled" || evt.Status == "Completed")
                {
                    return BadRequest(new { Message = "Cannot book for this event status." });
                }

                int occupiedSlots = evt.Reservations
                    .Where(r => r.Status == "Active")
                    .Sum(r => r.SlotsCount);

                int availableSlots = evt.TotalSlots - occupiedSlots;
                var createdReservations = new List<Reservation>();

                // Get max queue position safely
                var lastQueuePos = await _context.Reservations
                    .Where(r => r.EventId == request.EventId && r.QueuePosition != null)
                    .Select(r => r.QueuePosition)
                    .OrderByDescending(q => q)
                    .FirstOrDefaultAsync() ?? 0;
                int currentMaxQueue = lastQueuePos;

                bool isMonthlyMember = user.IsMonthlyMember == true;

                if (availableSlots >= request.SlotsRequested)
                {
                    // Scenario 1: Enough slots for entirely Active
                    var res = new Reservation
                    {
                        EventId = request.EventId,
                        UserId = request.UserId,
                        SlotsCount = request.SlotsRequested,
                        Status = "Active",
                        QueuePosition = null,
                        IsPaid = isMonthlyMember,
                        GuestNames = request.GuestNames
                    };
                    _context.Reservations.Add(res);
                    createdReservations.Add(res);
                }
                else if (availableSlots <= 0)
                {
                    // Scenario 2: Event is completely full, all go to Waiting list
                    var res = new Reservation
                    {
                        EventId = request.EventId,
                        UserId = request.UserId,
                        SlotsCount = request.SlotsRequested,
                        Status = "Waiting",
                        QueuePosition = currentMaxQueue + 1,
                        IsPaid = isMonthlyMember,
                        GuestNames = request.GuestNames
                    };
                    _context.Reservations.Add(res);
                    createdReservations.Add(res);
                }
                else
                {
                    // Scenario 3: Partial slots available (Split Reservation)
                    
                    // Create Active reservation for the available slots
                    var activeRes = new Reservation
                    {
                        EventId = request.EventId,
                        UserId = request.UserId,
                        SlotsCount = availableSlots,
                        Status = "Active",
                        QueuePosition = null,
                        IsPaid = isMonthlyMember,
                        GuestNames = request.GuestNames
                    };
                    _context.Reservations.Add(activeRes);
                    createdReservations.Add(activeRes);

                    // Create Waiting reservation for the remaining slots
                    var waitingRes = new Reservation
                    {
                        EventId = request.EventId,
                        UserId = request.UserId,
                        SlotsCount = request.SlotsRequested - availableSlots,
                        Status = "Waiting",
                        QueuePosition = currentMaxQueue + 1,
                        IsPaid = isMonthlyMember,
                        GuestNames = request.GuestNames
                    };
                    _context.Reservations.Add(waitingRes);
                    createdReservations.Add(waitingRes);
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                // Feature 1: Send WhatsApp Confirmation (Fix 1: Combined Partial Notification)
                try
                {
                    var activeCount = createdReservations.Where(r => r.Status == "Active").Sum(r => r.SlotsCount);
                    var waitingCount = createdReservations.Where(r => r.Status == "Waiting").Sum(r => r.SlotsCount);
                    
                    if (user != null && !string.IsNullOrEmpty(user.Phone))
                    {
                        string msg = $"✅ *Booking Processed!*\n" +
                                     $"🏸 *Event:* {evt.Title}\n" +
                                     $"✅ *Confirmed Slots:* {activeCount}";

                        if (waitingCount > 0)
                        {
                            msg += $"\n⏳ *Waitlisted Slots:* {waitingCount}";
                        }

                        await _whatsAppService.SendMessageAsync(user.Phone, msg);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Auto-WhatsApp Error: " + ex.Message);
                }

                return Ok(new
                {
                    Message = "Booking request processed.",
                    Reservations = createdReservations.Select(r => new
                    {
                        r.ReservationId,
                        r.EventId,
                        r.UserId,
                        r.SlotsCount,
                        r.Status,
                        r.QueuePosition,
                        r.IsPaid,
                        r.CreatedAt
                    })
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { Message = "An error occurred while processing the booking.", Details = ex.Message });
            }
        }
        [HttpPost("cancel")]
        public async Task<IActionResult> CancelBooking([FromBody] CancelBookingRequest request)
        {
            if (request.SlotsToCancel <= 0)
            {
                return BadRequest(new { Message = "Slots to cancel must be greater than 0." });
            }

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var reservation = await _context.Reservations
                    .Include(r => r.User)
                    .Include(r => r.Event)
                    .FirstOrDefaultAsync(r => r.ReservationId == request.ReservationId);

                if (reservation == null)
                {
                    return NotFound(new { Message = "Reservation not found." });
                }

                // Authorization: User can only cancel their own, unless Admin or SuperAdmin
                if (reservation.UserId != request.UserId)
                {
                    var requestUser = await _context.Users.FindAsync(request.UserId);
                    if (requestUser == null || (requestUser.Role != "Admin" && requestUser.Role != "SuperAdmin"))
                    {
                        return StatusCode(403, new { Message = "You are not authorized to cancel this booking." });
                    }
                }

                if (reservation.Status != "Active" && reservation.Status != "Waiting")
                {
                    return BadRequest(new { Message = "Only Active or Waiting reservations can be cancelled." });
                }

                if (request.SlotsToCancel > reservation.SlotsCount)
                {
                    return BadRequest(new { Message = "Cannot cancel more slots than originally booked." });
                }

                int eventId = reservation.EventId;
                bool wasActive = reservation.Status == "Active";
                int freedSlots = 0;

                // Partial vs Full Cancellation
                if (request.SlotsToCancel == reservation.SlotsCount)
                {
                    reservation.Status = "Cancelled";
                    reservation.QueuePosition = null;
                    if (wasActive) freedSlots = request.SlotsToCancel;
                }
                else
                {
                    reservation.SlotsCount -= request.SlotsToCancel;
                    if (wasActive) freedSlots = request.SlotsToCancel;
                }
                
                _context.Reservations.Update(reservation);

                // Check for Late Cancellation Penalty (dynamic settings)
                string returnMessage = "Cancellation processed successfully.";
                if (wasActive) // Only penalize if they held an active slot
                {
                     var settings = await _context.SystemSettings.FirstOrDefaultAsync() ?? new SystemSetting { LateCancelHours = 3, MaxViolations = 3 };
                     var hoursUntilEvent = (reservation.Event.EventDate - DateTime.Now).TotalHours;
                     
                     if (hoursUntilEvent < settings.LateCancelHours && hoursUntilEvent > 0)
                     {
                         // Apply penalty
                         var userToPenalize = reservation.User;
                         userToPenalize.ViolationCount = (userToPenalize.ViolationCount ?? 0) + 1;
                         
                         if (userToPenalize.ViolationCount >= settings.MaxViolations)
                         {
                             userToPenalize.IsBlacklisted = true;
                         }
                         
                         _context.Users.Update(userToPenalize);
                         returnMessage = $"Cancelled, but a violation was recorded due to late cancellation (less than {settings.LateCancelHours} hours).";
                     }
                }

                // Auto-bump Logic ONLY if Active slots were freed
                if (freedSlots > 0)
                {
                    var waitingList = await _context.Reservations
                        .Include(r => r.User) // Include User for WhatsApp
                        .Where(r => r.EventId == eventId && r.Status == "Waiting" && r.QueuePosition != null)
                        .OrderBy(r => r.QueuePosition)
                        .ToListAsync();

                    foreach (var waitingRes in waitingList)
                    {
                        if (freedSlots <= 0) break;

                        if (waitingRes.SlotsCount <= freedSlots)
                        {
                            // Bump entirely to Active
                            waitingRes.Status = "Active";
                            waitingRes.QueuePosition = null;
                            _context.Reservations.Update(waitingRes);
                            freedSlots -= waitingRes.SlotsCount;

                            // WhatsApp Alert for Waitlist Promotion (Fix 2: Standardized Promo Message)
                            try
                            {
                                if (waitingRes.User != null && !string.IsNullOrEmpty(waitingRes.User.Phone))
                                {
                                    string promoMsg = $"🎉 *GREAT NEWS from BHub!*\n\n" +
                                                      $"You have been promoted from the Waitlist to *ACTIVE* for:\n" +
                                                      $"🏸 *{reservation.Event.Title}*\n" +
                                                      $"🎟️ *Promoted Slots:* {waitingRes.SlotsCount}\n\n" +
                                                      $"Please make your payment to secure your spot! 🙏";
                                    await _whatsAppService.SendMessageAsync(waitingRes.User.Phone, promoMsg);
                                }
                            }
                            catch (Exception ex) { Console.WriteLine("Waitlist Promo Alert Error: " + ex.Message); }
                        }
                        else
                        {
                            // Split required: Promote some slots, keep rest in waiting
                            
                            // 1. Give them the available active slots
                            var newActiveRes = new Reservation
                            {
                                EventId = eventId,
                                UserId = waitingRes.UserId,
                                SlotsCount = freedSlots,
                                Status = "Active",
                                QueuePosition = null,
                                CreatedAt = DateTime.Now
                            };
                            _context.Reservations.Add(newActiveRes);

                            // WhatsApp Alert for Partial Promotion (Fix 2: Standardized Promo Message)
                            try
                            {
                                var partialUser = await _context.Users.FindAsync(waitingRes.UserId);
                                if (partialUser != null && !string.IsNullOrEmpty(partialUser.Phone))
                                {
                                    string promoMsg = $"🎉 *GREAT NEWS from BHub!*\n\n" +
                                                      $"You have been promoted from the Waitlist to *ACTIVE* for:\n" +
                                                      $"🏸 *{reservation.Event.Title}*\n" +
                                                      $"🎟️ *Promoted Slots:* {freedSlots}\n\n" +
                                                      $"Please make your payment to secure your spot! 🙏";
                                    await _whatsAppService.SendMessageAsync(partialUser.Phone, promoMsg);
                                }
                            }
                            catch (Exception ex) { Console.WriteLine("Waitlist Partial Promo Alert Error: " + ex.Message); }

                            // 2. Reduce their waiting slots
                            waitingRes.SlotsCount -= freedSlots;
                            _context.Reservations.Update(waitingRes);
                            
                            freedSlots = 0;
                            break;
                        }
                    }
                }

                // Re-calculate Queue Positions for those still waiting
                var remainingWaitlist = await _context.Reservations
                    .Where(r => r.EventId == eventId && r.Status == "Waiting" && r.QueuePosition != null)
                    .OrderBy(r => r.QueuePosition)
                    .ToListAsync();

                int currentQueue = 1;
                foreach (var wait in remainingWaitlist)
                {
                    if (wait.QueuePosition != currentQueue)
                    {
                        wait.QueuePosition = currentQueue;
                        _context.Reservations.Update(wait);
                    }
                    currentQueue++;
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(new { Message = returnMessage });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { Message = "An error occurred during cancellation.", Details = ex.Message });
            }
        }

        // --- NEW ADMIN OVERRIDE ENDPOINTS --- //

        // PUT /api/bookings/{reservationId}/admin
        [HttpPut("{reservationId}/admin")]
        public async Task<IActionResult> AdminUpdateBooking(int reservationId, [FromQuery] int adminId, [FromBody] AdminUpdateBookingRequest request)
        {
            var admin = await _context.Users.FindAsync(adminId);
            if (admin == null || admin.Role != "Admin")
            {
                return StatusCode(403, new { Message = "Unauthorized. Only Admins can override bookings." });
            }

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var reservation = await _context.Reservations
                    .Include(r => r.Event)
                    .ThenInclude(e => e.Reservations)
                    .FirstOrDefaultAsync(r => r.ReservationId == reservationId);

                if (reservation == null)
                {
                    return NotFound(new { Message = "Reservation not found." });
                }

                if (request.SlotsCount.HasValue && request.SlotsCount.Value > 0 && request.SlotsCount.Value != reservation.SlotsCount)
                {
                    int requestSlots = request.SlotsCount.Value;
                    int diff = requestSlots - reservation.SlotsCount;
                    
                    var evnt = reservation.Event;
                    int occupiedSlots = evnt.Reservations.Where(r => r.Status == "Active" && r.ReservationId != reservation.ReservationId).Sum(r => r.SlotsCount);
                    
                    if (reservation.Status == "Active")
                        occupiedSlots += reservation.SlotsCount; // Base it off current state before diff

                    int availableSlots = evnt.TotalSlots - occupiedSlots;

                    if (reservation.Status == "Active")
                    {
                        if (diff > 0)
                        {
                            // Increasing slots
                            if (availableSlots >= diff)
                            {
                                reservation.SlotsCount += diff;
                                _context.Reservations.Update(reservation);
                            }
                            else
                            {
                                // Not enough available slots. Split-Booking.
                                int slotsToActivate = availableSlots;
                                int slotsToWait = diff - slotsToActivate;
                                
                                if (slotsToActivate > 0)
                                {
                                    reservation.SlotsCount += slotsToActivate;
                                    _context.Reservations.Update(reservation);
                                }

                                if (slotsToWait > 0)
                                {
                                    var lastQueuePos = await _context.Reservations
                                        .Where(r => r.EventId == reservation.EventId && r.QueuePosition != null)
                                        .Select(r => r.QueuePosition)
                                        .OrderByDescending(q => q)
                                        .FirstOrDefaultAsync() ?? 0;

                                    var newWaitRecord = new Reservation
                                    {
                                        EventId = evnt.EventId,
                                        UserId = reservation.UserId,
                                        SlotsCount = slotsToWait,
                                        Status = "Waiting",
                                        QueuePosition = lastQueuePos + 1
                                    };
                                    _context.Reservations.Add(newWaitRecord);
                                }
                            }
                        }
                        else if (diff < 0)
                        {
                            // Decreasing slots. Auto-bump Waitlist.
                            int freedSlots = Math.Abs(diff);
                            reservation.SlotsCount -= freedSlots;
                            _context.Reservations.Update(reservation);
                            await _context.SaveChangesAsync(); // Commit partial state before waitlist parsing
                            
                            var waitingList = await _context.Reservations
                                .Include(r => r.User)
                                .Where(r => r.EventId == evnt.EventId && r.Status == "Waiting" && r.QueuePosition != null)
                                .OrderBy(r => r.QueuePosition)
                                .ToListAsync();

                            foreach (var waitingRes in waitingList)
                            {
                                if (freedSlots <= 0) break;

                                if (waitingRes.SlotsCount <= freedSlots)
                                {
                                    // Bump entirely to Active
                                    waitingRes.Status = "Active";
                                    waitingRes.QueuePosition = null;
                                    _context.Reservations.Update(waitingRes);
                                    
                                    // WhatsApp notification
                                    try
                                    {
                                        if (waitingRes.User != null && !string.IsNullOrEmpty(waitingRes.User.Phone))
                                        {
                                            string promoMsg = $"🎉 *GREAT NEWS from BHub!*\n\n" +
                                                              $"You have been promoted from the Waitlist to *ACTIVE* for:\n" +
                                                              $"🏸 *{evnt.Title}*\n" +
                                                              $"🎟️ *Promoted Slots:* {waitingRes.SlotsCount}\n\n" +
                                                              $"Please make your payment to secure your spot! 🙏";
                                            await _whatsAppService.SendMessageAsync(waitingRes.User.Phone, promoMsg);
                                        }
                                    }
                                    catch (Exception ex) { Console.WriteLine("Waitlist Promo Alert Error: " + ex.Message); }
                                    
                                    freedSlots -= waitingRes.SlotsCount;
                                }
                                else
                                {
                                    // Partial bump
                                    var newlyActiveRes = new Reservation
                                    {
                                        EventId = waitingRes.EventId,
                                        UserId = waitingRes.UserId,
                                        SlotsCount = freedSlots,
                                        Status = "Active",
                                        QueuePosition = null
                                    };
                                    _context.Reservations.Add(newlyActiveRes);

                                    // WhatsApp notification for partial bump
                                    try
                                    {
                                        var partialUser = waitingRes.User;
                                        if (partialUser != null && !string.IsNullOrEmpty(partialUser.Phone))
                                        {
                                            string promoMsg = $"🎉 *GREAT NEWS from BHub!*\n\n" +
                                                              $"You have been promoted from the Waitlist to *ACTIVE* for:\n" +
                                                              $"🏸 *{evnt.Title}*\n" +
                                                              $"🎟️ *Promoted Slots:* {freedSlots}\n\n" +
                                                              $"Please make your payment to secure your spot! 🙏";
                                            await _whatsAppService.SendMessageAsync(partialUser.Phone, promoMsg);
                                        }
                                    }
                                    catch (Exception ex) { Console.WriteLine("Waitlist Partial Promo Alert Error: " + ex.Message); }

                                    waitingRes.SlotsCount -= freedSlots;
                                    _context.Reservations.Update(waitingRes);
                                    freedSlots = 0;
                                }
                            }
                        }
                    }
                    else if (reservation.Status == "Waiting")
                    {
                         // If they are on the waiting list, they can increase/decrease their requested queue slots freely
                         reservation.SlotsCount = requestSlots;
                         _context.Reservations.Update(reservation);
                    }
                }

                if (!string.IsNullOrWhiteSpace(request.Status) && request.Status != reservation.Status)
                {
                    // Basic status overwrite manually injected by Admin
                    string oldStatus = reservation.Status;
                    reservation.Status = request.Status;
                    
                    if (request.Status == "Waiting" && oldStatus == "Active")
                    {
                         // Losing Active slots.
                         int freedSlots = reservation.SlotsCount;
                         var lastQueuePos = await _context.Reservations
                            .Where(r => r.EventId == reservation.EventId && r.QueuePosition != null)
                            .Select(r => r.QueuePosition)
                            .OrderByDescending(q => q)
                            .FirstOrDefaultAsync() ?? 0;
                         reservation.QueuePosition = lastQueuePos + 1;
                         _context.Reservations.Update(reservation);
                         await _context.SaveChangesAsync();

                         // Auto-bump the line to fill the void
                         var waitingList = await _context.Reservations
                            .Include(r => r.User)
                            .Where(r => r.EventId == reservation.EventId && r.Status == "Waiting" && r.QueuePosition != null && r.ReservationId != reservation.ReservationId)
                            .OrderBy(r => r.QueuePosition)
                            .ToListAsync();

                         foreach (var waitingRes in waitingList)
                         {
                             if (freedSlots <= 0) break;

                             if (waitingRes.SlotsCount <= freedSlots)
                             {
                                 waitingRes.Status = "Active";
                                 waitingRes.QueuePosition = null;
                                 _context.Reservations.Update(waitingRes);

                                 // WhatsApp notification
                                 try
                                 {
                                     if (waitingRes.User != null && !string.IsNullOrEmpty(waitingRes.User.Phone))
                                     {
                                         string promoMsg = $"🎉 *GREAT NEWS from BHub!*\n\n" +
                                                           $"You have been promoted from the Waitlist to *ACTIVE* for:\n" +
                                                           $"🏸 *{reservation.Event.Title}*\n" +
                                                           $"🎟️ *Promoted Slots:* {waitingRes.SlotsCount}\n\n" +
                                                           $"Please make your payment to secure your spot! 🙏";
                                         await _whatsAppService.SendMessageAsync(waitingRes.User.Phone, promoMsg);
                                     }
                                 }
                                 catch (Exception ex) { Console.WriteLine("Waitlist Promo Alert Error: " + ex.Message); }

                                 freedSlots -= waitingRes.SlotsCount;
                             }
                             else
                             {
                                 var newlyActiveRes = new Reservation
                                 {
                                     EventId = waitingRes.EventId,
                                     UserId = waitingRes.UserId,
                                     SlotsCount = freedSlots,
                                     Status = "Active",
                                     QueuePosition = null
                                 };
                                 _context.Reservations.Add(newlyActiveRes);

                                 // WhatsApp notification
                                 try
                                 {
                                     var partialUser = waitingRes.User;
                                     if (partialUser != null && !string.IsNullOrEmpty(partialUser.Phone))
                                     {
                                         string promoMsg = $"🎉 *GREAT NEWS from BHub!*\n\n" +
                                                           $"You have been promoted from the Waitlist to *ACTIVE* for:\n" +
                                                           $"🏸 *{reservation.Event.Title}*\n" +
                                                           $"🎟️ *Promoted Slots:* {freedSlots}\n\n" +
                                                           $"Please make your payment to secure your spot! 🙏";
                                         await _whatsAppService.SendMessageAsync(partialUser.Phone, promoMsg);
                                     }
                                 }
                                 catch (Exception ex) { Console.WriteLine("Waitlist Partial Promo Alert Error: " + ex.Message); }

                                 waitingRes.SlotsCount -= freedSlots;
                                 _context.Reservations.Update(waitingRes);
                                 freedSlots = 0;
                             }
                         }
                    }
                    else if (request.Status == "Active" && oldStatus == "Waiting")
                    {
                         // Force Active (Bypass capacities)
                         reservation.QueuePosition = null;
                         _context.Reservations.Update(reservation);
                    }
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(new { Message = "Booking successfully updated." });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { Message = "Database error updating booking.", Details = ex.Message });
            }
        }

        // DELETE /api/bookings/{reservationId}/admin
        [HttpDelete("{reservationId}/admin")]
        public async Task<IActionResult> AdminDeleteBooking(int reservationId, [FromQuery] int adminId)
        {
            var admin = await _context.Users.FindAsync(adminId);
            if (admin == null || admin.Role != "Admin")
            {
                return StatusCode(403, new { Message = "Unauthorized. Only Admins can force delete bookings." });
            }

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var reservation = await _context.Reservations.FindAsync(reservationId);
                if (reservation == null)
                {
                    return NotFound(new { Message = "Reservation not found." });
                }

                int eventId = reservation.EventId;
                bool wasActive = reservation.Status == "Active";
                int freedSlots = wasActive ? reservation.SlotsCount : 0;

                _context.Reservations.Remove(reservation);
                await _context.SaveChangesAsync(); // Commit the deletion so waitlist bump queries correctly

                // Auto-bump Logic exactly matching CancelBooking logic
                if (freedSlots > 0)
                {
                    var waitingList = await _context.Reservations
                        .Include(r => r.User)
                        .Include(r => r.Event)
                        .Where(r => r.EventId == eventId && r.Status == "Waiting" && r.QueuePosition != null)
                        .OrderBy(r => r.QueuePosition)
                        .ToListAsync();

                    foreach (var waitingRes in waitingList)
                    {
                        if (freedSlots <= 0) break;

                        if (waitingRes.SlotsCount <= freedSlots)
                        {
                            // Bump entirely to Active
                            waitingRes.Status = "Active";
                            waitingRes.QueuePosition = null;
                            _context.Reservations.Update(waitingRes);

                            // WhatsApp notification
                            try
                            {
                                if (waitingRes.User != null && !string.IsNullOrEmpty(waitingRes.User.Phone))
                                {
                                    string promoMsg = $"🎉 *GREAT NEWS from BHub!*\n\n" +
                                                      $"You have been promoted from the Waitlist to *ACTIVE* for:\n" +
                                                      $"🏸 *{waitingRes.Event.Title}*\n" +
                                                      $"🎟️ *Promoted Slots:* {waitingRes.SlotsCount}\n\n" +
                                                      $"Please make your payment to secure your spot! 🙏";
                                    await _whatsAppService.SendMessageAsync(waitingRes.User.Phone, promoMsg);
                                }
                            }
                            catch (Exception ex) { Console.WriteLine("Waitlist Promo Alert Error: " + ex.Message); }

                            freedSlots -= waitingRes.SlotsCount;
                        }
                        else
                        {
                            // Partial bump: Split into an Active portion and a remaining Waiting portion
                            var newlyActiveRes = new Reservation
                            {
                                EventId = waitingRes.EventId,
                                UserId = waitingRes.UserId,
                                SlotsCount = freedSlots,
                                Status = "Active",
                                QueuePosition = null
                            };
                            _context.Reservations.Add(newlyActiveRes);

                            // WhatsApp notification
                            try
                            {
                                if (waitingRes.User != null && !string.IsNullOrEmpty(waitingRes.User.Phone))
                                {
                                    string promoMsg = $"🎉 *GREAT NEWS from BHub!*\n\n" +
                                                      $"You have been promoted from the Waitlist to *ACTIVE* for:\n" +
                                                      $"🏸 *{waitingRes.Event.Title}*\n" +
                                                      $"🎟️ *Promoted Slots:* {freedSlots}\n\n" +
                                                      $"Please make your payment to secure your spot! 🙏";
                                    await _whatsAppService.SendMessageAsync(waitingRes.User.Phone, promoMsg);
                                }
                            }
                            catch (Exception ex) { Console.WriteLine("Waitlist Partial Promo Alert Error: " + ex.Message); }

                            waitingRes.SlotsCount -= freedSlots;
                            _context.Reservations.Update(waitingRes);
                            freedSlots = 0;
                        }
                    }
                    await _context.SaveChangesAsync();
                }

                await transaction.CommitAsync();
                return Ok(new { Message = "Booking force-deleted and waitlist bumped." });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { Message = "Database error cancelling booking.", Details = ex.Message });
            }
        }
        // PUT /api/bookings/{id}/toggle-payment
        [HttpPut("{id}/toggle-payment")]
        public async Task<IActionResult> TogglePayment(int id, [FromQuery] int adminId)
        {
            var admin = await _context.Users.FindAsync(adminId);
            if (admin == null || admin.Role != "Admin")
            {
                return StatusCode(403, new { Message = "Unauthorized. Only Admins can toggle payment status." });
            }

            var reservation = await _context.Reservations.FindAsync(id);
            if (reservation == null)
            {
                return NotFound(new { Message = "Reservation not found." });
            }

            reservation.IsPaid = !reservation.IsPaid;
            _context.Reservations.Update(reservation);
            await _context.SaveChangesAsync();

            return Ok(new { Message = "Payment status toggled.", IsPaid = reservation.IsPaid });
        }

        [HttpPost("{reservationId}/remind-payment")]
        public async Task<IActionResult> RemindPayment(int reservationId)
        {
            var res = await _context.Reservations
                .Include(r => r.User)
                .Include(r => r.Event)
                .FirstOrDefaultAsync(r => r.ReservationId == reservationId);

            if (res == null) return NotFound(new { Message = "Reservation not found." });
            if (res.User == null || string.IsNullOrEmpty(res.User.Phone)) return BadRequest(new { Message = "User or phone number not found." });

            try
            {
                string qrCodeUrl = "https://api.qrserver.com/v1/create-qr-code/?size=300x300&data=ThisIsAFakePaymentQRCodeForTesting";
                string msg = $"⚠️ *PAYMENT REMINDER*\n\n" +
                             $"Hi *{res.User.Username}*, friendly reminder from BHub Admin!\n\n" +
                             $"Please clear your payment for:\n" +
                             $"🏸 *{res.Event.Title}*\n" +
                             $"Scan the QR code below to pay! 🙏";

                await _whatsAppService.SendMessageAsync(res.User.Phone, msg, qrCodeUrl);
                return Ok(new { Message = "Payment reminder sent." });
            }
            catch (Twilio.Exceptions.ApiException ex)
            {
                Console.WriteLine($"Twilio API Error: {ex.Message}");
                return BadRequest(new { message = $"Twilio Error: {ex.Message}" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"General Error: {ex.Message}");
                return StatusCode(500, new { message = $"Server Error: {ex.Message}" });
            }
        }
    }

    public class BookingRequest
    {
        public int EventId { get; set; }
        public int UserId { get; set; }
        public int SlotsRequested { get; set; }
        public string? GuestNames { get; set; }
    }

    public class CancelBookingRequest
    {
        public int ReservationId { get; set; }
        public int UserId { get; set; }
        public int SlotsToCancel { get; set; }
    }

    public class AdminUpdateBookingRequest
    {
        public int? SlotsCount { get; set; }
        public string Status { get; set; }
    }
}
