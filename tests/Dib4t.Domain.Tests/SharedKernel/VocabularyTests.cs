using System.Text;
using Dib4t.SharedKernel.Vocabulary;

namespace Dib4t.Domain.Tests.SharedKernel;

/// <summary>
/// The stored vocabularies are the exact legacy strings (back-end <c>helpers/*.js</c>, PLAN.md §4.2, §4.3 rule 2):
/// accents included, NFC, in the legacy list order.
/// </summary>
public sealed class VocabularyTests
{
    public static TheoryData<string, IReadOnlyList<string>, string[]> Lists => new()
    {
        { nameof(JobStates), JobStates.All, ["Desenvolvimento", "Em Aprovação", "Aprovado", "Maquinação", "Montagem", "Para faturação", "Faturada", "Apagada"] },
        { nameof(JobPriorities), JobPriorities.All, ["Alta", "Média", "Baixa"] },
        { nameof(TaskTypes), TaskTypes.All, ["automation", "consultancy", "development", "machining", "assembly", "others", "components"] },
        { nameof(RoleNames), RoleNames.All, ["canSeeOrcamentos", "canSeeObras", "canSeeHistorico", "canSeeEntidades", "canSeeDefinicoes", "canSeeDashboard", "canSeeLeituras"] },
        { nameof(FieldsOfCompetence), FieldsOfCompetence.All, ["Desenvolvimento", "Maquinação", "Montagem", "Para faturação"] },
        { nameof(CommentTypes), CommentTypes.All, ["Quantidade de peças", "Tipo de material", "Detalhes de fabrico", "Zonas a considerar tipo de acabamento", "Tolerâncias a respeitar", "Outros"] },
        { nameof(QuestionResponses), QuestionResponses.All, ["Ok", "Nok", "n/a"] },
        { nameof(QuestionResponses.FirstTestTries), QuestionResponses.FirstTestTries, ["1", "2", "3"] },
        { nameof(EntityTypes), EntityTypes.All, ["Cliente", "Fornecedor", "Cliente e Fornecedor"] },
        { nameof(PrintTemplates), PrintTemplates.All, ["producao", "maquinacao"] },
    };

    [Theory]
    [MemberData(nameof(Lists))]
    public void Values_are_the_legacy_strings_in_the_legacy_order(string name, IReadOnlyList<string> actual, string[] legacy)
    {
        actual.Should().Equal(legacy, name);
        actual.Should().OnlyHaveUniqueItems().And.AllSatisfy(value => value.IsNormalized(NormalizationForm.FormC).Should().BeTrue());
    }

    [Fact]
    public void Defaults_are_the_mongoose_schema_defaults()
    {
        FieldsOfCompetence.Default.Should().Be("Desenvolvimento");
        QuestionResponses.DefaultFirstTestTry.Should().Be("1");
    }
}
