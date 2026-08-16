namespace Viernes.Memory.Privacy;

public enum MemoryContentRejectionReason
{
    Empty = 0,
    TooLong,
    ConversationLike,
    CredentialLike,
    ContainsControlCharacters
}
