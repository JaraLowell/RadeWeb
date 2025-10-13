using RadegastWeb.Hubs;
using RadegastWeb.Services;
using RadegastWeb.Data;
using RadegastWeb.Models;
using RadegastWeb.Middleware;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure URLs - allow binding to all interfaces
builder.WebHost.UseUrls("http://*:15269", "https://*:15277");

// Configure Serilog with additional filters
builder.Host.UseSerilog((context, configuration) =>
{
    configuration
        .WriteTo.Console()
        .WriteTo.File("logs/radegast-web-.log", rollingInterval: RollingInterval.Day)
        .ReadFrom.Configuration(context.Configuration)
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Extensions.Hosting", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", Serilog.Events.LogEventLevel.Error)
        .MinimumLevel.Override("Microsoft.AspNetCore.Server.Kestrel", Serilog.Events.LogEventLevel.Error)
        .MinimumLevel.Override("Microsoft.AspNetCore.HttpLogging", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.AspNetCore.Routing", Serilog.Events.LogEventLevel.Warning);
});

// Configure Entity Framework with SQLite
var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "data");
if (!Directory.Exists(dataDirectory))
{
    Directory.CreateDirectory(dataDirectory);
}

var dbPath = Path.Combine(dataDirectory, "radegast.db");
var connectionString = $"Data Source={dbPath}";

// Register DbContext factory for both scoped and singleton services
builder.Services.AddDbContextFactory<RadegastDbContext>(options =>
{
    options.UseSqlite(connectionString)
           .UseLoggerFactory(LoggerFactory.Create(builder => 
               builder.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning)
                     .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning)));
});

// Also register scoped DbContext for controllers that need it
builder.Services.AddScoped<RadegastDbContext>(provider =>
{
    var factory = provider.GetRequiredService<IDbContextFactory<RadegastDbContext>>();
    return factory.CreateDbContext();
});

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() { Title = "RadegastWeb API", Version = "v1" });
});

// Configure authentication
builder.Services.Configure<AuthenticationConfig>(
    builder.Configuration.GetSection("Authentication"));
builder.Services.AddSingleton<IAuthenticationService, AuthenticationService>();

// Add SignalR
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 1024 * 1024; // 1MB
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowExternalAccess", policy =>
    {
        policy.SetIsOriginAllowed(origin => true) // Allow any origin in development
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials(); // Important for SignalR
    });
});

// Add memory cache for name resolution service
builder.Services.AddMemoryCache();

// Register custom services - Use singleton for account management
builder.Services.AddSingleton<IGlobalDisplayNameCache, GlobalDisplayNameCache>();
builder.Services.AddSingleton<IPeriodicDisplayNameService, PeriodicDisplayNameService>();
builder.Services.AddSingleton<IAccountService, AccountService>();
builder.Services.AddSingleton<IDisplayNameService, DisplayNameService>();
builder.Services.AddSingleton<INoticeService, NoticeService>();
builder.Services.AddSingleton<IPresenceService, PresenceService>();
builder.Services.AddSingleton<IRegionInfoService, RegionInfoService>();
builder.Services.AddSingleton<INameResolutionService, NameResolutionService>();
builder.Services.AddSingleton<ISlUrlParser, SlUrlParser>();
builder.Services.AddSingleton<IGroupService, GroupService>();
builder.Services.AddSingleton<IStatsService, StatsService>();
builder.Services.AddScoped<IChatHistoryService, ChatHistoryService>();
builder.Services.AddHostedService<RadegastBackgroundService>();
builder.Services.AddHostedService<PeriodicDisplayNameService>();

// Add logging configuration with additional filters
builder.Services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddSerilog();
    logging.AddFilter("Microsoft.AspNetCore.Hosting", LogLevel.Error);
    logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel", LogLevel.Error);
    logging.AddFilter("Microsoft.Extensions.Hosting", LogLevel.Warning);
    logging.AddFilter("Microsoft.AspNetCore.HttpLogging", LogLevel.Warning);
    logging.AddFilter("Microsoft.AspNetCore.Routing", LogLevel.Warning);
    logging.AddFilter("Microsoft.AspNetCore.StaticFiles", LogLevel.Warning);
});

var app = builder.Build();

// Initialize database and load accounts
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<RadegastDbContext>();
    context.Database.EnsureCreated();
    
    // Reset all accounts to offline status on startup (safeguard for crashes)
    var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();
    await accountService.ResetAllAccountsToOfflineAsync();
    
    // Load existing accounts
    await accountService.LoadAccountsAsync();
    
    // Ensure test account exists for development
    /*
    if (app.Environment.IsDevelopment())
    {
        await accountService.EnsureTestAccountAsync();
    }
    */
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseDeveloperExceptionPage();
}

app.UseHttpsRedirection();

// Enable CORS
app.UseCors("AllowExternalAccess");

// Add custom authentication middleware
app.UseCustomAuthentication();

// Serve static files
app.UseStaticFiles();

app.UseAuthorization();

// Map controllers and hubs
app.MapControllers();
app.MapHub<RadegastHub>("/radegasthub");

// Serve index.html for the root path
app.MapFallbackToFile("index.html");

app.Run();
