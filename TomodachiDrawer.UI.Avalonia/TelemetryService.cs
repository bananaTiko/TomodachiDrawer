using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace TomodachiDrawer.UI.Avalonia
{
    /// <summary>TelemetryService handles reporting basic telemetry data to the developer. This is optional.</summary>
    internal class TelemetryService
    {
        public static bool TelemetryEnabled { get; set; } = false;

        private const string TELEMETRY_URL = "https://telemetry.l7y.media/";

        private HttpClient _http;

        public record StartupEventDto(
            string Os,
            string OsVersion,
            string Arch,
            string AppVersion
        );

        public record ImageEventDto(
            int ImageWidth,
            int ImageHeight,
            int ImageColourCount,
            string QuantizerMode,
            int? ColourLimit,
            string SwitchVersion,
            bool ExperimentalFeatures
        );


        public TelemetryService()
        {
            _http = new HttpClient
            {
                BaseAddress = new Uri(TELEMETRY_URL),
                Timeout = TimeSpan.FromSeconds(3)
            };
        }

        /// <summary>Sends startup data. Gets it all itself, just needs called.</summary>
        /// <returns>Sent or not</returns>
        public async Task<bool> ReportStart()
        {
            if (!TelemetryEnabled)
                return false;

            // startup contains basic system/app info.
            string os = OperatingSystem.IsLinux() ? "Linux" :
                OperatingSystem.IsWindows() ? "Windows" :
                OperatingSystem.IsMacOS() ? "macOS" : "Unknown";
            string osVersion = RuntimeInformation.OSDescription;
            string arch = RuntimeInformation.ProcessArchitecture.ToString();
            var currentVersion =
                Assembly
                    .GetEntryAssembly()
                    ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;
            var obj = new StartupEventDto(os, osVersion, arch, currentVersion ?? "unknown");

            try
            {
                await _http.PostAsJsonAsync("tomodachidrawer/startup", obj);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async Task<bool> ReportImage(ImageEventDto imageData)
        {
            if (!TelemetryEnabled)
                return false;

            try
            {
                await _http.PostAsJsonAsync("tomodachidrawer/image", imageData);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
