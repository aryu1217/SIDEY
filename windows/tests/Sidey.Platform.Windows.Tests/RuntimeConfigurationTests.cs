using System.Reflection;
using Sidey.Infrastructure;

namespace Sidey.Platform.Windows.Tests;

public sealed class RuntimeConfigurationTests
{
    [Theory]
    [InlineData(null, false, RealtimeTransportMode.FirebaseV2, RealtimeTransportMode.LegacySupabase)]
    [InlineData(null, true, RealtimeTransportMode.FirebaseV2, RealtimeTransportMode.FirebaseV2)]
    [InlineData("automatic", true, RealtimeTransportMode.FirebaseV2, RealtimeTransportMode.FirebaseV2)]
    [InlineData("legacy", true, RealtimeTransportMode.LegacySupabase, RealtimeTransportMode.LegacySupabase)]
    [InlineData("firebase-v2", false, RealtimeTransportMode.FirebaseV2, RealtimeTransportMode.LegacySupabase)]
    [InlineData("firebase-v2", true, RealtimeTransportMode.FirebaseV2, RealtimeTransportMode.FirebaseV2)]
    public void RealtimeTransportRequiresExplicitSelectionAndReadiness(
        string? value,
        bool firebaseV2Ready,
        RealtimeTransportMode requested,
        RealtimeTransportMode effective)
    {
        RealtimeTransportSelection selection = RealtimeTransportConfiguration.Parse(value, firebaseV2Ready);

        Assert.Equal(requested, selection.Requested);
        Assert.Equal(effective, selection.Effective);
    }

    [Fact]
    public void InvalidRealtimeTransportSelectionFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RealtimeTransportConfiguration.Parse("firebase", firebaseV2Ready: true));
    }

    [Theory]
    [InlineData("https://example.supabase.co", true)]
    [InlineData("http://localhost:54321", true)]
    [InlineData("http://127.0.0.1:54321", true)]
    [InlineData("http://example.supabase.co", false)]
    public void BackendOverrideAllowsOnlyHttpsOrLoopbackHttp(string value, bool expected)
    {
        Assert.Equal(expected, SupabaseRuntimeConfiguration.IsAllowedBackend(new Uri(value)));
    }

    [Theory]
    [InlineData("sb_publishable_public", false)]
    [InlineData("sb_secret_do-not-ship", true)]
    [InlineData("service_role_do-not-ship", true)]
    [InlineData("eyJhbGciOiJub25lIn0.eyJyb2xlIjoic2VydmljZV9yb2xlIn0.signature", true)]
    public void SecretAndServiceRoleKeysAreRejected(string value, bool expected)
    {
        Assert.Equal(expected, SupabaseRuntimeConfiguration.LooksLikeSecretKey(value));
    }

    [Fact]
    public void ProductionFallbackMatchesTheMacClientBackend()
    {
        Assert.Equal("whtejsviizgejauasqqt.supabase.co", SupabaseRuntimeConfiguration.ProductionHost);
        Assert.StartsWith("sb_publishable_", SupabaseRuntimeConfiguration.ProductionPublishableKey);
    }

    [Theory]
    [InlineData("https://fjglrvhvdthntkvrduyi.supabase.co", "1", true, true)]
    [InlineData("http://localhost:54321", "1", true, true)]
    [InlineData("https://whtejsviizgejauasqqt.supabase.co", null, false, true)]
    [InlineData("https://attacker.supabase.co", "1", true, false)]
    [InlineData("https://whtejsviizgejauasqqt.supabase.co:8443", "1", true, false)]
    [InlineData("https://user@whtejsviizgejauasqqt.supabase.co", "1", true, false)]
    [InlineData("https://whtejsviizgejauasqqt.supabase.co/path", "1", true, false)]
    [InlineData("https://fjglrvhvdthntkvrduyi.supabase.co", "true", true, false)]
    [InlineData("https://fjglrvhvdthntkvrduyi.supabase.co", "1", false, false)]
    [InlineData("http://staging.example.com", "1", true, false)]
    public void CommerceAllowsProductionAndExplicitKnownDevelopmentOrigins(
        string backendUrl,
        string? optIn,
        bool compiledSupport,
        bool expected)
    {
        var configuration = new SupabaseRuntimeConfiguration(
            new Uri(backendUrl),
            "sb_publishable_test");

        Assert.Equal(
            expected,
            WindowsCommerceConfiguration.IsEnabled(configuration, optIn, compiledSupport));
    }

    [Fact]
    public void CommerceNetworkSurfaceIsAvailableInRelease()
    {
        MethodInfo? commerceStateMethod = typeof(SupabaseBackendGateway).GetMethod(
            "GetWindowsCommerceStateAsync");
        MethodInfo? identityLinkMethod = typeof(SupabaseAnonymousAuthService).GetMethod(
            "BeginGoogleIdentityLinkAsync");

        Assert.NotNull(commerceStateMethod);
        Assert.NotNull(identityLinkMethod);
    }
}
