namespace Viernes.Mcp;

/// <summary>
/// Lo que contesta una herramienta: el texto, y si salió bien.
/// </summary>
/// <remarks>
/// La bandera existe para que del otro lado se note la diferencia entre «no hay misiones abiertas»
/// —una respuesta— y «no lo hice porque no tenés permiso» —un fallo—. Devolviendo sólo texto, MCP
/// marca todo como exitoso y el modelo tiene que adivinar leyendo la prosa, que es exactamente cómo
/// una herramienta que no hizo nada termina dándose por hecha.
/// <para>
/// Es un tipo propio y no <c>CallToolResult</c> del SDK para que <see cref="ViernesConnector"/>
/// pueda probarse sin MCP en el medio.
/// </para>
/// </remarks>
/// <param name="Ok">Si la herramienta hizo lo que le pidieron.</param>
/// <param name="Text">Lo que hay que contestar, ya escrito para leer.</param>
public readonly record struct ConnectorReply(bool Ok, string Text)
{
    /// <summary>Salió bien.</summary>
    public static ConnectorReply Fine(string text) => new(true, text);

    /// <summary>No se hizo, y acá está el motivo.</summary>
    public static ConnectorReply Nope(string text) => new(false, text);
}
