using System;
using Vanguard.Client.UI.OffRaid.Localization;

// Responsibility: Presents and coordinates In Raid Event Localization Contract in the in-raid UI.
// Flow: Canonical API/runtime state is projected into view models and Unity/TMP controls; explicit user actions are delegated back through API/service boundaries.
// Authority boundary: Presentation layer only; it does not become persistence, economy, medical, or raid-runtime authority.
// Invariant: UI refreshes are idempotent from canonical state and temporary view state must not outlive its owning screen/session.
namespace Vanguard.Client.UI.InRaid.Localization;

/// <summary>
/// Client-local presentation contract for future Vanguard in-raid user-facing events.
/// Producers transport only a semantic event code plus ordered parameters. They never
/// transport a pre-localized sentence. The fixed Operator decision HUD is intentionally
/// outside this contract for now and keeps its validated French short-state labels.
/// </summary>
internal sealed class VanguardInRaidEventTextEnvelope
{
    public string EventCode { get; init; } = string.Empty;

    public string[] Arguments { get; init; } = Array.Empty<string>();
}

internal static class VanguardInRaidEventLocalizationContract
{
    public const int SchemaVersion = 1;
    public const string LocalizationKeyPrefix = "event.";

    /// <summary>
    /// Resolves a semantic event envelope to the current local EFT presentation language.
    /// Unknown or unregistered event codes fail closed rather than leaking technical keys or
    /// a producer-side language into the user interface.
    /// </summary>
    public static bool TryRender(VanguardInRaidEventTextEnvelope? envelope, out string text)
    {
        text = string.Empty;
        if (envelope == null || !TryBuildLocalizationKey(envelope.EventCode, out string localizationKey))
        {
            return false;
        }

        if (!VanguardOperatorsLocalizationService.HasCatalogKey(localizationKey))
        {
            return false;
        }

        object?[] arguments = new object?[envelope.Arguments?.Length ?? 0];
        for (int i = 0; i < arguments.Length; i++)
        {
            arguments[i] = envelope.Arguments![i] ?? string.Empty;
        }

        text = VanguardOperatorsLocalizationService.Format(localizationKey, arguments);
        return !string.IsNullOrWhiteSpace(text);
    }

    public static bool TryBuildLocalizationKey(string? eventCode, out string localizationKey)
    {
        localizationKey = string.Empty;
        if (string.IsNullOrWhiteSpace(eventCode))
        {
            return false;
        }

        string normalized = eventCode.Trim();
        if (!IsSemanticCode(normalized))
        {
            return false;
        }

        localizationKey = LocalizationKeyPrefix + normalized;
        return true;
    }

    private static bool IsSemanticCode(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if ((c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '.'
                || c == '_'
                || c == '-')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
