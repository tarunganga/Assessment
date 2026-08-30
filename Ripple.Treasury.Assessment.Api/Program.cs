using Ripple.Treasury.Assessment.Api.ErrorHandling;
using Scalar.AspNetCore;
using Ripple.Treasury.Assessment.Infrastructure.Extensions;
using Ripple.Treasury.Assessment.Services.Extensions;

namespace Ripple.Treasury.Assessment.Api;

public class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        builder.Services.AddAuthorization();

        // Add controllers. Keeping the Async suffix in action names is what lets
        // nameof(...) in CreatedAtAction match; MVC strips it by default.
        builder.Services.AddControllers(options => options.SuppressAsyncSuffixInActionNames = false);

        // Add OpenAPI
        builder.Services.AddOpenApi();
        
        // Add Infrastructure
        string? connectString = builder.Configuration.GetConnectionString("TreasuryDb");

        if (string.IsNullOrWhiteSpace(connectString))
        {
            throw new InvalidOperationException("TreasuryDb connection string is not configured.");
        }
        
        // Add Infrastructure
        builder.Services.AddInfrastructure(connectString);
        
        // Register Services
        builder.Services.RegisterServices();
        
        // Add ProblemDetails
        builder.Services.AddProblemDetails();
        
        // Add Exception Handler
        builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();

        WebApplication app = builder.Build();

        // Use Global Exception Handler
        app.UseExceptionHandler();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        app.UseHttpsRedirection();

        app.MapControllers();

        app.UseAuthorization();

        app.Run();
    }
}