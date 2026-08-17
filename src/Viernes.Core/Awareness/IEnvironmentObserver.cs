namespace Viernes.Core.Awareness;

/// <summary>
/// Sabe qué está pasando en el equipo ahora mismo: qué tenés adelante y qué venías haciendo.
/// </summary>
/// <remarks>
/// Es lo que le da referente a las palabras que más se usan hablando y que hoy no significaban nada:
/// «esto anda lento», «cerrá eso», «seguí con lo de antes». Sin un modelo de la situación, cada turno
/// arranca de cero y el asistente tiene que preguntar a qué te referís —que es exactamente lo que
/// vuelve tedioso hablarle.
/// <para>
/// La implementación vive en la plataforma; el núcleo sólo consume un resumen ya destilado. Se
/// devuelve texto y no una estructura a propósito: lo que llega al modelo tiene que ser corto y
/// legible, no un volcado de estado que se cobra en tokens en cada turno.
/// </para>
/// </remarks>
public interface IEnvironmentObserver
{
    /// <summary>
    /// Resumen breve para inyectar en cada turno: qué ventana está adelante y hace cuánto que no
    /// tocás nada. Devuelve <c>null</c> si no hay nada que valga la pena contar.
    /// </summary>
    string? DescribeNow();

    /// <summary>
    /// Relato de dónde estuviste trabajando, para cuando preguntás por lo anterior. Es más caro que
    /// <see cref="DescribeNow"/> y por eso se pide sólo cuando hace falta.
    /// </summary>
    string DescribeRecentActivity();

    /// <summary>Métricas del equipo. Se consulta cuando preguntás por rendimiento, no siempre.</summary>
    string DescribeSystemLoad();
}
