namespace Dib4t.Application.Entities;

/// <summary>The legacy texts of <c>routes/entity.js</c>, verbatim (all answered with status 400).</summary>
public static class EntityMessages
{
    /// <summary><c>POST /</c>: an entity with exactly this name exists.</summary>
    public const string NameTaken = "Nome de entidade já registado";

    /// <summary><c>PUT /:id</c>: no entity with this id.</summary>
    public const string Missing = "Entidade não existe na bd";

    /// <summary><c>GET /:id</c>: no entity with this id.</summary>
    public const string NotFound = "Entidade não encontrada";
}
