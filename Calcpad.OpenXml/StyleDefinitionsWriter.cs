using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Calcpad.OpenXml
{
    internal static class StyleDefinitionsWriter
    {
        internal static void CreateStyleDefinitionsPart(MainDocumentPart mainPart)
        {
            var styleDefinitions = AddStyleDefinitionsPart(mainPart);
            AddDocDefaults(styleDefinitions);
            AddParagraphStyles(styleDefinitions);
            AddTableStyles(styleDefinitions);
        }

        /// <summary>
        /// Merges Calcpad's styles into a template's existing <see cref="StyleDefinitionsPart"/>.
        /// The template's <c>DocDefaults</c> (brand font/size) and any style it already defines are
        /// left untouched; only missing Calcpad styles are appended. Returns a map from Calcpad's
        /// heading ids (<c>h1</c>..<c>h6</c>) to the template's own heading style ids where present,
        /// so headings adopt the template's look.
        /// </summary>
        internal static Dictionary<string, string> MergeInto(MainDocumentPart mainPart)
        {
            var styles = mainPart.StyleDefinitionsPart!.Styles!;
            var existingIds = new HashSet<string>(
                styles.Elements<Style>()
                      .Select(s => s.StyleId?.Value)
                      .Where(id => id is not null)!,
                StringComparer.Ordinal);

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i <= 6; ++i)
            {
                var templateHeading = FindTemplateHeading(styles, i);
                var calcpadId = "h" + i;
                if (templateHeading is not null)
                    map[calcpadId] = templateHeading;          // use the template's heading style
                else if (!existingIds.Contains(calcpadId))
                    AppendHeadingStyle(styles, i);             // fall back to Calcpad's own
            }

            foreach (var style in BuildTableStyles())
                if (style.StyleId?.Value is string id && !existingIds.Contains(id))
                    styles.AppendChild(style);

            return map;
        }

        // Finds the template's built-in heading style for level i. Word keeps built-in style ids
        // language-invariant ("Heading1"), but we also match by style name for safety.
        private static string FindTemplateHeading(Styles styles, int level)
        {
            string wantId = "Heading" + level;
            string wantName = "heading " + level;
            foreach (var s in styles.Elements<Style>())
            {
                if (s.Type?.Value != StyleValues.Paragraph) continue;
                var id = s.StyleId?.Value;
                if (string.Equals(id, wantId, StringComparison.OrdinalIgnoreCase))
                    return id;
                var name = s.StyleName?.Val?.Value;
                if (name is not null && string.Equals(name, wantName, StringComparison.OrdinalIgnoreCase))
                    return id ?? wantId;
            }
            return null;
        }

        private static StyleDefinitionsPart AddStyleDefinitionsPart(MainDocumentPart mainPart)
        {
            var part = mainPart.AddNewPart<StyleDefinitionsPart>();
            var root = new Styles();
            root.Save(part);
            part.Styles.DocDefaults = new DocDefaults();
            return part;
        }

        private static void AddDocDefaults(StyleDefinitionsPart styleDefinitionsPart)
        {
            styleDefinitionsPart.Styles.DocDefaults.RunPropertiesDefault = new RunPropertiesDefault()
            {
                RunPropertiesBaseStyle = new RunPropertiesBaseStyle
                {
                    RunFonts = new RunFonts()
                    {
                        Ascii = "Calibri",
                        HighAnsi = "Calibri"
                    },
                    FontSize = new FontSize
                    {
                        Val = "24"
                    }
                }
            };
        }

        private static readonly string[] HeadingFontSizes = ["48", "40", "36", "32", "28", "26"];

        private static void AddParagraphStyles(StyleDefinitionsPart styleDefinitionsPart)
        {
            for (int i = 1; i <= 6; ++i)
                AppendHeadingStyle(styleDefinitionsPart.Styles, i);
        }

        // Appends Calcpad's own heading style "h{level}" (level is 1-based).
        private static void AppendHeadingStyle(Styles styles, int level)
        {
            var id = "h" + level;
            styles.AppendChild(new Style()
            {
                Type = StyleValues.Paragraph,
                StyleId = id,
                StyleName = new StyleName() { Val = id },
                BasedOn = new BasedOn() { Val = "Normal" },
                NextParagraphStyle = new NextParagraphStyle() { Val = "Normal" },
                StyleRunProperties = new StyleRunProperties()
                {
                    FontSize = new FontSize() { Val = HeadingFontSizes[level - 1] },
                    Bold = new Bold()
                }
            });
        }

        private static void AddTableStyles(StyleDefinitionsPart styleDefinitionsPart)
        {
            foreach (var style in BuildTableStyles())
                styleDefinitionsPart.Styles.AppendChild(style);
        }

        // The Calcpad table styles ("bcpd" bordered, "cpd" plain) as fresh elements, so both the
        // from-scratch and the template-merge paths can append their own copies.
        private static IEnumerable<Style> BuildTableStyles()
        {
            yield return new Style()
            {
                Type = StyleValues.Table,
                StyleId = "bcpd",
                StyleName = new StyleName() { Val = "Calcpad Bordered" },
                StyleTableCellProperties = new StyleTableCellProperties()
                {
                    TableCellMargin = new TableCellMargin()
                    {
                        LeftMargin = new LeftMargin() { Type = TableWidthUnitValues.Dxa, Width = "68" },
                        RightMargin = new RightMargin() { Type = TableWidthUnitValues.Dxa, Width = "68" },
                        TopMargin = new TopMargin() { Type = TableWidthUnitValues.Dxa, Width = "34" },
                        BottomMargin = new BottomMargin() { Type = TableWidthUnitValues.Dxa, Width = "34" }
                    },
                    TableCellVerticalAlignment = new TableCellVerticalAlignment()
                    {
                        Val = TableVerticalAlignmentValues.Center
                    }
                }
            };
            yield return new Style()
            {
                Type = StyleValues.Table,
                StyleId = "cpd",
                StyleName = new StyleName() { Val = "Calcpad" },
                StyleTableCellProperties = new StyleTableCellProperties()
                {
                    TableCellMargin = new TableCellMargin()
                    {
                        LeftMargin = new LeftMargin() { Type = TableWidthUnitValues.Dxa, Width = "0" },
                        RightMargin = new RightMargin() { Type = TableWidthUnitValues.Dxa, Width = "0" },
                        TopMargin = new TopMargin() { Type = TableWidthUnitValues.Dxa, Width = "0" },
                        BottomMargin = new BottomMargin() { Type = TableWidthUnitValues.Dxa, Width = "0" }
                    },
                    TableCellVerticalAlignment = new TableCellVerticalAlignment()
                    {
                        Val = TableVerticalAlignmentValues.Top
                    }
                }
            };
        }
    }
}
