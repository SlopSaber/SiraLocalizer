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
            internal readonly string Key;
            internal readonly string[] Values;

            internal PreparedRow(string key, string[] values)
            {
                Key = key;
                Values = values;
            }
        }

        internal sealed class Result
        {
            internal string Text;
            internal string[] Paths = Array.Empty<string>();
            internal PreparedRow[] Rows = Array.Empty<PreparedRow>();
            internal Exception Error;

            internal void ThrowIfFailed()
            {
                if (Error != null)
                    ExceptionDispatchInfo.Capture(Error).Throw();
            }
        }

        private sealed class Request
        {
            internal readonly Operation Operation;
            internal readonly string Value;
            internal readonly TaskCompletionSource<Result> Completion;

            internal Request(Operation operation, string value)
            {
                Operation = operation;
                Value = value;
                Completion = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private static readonly object Gate = new();
        private static readonly Queue<Request> Requests = new();
        private static Task _worker;

        internal static Task<Result> Prepare(Operation operation, string value)
        {
            var request = new Request(operation, value);
            lock (Gate)
            {
                Requests.Enqueue(request);
                if (_worker == null)
                    StartWorker();
            }
            return request.Completion.Task;
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
                lock (Gate)
                {
                    if (Requests.Count == 0)
                        return;
                    request = Requests.Dequeue();
                }
                request.Completion.SetResult(Execute(request.Operation, request.Value));
            }
        }

        private static void Completed(Task completed)
        {
            lock (Gate)
            {
                if (!ReferenceEquals(_worker, completed))
                    return;
                // The retained task is released only after its physical worker has finished.
                _worker = null;
                if (Requests.Count > 0)
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
                            result.Text = reader.ReadToEnd();
                        break;
                    case Operation.ReadResource:
                        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(value))
                        using (var reader = new StreamReader(stream))
                            result.Text = reader.ReadToEnd();
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
                result.Error = error;
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
                result.Paths = paths.ToArray();
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
                result.Rows = preparedRows.ToArray();
            }
        }
    }
}
