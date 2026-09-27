using DictationApp.Windows.Audio;

namespace DictationApp.Windows.Tests;

public class WarmMicrophoneTests
{
    [Theory]
    [InlineData(null, false, true)]
    [InlineData("chosen", true, true)] // the chosen microphone is not connected: the default stands in for it
    [InlineData("chosen", false, false)]
    public void A_default_or_stand_in_microphone_follows_a_change_of_the_Windows_default(string? requestedId, bool isFallback, bool follows) =>
        Assert.Equal(follows, WarmMicrophone.FollowsDefault(requestedId, isFallback));
}
