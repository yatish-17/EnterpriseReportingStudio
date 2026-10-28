using EnterpriseReporting.Api.Security;
using EnterpriseReporting.Api.Health;
using EnterpriseReporting.Api.Infrastructure;
using EnterpriseReporting.Application.Abstractions;
using EnterpriseReporting.Application.DataSources;
using EnterpriseReporting.Application.Datasets;
using EnterpriseReporting.Application.Reports;
using EnterpriseReporting.Application.Security;
using EnterpriseReporting.Infrastructure.Configuration;
using EnterpriseReporting.Infrastructure.Persistence;
using EnterpriseReporting.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.FileProviders;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

var database = builder.Configuration.GetConnectionString("EnterpriseReportingDb");
var connectionTimeoutSeconds = GetTimeout(builder.Configuration, "Database:ConnectionTimeoutSeconds", 5);
var commandTimeoutSeconds = GetTimeout(builder.Configuration, "Database:CommandTimeoutSeconds", 3);
var healthCheckTimeoutSeconds = GetTimeout(builder.Configuration, "Database:HealthCheckTimeoutSeconds", 10);
var databaseName = builder.Configuration["Database:Name"] ?? "EnterpriseReportingDb";
var databaseSettings = DatabaseConnectionSettings.Create(
    database,
    connectionTimeoutSeconds,
    commandTimeoutSeconds,
    healthCheckTimeoutSeconds,
    databaseName);

if (builder.Configuration.GetValue<bool>("Database:InitializeOnStartup"))
{
    var schemaPackage = Path.Combine(
        AppContext.BaseDirectory,
        "database",
        "EnterpriseReportingStudioWorkspace.dacpac");
    await WorkspaceDatabaseInitializer.EnsureCreatedAsync(
        databaseSettings,
        schemaPackage);
}

if (args.Contains("--provision-initial-admin", StringComparer.Ordinal))
{
    await InitialAdminProvisioningCommand.RunAsync(databaseSettings);
    return;
}

if (args.Length == 2 &&
    args[0] == "--reset-admin-password" &&
    int.TryParse(args[1], out var administratorUserId))
{
    await InitialAdminProvisioningCommand.ResetAdministratorPasswordAsync(
        databaseSettings,
        administratorUserId);
    return;
}

var jwtSettings = JwtSettings.Create(
    builder.Configuration["Authentication:SigningKey"],
    builder.Configuration["Authentication:Issuer"],
    builder.Configuration["Authentication:Audience"],
    builder.Environment.IsProduction());

builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(databaseSettings);
builder.Services.AddDataProtection();
builder.Services.Configure<Microsoft.AspNetCore.DataProtection.DataProtectionOptions>(
    options => options.ApplicationDiscriminator = "EnterpriseReportingStudio");
builder.Services.AddSingleton<IDatabaseReadinessProbe, SqlServerReadinessProbe>();
builder.Services.AddSingleton(jwtSettings);
builder.Services.AddSingleton<AccessTokenIssuer>();
builder.Services.AddSingleton<ReportShareTokenService>();
builder.Services.AddSingleton<PortablePackageLinkService>();
builder.Services.AddSingleton<LiveAccountClaimsValidation>();
builder.Services.AddSingleton<IPasswordHasher<PasswordHashingSubject>>(
    new PasswordHasher<PasswordHashingSubject>(
        Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions
        {
            IterationCount = 600_000
        })));
builder.Services.AddSingleton<DummyPasswordHash>();
builder.Services.AddScoped<IUserAuthenticationStore, SqlUserAuthenticationStore>();
builder.Services.AddScoped<ITeamService, SqlTeamService>();
builder.Services.AddScoped<IOrganizationReportHeaderSettings, SqlOrganizationReportHeaderSettings>();
builder.Services.AddScoped<ISqlDataSourceService>(services =>
    new SqlDataSourceService(
        databaseSettings.ConnectionString,
        services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(),
        services.GetRequiredService<ILogger<SqlDataSourceService>>()));
builder.Services.AddScoped<IDatasetService>(services =>
    new SqlDatasetService(
        databaseSettings.ConnectionString,
        services.GetRequiredService<ISqlDataSourceService>()));
builder.Services.AddScoped<IReportService>(services =>
    new SqlReportService(
        databaseSettings.ConnectionString,
        services.GetRequiredService<IDatasetService>()));
builder.Services.AddScoped<IReportFolderService>(services =>
    new SqlReportFolderService(databaseSettings.ConnectionString));
builder.Services.AddScoped<IMultiSourceReportService>(services =>
    new SqlMultiSourceReportService(
        databaseSettings.ConnectionString,
        services.GetRequiredService<IDatasetService>()));
builder.Services.AddScoped<AuthenticationService>();
builder.Services.AddControllers();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SigningKey)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
                context.HttpContext.RequestServices
                    .GetRequiredService<LiveAccountClaimsValidation>()
                    .ValidateAsync(context)
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Administrator", policy => policy.RequireRole("Admin"));
    options.AddPolicy("DesignerOrAdministrator", policy =>
        policy.RequireRole("Admin", "Report Designer"));
});
builder.Services
    .AddHealthChecks()
    .AddCheck<DatabaseReadinessHealthCheck>(
        "database",
        failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
        tags: ["ready"],
        timeout: TimeSpan.FromSeconds(healthCheckTimeoutSeconds));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
var publishedFrontendRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var developmentFrontendRoot = Path.GetFullPath(Path.Combine(
    app.Environment.ContentRootPath,
    "..",
    "..",
    "frontend",
    "dist"));
var frontendRoot = Directory.Exists(publishedFrontendRoot)
    ? publishedFrontendRoot
    : app.Environment.IsDevelopment()
        ? developmentFrontendRoot
        : publishedFrontendRoot;
if (Directory.Exists(frontendRoot))
{
    var frontendFiles = new PhysicalFileProvider(frontendRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = frontendFiles });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = frontendFiles });
}
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers().RequireAuthorization();
app.MapHealthChecks("/health/live", HealthCheckResponseWriter.ForLiveness()).AllowAnonymous();
app.MapHealthChecks("/health/ready", HealthCheckResponseWriter.ForReadiness()).AllowAnonymous();
app.MapGet("/shared/{token}", async context =>
{
    var indexFile = Path.Combine(frontendRoot, "index.html");
    if (!File.Exists(indexFile))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(indexFile);
}).AllowAnonymous();

app.Run();

static int GetTimeout(IConfiguration configuration, string key, int defaultValue)
{
    var configuredValue = configuration[key];
    if (configuredValue is null)
    {
        return defaultValue;
    }

    if (!int.TryParse(configuredValue, out var seconds) || seconds is < 1 or > 30)
    {
        throw new InvalidOperationException(
            $"Configuration '{key}' must be a whole number between 1 and 30.");
    }

    return seconds;
}

public partial class Program
{
}
