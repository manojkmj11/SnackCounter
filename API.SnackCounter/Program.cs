using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using SnackCounter.Application;
using SnackCounter.Domain;
using SnackCounter.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddAuthentication(DevelopmentEmailAuthenticationHandler.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, DevelopmentEmailAuthenticationHandler>(
        DevelopmentEmailAuthenticationHandler.AuthenticationScheme,
        _ => { });
builder.Services.AddAuthorization();
builder.Services.AddSignalR();
builder.Services.AddHttpClient("MockHr", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["MockHr:BaseUrl"] ?? "http://localhost:5205/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<IExternalHrClaimClient, MockHrClaimClient>();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration["Cors:AngularOrigin"] ?? "http://localhost:4200")
        .AllowCredentials()
        .AllowAnyHeader()
        .AllowAnyMethod()));
var databaseProvider = builder.Configuration["Database:Provider"] ?? "SqlServer";
var useInMemoryDatabase = string.Equals(databaseProvider, "InMemory", StringComparison.OrdinalIgnoreCase);
var useSqlServer = string.Equals(databaseProvider, "SqlServer", StringComparison.OrdinalIgnoreCase);
if (!useInMemoryDatabase && !useSqlServer)
{
    throw new InvalidOperationException($"Unsupported Database:Provider '{databaseProvider}'. Use SqlServer or InMemory.");
}

builder.Services.AddDbContextFactory<SnackCounterDbContext>(options =>
{
    if (useInMemoryDatabase)
    {
        options.UseInMemoryDatabase(builder.Configuration["Database:Name"] ?? "SnackCounter-Tests");
        return;
    }

    var connectionString = builder.Configuration.GetConnectionString("SnackCounter");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "ConnectionStrings:SnackCounter must be configured when Database:Provider is SqlServer.");
    }

    options.UseSqlServer(connectionString);
});
builder.Services.AddScoped<EfRefillStore>();
builder.Services.AddScoped<IRefillStore>(services => services.GetRequiredService<EfRefillStore>());
builder.Services.AddScoped<IVolunteerDirectory>(services => services.GetRequiredService<EfRefillStore>());
builder.Services.AddSingleton<IVolunteerSelector, EfVolunteerSelector>();
builder.Services.AddSingleton<IChatTool, InMemoryChatTool>();
builder.Services.AddSingleton<IHrSystem, InMemoryHrSystem>();
builder.Services.AddSingleton(TimeProvider.System);
var nokToInr = builder.Configuration.GetValue<decimal?>("DemoExchangeRates:NOK:INR") ?? 10m;
var inrToNok = builder.Configuration.GetValue<decimal?>("DemoExchangeRates:INR:NOK") ?? 0.1m;
// These fallback rates make the demo runnable; deployments should configure an approved rate source.
builder.Services.AddSingleton<ICurrencyConverter>(new TableCurrencyConverter(new Dictionary<(Currency From, Currency To), decimal>
{
    [(Currency.NOK, Currency.INR)] = nokToInr,
    [(Currency.INR, Currency.NOK)] = inrToNok
}));
builder.Services.AddScoped<RefillLifecycleService>(services =>
{
    var assignmentTimeoutMinutes = services.GetRequiredService<IConfiguration>()
        .GetValue("SnackCounter:AssignmentTimeoutMinutes", 24 * 60);
    return new RefillLifecycleService(
        services.GetRequiredService<IRefillStore>(),
        services.GetRequiredService<IVolunteerSelector>(),
        services.GetRequiredService<IChatTool>(),
        services.GetRequiredService<IHrSystem>(),
        services.GetRequiredService<TimeProvider>(),
        TimeSpan.FromMinutes(assignmentTimeoutMinutes),
        services.GetRequiredService<ICurrencyConverter>());
});
builder.Services.AddScoped<MonthlySpendingReportService>();
builder.Services.AddHostedService<SnackCounterAutomationWorker>();

var app = builder.Build();

var applyMigrations =
    app.Configuration.GetValue<bool?>("Database:ApplyMigrationsOnStartup") ?? app.Environment.IsDevelopment();
if (useSqlServer && applyMigrations)
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = await scope.ServiceProvider
        .GetRequiredService<IDbContextFactory<SnackCounterDbContext>>()
        .CreateDbContextAsync();
    await database.Database.MigrateAsync();
}
else if (useInMemoryDatabase)
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = await scope.ServiceProvider
        .GetRequiredService<IDbContextFactory<SnackCounterDbContext>>()
        .CreateDbContextAsync();
    await database.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    await SnackCounterSeedData.InitializeAsync(app.Services);
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<API.SnackCounter.Controllers.NotificationsHub>("/hubs/notifications").RequireAuthorization();

app.Run();

public partial class Program { }
