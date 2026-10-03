using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BGLib.Polyglot;
using Newtonsoft.Json;
using SiraLocalizer.Providers.Crowdin;
using SiraLocalizer.Providers.CrowdinApi.Models;

namespace SiraLocalizer.Utilities
{
    internal static class LocalizationPreparation
    {
        internal enum Operation
        {
            ReadUserCatalog,
            ReadFile,
            ReadResource,
            PrepareCsv,
            CheckCrowdinCache,
            CheckFile,
            ParseCrowdinPath,
            ParseCrowdinManifest,
            ResetDirectory,
            CreateFileDirectory,
            WriteCrowdinFile,
            WriteText,
            ReadHeldFile,
            ReleaseHeldFile,
            ReadApiBuildId,
            WriteApiBuildId,
            OpenApiCatalog,
            NextApiFile,
            ReleaseApiCatalog,
            PrepareApiFileId,
            ParseApiBuild,
            ParseApiDownload,
            ParseApiBuilds,
            ExportBaseGameCsv,
            ReadFeatureKeys,
            CountTranslationRows,
            CountSupportedLanguages,
            GroupTranslationStatuses,
        }

        internal readonly struct TranslationRow
        {
            internal readonly string key;
            internal readonly string english;
            internal readonly string translation;

            internal TranslationRow(string key, string english, string translation)
            {
                this.key = key;
                this.english = english;
                this.translation = translation;
            }
        }

        internal readonly struct StatusRow
        {
            internal readonly string name;
            internal readonly float percentage;
            internal readonly float clampedPercentage;

            internal StatusRow(string name, float percentage, float clampedPercentage)
            {
                this.name = name;
                this.percentage = percentage;
                this.clampedPercentage = clampedPercentage;
            }
        }

        private sealed class LanguageInput
        {
            internal readonly string[] names;
            internal readonly string[][] rows;
            internal readonly int dictionaryCount;
            internal readonly float threshold;

            internal LanguageInput(string[] names, string[][] rows, int dictionaryCount, float threshold)
            {
                this.names = names;
                this.rows = rows;
                this.dictionaryCount = dictionaryCount;
                this.threshold = threshold;
            }
        }

        private sealed class SummaryInput
        {
            internal readonly StatusRow[] rows;
            internal readonly NumberFormatInfo format;

            internal SummaryInput(StatusRow[] rows, NumberFormatInfo format)
            {
                this.rows = rows;
                this.format = format;
            }
        }

        internal readonly struct ExportAsset
        {
            internal readonly string name;
            internal readonly string text;
            internal readonly bool hasName;
            internal readonly bool hasText;

            internal ExportAsset(string name, string text, bool hasName, bool hasText)
            {
                this.name = name;
                this.text = text;
                this.hasName = hasName;
                this.hasText = hasText;
            }
        }

        internal readonly struct ExportMessage
        {
            internal readonly string text;
            internal readonly bool warning;

            internal ExportMessage(string text, bool warning = false)
            {
                this.text = text;
                this.warning = warning;
            }
        }

        private sealed class ExportInput
        {
            internal readonly int languageCount;
            internal readonly ExportAsset[] assets;

            internal ExportInput(int languageCount, ExportAsset[] assets)
            {
                this.languageCount = languageCount;
                this.assets = assets;
            }
        }

        internal readonly struct CrowdinPath
        {
            internal readonly string id;
            internal readonly string pathOnDisk;
            internal readonly string relativePath;

            internal CrowdinPath(string id, string pathOnDisk, string relativePath)
            {
                this.id = id;
                this.pathOnDisk = pathOnDisk;
                this.relativePath = relativePath;
            }
        }

        internal sealed class PreparedRow
        {
            internal readonly string key;
            internal readonly string[] values;

            internal PreparedRow(string key, string[] values)
            {
                this.key = key;
                this.values = values;
            }
        }

