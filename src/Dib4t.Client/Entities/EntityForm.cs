using Dib4t.Client.Forms;
using Dib4t.Contracts.Entities;

namespace Dib4t.Client.Entities;

/// <summary>
/// The entity dialog (legacy <c>entities/entityModal.jsx</c>) and its client-side check, the legacy yup
/// <c>entitySchema</c> (<c>schemas/entity/entitySchema.js</c>: name, type and paymentCondition required), with
/// yup's messages. New: every field <c>""</c>. Edit: the entity's values; a value the entity does not have stays
/// <c>null</c> and is not sent (legacy Formik keeps it <c>undefined</c>), so the save does not add it.
/// </summary>
public sealed class EntityForm
{
    private EntityForm(string? id, string title)
    {
        Id = id;
        Title = title;
    }

    /// <summary>The entity being edited; <c>null</c> for a new one.</summary>
    public string? Id { get; }

    public bool IsNew => Id is null;

    /// <summary>Legacy dialog title: <c>Editar entidade: {name}</c> or <c>Nova entidade</c>.</summary>
    public string Title { get; }

    public string? Name { get; set; }

    public string? FiscalNumber { get; set; }

    public string? Address { get; set; }

    public string? ZipCode { get; set; }

    public string? Location { get; set; }

    public string? Type { get; set; }

    public string? PaymentCondition { get; set; }

    public static EntityForm New() => new(null, "Nova entidade")
    {
        Name = string.Empty,
        FiscalNumber = string.Empty,
        Address = string.Empty,
        ZipCode = string.Empty,
        Location = string.Empty,
        Type = string.Empty,
        PaymentCondition = string.Empty,
    };

    public static EntityForm Edit(EntityResponse entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new EntityForm(entity.Id, $"Editar entidade: {entity.Name}")
        {
            Name = entity.Name,
            FiscalNumber = entity.FiscalNumber,
            Address = entity.Address,
            ZipCode = entity.ZipCode,
            Location = entity.Location,
            Type = entity.Type,
            PaymentCondition = entity.PaymentCondition,
        };
    }

    /// <summary>yup <c>required()</c> on name, type and payment condition (an empty text is missing).</summary>
    public EntityFormErrors Validate() => new(
        string.IsNullOrEmpty(Name) ? Yup.Required("name") : null,
        string.IsNullOrEmpty(Type) ? Yup.Required("type") : null,
        string.IsNullOrEmpty(PaymentCondition) ? Yup.Required("paymentCondition") : null);

    /// <summary>The body of <c>entityService.newEntity</c> / <c>updateEntity</c>; the server's Joi check stays authoritative.</summary>
    public EntityRequest ToRequest() =>
        new(Name ?? string.Empty, FiscalNumber, Address, ZipCode, Location, PaymentCondition ?? string.Empty, Type ?? string.Empty);
}

/// <summary>Per-field messages shown under the inputs.</summary>
public sealed record EntityFormErrors(string? Name, string? Type, string? PaymentCondition)
{
    public static EntityFormErrors None { get; } = new(null, null, null);

    public bool Any => Name is not null || Type is not null || PaymentCondition is not null;
}
