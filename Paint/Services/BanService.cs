using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Paint.Data;
using Paint.Models;

namespace Paint.Services;

public class BanService(ApplicationDbContext db, UserManager<ApplicationUser> userManager)
{
    public static string HashValue(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant().Trim())));

    public async Task<bool> IsIdentifierBannedAsync(BannedIdentifierType type, string value)
    {
        var hash = HashValue(value);
        return await db.BannedIdentifiers.AnyAsync(b => b.Type == type && b.ValueHash == hash);
    }

    public async Task ApplyPermBanAsync(ApplicationUser target, ApplicationUser bannedBy, string? reason)
    {
        target.IsBanned = true;
        target.BannedUntil = null;
        target.BanReason = reason;
        await userManager.UpdateSecurityStampAsync(target);
        await userManager.UpdateAsync(target);

        var now = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(target.LastKnownIp))
        {
            // LastKnownIp is stored as a SHA256 hash — add it directly without re-hashing
            await AddIdentifierPrehashedAsync(BannedIdentifierType.Ip, target.LastKnownIp, target.Id, bannedBy.Id);
        }

        if (!string.IsNullOrWhiteSpace(target.LastKnownDeviceToken))
        {
            // LastKnownDeviceToken is stored as a SHA256 hash — add it directly without re-hashing
            await AddIdentifierPrehashedAsync(BannedIdentifierType.DeviceToken, target.LastKnownDeviceToken, target.Id, bannedBy.Id);
        }

        db.ModerationActions.Add(new ModerationAction
        {
            ModeratorId = bannedBy.Id,
            Type = ModerationActionType.PermBan,
            TargetUserId = target.Id,
            Reason = reason,
            CreatedAt = now
        });

        await db.SaveChangesAsync();
    }

    public async Task ApplyTempBanAsync(ApplicationUser target, ApplicationUser bannedBy, DateTime until, string? reason)
    {
        target.IsBanned = true;
        target.BannedUntil = until;
        target.BanReason = reason;
        await userManager.UpdateSecurityStampAsync(target);
        await userManager.UpdateAsync(target);

        db.ModerationActions.Add(new ModerationAction
        {
            ModeratorId = bannedBy.Id,
            Type = ModerationActionType.TempBan,
            TargetUserId = target.Id,
            Reason = reason,
            ExpiresAt = until,
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
    }

    public async Task UnbanAsync(ApplicationUser target, ApplicationUser unbannedBy)
    {
        target.IsBanned = false;
        target.BannedUntil = null;
        target.BanReason = null;
        await userManager.UpdateAsync(target);

        db.ModerationActions.Add(new ModerationAction
        {
            ModeratorId = unbannedBy.Id,
            Type = ModerationActionType.Unban,
            TargetUserId = target.Id,
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
    }

    private async Task AddIdentifierPrehashedAsync(BannedIdentifierType type, string hash, string originalUserId, string bannedById)
    {
        var exists = await db.BannedIdentifiers.AnyAsync(b => b.Type == type && b.ValueHash == hash);
        if (!exists)
        {
            db.BannedIdentifiers.Add(new BannedIdentifier
            {
                Type = type,
                ValueHash = hash,
                OriginalUserId = originalUserId,
                BannedById = bannedById,
                CreatedAt = DateTime.UtcNow
            });
        }
    }
}
