namespace Viernes.Core.Live;

/// <summary>
/// La dirección del websocket, con la clave adentro y sin que la clave salga de acá.
/// </summary>
/// <remarks>
/// Esta API no acepta la credencial en un encabezado: va en la cadena de consulta. Eso convierte a
/// la propia URL en un secreto, y una URL es exactamente el tipo de dato que uno mete en un mensaje
/// de error sin pensarlo dos veces —«no me pude conectar a …»— y que después queda escrito en un
/// archivo de registro para siempre.
/// <para>
/// Por eso la URL completa se arma acá y no se guarda: quien necesita <em>nombrar</em> el destino
/// usa <see cref="Redacted"/>, que es la misma dirección sin la clave. Verificado contra el SDK
/// oficial de .NET, que apunta a este mismo host y ruta.
/// </para>
/// </remarks>
public static class LiveEndpoint
{
    /// <summary>La dirección sin la credencial. Es lo único que puede escribirse en un mensaje.</summary>
    public const string Redacted =
        "wss://generativelanguage.googleapis.com/ws/" +
        "google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    /// <summary>
    /// Arma la dirección con la clave.
    /// </summary>
    /// <remarks>
    /// El resultado no se cachea ni se guarda en ningún campo: se usa para conectar y se olvida.
    /// </remarks>
    public static Uri Build(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("Hace falta la clave de Google para abrir la sesión en vivo.", nameof(apiKey));
        }

        return new Uri($"{Redacted}?key={Uri.EscapeDataString(apiKey.Trim())}", UriKind.Absolute);
    }
}
