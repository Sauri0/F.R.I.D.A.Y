namespace Viernes.Memory.Privacy;

/// <summary>Contrato de privacidad visible para la UI y las revisiones de memoria.</summary>
public static class MemoryPrivacy
{
    public const bool IsUsedForModelTraining = false;
    public const bool StoresConversations = false;
    public const bool StoresCredentials = false;

    public const string Notice =
        "La memoria se guarda localmente para personalizar Viernes. " +
        "No almacena conversaciones, transcripciones ni credenciales, y no se usa para entrenar modelos.";
}
