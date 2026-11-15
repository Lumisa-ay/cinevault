using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CineVault.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CineVault.Services;

public sealed class LocalUserStore
{
    private readonly object gate = new();
    private readonly string file;
    private readonly List<LocalUser> users;
    private readonly PasswordHasher<LocalUser> hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 210000 }));
    private readonly LocalUser dummy = new() { UserName = "dummy" };
    private readonly string dummyHash;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public LocalUserStore(string dataRoot, IConfiguration config)
    {
        Directory.CreateDirectory(dataRoot);
        file = Path.Combine(dataRoot, "users.json");
        dummyHash = hasher.HashPassword(dummy, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        if (File.Exists(file))
        {
            users = JsonSerializer.Deserialize<List<LocalUser>>(File.ReadAllText(file), Json)
                ?? throw new InvalidDataException("用户数据为空，请从备份恢复。");
            if (!users.Any(u => u.Role == "Admin" && !u.Disabled))
                throw new InvalidDataException("用户数据没有可用管理员，请从备份恢复。");
            return;
        }
        users = [];
        var passwordFile = config["Bootstrap:AdminPasswordFile"];
        var password = !string.IsNullOrWhiteSpace(passwordFile)
            ? File.ReadAllText(passwordFile).TrimEnd('\r', '\n')
            : config["Bootstrap:AdminPassword"];
        if (string.IsNullOrEmpty(password))
        {
            password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var initialFile = Path.Combine(dataRoot, "initial-admin-password.txt");
            File.WriteAllText(initialFile, password + Environment.NewLine);
            Restrict(initialFile);
        }
        Create(config["Bootstrap:AdminUser"] ?? "admin", "系统管理员", password, "Admin");
    }
    private static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    private void Save()
    {
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(users, Json));
            Restrict(temporary);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(file)) File.Replace(temporary, file, null);
                    else File.Move(temporary, file);
                    break;
                }
                catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
                { Thread.Sleep(20 * (attempt + 1)); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void ValidatePassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length is < 12 or > 128)
            throw new ArgumentException("密码需要 12–128 个字符。");
    }
    public LocalUser[] All() { lock (gate) return users.Select(u => u.Snapshot()).OrderBy(u => u.CreatedAt).ToArray(); }
    public LocalUser? Find(string id) { lock (gate) return users.FirstOrDefault(u => u.Id == id)?.Snapshot(); }
    public bool SessionValid(string id, string stamp, string role)
    { lock (gate) return users.Any(u => u.Id == id && !u.Disabled && u.SecurityStamp == stamp && u.Role == role); }

    public LocalUser Create(string name, string displayName, string password, string role)
    {
        name = name.Trim();
        if (!Regex.IsMatch(name, @"\A[A-Za-z0-9_.-]{3,32}\z", RegexOptions.None, TimeSpan.FromSeconds(1)))
            throw new ArgumentException("用户名需要 3–32 个字母、数字、点、下划线或短横线。");
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 60) throw new ArgumentException("请填写 1–60 个字符的显示名称。");
        if (role is not ("Admin" or "User")) throw new ArgumentException("用户角色不合法。");
        ValidatePassword(password);
        lock (gate)
        {
            if (users.Any(u => u.UserName.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("用户名已存在。");
            var user = new LocalUser { UserName = name, DisplayName = displayName.Trim(), Role = role };
            user.PasswordHash = hasher.HashPassword(user, password);
            users.Add(user); Save(); return user.Snapshot();
        }
    }
    public LocalUser? Authenticate(string name, string password)
    {
        lock (gate)
        {
            var user = users.FirstOrDefault(u => u.UserName.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (user is null || user.Disabled || user.LockedUntil > DateTimeOffset.UtcNow)
            {
                hasher.VerifyHashedPassword(dummy, dummyHash, password);
                return null;
            }
            if (user.LockedUntil.HasValue) { user.LockedUntil = null; user.FailedAttempts = 0; }
            var result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
            if (result == PasswordVerificationResult.Failed)
            {
                user.FailedAttempts++;
                if (user.FailedAttempts >= 5) user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(15);
                Save(); return null;
            }
            if (result == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = hasher.HashPassword(user, password);
            user.FailedAttempts = 0; user.LockedUntil = null; Save();
            return user.Snapshot();
        }
    }
    public void ResetPassword(string id, string password)
    {
        ValidatePassword(password);
        lock (gate)
        {
            var user = users.FirstOrDefault(u => u.Id == id) ?? throw new KeyNotFoundException("用户不存在。");
            user.PasswordHash = hasher.HashPassword(user, password);
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            user.FailedAttempts = 0; user.LockedUntil = null; Save();
        }
    }
    public bool ChangePassword(string id, string current, string next)
    {
        ValidatePassword(next);
        lock (gate)
        {
            var user = users.FirstOrDefault(u => u.Id == id && !u.Disabled);
            if (user is null || hasher.VerifyHashedPassword(user, user.PasswordHash, current) == PasswordVerificationResult.Failed) return false;
            ResetPassword(id, next); return true;
        }
    }
    public void SetDisabled(string id, bool disabled, string actorId)
    {
        lock (gate)
        {
            var user = users.FirstOrDefault(u => u.Id == id) ?? throw new KeyNotFoundException("用户不存在。");
            if (id == actorId && disabled) throw new ArgumentException("不能停用当前登录的账户。");
            if (disabled && user.Role == "Admin" && users.Count(u => u.Role == "Admin" && !u.Disabled) <= 1)
                throw new ArgumentException("必须保留至少一个可用管理员。");
            user.Disabled = disabled; user.SecurityStamp = Guid.NewGuid().ToString("N"); Save();
        }
    }
}
