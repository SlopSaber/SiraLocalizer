using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using IPA.Utilities;
using Newtonsoft.Json;
using SiraLocalizer.Features;
using SiraLocalizer.Records;
using SiraLocalizer.Utilities;
using SiraUtil.Logging;
using UnityEngine.Networking;

namespace SiraLocalizer.Providers.Crowdin
{
    internal class CrowdinDownloader : ILocalizationProvider, ILocalizationDownloader
    {
        private const string kCrowdinHost = "https://distributions.crowdin.net";
        private const string kDistributionKey = "b8d0ace786d64ba14775878o9lk";

        private static readonly string kDataFolder = Path.Combine(UnityGame.UserDataPath, "SiraLocalizer");
        private static readonly string kLocalizationsFolder = Path.Combine(kDataFolder, "Localizations", "Downloaded");
        private static readonly string kDownloadedFolder = Path.Combine(kLocalizationsFolder, "Content");
        private static readonly string kManifestFilePath = Path.Combine(kLocalizationsFolder, "manifest.json");

        private readonly SiraLog _logger;

        internal CrowdinDownloader(SiraLog logger)
        {
            _logger = logger;
        }

        public string name => "Crowdin";

        public async IAsyncEnumerable<LocalizationFile> GetLocalizationAssetsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!await HasLocalCacheAsync())
            {
                yield break;
            }

            CrowdinDistributionManifest manifest = await ReadLocalManifestAsync();

            if (manifest == null)
            {
                yield break;
            }