        internal sealed class Result
        {
            internal string text;
            internal string[] paths = Array.Empty<string>();
            internal PreparedRow[] rows = Array.Empty<PreparedRow>();
            internal Exception error;
            internal bool exists;
            internal CrowdinPath crowdinPath;
            internal CrowdinDistributionManifest manifest;
            internal long fileLease;
            internal long? buildId;
            internal AbstractProjectBuildResponse buildResponse;
            internal DownloadLinkResponse downloadLink;
            internal AbstractProjectBuildResponse[] builds;
            internal ExportMessage[] exportMessages = Array.Empty<ExportMessage>();
            internal string[] keys;
            internal int totalWords;
            internal int translatedWords;
            internal Locale[] supportedLanguages = Array.Empty<Locale>();
            internal string fullyTranslated;
            internal string partiallyTranslated;
            internal string notSupported;

            internal void ThrowIfFailed()
            {
                if (error != null)
                    ExceptionDispatchInfo.Capture(error).Throw();
            }
        }

        private sealed class Request
        {
            internal readonly Operation operation;
            internal readonly string value;
            internal readonly string secondaryValue;
            internal readonly byte[] bytes;
            internal readonly long fileLease;
            internal readonly TaskCompletionSource<Result> completion;
            internal readonly ExportInput exportInput;
            internal readonly Stream resourceStream;
            internal readonly TranslationRow[] translationRows;
            internal readonly LanguageInput languageInput;
            internal readonly SummaryInput summaryInput;

