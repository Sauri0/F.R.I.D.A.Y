using Viernes.Core.Voice;
using Xunit;

namespace Viernes.Core.Tests.Voice;

public sealed class VoicePrivacySettingsTests
{
    [Fact]
    public void Defaults_AreMutedPushToTalkAndLocalOnly()
    {
        var settings = new VoicePrivacySettings();

        Assert.Equal(VoiceActivationMode.PushToTalk, settings.ActivationMode);
        Assert.True(settings.IsMicrophoneMuted);
        Assert.False(settings.AllowCloudTranscription);
    }
}
