using System.ComponentModel.DataAnnotations;

namespace Paint.Contracts;

public sealed class PublishArtworkRequest
{
    [Required]
    [StringLength(120, MinimumLength = 1)]
    public string Title { get; set; } = "";

    [Required]
    public string ImageBase64 { get; set; } = "";
}
