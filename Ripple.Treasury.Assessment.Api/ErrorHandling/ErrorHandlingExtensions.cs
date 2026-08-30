namespace Ripple.Treasury.Assessment.Api.ErrorHandling;

public static class ErrorHandlingExtensions
{
    public static void AddProblemDetailsErrorHandling(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
    }
}
