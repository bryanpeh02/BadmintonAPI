using System;
using System.Collections.Generic;

namespace BadmintonFYP.Api.Models;

public partial class User
{
    public int UserId { get; set; }

    public string Username { get; set; } = null!;

    public string PasswordHash { get; set; } = null!;

    public string? Role { get; set; }

    public string Phone { get; set; } = null!;

    public bool? IsMonthlyMember { get; set; }

    public int? ViolationCount { get; set; }

    public bool? IsBlacklisted { get; set; }

    public DateTime? CreatedAt { get; set; }

    public virtual ICollection<Reservation> Reservations { get; set; } = new List<Reservation>();
}
