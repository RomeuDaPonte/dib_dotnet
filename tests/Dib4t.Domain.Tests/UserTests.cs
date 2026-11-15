using Dib4t.Domain.Users;

namespace Dib4t.Domain.Tests;

public sealed class UserTests
{
    private static readonly User Imported = new("5f1e9a2b3c4d5e6f70819203", "Ana", "ana@dib4t.local", null,
        ["canSeeObras", null, "canSeeDefinicoes"], "Desenvolvimento");

    [Fact]
    public void Role_check_is_exact_and_skips_null_elements()
    {
        Imported.HasRole("canSeeDefinicoes").Should().BeTrue();
        Imported.HasRole("canseedefinicoes").Should().BeFalse();
        Imported.HasRole("canSeeDashboard").Should().BeFalse();
    }

    [Fact]
    public void Effective_roles_leave_out_null_elements_and_keep_the_order() =>
        Imported.EffectiveRoles.Should().Equal("canSeeObras", "canSeeDefinicoes");
}
