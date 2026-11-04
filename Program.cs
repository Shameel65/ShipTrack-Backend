using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using ShipTrack.Api.Controllers;
using ShipTrack.Api.Data;
using ShipTrack.Api.Hubs;
using ShipTrack.Api.Models;
using ShipTrack.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// 1. Database Configuration (SQL Server Local / DESKTOP-JK13KV3)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? "Server=DESKTOP-JK13KV3;Database=ShipTrackDb;User Id=sa;Password=123;TrustServerCertificate=True;MultipleActiveResultSets=true";

builder.Services.AddDbContext<ShipTrackDbContext>(options =>
    options.UseSqlServer(connectionString));

// 2. Add JWT Authentication & Authorization
var jwtSecret = builder.Configuration["Jwt:Key"] ?? "ShipTrack_Super_Secure_Secret_Key_For_JWT_Authentication_2026!";
var key = Encoding.UTF8.GetBytes(jwtSecret);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = false;
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateIssuer = false,
        ValidateAudience = false,
        ClockSkew = TimeSpan.Zero
    };
});

builder.Services.AddAuthorization();

// 2.5 Add HttpClient and Email Service for Real Live OTP Delivery (Brevo / SendGrid / SMTP)
builder.Services.AddHttpClient();
builder.Services.AddScoped<IEmailService, EmailService>();

// 3. Add SignalR for Live Real-time Location Updates
builder.Services.AddSignalR();

// 4. Add API Controllers
builder.Services.AddControllers();

// 5. Configure CORS for Angular Frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy =>
    {
        policy.WithOrigins("http://localhost:4200")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // Crucial for SignalR WebSockets
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "ShipTrack Enterprise API",
        Version = "v2.0",
        Description = "Courier & Logistics Management System — Consignment booking, live tracking, rider management, COD settlement, e-POD verification"
    });

    // JWT Bearer Auth in Swagger UI
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter your JWT token (without 'Bearer ' prefix)"
    });
    c.AddSecurityRequirement(doc => new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecuritySchemeReference("Bearer"),
            new List<string>()
        }
    });
});

var app = builder.Build();

// 6. Automated Database & Demo User Seeding on Startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ShipTrackDbContext>();
    db.Database.EnsureCreated();

    // Seed Demo Users if they do not exist
    if (!db.Users.Any())
    {
        db.Users.AddRange(
            new User
            {
                FullName = "System Administrator",
                Email = "admin@shiptrack.com",
                PasswordHash = AuthController.HashPassword("admin123"),
                Role = UserRole.Admin
            },
            new User
            {
                FullName = "Kamran Rider",
                Email = "rider@shiptrack.com",
                PasswordHash = AuthController.HashPassword("rider123"),
                Role = UserRole.Rider
            },
            new User
            {
                FullName = "Tariq Customer",
                Email = "customer@shiptrack.com",
                PasswordHash = AuthController.HashPassword("customer123"),
                Role = UserRole.Customer
            }
        );
        db.SaveChanges();
    }
}

app.UseCors("AllowAngular");

// Swagger UI — always enabled (dev + prod)
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "ShipTrack API v2.0");
    c.RoutePrefix = "swagger"; // accessible at /swagger
    c.DocumentTitle = "ShipTrack API — Swagger";
});

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// 7. Map Endpoints
app.MapControllers();
app.MapHub<TrackingHub>("/hubs/tracking");

app.MapGet("/", () => Results.Ok(new
{
    Service = "ShipTrack Enterprise API",
    Version = "2.0",
    Auth = "JWT Bearer Active",
    Status = "Healthy",
    SignalRHub = "/hubs/tracking"
}));

app.Run();
