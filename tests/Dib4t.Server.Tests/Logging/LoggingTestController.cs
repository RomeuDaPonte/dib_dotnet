using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Dib4t.Server.Tests.Logging;

/// <summary>Test-only endpoints (added to the app by <see cref="LoggingApp"/>): an unhandled exception and a secret in a log call.</summary>
[Route("api/test-logging")]
public sealed partial class LoggingTestController(ILogger<LoggingTestController> logger) : ControllerBase
{
    public const string ExceptionText = "Test failure 9a31";
    public const string Password = "pa55-word-SECRET-77";

    [HttpGet("throw")]
#pragma warning disable CA1822 // MVC actions are instance methods
    public IActionResult Throw() => throw new InvalidOperationException(ExceptionText);
#pragma warning restore CA1822

    [HttpPost("login")]
    public IActionResult Login()
    {
        LogLogin(logger, "ana@dib4t.local", Password);
        return Ok();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Login attempt {Email} with {Password}")]
    private static partial void LogLogin(ILogger logger, string email, string password);
}
