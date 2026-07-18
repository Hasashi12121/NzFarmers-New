using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NZFarmers.Areas.Identity.Data;
using NZFarmers.Data;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Authentication.Google;
using Stripe;
using NZFarmers.Services;


var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
// Add Email Service
builder.Services.AddScoped<IEmailService, EmailService>();

// Get the connection string
var connectionString = configuration.GetConnectionString("NZFarmersContextConnection")
    ?? throw new InvalidOperationException("Connection string 'NZFarmersContextConnection' not found.");

// Configure database
builder.Services.AddDbContext<NZFarmersContext>(options =>
    options.UseSqlServer(connectionString));

// Add Identity
builder.Services.AddDefaultIdentity<NZFarmersUser>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
})
.AddRoles<IdentityRole>()
.AddEntityFrameworkStores<NZFarmersContext>();

// Add Authentication with Google
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = IdentityConstants.ApplicationScheme;
    options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
})
.AddGoogle(options =>
{
    options.ClientId = configuration["Authentication:Google:ClientId"]
                        ?? throw new InvalidOperationException("Google ClientId not configured.");
    options.ClientSecret = configuration["Authentication:Google:ClientSecret"]
                        ?? throw new InvalidOperationException("Google ClientSecret not configured.");
});

// Configure Stripe using your secret key (this is used server-side)
StripeConfiguration.ApiKey = configuration["Stripe:SecretKey"]
    ?? throw new InvalidOperationException("Stripe SecretKey not configured.");

// Add MVC services
builder.Services.AddControllersWithViews();

var app = builder.Build();

// Seed roles
using (var scope = app.Services.CreateScope())
{
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    string[] roleNames = { "Admin", "Farmer", "Consumer" };

    foreach (var roleName in roleNames)
    {
        if (!await roleManager.RoleExistsAsync(roleName))
        {
            await roleManager.CreateAsync(new IdentityRole(roleName));
        }
    }
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
