using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ShipTrack.Api.Services;

public interface IEmailService
{
    Task<bool> SendOtpEmailAsync(string toEmail, string recipientName, string otpCode);
    Task<bool> SendShipmentBookedEmailAsync(string toEmail, string recipientName, string trackingNumber, string deliveryPin, decimal totalAmount, string paymentMethod);
    Task<bool> SendOutForDeliveryEmailAsync(string toEmail, string recipientName, string trackingNumber, string riderName, string vehicleInfo, string deliveryPin);
}

public class EmailService : IEmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public EmailService(IConfiguration config, ILogger<EmailService> logger, IHttpClientFactory httpClientFactory)
    {
        _config = config;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<bool> SendOtpEmailAsync(string toEmail, string recipientName, string otpCode)
    {
        var subject = $"Your ShipTrack Security Verification Code: {otpCode}";
        var htmlContent = GenerateOtpEmailHtml(recipientName, otpCode);
        return await SendEmailCoreAsync(toEmail, recipientName, subject, htmlContent);
    }

    public async Task<bool> SendShipmentBookedEmailAsync(string toEmail, string recipientName, string trackingNumber, string deliveryPin, decimal totalAmount, string paymentMethod)
    {
        var subject = $"📦 Shipment Confirmed: {trackingNumber} - ShipTrack Logistics";
        var htmlContent = GenerateBookingEmailHtml(recipientName, trackingNumber, deliveryPin, totalAmount, paymentMethod);
        return await SendEmailCoreAsync(toEmail, recipientName, subject, htmlContent);
    }

    public async Task<bool> SendOutForDeliveryEmailAsync(string toEmail, string recipientName, string trackingNumber, string riderName, string vehicleInfo, string deliveryPin)
    {
        var subject = $"🛵 Out for Delivery: {trackingNumber} is Arriving Today!";
        var htmlContent = GenerateOutForDeliveryHtml(recipientName, trackingNumber, riderName, vehicleInfo, deliveryPin);
        return await SendEmailCoreAsync(toEmail, recipientName, subject, htmlContent);
    }

    private async Task<bool> SendEmailCoreAsync(string toEmail, string recipientName, string subject, string htmlContent)
    {
        var enableRealEmail = _config.GetValue<bool>("EmailSettings:EnableRealEmail", false);
        if (!enableRealEmail)
        {
            _logger.LogInformation("[EMAIL SERVICE DEMO] Real email sending disabled in appsettings.json. Email '{Subject}' to {Email}.", subject, toEmail);
            return false;
        }

        var provider = _config["EmailSettings:Provider"] ?? "Brevo";
        var senderEmail = _config["EmailSettings:SenderEmail"] ?? "security@shiptrack.com";
        var senderName = _config["EmailSettings:SenderName"] ?? "ShipTrack Fleet Logistics";

        // 1. BREVO CLOUD (Try REST API first, then SMTP Relay fallback)
        if (string.Equals(provider, "Brevo", StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = _config["EmailSettings:Brevo:ApiKey"];
            if (!string.IsNullOrWhiteSpace(apiKey) && !apiKey.Contains("YOUR_"))
            {
                var apiSuccess = await SendViaBrevoApiAsync(apiKey, senderEmail, senderName, toEmail, recipientName, subject, htmlContent);
                if (apiSuccess) return true;
                _logger.LogInformation("[BREVO] REST API error. Automatically attempting Brevo SMTP Relay fallback on port 587...");
            }

            return await SendViaSmtpRelayAsync(senderEmail, senderName, toEmail, recipientName, subject, htmlContent);
        }

        // 2. SENDGRID CLOUD API
        if (string.Equals(provider, "SendGrid", StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = _config["EmailSettings:SendGrid:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains("YOUR_"))
            {
                return false;
            }

            return await SendViaSendGridApiAsync(apiKey, senderEmail, senderName, toEmail, recipientName, subject, htmlContent);
        }

        // 3. GENERIC SMTP RELAY
        return await SendViaSmtpRelayAsync(senderEmail, senderName, toEmail, recipientName, subject, htmlContent);
    }

    private async Task<bool> SendViaBrevoApiAsync(
        string apiKey, 
        string senderEmail, 
        string senderName, 
        string toEmail, 
        string toName, 
        string subject, 
        string htmlContent)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("api-key", apiKey);
            client.DefaultRequestHeaders.Add("accept", "application/json");

            var payload = new
            {
                sender = new { name = senderName, email = senderEmail },
                to = new[] { new { email = toEmail, name = toName } },
                subject,
                htmlContent
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync("https://api.brevo.com/v3/smtp/email", content);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("[BREVO SUCCESS] Real email '{Subject}' delivered to {Email}!", subject, toEmail);
                return true;
            }

            var err = await response.Content.ReadAsStringAsync();
            _logger.LogError("[BREVO API ERROR] Status {StatusCode}: {Error}", response.StatusCode, err);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BREVO EXCEPTION] Failed to send email via Brevo REST API.");
            return false;
        }
    }

    private async Task<bool> SendViaSendGridApiAsync(
        string apiKey, 
        string senderEmail, 
        string senderName, 
        string toEmail, 
        string toName, 
        string subject, 
        string htmlContent)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            var payload = new
            {
                personalizations = new[]
                {
                    new
                    {
                        to = new[] { new { email = toEmail, name = toName } },
                        subject
                    }
                },
                from = new { email = senderEmail, name = senderName },
                content = new[]
                {
                    new { type = "text/html", value = htmlContent }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync("https://api.sendgrid.com/v3/mail/send", content);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Accepted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SENDGRID EXCEPTION] Failed to send email.");
            return false;
        }
    }

    private async Task<bool> SendViaSmtpRelayAsync(
        string senderEmail, 
        string senderName, 
        string toEmail, 
        string toName, 
        string subject, 
        string htmlContent)
    {
        try
        {
            var host = _config["EmailSettings:Smtp:Host"] ?? "smtp-relay.brevo.com";
            var port = _config.GetValue<int>("EmailSettings:Smtp:Port", 587);
            var enableSsl = _config.GetValue<bool>("EmailSettings:Smtp:EnableSsl", true);
            var username = _config["EmailSettings:Smtp:Username"];
            var password = _config["EmailSettings:Smtp:Password"];

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                return false;
            }

            using var client = new SmtpClient(host, port)
            {
                EnableSsl = enableSsl,
                Credentials = new NetworkCredential(username, password),
                Timeout = 10000
            };

            var mail = new MailMessage
            {
                From = new MailAddress(senderEmail, senderName),
                Subject = subject,
                IsBodyHtml = true,
                Body = htmlContent
            };

            mail.To.Add(new MailAddress(toEmail, toName));
            await client.SendMailAsync(mail);

            _logger.LogInformation("[SMTP SUCCESS] Real email '{Subject}' sent via SMTP to {Email}.", subject, toEmail);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SMTP ERROR] Failed to send email via SMTP relay to {Email}.", toEmail);
            return false;
        }
    }

    private static string GenerateOtpEmailHtml(string recipientName, string otpCode)
    {
        return $@"
<!DOCTYPE html>
<html>
<head>
  <meta charset='utf-8'>
  <style>
    body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f1f5f9; margin: 0; padding: 24px; }}
    .container {{ max-width: 540px; margin: 0 auto; background: #ffffff; border-radius: 12px; border: 1px solid #e2e8f0; overflow: hidden; box-shadow: 0 4px 14px rgba(0,0,0,0.06); }}
    .header {{ background: #0f172a; padding: 28px 24px; text-align: center; color: white; }}
    .header h1 {{ margin: 0; font-size: 24px; font-weight: 800; }}
    .header p {{ margin: 4px 0 0 0; font-size: 13px; color: #94a3b8; }}
    .content {{ padding: 32px 28px; color: #334155; line-height: 1.6; }}
    .greeting {{ font-size: 16px; margin-top: 0; }}
    .otp-card {{ background: #eff6ff; border: 2px dashed #3b82f6; border-radius: 12px; padding: 24px; text-align: center; margin: 28px 0; }}
    .otp-label {{ font-size: 11px; font-weight: 800; color: #1d4ed8; text-transform: uppercase; letter-spacing: 1px; }}
    .otp-code {{ font-size: 38px; font-weight: 900; letter-spacing: 10px; color: #1e3a8a; font-family: monospace; margin: 8px 0; }}
    .otp-expiry {{ font-size: 12px; color: #ef4444; font-weight: 700; }}
    .security-note {{ font-size: 13px; color: #64748b; background: #f8fafc; padding: 12px; border-radius: 8px; border-left: 4px solid #f59e0b; margin-top: 20px; }}
    .footer {{ background: #f8fafc; padding: 18px; text-align: center; font-size: 11px; color: #94a3b8; border-top: 1px solid #e2e8f0; }}
  </style>
</head>
<body>
  <div class='container'>
    <div class='header'>
      <h1>🚚 ShipTrack Fleet Logistics</h1>
      <p>Enterprise Authentication & Security Gateway</p>
    </div>
    <div class='content'>
      <p class='greeting'>Hello <strong>{recipientName}</strong>,</p>
      <p>A password reset request was initiated for your ShipTrack account. Please enter the following 6-digit security code to verify your identity and set a new password:</p>
      
      <div class='otp-card'>
        <div class='otp-label'>Your 6-Digit One-Time Password</div>
        <div class='otp-code'>{otpCode}</div>
        <div class='otp-expiry'>⏱️ Code expires in 10 minutes</div>
      </div>
      
      <div class='security-note'>
        <strong>⚠️ Security Tip:</strong> Never share this code with anyone. ShipTrack staff will never ask for your verification code.
      </div>
    </div>
    <div class='footer'>
      &copy; 2026 ShipTrack Logistics Inc. • Security Gateway
    </div>
  </div>
</body>
</html>";
    }

    private static string GenerateBookingEmailHtml(string recipientName, string trackingNumber, string deliveryPin, decimal totalAmount, string paymentMethod)
    {
        var payText = paymentMethod == "StripeOnline" ? "Prepaid Online via Stripe (Rs. 0 to collect)" : $"Rs. {totalAmount} (Cash on Delivery)";
        return $@"
<!DOCTYPE html>
<html>
<head>
  <meta charset='utf-8'>
  <style>
    body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f1f5f9; margin: 0; padding: 24px; }}
    .container {{ max-width: 540px; margin: 0 auto; background: #ffffff; border-radius: 12px; border: 1px solid #e2e8f0; overflow: hidden; box-shadow: 0 4px 14px rgba(0,0,0,0.06); }}
    .header {{ background: #0284c7; padding: 28px 24px; text-align: center; color: white; }}
    .header h1 {{ margin: 0; font-size: 22px; font-weight: 800; }}
    .content {{ padding: 28px; color: #334155; line-height: 1.6; }}
    .info-box {{ background: #f0fdf4; border: 1px solid #bbf7d0; border-radius: 10px; padding: 18px; margin: 20px 0; }}
    .info-row {{ display: flex; justify-content: space-between; margin-bottom: 8px; font-size: 14px; }}
    .pin-box {{ background: #0f172a; color: white; border-radius: 8px; padding: 14px; text-align: center; margin: 18px 0; }}
    .pin-code {{ font-size: 26px; font-weight: 900; letter-spacing: 6px; color: #38bdf8; font-family: monospace; }}
    .btn-track {{ display: block; text-align: center; background: #2563eb; color: white; padding: 12px; border-radius: 8px; text-decoration: none; font-weight: 800; margin-top: 20px; }}
    .footer {{ background: #f8fafc; padding: 14px; text-align: center; font-size: 11px; color: #94a3b8; border-top: 1px solid #e2e8f0; }}
  </style>
</head>
<body>
  <div class='container'>
    <div class='header'>
      <h1>📦 Shipment Dispatched!</h1>
      <p>ShipTrack Fleet Logistics</p>
    </div>
    <div class='content'>
      <p>Hello <strong>{recipientName}</strong>,</p>
      <p>Your consignment has been registered and is being routed through our central logistics network.</p>
      
      <div class='info-box'>
        <div class='info-row'><span>Tracking ID:</span> <strong>{trackingNumber}</strong></div>
        <div class='info-row'><span>Amount Due:</span> <strong>{payText}</strong></div>
      </div>

      <div class='pin-box'>
        <div style='font-size: 11px; color: #94a3b8; text-transform: uppercase; font-weight: 700;'>Anti-Theft Handover Verification PIN</div>
        <div class='pin-code'>{deliveryPin}</div>
        <small style='font-size: 11px; color: #cbd5e1;'>Please share this 4-digit code with the courier rider at doorstep</small>
      </div>

      <a href='http://localhost:4200/track/{trackingNumber}' class='btn-track'>📍 Track Live GPS Location</a>
    </div>
    <div class='footer'>
      &copy; 2026 ShipTrack Logistics • Real-time Courier Dispatch
    </div>
  </div>
</body>
</html>";
    }

    private static string GenerateOutForDeliveryHtml(string recipientName, string trackingNumber, string riderName, string vehicleInfo, string deliveryPin)
    {
        return $@"
<!DOCTYPE html>
<html>
<head>
  <meta charset='utf-8'>
  <style>
    body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f1f5f9; margin: 0; padding: 24px; }}
    .container {{ max-width: 540px; margin: 0 auto; background: #ffffff; border-radius: 12px; border: 1px solid #e2e8f0; overflow: hidden; }}
    .header {{ background: #16a34a; padding: 28px 24px; text-align: center; color: white; }}
    .content {{ padding: 28px; color: #334155; line-height: 1.6; }}
    .rider-card {{ background: #eff6ff; border: 1.5px solid #bfdbfe; border-radius: 10px; padding: 18px; margin: 20px 0; text-align: center; }}
    .btn-track {{ display: block; text-align: center; background: #16a34a; color: white; padding: 12px; border-radius: 8px; text-decoration: none; font-weight: 800; }}
  </style>
</head>
<body>
  <div class='container'>
    <div class='header'>
      <h1 style='margin:0;'>🛵 Courier Out for Delivery!</h1>
    </div>
    <div class='content'>
      <p>Hello <strong>{recipientName}</strong>,</p>
      <p>Great news! Your parcel (<strong>{trackingNumber}</strong>) is on the courier motorcycle and arriving today.</p>

      <div class='rider-card'>
        <div style='font-size: 12px; color: #1d4ed8; font-weight: 700;'>ASSIGNED COURIER RIDER</div>
        <h3 style='margin: 4px 0; color: #0f172a;'>{riderName}</h3>
        <p style='margin: 0; color: #64748b; font-size: 13px;'>Vehicle: {vehicleInfo}</p>
        <div style='margin-top: 12px; background: white; padding: 8px; border-radius: 6px; font-weight: 800; color: #0f172a;'>
          Your Handover PIN: <span style='color: #2563eb;'>{deliveryPin}</span>
        </div>
      </div>

      <a href='http://localhost:4200/track/{trackingNumber}' class='btn-track'>📍 Track Live Rider on Map</a>
    </div>
  </div>
</body>
</html>";
    }
}
