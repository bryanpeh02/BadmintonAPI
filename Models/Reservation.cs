using System;
using System.Collections.Generic;

namespace BadmintonFYP.Api.Models;

public partial class Reservation
{
    public int ReservationId { get; set; }

    public int EventId { get; set; }

    public int UserId { get; set; }

    public int SlotsCount { get; set; }

    public string Status { get; set; } = null!;

    public int? QueuePosition { get; set; }

    public bool IsPaid { get; set; } = false;

    public string? GuestNames { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual Event Event { get; set; } = null!;

    public virtual User User { get; set; } = null!;
}
