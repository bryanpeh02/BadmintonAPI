using System;
using System.Collections.Generic;

namespace BadmintonFYP.Api.Models;

public partial class Event
{
    public int EventId { get; set; }

    public string Title { get; set; } = null!;

    public string? Description { get; set; }

    public DateTime EventDate { get; set; }

    public int TotalSlots { get; set; }

    public string? Status { get; set; }

    public decimal Price { get; set; } = 0;

    public DateTime? CreatedAt { get; set; }

    public virtual ICollection<Reservation> Reservations { get; set; } = new List<Reservation>();
}
