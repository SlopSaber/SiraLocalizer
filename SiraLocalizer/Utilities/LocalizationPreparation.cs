using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BGLib.Polyglot;

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
            internal readonly TaskCompletionSource<Result> completion;

            internal Request(Operation operation, string value)
            {
                this.operation = operation;
                this.value = value;
                completion = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private static readonly object kGate = new();
        private static readonly Queue<Request> kRequests = new();
        private static Task _worker;

        internal static Task<Result> Prepare(Operation operation, string value)
        {
            var request = new Request(operation, value);
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
                request.completion.SetResult(Execute(request.operation, request.value));
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

        private static Result Execute(Operation operation, string value)
        {
            var result = new Result();
            try
            {
                switch (operation)
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
                    default:
                        throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
                }
            }
            catch (Exception error)
            {
                result.error = error;
            }
            return result;
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
