using System;

namespace BadmintonFYP.Api.Models;

public partial class Announcement
{
    public int Id { get; set; }
    public string Message { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