            internal Request(Operation operation, string value, string secondaryValue, byte[] bytes, long fileLease, ExportInput exportInput = null, Stream resourceStream = null, TranslationRow[] translationRows = null, LanguageInput languageInput = null, SummaryInput summaryInput = null)
            {
                this.operation = operation;
                this.value = value;
                this.secondaryValue = secondaryValue;
                this.bytes = bytes;
                this.fileLease = fileLease;
                this.exportInput = exportInput;
                this.resourceStream = resourceStream;
                this.translationRows = translationRows;
                this.languageInput = languageInput;
                this.summaryInput = summaryInput;
                completion = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private static readonly object kGate = new();
        private static readonly Queue<Request> kRequests = new();
        private static readonly Regex kCrowdinPathRegex = new(@"^\/[A-Za-z\-_]+(?:\/[A-Za-z\-_]+)*\.csv$");
        private static readonly Dictionary<long, StreamReader> kFileReaders = new();
        private static readonly Dictionary<long, IEnumerator<string>> kApiCatalogs = new();
        private static long _nextFileLease;
        private static Task _worker;

        internal static Task<Result> Prepare(Operation operation, string value, string secondaryValue = null, byte[] bytes = null, long fileLease = 0)
        {
            var request = new Request(operation, value, secondaryValue, bytes, fileLease);
            return Queue(request);
        }

        internal static Task<Result> PrepareExport(string path, int languageCount, ExportAsset[] assets)
        {
            return Queue(new Request(Operation.ExportBaseGameCsv, path, null, null, 0, new ExportInput(languageCount, assets)));
        }

        internal static Task<Result> PrepareFeatureKeys(Stream ownedStream)
        {
            return Queue(new Request(Operation.ReadFeatureKeys, null, null, null, 0, resourceStream: ownedStream));
        }

        internal static Task<Result> PrepareTranslationCounts(TranslationRow[] ownedRows)
        {
            return Queue(new Request(Operation.CountTranslationRows, null, null, null, 0, translationRows: ownedRows));
        }

        internal static Task<Result> PrepareSupportedLanguages(string[] ownedNames, string[][] ownedRows, int dictionaryCount, float threshold)
        {
            return Queue(new Request(Operation.CountSupportedLanguages, null, null, null, 0,
                languageInput: new LanguageInput(ownedNames, ownedRows, dictionaryCount, threshold)));
        }

        internal static Task<Result> PrepareStatusGroups(StatusRow[] ownedRows, NumberFormatInfo ownedFormat)
        {
            return Queue(new Request(Operation.GroupTranslationStatuses, null, null, null, 0,
                summaryInput: new SummaryInput(ownedRows, ownedFormat)));
        }

        internal static Result Complete(Task<Result> task)
        {
            if (!task.IsCompleted)
            {
                using var completed = new ManualResetEventSlim();
                task.ContinueWith(static (_, state) => ((ManualResetEventSlim)state).Set(), completed,
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                completed.Wait();
            }
            return task.GetAwaiter().GetResult();
        }

        private static readonly char[] kWhiteSpaceCharacters = [' ', '\n', '\r', '\t', '\x00A0', '\x1680', '\x2000', '\x2001', '\x2002', '\x2003', '\x2004', '\x2005', '\x2006', '\x2007', '\x2008', '\x2009', '\x200A', '\x202F', '\x205F', '\x3000'];

        private static void CountTranslationRows(Result result, TranslationRow[] rows)
        {
            foreach (TranslationRow row in rows)
            {
                if (row.key.Equals(row.english, StringComparison.Ordinal)) continue;
                int words = CountWords(row.english);
                result.totalWords += words;
                if (!string.IsNullOrWhiteSpace(row.translation)) result.translatedWords += words;
            }
        }

        private static int CountWords(string text)
        {
            int words = 0;
            bool inWord = false;
            foreach (char character in text)
            {
                if (Array.IndexOf(kWhiteSpaceCharacters, character) >= 0)
                {
                    inWord = false;
                }
                else if (!inWord)
                {
                    words++;
                    inWord = true;
                }
            }
            return words;
        }

        private static void CountSupportedLanguages(Result result, LanguageInput input)
        {
            var supported = new List<Locale>();
            try
            {
                foreach (int lang in Enum.GetValues(typeof(Locale)))
                {
                    if (string.IsNullOrWhiteSpace(input.names.ElementAtOrDefault(lang))) continue;
                    if ((Locale)lang is Locale.DebugKeys or Locale.DebugEnglishReverted or Locale.DebugEntryWithMaxLength) continue;
                    int count = 0;
                    foreach (string[] row in input.rows)
                        if (!string.IsNullOrWhiteSpace(row.ElementAtOrDefault(lang))) ++count;
                    if ((float)count / input.dictionaryCount > input.threshold) supported.Add((Locale)lang);
                }
            }
            finally
            {
                result.supportedLanguages = supported.ToArray();
            }
        }

        private static void GroupTranslationStatuses(Result result, SummaryInput input)
        {
            var full = new List<string>();
            var partial = new List<string>();
            var none = new List<string>();
            foreach (StatusRow row in input.rows)
            {
                if (row.percentage == 100) full.Add(row.name);
                else if (row.percentage is < 100 and > 0)
                    partial.Add(string.Format(input.format, "{0} ({1:0}%)", row.name, row.clampedPercentage));
                else if (row.percentage == 0) none.Add(row.name);
            }
            result.fullyTranslated = full.Count > 0 ? string.Join(", ", full) : null;
            result.partiallyTranslated = partial.Count > 0 ? string.Join(", ", partial) : null;
            result.notSupported = none.Count > 0 ? string.Join(", ", none) : null;
        }

        private static Task<Result> Queue(Request request)
        {
            lock (kGate)
            {
                kRequests.Enqueue(request);
                if (_worker == null)
                    StartWorker();
            }
            return request.completion.Task;
        }

        private static void StartWorker()
        {
            _worker = Task.Factory.StartNew(ProcessQueue, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            _worker.ContinueWith(Completed, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static void ProcessQueue()
        {
            while (true)
            {
                Request request;
                lock (kGate)
                {
                    if (kRequests.Count == 0)
                        return;
                    request = kRequests.Dequeue();
                }
                request.completion.SetResult(Execute(request));
            }
        }

        private static void Completed(Task completed)
        {
            lock (kGate)
            {
                if (!ReferenceEquals(_worker, completed))
                    return;
                // The retained task is released only after its physical worker has finished.
                _worker = null;
                if (kRequests.Count > 0)
                    StartWorker();
            }
        }

        private static Result Execute(Request request)
        {
            var result = new Result();
            string value = request.value;
            try
            {
                switch (request.operation)
                {
                    case Operation.CountTranslationRows:
                        CountTranslationRows(result, request.translationRows);
                        break;
                    case Operation.CountSupportedLanguages:
                        CountSupportedLanguages(result, request.languageInput);
                        break;
                    case Operation.GroupTranslationStatuses:
                        GroupTranslationStatuses(result, request.summaryInput);
                        break;
                    case Operation.ReadUserCatalog:
                        ReadUserCatalog(result, value);
                        break;
                    case Operation.ReadFile:
                        using (var reader = new StreamReader(value))
                            result.text = reader.ReadToEnd();
                        break;
                    case Operation.ReadResource:
                        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(value))
                        using (var reader = new StreamReader(stream))
                            result.text = reader.ReadToEnd();
                        break;
                    case Operation.PrepareCsv:
                        PrepareCsv(result, value);
                        break;
                    case Operation.CheckCrowdinCache:
                        result.exists = File.Exists(value) && Directory.Exists(request.secondaryValue);
                        break;
                    case Operation.CheckFile:
                        result.exists = File.Exists(value);
                        break;
                    case Operation.ParseCrowdinPath:
                        result.crowdinPath = ParseCrowdinPath(value, request.secondaryValue);
                        break;
                    case Operation.ParseCrowdinManifest:
                        using (var reader = new JsonTextReader(new StringReader(value)))
                        {
                            // Bypass global factory callbacks; custom settings stay on the caller.
                            var serializer = JsonSerializer.Create();
                            serializer.CheckAdditionalContent = true;
                            result.manifest = serializer.Deserialize<CrowdinDistributionManifest>(reader);
                        }
                        break;
                    case Operation.ResetDirectory:
                        if (Directory.Exists(value)) Directory.Delete(value, true);
                        Directory.CreateDirectory(value);
                        break;
                    case Operation.CreateFileDirectory:
                        Directory.CreateDirectory(Path.GetDirectoryName(value));
                        break;
                    case Operation.WriteCrowdinFile:
                        WriteCrowdinFile(value, request.bytes);
                        break;
                    case Operation.WriteText:
                        using (var writer = new StreamWriter(value))
                            writer.Write(request.secondaryValue);
                        break;
                    case Operation.ReadHeldFile:
                        var heldReader = new StreamReader(value);
                        long lease = ++_nextFileLease;
                        try { kFileReaders.Add(lease, heldReader); }
                        catch { heldReader.Dispose(); throw; }
                        result.fileLease = lease;
                        result.text = heldReader.ReadToEnd();
                        break;
                    case Operation.ReleaseHeldFile:
                        if (kFileReaders.TryGetValue(request.fileLease, out StreamReader fileReader))
                        {
                            try { fileReader.Dispose(); }
                            finally { kFileReaders.Remove(request.fileLease); }
                        }
                        break;
                    case Operation.ReadApiBuildId:
                        if (File.Exists(value) && long.TryParse(File.ReadAllText(value), NumberStyles.None, CultureInfo.InvariantCulture, out long buildId))
                            result.buildId = buildId;
                        break;
                    case Operation.WriteApiBuildId:
                        File.WriteAllText(value, request.secondaryValue);
                        break;
                    case Operation.OpenApiCatalog:
                        if (Directory.Exists(value))
                        {
                            IEnumerator<string> catalog = Directory.EnumerateFiles(value, "*.csv", SearchOption.AllDirectories).GetEnumerator();
                            long catalogLease = ++_nextFileLease;
                            try { kApiCatalogs.Add(catalogLease, catalog); }
                            catch { catalog.Dispose(); throw; }
                            result.fileLease = catalogLease;
                        }
                        break;
                    case Operation.NextApiFile:
                        IEnumerator<string> iterator = kApiCatalogs[request.fileLease];
                        result.exists = iterator.MoveNext();
                        if (result.exists) result.text = iterator.Current;
                        break;
                    case Operation.ReleaseApiCatalog:
                        if (kApiCatalogs.TryGetValue(request.fileLease, out IEnumerator<string> apiCatalog))
                        {
                            try { apiCatalog.Dispose(); }
                            finally { kApiCatalogs.Remove(request.fileLease); }
                        }
                        break;
                    case Operation.PrepareApiFileId:
                        result.text = Path.ChangeExtension(value.Replace(request.secondaryValue, string.Empty).Substring(1).Replace('\\', '/'), null);
                        break;
                    case Operation.ParseApiBuild:
                        result.buildResponse = ParseApiJson<DataResponse<AbstractProjectBuildResponse>>(request.bytes).data;
                        break;
                    case Operation.ParseApiDownload:
                        result.downloadLink = ParseApiJson<DataResponse<DownloadLinkResponse>>(request.bytes).data;
                        break;
                    case Operation.ParseApiBuilds:
                        IList<DataResponse<AbstractProjectBuildResponse>> items = ParseApiJson<PaginatedDataResponse<AbstractProjectBuildResponse>>(request.bytes).data;
                        var values = new AbstractProjectBuildResponse[items.Count];
                        for (int i = 0; i < items.Count; i++) values[i] = items[i].data;
                        result.builds = values;
                        break;
                    case Operation.ExportBaseGameCsv:
                        ExportBaseGameCsv(result, value, request.exportInput);
                        break;
                    case Operation.ReadFeatureKeys:
                        using (var resourceReader = new StreamReader(request.resourceStream))
                            result.keys = PolyglotUtil.GetKeysFromLocalizationAsset(resourceReader.ReadToEnd()).ToArray();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(request.operation), request.operation, null);
                }
            }
            catch (Exception error)
            {
                result.error = error;
            }
            return result;
        }

        private static void ExportBaseGameCsv(Result result, string path, ExportInput input)
        {
            var messages = new List<ExportMessage>();
            int[] supportedLanguages = [(int)LocalizationLanguage.French, (int)LocalizationLanguage.Spanish, (int)LocalizationLanguage.German, (int)LocalizationLanguage.Japanese, (int)LocalizationLanguage.Korean];
            try
            {
                using var writer = new StreamWriter(path);
                writer.WriteLine("Polyglot,100," + new string(',', input.languageCount));
                foreach (ExportAsset asset in input.assets)
                {
                    if (!asset.hasName) break;
                    messages.Add(new ExportMessage($"Processing '{asset.name}'"));
                    if (!asset.hasText) break;

                    List<List<string>> rows = CsvReader.Parse(asset.text);
                    foreach (List<string> row in rows.SkipWhile(r => r[0] != "Polyglot").Skip(1))
                    {
                        string key = row.ElementAtOrDefault(0);
                        if (key is "PSVR_SAFE_AREA_CONFIRMATION_TEXT" or "PSVR2_CONTROLLER_REQUEST") continue;
                        string context = row.ElementAtOrDefault(1);
                        string english = row.ElementAtOrDefault(2);
                        string[] languages = new string[input.languageCount];
                        if (key.Equals(english, StringComparison.Ordinal)) continue;
                        foreach (int language in supportedLanguages)
                            languages[language - 1] = EscapeExportValue(row.ElementAtOrDefault(language + 2));

                        string pattern = null;
                        string replacement = null;
                        switch (key)
                        {
                            case "MISSION_HELP_MIN_HANDS_MOVEMENT_TITLE":
                            case "MISSION_HELP_MAX_HANDS_MOVEMENT":
                                pattern = @"\.</color>";
                                replacement = "</color>.";
                                break;
                            case "LABEL_MULTIPLAYER_MAINTENANCE_UPCOMING":
                                pattern = "maintatance";
                                replacement = "maintenance";
                                break;
                            case "TEXT_INVALID_PASSWORD":
                                pattern = "You";
                                replacement = "Your";
                                break;
                        }
                        if (pattern != null)
                        {
                            string corrected = Regex.Replace(english, pattern, replacement);
                            if (corrected == english)
                                messages.Add(new ExportMessage($"Rule for '{key}' ('{pattern}' -> '{replacement}') did nothing on '{english}'", true));
                            else english = corrected;
                        }
                        writer.WriteLine($"{EscapeExportValue(key)},{EscapeExportValue(context)},{EscapeExportValue(english)},{string.Join(",", languages)}");
                    }
                }
            }
            finally
            {
                result.exportMessages = messages.ToArray();
            }
        }

        private static string EscapeExportValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n')) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static T ParseApiJson<T>(byte[] data)
        {
            string text = Encoding.UTF8.GetString(data);
            using var reader = new JsonTextReader(new StringReader(text));
            var serializer = JsonSerializer.Create();
            serializer.CheckAdditionalContent = true;
            return serializer.Deserialize<T>(reader);
        }

        private static CrowdinPath ParseCrowdinPath(string filePath, string directory)
        {
            if (!kCrowdinPathRegex.IsMatch(filePath))
                throw new ArgumentException($"Path '{filePath}' is invalid", nameof(filePath));
            string relativePath = filePath.Substring(1);
            return new CrowdinPath(Path.ChangeExtension(relativePath, null), Path.Combine(directory, relativePath), relativePath);
        }

        private static void WriteCrowdinFile(string path, byte[] data)
        {
            // The caller transfers the fresh download byte array exclusively to this request.
            if (data[0] == 0x1f && data[1] == 0x8b)
            {
                using var memoryStream = new MemoryStream(data);
                using var gzipStream = new GZipStream(memoryStream, CompressionMode.Decompress);
                using var fileStream = new FileStream(path, FileMode.Create);
                gzipStream.CopyTo(fileStream);
            }
            else
            {
                using var fileStream = new FileStream(path, FileMode.Create);
                fileStream.Write(data, 0, data.Length);
            }
        }

        private static void ReadUserCatalog(Result result, string path)
        {
            var paths = new List<string>();
            try
            {
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
                foreach (string filePath in Directory.EnumerateFiles(path, "*.csv"))
                    paths.Add(filePath);
            }
            finally
            {
                result.paths = paths.ToArray();
            }
        }

        private static void PrepareCsv(Result result, string text)
        {
            var preparedRows = new List<PreparedRow>();
            try
            {
                List<List<string>> rows = CsvReader.Parse(text.Replace("\r\n", "\n"));
                foreach (List<string> row in rows.SkipWhile(candidate => candidate[0] != "Polyglot").Skip(1))
                {
                    string key = row[0];
                    if (string.IsNullOrEmpty(key) || LocalizationImporter.IsLineBreak(key) || row.Count <= 1)
                        continue;

                    string longestString = string.Empty;
                    foreach (string value in row.Skip(2))
                    {
                        if (longestString.Length < value.Length)
                            longestString = value;
                    }

                    char[] characters = row[2].ToCharArray();
                    Array.Reverse(characters);
                    string reversed = new(characters);
                    row.Add(row[0]);
                    row.Add(reversed);
                    row.Add(longestString);
                    row.RemoveAt(0);
                    row.RemoveAt(0);
                    preparedRows.Add(new PreparedRow(key, row.ToArray()));
                }
            }
            finally
            {
                // A malformed later row must not discard the successfully prepared prefix.
                result.rows = preparedRows.ToArray();
            }
        }
    }
}
