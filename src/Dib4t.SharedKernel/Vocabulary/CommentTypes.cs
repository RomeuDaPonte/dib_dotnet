namespace Dib4t.SharedKernel.Vocabulary;

/// <summary>Job comment types (legacy <c>helpers/commentTypes.js</c>), stored in <c>jobs.commentType[]</c>.</summary>
public static class CommentTypes
{
    public const string QuantityOfPieces = "Quantidade de peças";
    public const string MaterialType = "Tipo de material";
    public const string ManufacturingDetails = "Detalhes de fabrico";
    public const string FinishType = "Zonas a considerar tipo de acabamento";
    public const string Tolerance = "Tolerâncias a respeitar";
    public const string Other = "Outros";

    /// <summary>In the legacy order (<c>getAllCommentTypes</c>).</summary>
    public static IReadOnlyList<string> All { get; } =
        [QuantityOfPieces, MaterialType, ManufacturingDetails, FinishType, Tolerance, Other];
}
