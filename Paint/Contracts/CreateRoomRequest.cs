using System.ComponentModel.DataAnnotations;

namespace Paint.Contracts;

public sealed class CreateRoomRequest
{
    [Required]
    [StringLength(100, MinimumLength = 3)]
    public string Name { get; set; } = "";

    [Range(1, 100)]
    public int MaxUsers { get; set; } = 10;

    [Range(1, 8192)]
    public int CanvasWidth { get; set; } = PaintDefaults.CanvasWidth;

    [Range(1, 8192)]
    public int CanvasHeight { get; set; } = PaintDefaults.CanvasHeight;

    [StringLength(100)]
    public string? Password { get; set; }
}
