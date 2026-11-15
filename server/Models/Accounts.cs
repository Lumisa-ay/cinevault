using System.ComponentModel.DataAnnotations;

namespace CineVault.Models;

public sealed class LocalUser
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "User";
    public string PasswordHash { get; set; } = "";
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public bool Disabled { get; set; }
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public LocalUser Snapshot() => (LocalUser)MemberwiseClone();
}
public sealed class LoginInput
{
    [Required, StringLength(32)] public string UserName { get; set; } = "";
    [Required, StringLength(128)] public string Password { get; set; } = "";
    public string? ReturnUrl { get; set; }
}
public sealed class CreateUserInput
{
    [Required, StringLength(32)] public string UserName { get; set; } = "";
    [Required, StringLength(60)] public string DisplayName { get; set; } = "";
    [Required, StringLength(128, MinimumLength = 12)] public string Password { get; set; } = "";
    public string Role { get; set; } = "User";
}
public sealed class ChangePasswordInput
{
    [Required, StringLength(128)] public string CurrentPassword { get; set; } = "";
    [Required, StringLength(128, MinimumLength = 12)] public string NewPassword { get; set; } = "";
    [Compare(nameof(NewPassword))] public string ConfirmPassword { get; set; } = "";
}
