namespace Viernes.Memory.Privacy;

/// <summary>Contrato de privacidad visible para la UI y las revisiones de memoria.</summary>
/// <remarks>
/// <b>Esto es una promesa que se le muestra al usuario, así que cambia cuando cambia lo que hace el
/// programa — nunca después.</b> Decía que no se almacenaban conversaciones ni transcripciones, y
/// era cierto: las charlas vivían en una lista en memoria y se tiraban al cerrar. Desde que cada
/// charla queda escrita en <c>cerebro\charlas</c>, esa frase pasó a ser mentira, y una promesa de
/// privacidad que dejó de ser verdad es peor que no haber prometido nada.
/// </remarks>
public static class MemoryPrivacy
{
    /// <summary>Nada de esto sale de la máquina ni entrena a ningún modelo.</summary>
    public const bool IsUsedForModelTraining = false;

    /// <summary>
    /// Ahora sí: cada conversación queda escrita en un archivo de texto, en la máquina.
    /// </summary>
    public const bool StoresConversations = true;

    /// <summary>
    /// Sigue en falso, y ahora hay que sostenerlo activamente.
    /// </summary>
    /// <remarks>
    /// Antes era gratis: no se guardaba nada de lo hablado, así que no había dónde meter una clave.
    /// Guardando transcriptos, lo que sostiene esta línea es el tapado de
    /// <c>MemoryContentPolicy.Redact</c>, que corre sobre cada turno antes de tocar el disco.
    /// </remarks>
    public const bool StoresCredentials = false;

    public const string Notice =
        "Todo se guarda en tu computadora y nada se usa para entrenar modelos. " +
        "Las conversaciones quedan escritas en archivos de texto dentro de la carpeta «cerebro», " +
        "que podés leer, corregir o borrar cuando quieras. " +
        "Lo que parece una credencial se tapa antes de escribirse.";
}
