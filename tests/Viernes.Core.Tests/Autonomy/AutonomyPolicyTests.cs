using Viernes.Core.Autonomy;
using Xunit;

namespace Viernes.Core.Tests.Autonomy;

/// <summary>
/// La compuerta de permisos: qué puede hacer sola, qué pregunta, y qué no hace nunca.
/// </summary>
/// <remarks>
/// No tenía ninguna prueba, y es el único lugar del proyecto donde equivocarse manda un mail que no
/// se puede volver a traer. Las dos primeras salen de un bug real: el desplegable de permisos decía
/// «anotado: esto no lo hace nunca» y no frenaba nada, porque la instancia que decide cacheaba las
/// reglas y no las releía jamás.
/// </remarks>
public sealed class AutonomyPolicyTests : IDisposable
{
    private readonly string path = Path.Combine(
        Path.GetTempPath(), $"autonomia-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task RevocarDesdeOtraInstancia_FrenaAlaQueYaEstabaAndando()
    {
        // La de la izquierda es la del runtime: viva desde que arranca la aplicación.
        var running = new AutonomyPolicy(this.path);
        await running.LearnAsync("borrar", "*", AutonomyLevel.Automatico, "dale nomás");
        Assert.Equal(AutonomyLevel.Automatico, await running.DecideAsync("borrar", "carpeta vieja"));

        // La de la derecha es la del desplegable: se arma para escribir y se descarta.
        var panel = new AutonomyPolicy(this.path);
        await panel.LearnAsync("borrar", "*", AutonomyLevel.Nunca, "no, nunca");

        // Acá está el bug que esto impide: sin releer, la del runtime seguía contestando Automático
        // hasta reiniciar, y el panel ya le había dicho al usuario que quedaba prohibido.
        Assert.Equal(AutonomyLevel.Nunca, await running.DecideAsync("borrar", "carpeta vieja"));
    }

    [Fact]
    public async Task LoQueAprendeUnaInstancia_NoBorraLoQueEscribioLaOtra()
    {
        var running = new AutonomyPolicy(this.path);
        await running.LearnAsync("enviar", "proveedores", AutonomyLevel.Automatico, "rutina");

        var panel = new AutonomyPolicy(this.path);
        await panel.LearnAsync("borrar", "*", AutonomyLevel.Nunca, "no, nunca");

        // La segunda mitad del mismo bug: la instancia vieja guardaba SU lista completa encima del
        // archivo y la prohibición desaparecía del disco sin que nada avisara.
        await running.LearnAsync("publicar", "*", AutonomyLevel.Preguntar, "preguntame");

        var reloaded = await new AutonomyPolicy(this.path).ListAsync();
        Assert.Contains(reloaded, rule => rule.Action == "borrar" && rule.Level == AutonomyLevel.Nunca);
        Assert.Contains(reloaded, rule => rule.Action == "enviar" && rule.Level == AutonomyLevel.Automatico);
        Assert.Contains(reloaded, rule => rule.Action == "publicar");
    }

    [Fact]
    public async Task UnNuncaLeGanaAUnPermisoMasEspecificoDadoAntes()
    {
        var policy = new AutonomyPolicy(this.path);
        await policy.LearnAsync("enviar", "juan@empresa.com", AutonomyLevel.Automatico, "es de confianza");
        await policy.LearnAsync("enviar", "*", AutonomyLevel.Nunca, "pará con los mails");

        // Se aprende a confiar de a poco y a desconfiar de golpe. Si la regla más específica ganara,
        // una prohibición general no serviría para nada: siempre habría un permiso viejo debajo.
        Assert.Equal(AutonomyLevel.Nunca, await policy.DecideAsync("enviar", "juan@empresa.com"));
    }

    [Fact]
    public async Task NombrarALaPersonaValeMasQueHablarDeLaAccionEnGeneral()
    {
        var policy = new AutonomyPolicy(this.path);
        await policy.LearnAsync("enviar", "*", AutonomyLevel.Preguntar, "por las dudas");
        await policy.LearnAsync("enviar", "juan@empresa.com", AutonomyLevel.Automatico, "con Juan sí");

        Assert.Equal(AutonomyLevel.Automatico, await policy.DecideAsync("enviar", "juan@empresa.com"));
        Assert.Equal(AutonomyLevel.Preguntar, await policy.DecideAsync("enviar", "cliente@otra.com"));
    }

    [Fact]
    public async Task SinReglas_LoQueSaleDelEquipoPregunta()
    {
        var policy = new AutonomyPolicy(this.path);

        Assert.Equal(AutonomyLevel.Preguntar, await policy.DecideAsync("enviar", "alguien@ejemplo.com"));
        Assert.Equal(AutonomyLevel.Preguntar, await policy.DecideAsync("borrar", "el informe"));
        Assert.Equal(AutonomyLevel.Preguntar, await policy.DecideAsync("pagar", "la factura"));
    }

    [Fact]
    public async Task SinReglas_LeerYBuscarNoPreguntanNunca()
    {
        var policy = new AutonomyPolicy(this.path);

        // Pedir permiso para leer un mail convierte al asistente en un trámite.
        Assert.Equal(AutonomyLevel.Automatico, await policy.DecideAsync("leer", "la bandeja"));
        Assert.Equal(AutonomyLevel.Automatico, await policy.DecideAsync("buscar", "facturas de marzo"));
    }

    [Fact]
    public async Task AprenderDosVecesSobreLoMismo_DejaUnaSolaRegla()
    {
        var policy = new AutonomyPolicy(this.path);
        await policy.LearnAsync("enviar", "juan@empresa.com", AutonomyLevel.Preguntar, "primero");
        await policy.LearnAsync("enviar", "juan@empresa.com", AutonomyLevel.Automatico, "después");

        var rules = await policy.ListAsync();
        var rule = Assert.Single(rules);
        Assert.Equal(AutonomyLevel.Automatico, rule.Level);
        Assert.Equal("después", rule.Because);
    }

    [Fact]
    public async Task UnArchivoIlegible_CaeDelLadoSeguro()
    {
        await File.WriteAllTextAsync(this.path, "{ esto no es json");

        // Sin reglas, todo lo que sale del equipo vuelve a preguntarse. Nunca al revés.
        Assert.Equal(AutonomyLevel.Preguntar, await new AutonomyPolicy(this.path).DecideAsync("borrar", "todo"));
    }

    public void Dispose()
    {
        if (File.Exists(this.path))
        {
            File.Delete(this.path);
        }
    }
}
