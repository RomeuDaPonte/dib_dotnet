using Dib4t.Application.Account;
using Dib4t.Infrastructure.Auth;
using Dib4t.Infrastructure.Mongo;
using Dib4t.Infrastructure.Mongo.Repositories;
using Dib4t.Tests.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Bson;

namespace Dib4t.Infrastructure.Tests.Auth;

/// <summary><c>users</c> reads on mongo:9.0.8 with imported shapes, and the <c>Jwt:Key</c> startup checks.</summary>
public sealed class UserRepositoryAndKeyTests(MongoFixture mongo) : IClassFixture<MongoFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Finds_by_exact_email_first_in_natural_order_and_by_id()
    {
        MongoMappings.Register();
        var database = mongo.NewDatabase();
        var first = ObjectId.GenerateNewId();
        var users = database.GetCollection<BsonDocument>(MongoCollectionNames.Users);
        await users.InsertManyAsync(
        [
            // Imported shape: roles with a null element, no fieldOfCompetencies (Mongoose default applies), no __v.
            new BsonDocument { { "_id", first }, { "roles", new BsonArray { "canSeeObras", BsonNull.Value } }, { "name", "Ana" }, { "email", "ana@dib4t.local" }, { "password", "$2a$10$x" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "roles", new BsonArray() }, { "name", "Ana 2" }, { "email", "ana@dib4t.local" }, { "password", "$2a$10$y" } },
        ], cancellationToken: Cancellation);
        var repository = new MongoUserRepository(database);

        var byEmail = await repository.FindByEmailAsync("ana@dib4t.local", Cancellation);
        var byId = await repository.FindByIdAsync(first.ToString(), Cancellation);

        byEmail.Should().NotBeNull();
        byEmail!.Id.Should().Be(first.ToString());
        byEmail.Roles.Should().Equal("canSeeObras", null);
        byEmail.FieldOfCompetencies.Should().Be("Desenvolvimento");
        byId.Should().BeEquivalentTo(byEmail);
        (await repository.FindByEmailAsync("ANA@dib4t.local", Cancellation)).Should().BeNull();
        (await repository.FindByIdAsync("0123456789abcdef01234567", Cancellation)).Should().BeNull();
        (await repository.FindByIdAsync("me", Cancellation)).Should().BeNull();
    }

    [Theory]
    [InlineData("Development", "", "Key field is required")]
    [InlineData("Development", "too-short-key", "256 bits")]
    [InlineData("Production", "development-only-0123456789abcdef0123456789abcdef", "development key")]
    public void Invalid_keys_stop_the_start(string environment, string key, string message)
    {
        var validate = () => KeyOptions(environment, key);

        validate.Should().Throw<OptionsValidationException>().WithMessage($"*{message}*");
    }

    [Theory]
    [InlineData("Development", "development-only-0123456789abcdef0123456789abcdef")]
    [InlineData("Production", "a-production-key-from-the-environment-0123456789")]
    public void Valid_keys_are_accepted(string environment, string key) =>
        KeyOptions(environment, key).Key.Should().Be(key);

    private static JwtOptions KeyOptions(string environment, string key)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = key,
            ["Mongo:ConnectionString"] = "mongodb://127.0.0.1:1/dib4t",
            ["Logs:Path"] = Path.Combine(Path.GetTempPath(), $"dib4t-unused-{Guid.NewGuid():N}.db"),
        }).Build();
        var services = new ServiceCollection().AddSingleton<IHostEnvironment>(new TestEnvironment(environment)).AddInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<JwtOptions>>().Value;
    }

    [Fact]
    public void Adapters_are_registered_for_the_auth_ports()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = new string('k', 32),
            ["Mongo:ConnectionString"] = "mongodb://127.0.0.1:1/dib4t",
        }).Build();
        var services = new ServiceCollection().AddSingleton<IHostEnvironment>(new TestEnvironment("Development")).AddSingleton(TimeProvider.System).AddLogging().AddInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IPasswordHasher>().Should().BeOfType<BcryptPasswordHasher>();
        provider.GetRequiredService<ITokenService>().Should().BeOfType<JwtTokenService>();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IUserRepository>().Should().BeOfType<MongoUserRepository>();
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Dib4t.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
