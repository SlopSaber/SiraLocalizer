using SiraLocalizer.Utilities;

namespace SiraLocalizer.Records
{
    internal record LocalizationFile
    {
        internal string content { get; }

        internal int priority { get; }

        internal LocalizationPreparation.Result prepared { get; set; }

        internal LocalizationFile(string content, int priority)
        {
            this.content = content;
            this.priority = priority;
        }
    }
}
