using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IPA.Utilities;
using Newtonsoft.Json;
using SiraLocalizer.Providers.CrowdinApi.Models;
using SiraLocalizer.Records;
using SiraLocalizer.Utilities;
using SiraLocalizer.Utilities.WebRequests;
using SiraUtil.Logging;
using UnityEngine.Networking;

namespace SiraLocalizer.Providers.CrowdinApi
{
    internal class CrowdinApiDownloader : ILocalizationProvider, ILocalizationDownloader
    {
        private const long kProjectId = 436238;
        private const string kBaseApiUrl = "https://api.crowdin.com/api/v2";

        private static readonly string kDataFolder = Path.Combine(UnityGame.UserDataPath, "SiraLocalizer");
        private static readonly string kLocalizationsFolder = Path.Combine(kDataFolder, "Localizations", "Build");
        private static readonly string kDownloadedFolder = Path.Combine(kLocalizationsFolder, "Content");
        private static readonly string kBuildIdPath = Path.Combine(kLocalizationsFolder, "buildid");

        private readonly SiraLog _logger;
        private readonly Settings _settings;
        private readonly UnityWebRequestHelper _webRequestHelper;

        internal CrowdinApiDownloader(SiraLog logger, Settings settings, UnityWebRequestHelper webRequestHelper)
        {
            _logger = logger;
            _settings = settings;
            _webRequestHelper = webRequestHelper;
        }

        public string name => "Crowdin API";

        public Task<bool> CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            // there doesn't seem to be a way to check if the latest build is up-to-date, so assume new content is available
            return Task.FromResult(true);
        }

        public async Task DownloadLocalizationsAsync(CancellationToken cancellationToken)
        {
            AbstractProjectBuildResponse buildResponse;

            try
            {
                buildResponse = await CreateOrGetLatestBuild();
            }
            catch (WebRequestException ex)
            {
                // too many requests
                if (ex.ResponseCode != 429)
                {
                    throw;
                }

                _logger.Warn("Got 429 Too Many Requests when trying to create new build; fetching latest build instead");
                buildResponse = await GetLatestBuild();
            }

            if (buildResponse == null)
            {
                _logger.Error("Could not fetch latest build from Crowdin");
                return;
            }

            LocalizationPreparation.Result local = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.ReadApiBuildId, kBuildIdPath);
            local.ThrowIfFailed();
            long? localBuildId = local.buildId;

            if (buildResponse.id == localBuildId)
            {
                _logger.Info("Latest build is already downloaded");
                return;
            }

            DownloadLinkResponse resp = await WaitForBuildToFinishAsync(buildResponse.id);

            await DownloadAndExtractBuild(resp.url, cancellationToken);

