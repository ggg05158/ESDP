using System.Windows.Media;

namespace ESDP
{
    internal static class AppFonts
    {
        public const string DefaultFamilyName = "맑은 고딕";

        public static FontFamily Resolve(string? familyName)
        {
            string requested = string.IsNullOrWhiteSpace(familyName)
                ? DefaultFamilyName
                : familyName.Trim();
            return new FontFamily(requested);
        }

        public static string ToDisplayName(FontFamily family)
        {
            return family.Source;
        }

        public static string NormalizeFamilyName(string? familyName)
        {
            return string.IsNullOrWhiteSpace(familyName)
                ? DefaultFamilyName
                : familyName.Trim();
        }
    }
}
