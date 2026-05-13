using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;

namespace SecurityAnalyzer.Web.Pages;

// Antiforgery is intentionally skipped on this page for the same reason
// as OnPostStartRedTeam on the Index page: on Railway, the data
// protection key ring isn't sticky across deploys, so antiforgery
// tokens issued before a deploy would fail validation after the next
// one and lock the operator out of the dashboard entirely.  The attack
// this leaves open is "tricking an operator into logging in as
// themselves," which has no effect.
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public class LoginModel : PageModel
{
    [BindProperty] public string Username { get; set; } = string.Empty;
    [BindProperty] public string Password { get; set; } = string.Empty;
    public string? ErrorMessage { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
        {
            ErrorMessage = "Username and password are required.";
            return Page();
        }

        var connStr = Environment.GetEnvironmentVariable("SECURITY_ANALYZER_DB")
            ?? throw new InvalidOperationException("SECURITY_ANALYZER_DB env var is not set");

        using var db = new SqlConnection(connStr);
        db.Open();

        var stored = db.QueryFirstOrDefault<string?>(
            @"SELECT PasswordHash FROM dbo.Users WHERE Username = @u",
            new { u = Username });

        if (stored is null || !PasswordHash.Verify(Password, stored))
        {
            ErrorMessage = "Invalid username or password.";
            return Page();
        }

        var claims = new[] { new Claim(ClaimTypes.Name, Username) };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        return RedirectToPage("/Index");
    }
}
