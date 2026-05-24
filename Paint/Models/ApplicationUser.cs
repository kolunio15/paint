using Microsoft.AspNetCore.Identity;

namespace Paint.Models;

public class ApplicationUser : IdentityUser
{
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
