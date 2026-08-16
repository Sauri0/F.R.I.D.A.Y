namespace Viernes.Core.Voice;

/// <summary>Non-secret voice preferences suitable for local persistence by the Windows host.</summary>
public sealed record VoicePrivacySettings(
    VoiceActivationMode ActivationMode = VoiceActivationMode.PushToTalk,
    bool IsMicrophoneMuted = true,
    bool AllowCloudTranscription = false);
