using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ShipTrack.Api.Data;
using ShipTrack.Api.Models;
using ShipTrack.Api.Services;

namespace ShipTrack.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly ShipTrackDbContext _db;
    private readonly IConfiguration _config;
    private readonly IEmailService _emailService;

    // In-memory OTP storage: Email -> (OTP code, Expiry)
    private static readonly ConcurrentDictionary<string, (string Otp, DateTime Expiry)> OtpCache = new();

    // Persistent storage file path for screen permissions
    private static readonly string PermissionsFilePath = Path.Combine(AppContext.BaseDirectory, "role_permissions.json");

    public AuthController(ShipTrackDbContext db, IConfiguration config, IEmailService emailService)
    {
        _db = db;
        _config = config;
        _emailService = emailService;
    }

    public record LoginRequest(string Email, string Password);
    public record RegisterRequest(string FullName, string Email, string Password, UserRole Role);
    public record UpdateRoleRequest(UserRole Role);
    public record ForgotPasswordRequest(string Email);
    public record ResetPasswordRequest(string Email, string Otp, string NewPassword);
    public record ScreenPermissionsDto(
        bool CustomerCanBook,
        bool CustomerCanTrack,
        bool CustomerCanRiderConsole,
        bool CustomerCanAdminFleet,
        bool RiderCanBook,
        bool RiderCanTrack,
        bool RiderCanRiderConsole,
        bool RiderCanAdminFleet
    );

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var hash = HashPassword(req.Password);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == req.Email.ToLower() && u.PasswordHash == hash);

        if (user == null)
        {
            return Unauthorized(new { message = "Invalid email or password credentials." });
        }

        var token = GenerateJwtToken(user);

        return Ok(new
        {
            token,
            user = new
            {
                id = user.Id,
                fullName = user.FullName,
                email = user.Email,
                role = user.Role.ToString()
            }
        });
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        if (await _db.Users.AnyAsync(u => u.Email.ToLower() == req.Email.ToLower()))
        {
            return BadRequest(new { message = "An account with this email already exists." });
        }

        var user = new User
        {
            FullName = req.FullName,
            Email = req.Email.ToLower(),
            PasswordHash = HashPassword(req.Password),
            Role = req.Role,
            CreatedAt = DateTime.UtcNow
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        // Sync with UserRoles relational table
        var roleEntity = await _db.Roles.FirstOrDefaultAsync(r => r.Name == req.Role.ToString());
        if (roleEntity != null)
        {
            _db.UserRoles.Add(new UserRoleMapping
            {
                UserId = user.Id,
                RoleId = roleEntity.Id,
                AssignedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }

        var token = GenerateJwtToken(user);

        return Ok(new
        {
            message = "User created successfully",
            token,
            user = new
            {
                id = user.Id,
                fullName = user.FullName,
                email = user.Email,
                role = user.Role.ToString()
            }
        });
    }

    // Role-based User Management Endpoints (Admin Screen)
    [HttpGet("users")]
    public async Task<IActionResult> GetAllUsers()
    {
        var users = await _db.Users
            .OrderByDescending(u => u.CreatedAt)
            .Select(u => new
            {
                u.Id,
                u.FullName,
                u.Email,
                Role = u.Role.ToString(),
                u.CreatedAt
            })
            .ToListAsync();

        return Ok(users);
    }

    [HttpPut("users/{id}/role")]
    public async Task<IActionResult> UpdateUserRole(Guid id, [FromBody] UpdateRoleRequest req)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null)
            return NotFound(new { message = "User not found" });

        user.Role = req.Role;

        // Sync with UserRoles relational table
        var existingUserRoles = await _db.UserRoles.Where(ur => ur.UserId == user.Id).ToListAsync();
        _db.UserRoles.RemoveRange(existingUserRoles);

        var newRoleEntity = await _db.Roles.FirstOrDefaultAsync(r => r.Name == req.Role.ToString());
        if (newRoleEntity != null)
        {
            _db.UserRoles.Add(new UserRoleMapping
            {
                UserId = user.Id,
                RoleId = newRoleEntity.Id,
                AssignedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "Role updated successfully",
            userId = user.Id,
            newRole = user.Role.ToString()
        });
    }

    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles()
    {
        var roles = await _db.Roles
            .Select(r => new
            {
                r.Id,
                r.Name,
                r.Description,
                UserCount = r.UserRoles.Count
            })
            .ToListAsync();

        return Ok(roles);
    }

    // Requirement 1: Delete User
    [HttpDelete("users/{id}")]
    public async Task<IActionResult> DeleteUser(Guid id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null)
            return NotFound(new { message = "User not found" });

        if (user.Email.ToLower() == "admin@shiptrack.com")
        {
            return BadRequest(new { message = "System master administrator cannot be deleted." });
        }

        _db.Users.Remove(user);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = $"User {user.FullName} ({user.Email}) was permanently deleted successfully."
        });
    }

    // Requirement 6: Forgot Password - Step 1: Send 6-Digit OTP
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == req.Email.ToLower());
        if (user == null)
        {
            return NotFound(new { message = $"No account found registered with '{req.Email}'." });
        }

        var otp = Random.Shared.Next(100000, 999999).ToString();
        OtpCache[req.Email.ToLower()] = (otp, DateTime.UtcNow.AddMinutes(10));

        Console.WriteLine($"[SECURITY OTP AUDIT] 6-Digit OTP {otp} generated for {req.Email}");

        var realEmailSent = await _emailService.SendOtpEmailAsync(user.Email, user.FullName, otp);

        return Ok(new
        {
            message = realEmailSent 
                ? $"6-digit verification code has been dispatched directly to your real email inbox ({req.Email})." 
                : $"6-digit OTP verification code has been dispatched to {req.Email}.",
            otp = realEmailSent ? null : otp, // If real email was sent, do not show on-screen demo code!
            email = req.Email,
            realEmailSent
        });
    }

    // Requirement 6: Reset Password - Step 2: Verify OTP & Update Password
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest req)
    {
        if (!OtpCache.TryGetValue(req.Email.ToLower(), out var entry))
        {
            return BadRequest(new { message = "No pending OTP verification found for this email. Please request a new code." });
        }

        if (DateTime.UtcNow > entry.Expiry)
        {
            OtpCache.TryRemove(req.Email.ToLower(), out _);
            return BadRequest(new { message = "OTP has expired (10 minute limit). Please request a new code." });
        }

        if (entry.Otp != req.Otp.Trim())
        {
            return BadRequest(new { message = "Incorrect OTP code. Please enter the valid 6-digit code." });
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == req.Email.ToLower());
        if (user == null)
        {
            return NotFound(new { message = "User account not found." });
        }

        user.PasswordHash = HashPassword(req.NewPassword);
        await _db.SaveChangesAsync();

        OtpCache.TryRemove(req.Email.ToLower(), out _);

        return Ok(new
        {
            message = "Password updated successfully! You can now log in with your new password."
        });
    }

    // Requirement 2: Screen Permissions Management (Persistent on disk)
    [HttpGet("permissions")]
    public IActionResult GetPermissions()
    {
        var perms = LoadPermissionsFromFile();
        return Ok(perms);
    }

    [HttpPut("permissions")]
    public IActionResult UpdatePermissions([FromBody] ScreenPermissionsDto dto)
    {
        SavePermissionsToFile(dto);
        return Ok(new
        {
            message = "Role screen access permissions updated successfully!",
            permissions = dto
        });
    }

    private static ScreenPermissionsDto LoadPermissionsFromFile()
    {
        try
        {
            if (System.IO.File.Exists(PermissionsFilePath))
            {
                var json = System.IO.File.ReadAllText(PermissionsFilePath);
                var perms = JsonSerializer.Deserialize<ScreenPermissionsDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (perms != null) return perms;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Notice: Loading default permissions. {ex.Message}");
        }

        return new ScreenPermissionsDto(
            CustomerCanBook: true,
            CustomerCanTrack: true,
            CustomerCanRiderConsole: false,
            CustomerCanAdminFleet: false,
            RiderCanBook: false,
            RiderCanTrack: true,
            RiderCanRiderConsole: true,
            RiderCanAdminFleet: false
        );
    }

    private static void SavePermissionsToFile(ScreenPermissionsDto dto)
    {
        try
        {
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(PermissionsFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving permissions file: {ex.Message}");
        }
    }

    private string GenerateJwtToken(User user)
    {
        var secret = _config["Jwt:Key"] ?? "ShipTrack_Super_Secure_Secret_Key_For_JWT_Authentication_2026!";
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.FullName),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"] ?? "ShipTrackServer",
            audience: _config["Jwt:Audience"] ?? "ShipTrackClient",
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string HashPassword(string password)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(password + "ShipTrack_Salt_Key");
        return Convert.ToBase64String(sha.ComputeHash(bytes));
    }
}
