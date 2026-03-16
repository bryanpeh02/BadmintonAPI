using System;

namespace BadmintonFYP.Api.Models;

public partial class SystemSetting
{
    public int Id { get; set; }
    public int LateCancelHours { get; set; } = 3;
    public int MaxViolations { get; set; } = 3;
}
