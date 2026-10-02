using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using SiraLocalizer.Records;
using SiraLocalizer.Utilities;

namespace SiraLocalizer.Providers
{
    internal class ResourceLocalizationProvider : ILocalizationProvider
    {
        private static readonly string[] kResourcesToLoad = new[]
        {
            "SiraLocalizer.Resources.sira-localizer.csv",
        };

        public async IAsyncEnumerable<LocalizationFile> GetLocalizationAssetsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (string resourceName in kResourcesToLoad)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = await LocalizationPreparation.Prepare(LocalizationPreparation.Operation.ReadResource, resourceName);
                cancellationToken.ThrowIfCancellationRequested();
                file.ThrowIfFailed();
                yield return new LocalizationFile(file.text, 0);
            }
        }
    }
}
