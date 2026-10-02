using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using IPA.Utilities;
using SiraLocalizer.Records;
using SiraLocalizer.Utilities;

namespace SiraLocalizer.Providers
{
    internal class UserLocalizationFileProvider : ILocalizationProvider
    {
        public async IAsyncEnumerable<LocalizationFile> GetLocalizationAssetsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            string folder = Path.GetFullPath(Path.Combine(UnityGame.UserDataPath, "SiraLocalizer", "Localizations", "User"));

            var catalog = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.ReadUserCatalog, folder);
            foreach (string filePath in catalog.Paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.ReadFile, filePath);
                cancellationToken.ThrowIfCancellationRequested();
                file.ThrowIfFailed();
                yield return new LocalizationFile(file.Text, 2000);
            }
            catalog.ThrowIfFailed();
        }
    }
}
