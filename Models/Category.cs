using System.Collections.Generic;

namespace FileSorter.Models;

/// <summary>
/// One sorting category. Has a stable id (used to match folders across language switches),
/// localized display names for RU/EN, the list of file extensions it owns,
/// and an optional list of regex patterns matched against the filename.
/// </summary>
public class Category
{
    public string Id { get; set; } = "";
    public string NameRu { get; set; } = "";
    public string NameEn { get; set; } = "";
    public List<string> Extensions { get; set; } = new();

    /// <summary>
    /// Optional regex patterns matched against the *filename* (not full path).
    /// Used as a fallback when the extension is not claimed by any category.
    /// Example: "^IMG_\\d+" → catch all camera files in "Изображения".
    /// </summary>
    public List<string> NameRegex { get; set; } = new();

    public string DisplayName(string lang) =>
        lang == "en" ? (string.IsNullOrWhiteSpace(NameEn) ? NameRu : NameEn)
                     : (string.IsNullOrWhiteSpace(NameRu) ? NameEn : NameRu);
}
