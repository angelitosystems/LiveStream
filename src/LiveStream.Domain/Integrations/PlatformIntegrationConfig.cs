using System;
using System.Collections.Generic;
using LiveStream.Domain.Entities;

namespace LiveStream.Domain.Integrations;

public enum IntegrationStatus
{
    Disconnected,
    Connecting,
    Connected,
    TokenExpired,
    Error,
    RateLimited,
}

public sealed record PlatformIntegrationConfig(
    StreamPlatform Platform,
    string AccountName = "",
    string AccountId = "",
    string CredentialName = "",
    bool Enabled = false,
    IntegrationStatus Status = IntegrationStatus.Disconnected,
    string? ErrorMessage = null,
    DateTime? LastConnected = null,
    IReadOnlyList<string>? Scopes = null,
    bool EnableChat = true,
    bool EnableAlerts = true,
    bool AutoReconnect = true,
    string AdditionalSettingsJson = "{}")
{
    public static PlatformIntegrationConfig CreateDefault(StreamPlatform platform) =>
        new(platform, CredentialName: $"integration-token-{platform.ToString().ToLowerInvariant()}");
}
