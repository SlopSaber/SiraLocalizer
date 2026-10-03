using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BGLib.Polyglot;
using JetBrains.Annotations;
using SiraLocalizer.Providers;
using SiraLocalizer.Records;
using SiraLocalizer.Utilities;
using SiraUtil.Affinity;
using SiraUtil.Logging;
using Zenject;

namespace SiraLocalizer
{
    internal class LocalizationManager : IAffinity, IInitializable, IDisposable
    {
        internal const float kMinimumTranslatedPercent = 0.50f;

        private readonly SiraLog _logger;
        private readonly Settings _config;
        private readonly List<ILocalizationProvider> _localizationProviders;
        private readonly List<ILocalizationDownloader> _localizationDownloaders;
        private readonly SettingsManager _settingsManager;

        private readonly List<LocalizationFile> _localizationFiles = new();
        private int _registrationRevision;
        private bool _disposed;

        public LocalizationManager(SiraLog logger, Settings config, List<ILocalizationProvider> localizationProviders, List<ILocalizationDownloader> localizationDownloaders, SettingsManager settingsManager)
        {
            _logger = logger;
            _config = config;
            _localizationProviders = localizationProviders;
            _localizationDownloaders = localizationDownloaders;
            _settingsManager = settingsManager;
        }

        public async void Initialize()
        {
            int revision = _registrationRevision;
            try
            {
                if (_config.automaticallyDownloadLocalizations)
                {
                    await CheckForUpdatesAndDownloadIfAvailable(CancellationToken.None);
                }

                if (_disposed || revision != _registrationRevision) return;
                await RegisterLocalizationsAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Error(ex);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            DeregisterLocalizations();
        }

        internal async Task CheckForUpdatesAndDownloadIfAvailable(CancellationToken cancellationToken)
        {
            List<ILocalizationDownloader> list = await CheckForUpdatesAsync(cancellationToken);

            if (list.Count == 0)
            {
                return;
            }

            await DownloadLocalizationsAsync(list, cancellationToken);
        }

        internal async Task<List<ILocalizationDownloader>> CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            _logger.Info("Checking for updates");

            List<ILocalizationDownloader> list = new();

            foreach (ILocalizationDownloader localizationDownloader in _localizationDownloaders)
            {
                try
                {

                    if (await localizationDownloader.CheckForUpdatesAsync(cancellationToken))
                    {
                        _logger.Info($"Updates available from {localizationDownloader.name}");

                        list.Add(localizationDownloader);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error occured while checking for updates for {localizationDownloader.name} ({localizationDownloader.GetType().FullName})\n{ex}");
                }
            }

            return list;
        }

        internal async Task DownloadLocalizationsAsync(List<ILocalizationDownloader> localizationDownloaders, CancellationToken cancellationToken)
        {
            foreach (ILocalizationDownloader localizationDownloader in localizationDownloaders)
            {
                _logger.Info($"Downloading updates from {localizationDownloader.name}");

                try
                {
                    await localizationDownloader.DownloadLocalizationsAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error occured while downloading for updates for {localizationDownloader.name} ({localizationDownloader.GetType().FullName})\n{ex}");
                }
            }
        }

        internal async Task ReloadLocalizations(CancellationToken cancellationToken)
        {
            DeregisterLocalizations();
            await RegisterLocalizationsAsync(cancellationToken);
        }

        private async Task RegisterLocalizationsAsync(CancellationToken cancellationToken)
        {
            int revision = ++_registrationRevision;
            foreach (ILocalizationProvider localizationProvider in _localizationProviders)
            {
                if (_disposed || revision != _registrationRevision) return;
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await foreach (LocalizationFile file in localizationProvider.GetLocalizationAssetsAsync(cancellationToken))
                    {
                        if (_disposed || revision != _registrationRevision) return;
                        cancellationToken.ThrowIfCancellationRequested();
                        file.prepared = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.PrepareCsv, file.content);
                        if (_disposed || revision != _registrationRevision) return;
                        cancellationToken.ThrowIfCancellationRequested();
                        _localizationFiles.Add(file);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error occured while adding localizations from {localizationProvider.GetType().FullName}\n{ex}");
                }
            }

            if (_disposed || revision != _registrationRevision) return;
            cancellationToken.ThrowIfCancellationRequested();
            LocalizationImporter.ImportFromFiles(Localization.Instance.inputFiles);
        }

        private void DeregisterLocalizations()
        {
            _registrationRevision++;
            _localizationFiles.Clear();
        }

