using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BGLib.Polyglot;
using Newtonsoft.Json;
using SiraLocalizer.Providers.Crowdin;

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
            internal readonly TaskCompletionSource<Result> completion;

            internal Request(Operation operation, string value, string secondaryValue, byte[] bytes)
            {
                this.operation = operation;
                this.value = value;
                this.secondaryValue = secondaryValue;
                this.bytes = bytes;
                completion = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private static readonly object kGate = new();
        private static readonly Queue<Request> kRequests = new();
        private static readonly Regex kCrowdinPathRegex = new(@"^\/[A-Za-z\-_]+(?:\/[A-Za-z\-_]+)*\.csv$");
        private static Task _worker;

        internal static Task<Result> Prepare(Operation operation, string value, string secondaryValue = null, byte[] bytes = null)
        {
            var request = new Request(operation, value, secondaryValue, bytes);
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
                            JsonSerializer serializer = JsonSerializer.Create();
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
