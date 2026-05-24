using System.ComponentModel.DataAnnotations;

namespace Paint.Contracts;

public sealed class CreateRoomRequest
{
    [Required]
    [StringLength(100, MinimumLength = 3)]
    public string Name { get; set; } = "";

    [Range(1, 100)]
    public int MaxUsers { get; set; } = 20;

    [StringLength(100)]
    public string? Password { get; set; }

    [Range(1, 10000)]
    public int PixelWidth { get; set; } = 1024;

    [Range(1, 10000)]
    public int PixelHeight { get; set; } = 768;

    public bool IsProtected { get; set; }
}
