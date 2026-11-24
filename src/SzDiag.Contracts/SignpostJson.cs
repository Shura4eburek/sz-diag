using System.Text.Json;
using System.Text.Json.Serialization;

namespace SzDiag.Contracts;

/// <summary>Единые настройки JSON для подписываемых записей: camelCase, без лишних отступов.
/// Подпись идёт над байтами, поэтому формат обязан быть одним на подписывающей стороне.</summary>
public static class SignpostJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
