using System.ComponentModel.DataAnnotations;

namespace Paint.Models;

public class PaintRoom
{
    [Key]
    [MaxLength(128)]
    public string Id { get; set; } = "";

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    public int MaxUsers { get; set; } = 20;

    public int PixelWidth { get; set; } = 1024;

    public int PixelHeight { get; set; } = 768;

    public bool IsProtected { get; set; }

    public string? PasswordHash { get; set; }

    [MaxLength(450)]
    public string? CreatedByUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<CanvasEvent> CanvasEvents { get; set; } = [];
}
