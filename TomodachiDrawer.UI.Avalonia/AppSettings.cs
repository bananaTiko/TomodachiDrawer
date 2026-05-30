using System.Text.Json.Serialization;
using TomodachiDrawer.Core.Models;

namespace TomodachiDrawer.UI.Avalonia;

internal class AppSettings
{
    public SwitchVersion SelectedSwitchVersion { get; set; } = SwitchVersion.None;

    public int SelectedThemeIndex { get; set; } = 0;

    public bool EnableExperimentalFeatures { get; set; } = false;

    public bool CheckForUpdatesOnStart { get; set; } = true;

    public string SelectedColourMatcher { get; set; } = "Arbitrary";

    public int ColourLimit { get; set; } = 16;

    public string SelectedDenoiser { get; set; } = "None";

    public int FirstStartId { get; set; } = 0;

    /// <summary>This is null by default to indicate they havent been asked yet.</summary>
    public bool? EnableTelemetry { get; set; } = null;
}

// Source gen serialization to avoid trimming warnings.
[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class AppSettingsContext : JsonSerializerContext { }