            LocalizationPreparation.Result written = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.WriteApiBuildId, kBuildIdPath, buildResponse.id.ToString());
            written.ThrowIfFailed();
        }

        public async IAsyncEnumerable<LocalizationFile> GetLocalizationAssetsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LocalizationPreparation.Result catalog = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.OpenApiCatalog, kDownloadedFolder);
            catalog.ThrowIfFailed();
            if (catalog.fileLease == 0)
            {
                yield break;
            }

            try
            {
                while (true)
                {
                    LocalizationPreparation.Result next = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.NextApiFile, null, fileLease: catalog.fileLease);
                    next.ThrowIfFailed();
                    if (!next.exists) break;
                    string filePath = next.text;
                    cancellationToken.ThrowIfCancellationRequested();

                    LocalizationPreparation.Result parsed = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.PrepareApiFileId, filePath, kDownloadedFolder);
                    parsed.ThrowIfFailed();
                    string id = parsed.text;

                    if (!LocalizationDefinition.IsDefinitionLoaded(id))
                    {
                        _logger.Debug($"No localized plugin registered for '{id}'; ignored");
                        continue;
                    }

                    string content = null;

                    try
                    {
                        LocalizationPreparation.Result read = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.ReadFile, filePath);
                        read.ThrowIfFailed();
                        content = read.text;
                    }
                    catch (IOException ex)
                    {
                        _logger.Error($"Failed to read file '{filePath}'\n{ex}");
                    }

                    if (content != null)
                    {
                        yield return new LocalizationFile(content, 1000);
                    }
                }
            }
            finally
            {
                LocalizationPreparation.Result released = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.ReleaseApiCatalog, null, fileLease: catalog.fileLease);
                released.ThrowIfFailed();
            }
        }

        private async Task<AbstractProjectBuildResponse> CreateOrGetLatestBuild()
        {
            using UnityWebRequest webRequest = CreateApiRequest($"/projects/{kProjectId}/translations/builds", UnityWebRequest.kHttpVerbPOST);
            await _webRequestHelper.SendRequest(webRequest);

            if (webRequest.responseCode != 201)
            {
                throw new CrowdinApiException($"Unexpected response code {webRequest.responseCode}");
            }

            return await DeserializeResponseAsync<AbstractProjectBuildResponse>(webRequest.downloadHandler.data);
        }

        private async Task<AbstractProjectBuildResponse> GetLatestBuild()
        {
            Dictionary<string, string> queryParameters = new()
            {
                { "limit", "1" },
            };

            using UnityWebRequest webRequest = CreateApiRequest($"/projects/{kProjectId}/translations/builds", queryParameters: queryParameters);
            await _webRequestHelper.SendRequest(webRequest);

            if (webRequest.responseCode != 200)
            {
                throw new CrowdinApiException($"Unexpected response code {webRequest.responseCode}");
            }

            return (await DeserializeBuildsAsync(webRequest.downloadHandler.data)).FirstOrDefault();
        }

        private async Task<DownloadLinkResponse> WaitForBuildToFinishAsync(long buildId)
        {
            while (true)
            {
                using UnityWebRequest webRequest = CreateApiRequest($"/projects/{kProjectId}/translations/builds/{buildId}/download");
                await _webRequestHelper.SendRequest(webRequest);

                switch (webRequest.responseCode)
                {
                    case 200:
                        return await DeserializeResponseAsync<DownloadLinkResponse>(webRequest.downloadHandler.data);

                    case 202:
                        AbstractProjectBuildResponse buildResponse = await DeserializeResponseAsync<AbstractProjectBuildResponse>(webRequest.downloadHandler.data);

                        if (buildResponse.status != ProjectBuildStatus.InProgress)
                        {
                            throw new BuildFailedException(buildResponse.status);
                        }

                        _logger.Info("Waiting for build to complete");
                        await Task.Delay(1000);

                        break;

                    default:
                        throw new CrowdinApiException($"Unexpected response code {webRequest.responseCode}");
                }
            }
        }

        private async Task DownloadAndExtractBuild(string url, CancellationToken cancellationToken)
        {
            using var webRequest = UnityWebRequest.Get(url);
            await _webRequestHelper.SendRequest(webRequest);

            byte[] data = webRequest.downloadHandler.data;
            string downloadedFolder = kDownloadedFolder;
            await Task.Run(() =>
            {
                if (Directory.Exists(downloadedFolder))
                {
                    Directory.Delete(downloadedFolder, true);
                }

                Directory.CreateDirectory(downloadedFolder);
                using var memoryStream = new MemoryStream(data);
                using var archive = new ZipArchive(memoryStream);
                archive.ExtractToDirectory(downloadedFolder);
            }, cancellationToken);
        }

        private UnityWebRequest CreateApiRequest(string path, string method = "GET", object body = null, Dictionary<string, string> queryParameters = null)
        {
            if (queryParameters?.Count > 0)
            {
                path += "?" + string.Join("&", queryParameters.Select(kvp => $"{WebUtility.UrlEncode(kvp.Key)}={WebUtility.UrlEncode(kvp.Value)}"));
            }

            var webRequest = new UnityWebRequest($"{kBaseApiUrl}{path}", method, new DownloadHandlerBuffer(), null);
            webRequest.SetRequestHeader("Authorization", $"Bearer {_settings.crowdinAccessToken}");
            webRequest.SetRequestHeader("Content-Type", "application/json");

            if (body != null)
            {
                webRequest.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body)));
            }

            return webRequest;
        }

        private T DeserializeResponse<T>(byte[] data)
        {
            string str = Encoding.UTF8.GetString(data);
            return JsonConvert.DeserializeObject<DataResponse<T>>(str).data;
        }

        private async Task<T> DeserializeResponseAsync<T>(byte[] data)
        {
            if (JsonConvert.DefaultSettings != null)
                return DeserializeResponse<T>(data);
            LocalizationPreparation.Operation operation;
            if (typeof(T) == typeof(AbstractProjectBuildResponse))
                operation = LocalizationPreparation.Operation.ParseApiBuild;
            else if (typeof(T) == typeof(DownloadLinkResponse))
                operation = LocalizationPreparation.Operation.ParseApiDownload;
            else
                return DeserializeResponse<T>(data);
            LocalizationPreparation.Result parsed = await LocalizationPreparation.Prepare(operation, null, bytes: data);
            parsed.ThrowIfFailed();
            return operation == LocalizationPreparation.Operation.ParseApiBuild ? (T)(object)parsed.buildResponse : (T)(object)parsed.downloadLink;
        }

        private async Task<AbstractProjectBuildResponse[]> DeserializeBuildsAsync(byte[] data)
        {
            if (JsonConvert.DefaultSettings != null)
                return DeserializePaginatedResponse<AbstractProjectBuildResponse>(data);
            LocalizationPreparation.Result parsed = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.ParseApiBuilds, null, bytes: data);
            parsed.ThrowIfFailed();
            return parsed.builds;
        }

        private T[] DeserializePaginatedResponse<T>(byte[] data)
        {
            string str = Encoding.UTF8.GetString(data);
            IList<DataResponse<T>> items = JsonConvert.DeserializeObject<PaginatedDataResponse<T>>(str).data;
            var values = new T[items.Count];

            for (int i = 0; i < items.Count; i++)
            {
                values[i] = items[i].data;
            }

            return values;
        }
    }
}
