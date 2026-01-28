using FoodHub.Api.GraphQL.Mutations;
using FoodHub.Api.GraphQL.Queries;
using FoodHub.Api.Auth.Google;

using FoodHub.User.Infrastructure;
using Microsoft.Azure.Cosmos;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Azure.Identity;
using Serilog;
using Serilog.Events;
using HotChocolate.AspNetCore;


var builder = WebApplication.CreateBuilder(args);

// Azure Key Vault Configuration (Production only)
// if (builder.Environment.IsDevelopment())
{
    var keyVaultEndpoint = builder.Configuration["KeyVault:Endpoint"];
    if (!string.IsNullOrEmpty(keyVaultEndpoint))
    {
        builder.Configuration.AddAzureKeyVault(
            new Uri(keyVaultEndpoint),
            new DefaultAzureCredential());
    }
}

// User module registration
builder.Services.AddUserModule(builder.Configuration);

// Auth services
builder.Services.Configure<GoogleAuthOptions>(builder.Configuration.GetSection("GoogleAuth"));
builder.Services.AddScoped<IGoogleTokenValidator, GoogleTokenValidator>();
builder.Services.AddScoped<FoodHub.Api.Auth.JWT.IJwtTokenGenerator, FoodHub.Api.Auth.JWT.JwtTokenGenerator>();

// JWT Authentication
var jwtSection = builder.Configuration.GetSection("Jwt");
var secret = jwtSection.GetValue<string>("Secret") ?? throw new InvalidOperationException("JWT Secret is not configured");
var issuer = jwtSection.GetValue<string>("Issuer") ?? "FoodHub";
var audience = jwtSection.GetValue<string>("Audience") ?? "FoodHub";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = issuer,
            ValidAudience = audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret))
        };
    });

builder.Services.AddAuthorization();

// Add Controllers for auth endpoints
builder.Services.AddControllers();

builder.Services
    .AddGraphQLServer()
    .AddAuthorization()
    .AddQueryType()
    .AddTypeExtension<UserQuery>()
    .AddMutationType()
    .AddTypeExtension<UserMutation>();

// Serilog configuration
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .WriteTo.Debug()
    .CreateLogger();

// Expose Serilog's logger for code that requires Serilog-specific APIs
builder.Services.AddSingleton<Serilog.ILogger>(Log.Logger);

builder.Host.UseSerilog();

var app = builder.Build();

// Ensure User database is created
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<FoodHub.User.Infrastructure.Sql.UserDbContext>();
    context.Database.EnsureCreated();
}

// Correlation ID Middleware
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? Guid.NewGuid().ToString();
    context.Response.Headers.Append("X-Correlation-ID", correlationId);

    using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
    {
        await next();
    }
});

app.UseAuthentication();
app.UseAuthorization();

// Map Controllers for auth endpoints
app.MapControllers();

app.MapGraphQL("/graphql");

// To enable the Banana Cake Pop IDE, add the appropriate HotChocolate tooling package
// and call `app.UseBananaCakePop()` here (optional).

app.Run();

