using System.Text.Json;
using Dib4t.Application.Account;
using Dib4t.Application.Common;
using Dib4t.Domain.Users;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Dib4t.Application.Tests.Account;

/// <summary>Legacy <c>POST /api/account/login</c> flow (<c>routes/account.js</c>) and the token's user (PLAN.md §4.6).</summary>
public sealed class AccountServiceTests
{
    private const string Password = "char-test-1";
    private const string Hash = "$2a$10$abcdefghijklmnopqrstuuSomeHashValueThatIsNeverLogged000000";

    private static readonly User Ana = new("5f1e9a2b3c4d5e6f70819203", "Ana Administradora", "ana.admin@dib4t.local", Hash,
        ["canSeeOrcamentos", null, "canSeeDefinicoes"], "Desenvolvimento");

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IPasswordHasher _passwords = Substitute.For<IPasswordHasher>();
    private readonly ITokenService _tokens = Substitute.For<ITokenService>();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-15T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly RecordingLogger<AccountService> _logger = new();
    private readonly AccountService _service;

    public AccountServiceTests()
    {
        _users.FindByEmailAsync(Ana.Email!, Arg.Any<CancellationToken>()).Returns(Ana);
        _users.FindByIdAsync(Ana.Id, Arg.Any<CancellationToken>()).Returns(Ana);
        _passwords.Verify(Password, Hash).Returns(true);
        _tokens.CreateToken(Ana).Returns("header.payload.signature");
        var sessions = new UserSessionCache(Options.Create(new UserSessionOptions()), _time);
        _service = new AccountService(_users, _passwords, _tokens, sessions, _logger);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Valid_credentials_return_the_token_and_log_the_login()
    {
        var result = await LoginAsync(Ana.Email!, Password);

        result.Value.Should().Be("header.payload.signature");
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information)
            .Which.Message.Should().Be($"Login succeeded for ana.admin@dib4t.local (user {Ana.Id})");
    }

    [Fact]
    public async Task Invalid_body_returns_the_Joi_text_before_reading_the_database()
    {
        var result = await _service.LoginAsync(JsonElement.Parse("""{"email":"ana.admin@dib4t.local"}"""), Cancellation);

        result.Error.Should().Be(Error.Validation("\"password\" is required"));
        await _users.DidNotReceiveWithAnyArgs().FindByEmailAsync(default!, Cancellation);
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Unknown_email_is_the_legacy_400_text_and_never_checks_a_password()
    {
        var result = await LoginAsync("nobody@dib4t.local", Password);

        result.Error.Should().Be(Error.Validation("Invalid email or password."));
        _passwords.DidNotReceiveWithAnyArgs().Verify(default!, default!);
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Be("Login failed for nobody@dib4t.local: unknown e-mail");
    }

    [Fact]
    public async Task Wrong_password_is_the_legacy_400_text()
    {
        var result = await LoginAsync(Ana.Email!, "wrong-pass");

        result.Error.Should().Be(Error.Validation("Password incorreta!"));
        _tokens.DidNotReceiveWithAnyArgs().CreateToken(default!);
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Email_match_is_exact_like_the_legacy_findOne()
    {
        var result = await LoginAsync("ANA.ADMIN@DIB4T.LOCAL", Password);

        result.Error!.Message.Should().Be("Invalid email or password.");
        await _users.Received(1).FindByEmailAsync("ANA.ADMIN@DIB4T.LOCAL", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_user_without_a_password_hash_fails_like_bcrypt_compare_in_the_legacy_app()
    {
        _users.FindByEmailAsync("nohash@dib4t.local", Arg.Any<CancellationToken>()).Returns(Ana with { Email = "nohash@dib4t.local", PasswordHash = null });

        var login = () => LoginAsync("nohash@dib4t.local", Password);

        await login.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task No_log_entry_contains_the_password_or_the_hash()
    {
        await LoginAsync(Ana.Email!, Password);
        await LoginAsync(Ana.Email!, "wrong-pass");
        await _service.LoginAsync(JsonElement.Parse("""{"email":"ana.admin@dib4t.local","password":"1234"}"""), Cancellation);

        _logger.Entries.Should().HaveCount(3);
        _logger.Entries.Should().AllSatisfy(e =>
            e.Message.Should().NotContain(Password).And.NotContain("wrong-pass").And.NotContain("1234\"").And.NotContain(Hash));
    }

    [Fact]
    public async Task The_token_user_is_read_once_within_the_cache_window_then_again()
    {
        (await _service.GetSessionUserAsync(Ana.Id, Cancellation)).Should().Be(Ana);
        _time.Advance(TimeSpan.FromSeconds(29));
        await _service.GetSessionUserAsync(Ana.Id, Cancellation);
        await _users.Received(1).FindByIdAsync(Ana.Id, Arg.Any<CancellationToken>());

        _time.Advance(TimeSpan.FromSeconds(1));
        var withoutSettings = Ana with { Roles = ["canSeeOrcamentos"] };
        _users.FindByIdAsync(Ana.Id, Arg.Any<CancellationToken>()).Returns(withoutSettings);

        (await _service.GetSessionUserAsync(Ana.Id, Cancellation)).Should().Be(withoutSettings);
        await _users.Received(2).FindByIdAsync(Ana.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_user_is_cached_too()
    {
        const string Deleted = "0123456789abcdef01234567";

        (await _service.GetSessionUserAsync(Deleted, Cancellation)).Should().BeNull();
        (await _service.GetSessionUserAsync(Deleted, Cancellation)).Should().BeNull();

        await _users.Received(1).FindByIdAsync(Deleted, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Me_reads_the_database_every_time()
    {
        await _service.GetSessionUserAsync(Ana.Id, Cancellation);
        await _service.GetUserAsync(Ana.Id, Cancellation);
        await _service.GetUserAsync(Ana.Id, Cancellation);

        await _users.Received(3).FindByIdAsync(Ana.Id, Arg.Any<CancellationToken>());
    }

    private Task<Result<string>> LoginAsync(string email, string password) =>
        _service.LoginAsync(JsonSerializer.SerializeToElement(new { email, password }), Cancellation);
}
