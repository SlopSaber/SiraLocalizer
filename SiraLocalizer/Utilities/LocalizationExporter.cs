using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BGLib.Polyglot;
using IPA.Utilities;
using SiraUtil.Logging;
using Zenject;

namespace SiraLocalizer.Utilities
{
    internal class LocalizationExporter : IInitializable, ITickable, IDisposable
    {
        private readonly SiraLog _logger;
        private readonly LocalizationModel _localizationModel;
        private Task<LocalizationPreparation.Result> _preparation;
        private Exception _captureError;
        private bool _disposed;

        internal LocalizationExporter(SiraLog logger, LocalizationModel localizationModel)
        {
            _logger = logger;
            _localizationModel = localizationModel;
        }

        public void Initialize()
        {
            string filePath = Path.Combine(UnityGame.InstallPath, "beat-saber.csv");
            int numberOfLanguages = Enum.GetNames(typeof(Locale)).Length - 1;
            _logger.Info($"Dumping base game localization to '{filePath}'");

            var assets = new List<LocalizationPreparation.ExportAsset>();
            string name = null;
            bool hasName = false;
            try
            {
                foreach (LocalizationAsset asset in _localizationModel.inputFiles.Take(2))
                {
                    name = null;
                    hasName = false;
                    name = asset.TextAsset.name;
                    hasName = true;
                    string text = asset.TextAsset.text;
                    assets.Add(new LocalizationPreparation.ExportAsset(name, text, true, true));
                    name = null;
                    hasName = false;
                }
            }
            catch (Exception error)
            {
                _captureError = error;
                assets.Add(new LocalizationPreparation.ExportAsset(name, null, hasName, false));
            }

            try
            {
                _preparation = LocalizationPreparation.PrepareExport(filePath, numberOfLanguages, assets.ToArray());
            }
            catch (Exception error)
            {
                LogFailure(error);
                _captureError = null;
            }
        }

        public void Tick()
        {
            if (_disposed || _preparation == null || !_preparation.IsCompleted) return;
            LocalizationPreparation.Result result = _preparation.GetAwaiter().GetResult();
            _preparation = null;
            foreach (LocalizationPreparation.ExportMessage message in result.exportMessages)
            {
                if (message.warning) _logger.Warn(message.text);
                else _logger.Info(message.text);
            }
            Exception error = result.error ?? _captureError;
            _captureError = null;
            if (error != null) LogFailure(error);
        }

        public void Dispose()
        {
            _disposed = true;
            if (_preparation != null)
            {
                if (!_preparation.IsCompleted) ((IAsyncResult)_preparation).AsyncWaitHandle.WaitOne();
                _ = _preparation.GetAwaiter().GetResult();
                _preparation = null;
            }
            _captureError = null;
        }

        private void LogFailure(Exception error)
        {
            _logger.Error("Could not dump base game localization");
            _logger.Error(error.ToString());
        }
    }
}
