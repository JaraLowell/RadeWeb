using RadegastWeb.Hubs;
using RadegastWeb.Services;
using RadegastWeb.Data;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
builder.Host.UseSerilog((context, configuration) =>
{
    configuration
        .WriteTo.Console()
        .WriteTo.File("logs/radegast-web-.log", rollingInterval: RollingInterval.Day)
        .ReadFrom.Configuration(context.Configuration);
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

// Add SignalR
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 1024 * 1024; // 1MB
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Register custom services - Use singleton for account management
builder.Services.AddSingleton<IAccountService, AccountService>();
builder.Services.AddSingleton<IDisplayNameService, DisplayNameService>();
builder.Services.AddSingleton<INoticeService, NoticeService>();
builder.Services.AddSingleton<IPresenceService, PresenceService>();
builder.Services.AddSingleton<IRegionInfoService, RegionInfoService>();
builder.Services.AddScoped<IChatHistoryService, ChatHistoryService>();
builder.Services.AddHostedService<RadegastBackgroundService>();

// Add logging configuration
builder.Services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddSerilog();
});

var app = builder.Build();

// Initialize database and load accounts
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<RadegastDbContext>();
    context.Database.EnsureCreated();
    
    // Load existing accounts
    var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();
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
app.UseCors("AllowAll");

// Serve static files
app.UseStaticFiles();

app.UseAuthorization();

// Map controllers and hubs
app.MapControllers();
app.MapHub<RadegastHub>("/radegasthub");

// Serve index.html for the root path
app.MapFallbackToFile("index.html");

app.Run();
