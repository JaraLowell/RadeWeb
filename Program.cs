using RadegastWeb.Hubs;
using RadegastWeb.Services;
using RadegastWeb.Data;
using RadegastWeb.Models;
using RadegastWeb.Middleware;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Text.Json;

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
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() 
    { 
        Title = "Radegast Web API", 
        Version = "v1.2",
        Description = "Multi-Account Second Life Web Client API",
        Contact = new() 
        {
            Name = "JaraLowell",
            Url = new Uri("https://github.com/JaraLowell/RadeWeb")
        }
    });
    
    // Include XML comments if available
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }
});

// Configure authentication
builder.Services.Configure<AuthenticationConfig>(
    builder.Configuration.GetSection("Authentication"));
builder.Services.AddSingleton<IAuthenticationService, AuthenticationService>();

// Add SignalR with improved configuration
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 1024 * 1024; // 1MB
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60); // Server will timeout client after 60 seconds of inactivity
    options.KeepAliveInterval = TimeSpan.FromSeconds(15); // Send keep-alive ping every 15 seconds
    options.HandshakeTimeout = TimeSpan.FromSeconds(15); // Handshake must complete within 15 seconds
    options.MaximumParallelInvocationsPerClient = 10; // Limit concurrent method calls per client
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

// Add HttpClient for AI chat service
builder.Services.AddHttpClient();

// Register custom services - Use singleton for account management
builder.Services.AddSingleton<IAccountService, AccountService>();
builder.Services.AddSingleton<INoticeService, NoticeService>();
builder.Services.AddSingleton<IPresenceService, PresenceService>();
builder.Services.AddSingleton<IRegionInfoService, RegionInfoService>();
builder.Services.AddSingleton<INameResolutionService, NameResolutionService>();
builder.Services.AddSingleton<ISlUrlParser, SlUrlParser>();
builder.Services.AddSingleton<IGroupService, GroupService>();
builder.Services.AddSingleton<IStatsService, StatsService>();
builder.Services.AddSingleton<IChatLogService, ChatLogService>();
builder.Services.AddSingleton<IRegionMapCacheService, RegionMapCacheService>();
builder.Services.AddSingleton<ICorradeService, CorradeService>();
builder.Services.AddSingleton<IAiChatService, AiChatService>();
builder.Services.AddSingleton<IChatHistoryService, ChatHistoryService>();
builder.Services.AddSingleton<IScriptDialogService, ScriptDialogService>();
builder.Services.AddSingleton<ITeleportRequestService, TeleportRequestService>();
builder.Services.AddSingleton<IConnectionTrackingService, ConnectionTrackingService>();
builder.Services.AddSingleton<IChatProcessingService, ChatProcessingService>();
builder.Services.AddSingleton<ISLTimeService, SLTimeService>();

// Interactive notice services
builder.Services.AddSingleton<IFriendshipRequestService, FriendshipRequestService>();
builder.Services.AddSingleton<IGroupInvitationService, GroupInvitationService>();

// Display name services - Unified approach with separate global cache
builder.Services.AddSingleton<IGlobalDisplayNameCache, GlobalDisplayNameCache>();
builder.Services.AddSingleton<IMasterDisplayNameService, MasterDisplayNameService>();

// Compatibility adapter for existing IDisplayNameService interface
builder.Services.AddSingleton<IDisplayNameService>(provider => 
    new DisplayNameServiceCompatibilityAdapter(provider.GetRequiredService<IMasterDisplayNameService>()));

builder.Services.AddHostedService<RadegastBackgroundService>();
builder.Services.AddHostedService<MasterDisplayNameService>();

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
    context.Database.Migrate(); // This applies pending migrations automatically
    
    // Reset all accounts to offline status on startup (safeguard for crashes)
    var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();
    await accountService.ResetAllAccountsToOfflineAsync();
    
    // Load existing accounts
    await accountService.LoadAccountsAsync();
    
    // Ensure services are initialized by requesting them once
    var corradeService = scope.ServiceProvider.GetRequiredService<ICorradeService>();
    var aiChatService = scope.ServiceProvider.GetRequiredService<IAiChatService>();
    
    // Ensure test account exists for development
    /*
    if (app.Environment.IsDevelopment())
    {
        await accountService.EnsureTestAccountAsync();
    }
    */
}

// Configure the HTTP request pipeline.
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Radegast Web API v1.2");
    c.RoutePrefix = "swagger"; // Set swagger UI at /swagger
});

if (app.Environment.IsDevelopment())
{
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