        internal List<TranslationStatus> GetTranslationStatuses(Locale language)
        {
            var languageStrings = Localization.Instance._languageStrings;
            var statuses = new List<TranslationStatus>();

            foreach (LocalizationDefinition def in LocalizationDefinition.loadedDefinitions)
            {
                int total = 0;
                int translated = 0;
                var pendingRows = new List<LocalizationPreparation.TranslationRow>();
                IEqualityComparer<string> comparer = languageStrings.Comparer;
                bool canBatch = def.keys is string[] &&
                    (ReferenceEquals(comparer, EqualityComparer<string>.Default) ||
                     ReferenceEquals(comparer, StringComparer.Ordinal) || ReferenceEquals(comparer, StringComparer.OrdinalIgnoreCase));

                void CompleteRows()
                {
                    if (pendingRows.Count == 0) return;
                    LocalizationPreparation.TranslationRow[] rows = pendingRows.ToArray();
                    pendingRows.Clear();
                    LocalizationPreparation.Result prepared = LocalizationPreparation.Complete(LocalizationPreparation.PrepareTranslationCounts(rows));
                    prepared.ThrowIfFailed();
                    total += prepared.totalWords;
                    translated += prepared.translatedWords;
                }

                try
                {
                    foreach (string key in def.keys)
                    {
                        if (!languageStrings.TryGetValue(key, out List<string> strings))
                        {
                            CompleteRows();
                            _logger.Warn($"Key '{key}' does not exist");
                            continue;
                        }
                        if (strings.Count == 0) continue;
                        if (!canBatch || strings.GetType() != typeof(List<string>))
                        {
                            CompleteRows();
                            LocalizationPreparation.Result counted = LocalizationPreparation.Complete(LocalizationPreparation.PrepareTranslationCounts(
                                [new LocalizationPreparation.TranslationRow(key, strings[(int)LocalizationLanguage.English], null)]));
                            counted.ThrowIfFailed();
                            if (counted.keyMatched) continue;
                            total += counted.totalWords;
                            LocalizationPreparation.Result translatedRow = LocalizationPreparation.Complete(LocalizationPreparation.Prepare(
                                LocalizationPreparation.Operation.CheckTranslation, strings.ElementAtOrDefault((int)language)));
                            translatedRow.ThrowIfFailed();
                            if (translatedRow.exists) translated += counted.totalWords;
                            continue;
                        }
                        pendingRows.Add(new LocalizationPreparation.TranslationRow(key,
                            strings[(int)LocalizationLanguage.English], strings.ElementAtOrDefault((int)language)));
                    }
                    CompleteRows();
                }
                catch
                {
                    // Earlier row failures must precede a later native lookup or logger failure.
                    CompleteRows();
                    throw;
                }

                statuses.Add(new TranslationStatus(def.name, total, translated));
            }

            return statuses;
        }

        [AffinityPatch(typeof(LocalizationImporter), nameof(LocalizationImporter.ImportFromFiles))]
        [AffinityPostfix]
        [UsedImplicitly]
        private void LocalizationImporter_PostImportFromFiles()
        {
            // prevent exceptions on our end from breaking Polyglot's load process
            try
            {
                AddLocalizationFilesToPolyglot();
                UpdateSupportedLanguages();
            }
            catch (Exception ex)
            {
                _logger.Critical(ex);
            }
        }

        private void AddLocalizationFilesToPolyglot()
        {
            foreach (LocalizationFile localizationFile in _localizationFiles.OrderBy(l => l.priority))
            {
                ImportPreparedFile(localizationFile.prepared);
            }
        }

        /// <summary>
        /// Applies prepared CSV rows without replacing existing English strings.
        /// </summary>
        private void ImportPreparedFile(LocalizationPreparation.Result prepared)
        {
            var languageStrings = Localization.Instance._languageStrings;
            foreach (LocalizationPreparation.PreparedRow preparedRow in prepared.rows)
            {
                string key = preparedRow.key;
                List<string> row = new(preparedRow.values);

                if (languageStrings.TryGetValue(key, out List<string> existingValues))
                {
                    // keep English, overwrite everything else
                    row[0] = existingValues[0];
                }

                languageStrings[key] = row;
            }
            prepared.ThrowIfFailed();
        }

        private void UpdateSupportedLanguages()
        {
            if (Localization._instance == null)
            {
                return;
            }

            IEnumerable<Locale> languages = GetSupportedLanguages();

            List<LocalizationLanguage> supportedLanguages = Localization.Instance._localization.supportedLanguages;
            supportedLanguages.Clear();
            supportedLanguages.AddRange(languages.Cast<LocalizationLanguage>());

            Localization.Instance.SelectedLanguage = _settingsManager.settings.misc.language.ToLocalizationLanguage();
        }

        private IEnumerable<Locale> GetSupportedLanguages()
        {
            var languageStrings = Localization.Instance._languageStrings;

            if (!languageStrings.TryGetValue("LANGUAGE_THIS", out List<string> languageNames))
            {
                yield break;
            }

            if ((languageNames != null && languageNames.GetType() != typeof(List<string>)) ||
                languageStrings.Values.Any(values => values != null && values.GetType() != typeof(List<string>)))
            {
                foreach (int lang in Enum.GetValues(typeof(Locale)))
                {
                    LocalizationPreparation.Result named = LocalizationPreparation.Complete(LocalizationPreparation.Prepare(
                        LocalizationPreparation.Operation.CheckTranslation, languageNames.ElementAtOrDefault(lang)));
                    named.ThrowIfFailed();
                    if (!named.exists) continue;
                    if ((Locale)lang is Locale.DebugKeys or Locale.DebugEnglishReverted or Locale.DebugEntryWithMaxLength) continue;
                    int count = 0;
                    foreach (List<string> values in languageStrings.Values)
                    {
                        LocalizationPreparation.Result translatedRow = LocalizationPreparation.Complete(LocalizationPreparation.Prepare(
                            LocalizationPreparation.Operation.CheckTranslation, values.ElementAtOrDefault(lang)));
                        translatedRow.ThrowIfFailed();
                        if (translatedRow.exists) ++count;
                    }
                    if ((float)count / languageStrings.Count > kMinimumTranslatedPercent) yield return (Locale)lang;
                }
                yield break;
            }

            string[][] rows = languageStrings.Values.Select(values => values?.ToArray()).ToArray();
            LocalizationPreparation.Result prepared = LocalizationPreparation.Complete(LocalizationPreparation.PrepareSupportedLanguages(
                languageNames?.ToArray(), rows, languageStrings.Count, kMinimumTranslatedPercent));
            foreach (Locale language in prepared.supportedLanguages) yield return language;
            prepared.ThrowIfFailed();
        }
    }
}