            foreach (string filePath in manifest.files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                LocalizationPreparation.CrowdinPath parsed = await ParsePathAsync(filePath);

                if (!LocalizationDefinition.IsDefinitionLoaded(parsed.id))
                {
                    _logger.Debug($"No localized plugin registered for '{parsed.id}'; ignored");
                    continue;
                }

                if (!await FileExistsAsync(parsed.pathOnDisk))
                {
                    _logger.Error($"File '{parsed.pathOnDisk}' not found");
                    continue;
                }

                string content = null;

                try
                {
                    content = await ReadFileAsync(parsed.pathOnDisk);
                }
                catch (IOException ex)
                {
                    _logger.Error($"Failed to read file '{parsed.pathOnDisk}'");
                    _logger.Error(ex);
                }

                if (content != null)
                {
                    yield return new LocalizationFile(content, 1000);
                }
            }
        }

        public async Task DownloadLocalizationsAsync(CancellationToken cancellationToken)
        {
            string manifestContent = await GetRemoteManifestContentAsync();

            if (manifestContent == null)
            {
                _logger.Error("Got empty manifest from Crowdin");
                return;
            }

            CrowdinDistributionManifest manifest = await DeserializeManifestAsync(manifestContent);

            if (manifest == null)
            {
                return;
            }

            if (!await CheckIfUpdateAvailableAsync(manifest))
            {
                _logger.Info("Translations are up-to-date");
                return;
            }

            LocalizationPreparation.Result reset = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.ResetDirectory, kDownloadedFolder);
            reset.ThrowIfFailed();

            foreach (string filePath in manifest.files)
            {
                LocalizationPreparation.CrowdinPath parsed = await ParsePathAsync(filePath);

                if (!LocalizationDefinition.IsDefinitionLoaded(parsed.id))
                {
                    _logger.Trace($"'{parsed.id}' does not belong to a loaded {nameof(LocalizedPlugin)}; ignored");
                    continue;
                }

                await DownloadFileAsync(parsed.relativePath, manifest.timestamp, parsed.pathOnDisk);
            }

            LocalizationPreparation.Result written = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.WriteText, kManifestFilePath, manifestContent);
            written.ThrowIfFailed();
        }

        public async Task<bool> CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            string manifestContent = await GetRemoteManifestContentAsync();

            if (manifestContent == null)
            {
                return false;
            }

            CrowdinDistributionManifest manifest = await DeserializeManifestAsync(manifestContent);

            return manifest != null && await CheckIfUpdateAvailableAsync(manifest);
        }

        private async Task<CrowdinDistributionManifest> DeserializeManifestAsync(string manifestContent)
        {
            try
            {
                if (JsonConvert.DefaultSettings != null)
                    return JsonConvert.DeserializeObject<CrowdinDistributionManifest>(manifestContent);
                LocalizationPreparation.Result prepared = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.ParseCrowdinManifest, manifestContent);
                prepared.ThrowIfFailed();
                return prepared.manifest;
            }
            catch (JsonException ex)
            {
                _logger.Error("Failed to deserialize manifest");
                _logger.Error(ex);

                return null;
            }
        }

        private async Task<string> GetRemoteManifestContentAsync()
        {
            string url = $"{kCrowdinHost}/{kDistributionKey}/manifest.json";

            _logger.Info($"Fetching Crowdin manifest");

            using var request = UnityWebRequest.Get(url);
            await request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.ProtocolError)
            {
                _logger.Error($"'{url}' responded with {request.responseCode} ({request.error})");
                return null;
            }
            else if (request.result != UnityWebRequest.Result.Success)
            {
                _logger.Error($"Request to '{url}' failed: {request.result}");
                return null;
            }

            return request.downloadHandler.text;
        }

        private async Task<bool> CheckIfUpdateAvailableAsync(CrowdinDistributionManifest remoteManifest)
        {
            if (!await HasLocalCacheAsync()) return true;

            foreach (string filePath in remoteManifest.files)
            {
                LocalizationPreparation.CrowdinPath parsed = await ParsePathAsync(filePath);

                if (LocalizationDefinition.IsDefinitionLoaded(parsed.id) && !await FileExistsAsync(parsed.pathOnDisk))
                {
                    return true;
                }
            }

            CrowdinDistributionManifest localManifest = await ReadLocalManifestAsync();

            return localManifest?.timestamp != remoteManifest.timestamp;
        }

        private static async Task<bool> HasLocalCacheAsync()
        {
            LocalizationPreparation.Result result = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.CheckCrowdinCache, kManifestFilePath, kDownloadedFolder);
            result.ThrowIfFailed();
            return result.exists;
        }

        private static async Task<bool> FileExistsAsync(string path)
        {
            LocalizationPreparation.Result result = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.CheckFile, path);
            result.ThrowIfFailed();
            return result.exists;
        }

        private static async Task<LocalizationPreparation.CrowdinPath> ParsePathAsync(string filePath)
        {
            LocalizationPreparation.Result result = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.ParseCrowdinPath, filePath, kDownloadedFolder);
            result.ThrowIfFailed();
            return result.crowdinPath;
        }

        private static async Task<string> ReadFileAsync(string path)
        {
            LocalizationPreparation.Result result = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.ReadFile, path);
            result.ThrowIfFailed();
            return result.text;
        }

        private async Task<CrowdinDistributionManifest> ReadLocalManifestAsync()
        {
            try
            {
                LocalizationPreparation.Result read = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.ReadHeldFile, kManifestFilePath);
                try
                {
                    read.ThrowIfFailed();
                    return await DeserializeManifestAsync(read.text);
                }
                finally
                {
                    if (read.fileLease != 0)
                    {
                        // Custom JSON callbacks run while the original file lease remains open.
                        LocalizationPreparation.Result released = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.ReleaseHeldFile, null, fileLease: read.fileLease);
                        released.ThrowIfFailed();
                    }
                }
            }
            catch (IOException ex)
            {
                _logger.Error("Failed to read local manifest");
                _logger.Error(ex);

                return null;
            }
        }

        private async Task DownloadFileAsync(string relativePath, long timestamp, string filePath)
        {
            _logger.Info($"Downloading '{relativePath}'");

            string url = $"{kCrowdinHost}/{kDistributionKey}/content/{relativePath}?timestamp={timestamp}";
            using var request = UnityWebRequest.Get(url);
            await request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.ProtocolError)
            {
                _logger.Error($"'{url}' responded with {request.responseCode} ({request.error})");
                return;
            }
            else if (request.result != UnityWebRequest.Result.Success)
            {
                _logger.Error($"Request to '{url}' failed: {request.result}");
                return;
            }

            LocalizationPreparation.Result directory = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.CreateFileDirectory, filePath);
            directory.ThrowIfFailed();

            byte[] data = request.downloadHandler.data;

            LocalizationPreparation.Result written = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.WriteCrowdinFile, filePath, bytes: data);
            written.ThrowIfFailed();
        }
    }
}
