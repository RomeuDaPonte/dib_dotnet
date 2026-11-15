using System.Text.Json;
using Dib4t.Application.Account;
using Dib4t.Application.Common;
using Dib4t.Domain.Users;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Dib4t.Application.Tests.Account;

/// <summary>Legacy user management flow (<c>routes/account.js</c>): order of the checks, texts, D2, sessions, logs.</summary>
public sealed class UserAdministrationServiceTests
{
    private const string BrunoId = "5f1e9a2b3c4d5e6f70819204";
    private const string NewHash = "$2a$10$newHashNewHashNewHashNewHashNewHashNewHashNewHashNewHas";

    private static readonly User Bruno = new(BrunoId, "Bruno Maquinação", "bruno.limitado@dib4t.local", "$2a$04$stored",
        ["canSeeObras", "canSeeLeituras"], "Maquinação");

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IPasswordHasher _passwords = Substitute.For<IPasswordHasher>();
    private readonly FakeTimeProvider _time = new();
    private readonly RecordingLogger<UserAdministrationService> _logger = new();
    private readonly UserSessionCache _sessions;
    private readonly UserAdministrationService _service;

    public UserAdministrationServiceTests()
    {
        _users.FindByIdAsync(BrunoId, Arg.Any<CancellationToken>()).Returns(Bruno);
        _users.UpdateAsync(BrunoId, Arg.Any<UserUpdate>(), Arg.Any<CancellationToken>()).Returns(true);
        _users.DeleteAsync(BrunoId, Arg.Any<CancellationToken>()).Returns(true);
        _users.InsertAsync(Arg.Any<NewUser>(), Arg.Any<CancellationToken>()).Returns("5f1e9a2b3c4d5e6f70819299");
        _passwords.Hash(Arg.Any<string>()).Returns(NewHash);
        _sessions = new UserSessionCache(Options.Create(new UserSessionOptions()), _time);
        _service = new UserAdministrationService(_users, _passwords, _sessions, _logger);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_checks_the_email_before_the_model_and_hashes_with_the_legacy_format()
    {
        _users.FindByEmailAsync("ana.admin@dib4t.local", Arg.Any<CancellationToken>()).Returns(Bruno with { Email = "ana.admin@dib4t.local" });

        // A body the model refuses (fieldOfCompetencies not in the enum) still answers the e-mail check first.
        var taken = await _service.CreateAsync(Body("""{"name":"Ana","email":"ana.admin@dib4t.local","password":"char-test-1","fieldOfCompetencies":"Faturada"}"""), Cancellation);
        var refused = await _service.CreateAsync(Body("""{"name":"Ana","email":"ana.nova@dib4t.local","password":"char-test-1","fieldOfCompetencies":"Faturada"}"""), Cancellation);
        var created = await _service.CreateAsync(Body("""{"name":"Ana Nova","email":"ana.nova@dib4t.local","password":"char-test-1","roles":["canSeeObras",null]}"""), Cancellation);

        taken.Error.Should().Be(Error.Validation("Email já registado."));
        refused.Error!.Kind.Should().Be(ErrorKind.Failure);
        refused.Error.Message.Should().Be("Something failed.");
        created.Value.Should().BeEquivalentTo(new User("5f1e9a2b3c4d5e6f70819299", "Ana Nova", "ana.nova@dib4t.local", NewHash, ["canSeeObras", null], "Desenvolvimento"));
        await _users.Received(1).InsertAsync(new NewUser("Ana Nova", "ana.nova@dib4t.local", NewHash, created.Value.Roles, "Desenvolvimento"), Arg.Any<CancellationToken>());
        _passwords.Received(1).Hash("char-test-1");
    }

    [Fact]
    public async Task Stored_strings_are_NFC_and_the_duplicate_check_uses_the_stored_form()
    {
        var decomposed = "João Simões"; // NFD "João Simões"
        await _service.CreateAsync(Body(JsonSerializer.Serialize(new { name = decomposed, email = "joão@dib4t.local", password = "char-test-1" })), Cancellation);

        await _users.Received(1).FindByEmailAsync("joão@dib4t.local", Arg.Any<CancellationToken>());
        await _users.Received(1).InsertAsync(Arg.Is<NewUser>(u => u.Name == "João Simões" && u.Email == "joão@dib4t.local"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_order_is_joi_then_id_then_404_then_the_model()
    {
        var invalid = await _service.UpdateAsync("abc", Body("""{"name":"B","email":"not-an-email"}"""), Cancellation);
        var badId = await _service.UpdateAsync("abc", Body(Edit()), Cancellation);
        var missing = await _service.UpdateAsync("0123456789abcdef01234567", Body(Edit(fieldOfCompetencies: null)), Cancellation);
        var refused = await _service.UpdateAsync(BrunoId, Body(Edit(fieldOfCompetencies: null)), Cancellation);
        var illegalPassword = await _service.UpdateAsync(BrunoId, Body(Edit(password: "12345")), Cancellation);

        invalid.Error.Should().Be(Error.Validation("\"email\" must be a valid email"));
        badId.Error.Should().Be(Error.Failure("Something failed."));
        missing.Error.Should().Be(Error.NotFound("Utilizador não exista na bd"));
        refused.Error!.Message.Should().Be("Something failed.");
        illegalPassword.Error!.Message.Should().Be("Something failed.");
        await _users.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<UserUpdate>(), Arg.Any<CancellationToken>());
        _passwords.DidNotReceive().Hash(Arg.Any<string>());
    }

    [Theory]
    [InlineData("\"\"", false)]
    [InlineData("null", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("\"char-test-2\"", true)]
    [InlineData("\" \"", true)]
    public async Task Edit_rehashes_only_a_sent_password(string password, bool rehashed)
    {
        var result = await _service.UpdateAsync(BrunoId, Body(Edit(password: password)), Cancellation);

        result.Value.Should().BeEquivalentTo(new UserEditResult(BrunoId, "Bruno Maquinação Silva", "bruno.limitado@dib4t.local", ["canSeeObras", null]));
        await _users.Received(1).UpdateAsync(BrunoId,
            new UserUpdate("Bruno Maquinação Silva", "bruno.limitado@dib4t.local", "Montagem", result.Value.Roles, rehashed ? NewHash : null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_without_roles_removes_them_and_the_answer_leaves_them_out()
    {
        var result = await _service.UpdateAsync(BrunoId, Body("""{"name":"Bruno","email":"bruno.limitado@dib4t.local","fieldOfCompetencies":"Montagem"}"""), Cancellation);

        result.Value.Roles.Should().BeNull();
        await _users.Received(1).UpdateAsync(BrunoId, Arg.Is<UserUpdate>(u => u.Roles == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_and_delete_drop_the_cached_session_so_new_roles_apply_at_once()
    {
        var loads = 0;
        Task<User?> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<User?>(Bruno);
        }

        await _sessions.GetOrLoadAsync(BrunoId, Load, Cancellation);
        await _service.UpdateAsync(BrunoId, Body(Edit()), Cancellation);
        await _sessions.GetOrLoadAsync(BrunoId, Load, Cancellation);
        await _service.DeleteAsync(BrunoId, Cancellation);
        await _sessions.GetOrLoadAsync(BrunoId, Load, Cancellation);

        loads.Should().Be(3, "the 30 s window does not delay an administrator's change");
    }

    [Fact]
    public async Task Delete_removes_the_requested_user_D2()
    {
        var deleted = await _service.DeleteAsync(BrunoId.ToUpperInvariant(), Cancellation);
        var missing = await _service.DeleteAsync("0123456789abcdef01234567", Cancellation);
        var twelveChars = await _service.DeleteAsync("abcdefghijkl", Cancellation);
        var badId = await _service.DeleteAsync("abc", Cancellation);

        deleted.Value.Should().Be(Bruno);
        await _users.Received(1).DeleteAsync(BrunoId, Arg.Any<CancellationToken>());
        missing.Error.Should().Be(Error.NotFound("Utilizador não exista na bd"));
        twelveChars.Error.Should().Be(Error.NotFound("Utilizador não exista na bd"), "a 12-character id casts to an ObjectId that matches nothing");
        badId.Error.Should().Be(Error.Failure("Something failed."));
    }

    [Fact]
    public async Task A_delete_that_deletes_nothing_is_the_legacy_500()
    {
        _users.DeleteAsync(BrunoId, Arg.Any<CancellationToken>()).Returns(false);

        (await _service.DeleteAsync(BrunoId, Cancellation)).Error.Should().Be(Error.Failure("Erro no servidor!!"));
    }

    [Fact]
    public async Task Get_answers_not_found_and_casts_like_legacy()
    {
        (await _service.GetAsync(BrunoId, Cancellation)).Value.Should().Be(Bruno);
        (await _service.GetAsync("0123456789abcdef01234567", Cancellation)).Error.Should().Be(Error.NotFound("not found!"));
        (await _service.GetAsync("me", Cancellation)).Error.Should().Be(Error.Failure("Something failed."));
    }

    [Fact]
    public async Task Changes_are_logged_without_passwords_or_hashes()
    {
        await _service.CreateAsync(Body("""{"name":"Ana Nova","email":"ana.nova@dib4t.local","password":"secret-pass-1"}"""), Cancellation);
        await _service.UpdateAsync(BrunoId, Body(Edit(password: "\"secret-pass-2\"")), Cancellation);
        await _service.DeleteAsync(BrunoId, Cancellation);

        _logger.Entries.Select(e => e.Message).Should().Equal(
            "User 5f1e9a2b3c4d5e6f70819299 (ana.nova@dib4t.local) created",
            "User 5f1e9a2b3c4d5e6f70819204 (bruno.limitado@dib4t.local) updated (password changed: True)",
            "User 5f1e9a2b3c4d5e6f70819204 (bruno.limitado@dib4t.local) deleted");
        _logger.Entries.Should().NotContain(e => e.Message.Contains("secret-pass") || e.Message.Contains(NewHash));
    }

    private static JsonElement Body(string json) => JsonElement.Parse(json);

    /// <summary>The legacy UI's edit body: one role slot per checkbox (null = unchecked), password "" = unchanged.</summary>
    private static string Edit(string? password = "\"\"", string? fieldOfCompetencies = "\"Montagem\"")
    {
        var field = fieldOfCompetencies is null ? string.Empty : $",\"fieldOfCompetencies\":{fieldOfCompetencies}";
        return $$"""{"name":"Bruno Maquinação Silva","email":"bruno.limitado@dib4t.local","roles":["canSeeObras",null],"password":{{password ?? "\"\""}}{{field}}}""";
    }
}
