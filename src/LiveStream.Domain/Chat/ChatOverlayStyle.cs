using LiveStream.Domain.Compositions;

namespace LiveStream.Domain.Chat;

public enum ChatTheme
{
    DarkCard,
    Minimal,
    NeonGamer,
    RetroBroadcast,
    CleanModern,
    TransparentSubtitle,
}

public enum ChatAnimation
{
    Fade,
    Slide,
    Pop,
    None,
}

public sealed record ChatOverlayStyle(
    ChatTheme Theme = ChatTheme.DarkCard,
    string FontFamily = "Segoe UI",
    int FontSize = 16,
    string TextColor = "#FFFFFF",
    string AccentColor = "#3B82F6",
    string BackgroundColor = "#1F2937",
    double Opacity = 0.85,
    double CornerRadius = 8.0,
    ChatAnimation AnimationIn = ChatAnimation.Slide,
    ChatAnimation AnimationOut = ChatAnimation.Fade,
    int MessageDurationSec = 8,
    int MaxVisibleMessages = 6,
    bool ShowPlatformBadge = true,
    bool ShowAvatars = true,
    bool ShowBadges = true,
    bool OnlyShowModerated = false,
    CompositionFormat TargetComposition = CompositionFormat.Both);
