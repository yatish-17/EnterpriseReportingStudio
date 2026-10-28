using System.Text;
using EnterpriseReporting.Api.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace EnterpriseReporting.Tests;

public sealed class ApiExceptionHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_ReturnsSafeProblemDetailsWithoutExceptionContent()
    {
        const string sensitiveExceptionText = "private-token-do-not-return";
        var responseBody = new MemoryStream();
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "test-trace-id"
        };
        context.Request.Path = "/diagnostic";
        context.Response.Body = responseBody;
        var handler = new ApiExceptionHandler(NullLogger<ApiExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(
            context,
            new InvalidOperationException(sensitiveExceptionText),
            CancellationToken.None);

        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        Assert.Contains("An unexpected error occurred.", body);
        Assert.Contains("test-trace-id", body);
        Assert.DoesNotContain(sensitiveExceptionText, body);
        Assert.DoesNotContain("InvalidOperationException", body);
        Assert.DoesNotContain(" at ", body);
    }
}
