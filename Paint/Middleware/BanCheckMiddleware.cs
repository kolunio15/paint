using Microsoft.AspNetCore.Identity;
using Paint.Models;
using Paint.Services;

namespace Paint.Middleware;

public class BanCheckMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is not null && IsEffectivelyBanned(user))
            {
                await signInManager.SignOutAsync();
                var query = $"?reason={Uri.EscapeDataString(user.BanReason ?? "")}";
                if (user.BannedUntil.HasValue)
                    query += $"&until={Uri.EscapeDataString(user.BannedUntil.Value.ToString("o"))}";
                context.Response.Redirect("/Banned" + query);
                return;
            }

            if (user is not null)
            {
                var changed = false;

                var ip = context.Connection.RemoteIpAddress?.ToString();
                if (ip is not null)
                {
                    var ipHash = BanService.HashValue(ip);
                    if (user.LastKnownIp != ipHash)
                    {
                        user.LastKnownIp = ipHash;
                        changed = true;
                    }
                }

                var deviceToken = context.Request.Cookies["paint_dt"];
                if (!string.IsNullOrWhiteSpace(deviceToken))
                {
                    var tokenHash = BanService.HashValue(deviceToken);
                    if (user.LastKnownDeviceToken != tokenHash)
                    {
                        user.LastKnownDeviceToken = tokenHash;
                        changed = true;
                    }
                }

                // Single write per request, and only when an identifier actually changed.
                if (changed)
                    await userManager.UpdateAsync(user);
            }
        }

        await next(context);
    }

    private static bool IsEffectivelyBanned(ApplicationUser user)
    {
        if (!user.IsBanned) return false;
        if (user.BannedUntil.HasValue && user.BannedUntil.Value < DateTime.UtcNow) return false;
        return true;
    }
}
