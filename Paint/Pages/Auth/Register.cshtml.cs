using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Paint.Models;
using Paint.Services;

namespace Paint.Pages.Auth;

[AllowAnonymous]
public class RegisterModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly BanService _banService;
    private readonly ModerationService _moderation;

    public RegisterModel(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager, BanService banService, ModerationService moderation)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _banService = banService;
        _moderation = moderation;
    }

    [BindProperty]
    public RegisterInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (ip is not null && await _banService.IsIdentifierBannedAsync(BannedIdentifierType.Ip, ip))
        {
            ModelState.AddModelError(string.Empty, "Registration is not available from your location.");
            return Page();
        }

        if (!string.IsNullOrWhiteSpace(Input.DeviceToken) &&
            await _banService.IsIdentifierBannedAsync(BannedIdentifierType.DeviceToken, Input.DeviceToken))
        {
            ModelState.AddModelError(string.Empty, "Registration is not available from this device.");
            return Page();
        }

        var user = new ApplicationUser
        {
            UserName = Input.UserName.Trim(),
            DisplayName = Input.UserName.Trim(),
            CreatedAt = DateTime.UtcNow,
            LastKnownIp = ip is not null ? BanService.HashValue(ip) : null
        };

        var result = await _userManager.CreateAsync(user, Input.Password);
        if (result.Succeeded)
        {
            await _signInManager.SignInAsync(user, isPersistent: false);
            await _moderation.EnqueueAsync(QueueItemType.UserRegistered, targetUserId: user.Id);
            return RedirectToLocal(ReturnUrl);
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }

        return Page();
    }

    private IActionResult RedirectToLocal(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        return RedirectToPage("/Rooms");
    }

    public class RegisterInput
    {
        [Required]
        [Display(Name = "User name")]
        [StringLength(64, MinimumLength = 3)]
        public string UserName { get; set; } = "";

        [Required]
        [DataType(DataType.Password)]
        [StringLength(100, MinimumLength = 6)]
        public string Password { get; set; } = "";

        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Confirm password")]
        [Compare(nameof(Password))]
        public string ConfirmPassword { get; set; } = "";

        public string? DeviceToken { get; set; }
    }
}
