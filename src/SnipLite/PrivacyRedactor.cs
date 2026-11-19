using System.Text;
using System.Text.RegularExpressions;

namespace SnipLite;

/// <summary>
/// Finds sensitive text in an OCR document and returns the bounding boxes that
/// should be pixelated: mainland phone numbers, e-mail addresses, ID cards,
/// bank cards and common API keys / tokens.
/// </summary>
internal static class PrivacyRedactor
{
    private static readonly Regex[] Patterns =
    {
        // 中国大陆手机号：1 开头 11 位，容忍 OCR 在中间插入的空格或短横线。
        new(@"(?<!\d)1[3-9]\d[\s\-]?\d{4}[\s\-]?\d{4}(?!\d)", RegexOptions.Compiled),
        // 邮箱地址
        new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled),
        // 身份证号：17 位数字 + 校验位（数字或 X）
        new(@"(?<![0-9A-Za-z])\d{17}[\dXx](?![0-9A-Za-z])", RegexOptions.Compiled),
        // 银行卡号：16 ~ 19 位连续数字
        new(@"(?<!\d)\d{16,19}(?!\d)", RegexOptions.Compiled),
        // 常见密钥 / Token：带已知前缀的长串，或 key = value 形式
        new(@"(?i)(?<![A-Za-z0-9])(?:sk|pk|ak|ghp|gho|ghs|ghu|xox[abprs]|eyJ)[A-Za-z0-9_\-\.]{12,}", RegexOptions.Compiled),
        new(@"(?i)(?:api[_-]?key|access[_-]?token|secret|password|passwd)\s*[:=]\s*[A-Za-z0-9_\-\.\/+]{8,}", RegexOptions.Compiled)
    };

    public static List<RectangleF> Find(OcrDocument document)
    {
        var matches = new List<RectangleF>();
        foreach (var line in GroupLines(document.Characters))
        {
            if (line.Chars.Count == 0) continue;
            foreach (var pattern in Patterns)
            {
                foreach (Match match in pattern.Matches(line.Text.ToString()))
                {
                    if (!match.Success || match.Length == 0) continue;
                    var box = BoxFor(line, match.Index, match.Index + match.Length);
                    if (box.Width >= 2 && box.Height >= 2) matches.Add(box);
                }
            }
        }
        return Merge(matches);
    }

    private sealed class Line
    {
        public readonly StringBuilder Text = new();
        public readonly List<RecognizedCharacter> Chars = new();
        /// <summary>UTF-16 start offset of every character element in <see cref="Text"/>.</summary>
        public readonly List<int> Starts = new();
        public readonly List<int> Lengths = new();
    }

    private static IEnumerable<Line> GroupLines(IReadOnlyList<RecognizedCharacter> characters)
    {
        Line? line = null;
        foreach (var character in characters)
        {
            if (line is null || character.Prefix.Contains('\n'))
            {
                if (line is not null) yield return line;
                line = new Line();
            }
            line.Starts.Add(line.Text.Length);
            line.Lengths.Add(character.Text.Length);
            line.Text.Append(character.Text);
            line.Chars.Add(character);
        }
        if (line is not null) yield return line;
    }

    private static RectangleF BoxFor(Line line, int start, int end)
    {
        var box = RectangleF.Empty;
        var first = true;
        for (var i = 0; i < line.Chars.Count; i++)
        {
            var from = line.Starts[i];
            var to = from + line.Lengths[i];
            if (to <= start || from >= end) continue;
            box = first ? line.Chars[i].Bounds : RectangleF.Union(box, line.Chars[i].Bounds);
            first = false;
        }
        return box;
    }

    /// <summary>Joins overlapping boxes so one sensitive value produces one mask.</summary>
    private static List<RectangleF> Merge(List<RectangleF> boxes)
    {
        var merged = new List<RectangleF>();
        foreach (var box in boxes.OrderBy(b => b.Left))
        {
            var current = box;
            // Growing the union can make it reach a box that did not overlap the
            // original rect, so keep folding until nothing else intersects.
            bool changed;
            do
            {
                changed = false;
                for (var i = merged.Count - 1; i >= 0; i--)
                {
                    if (!merged[i].IntersectsWith(current)) continue;
                    current = RectangleF.Union(merged[i], current);
                    merged.RemoveAt(i);
                    changed = true;
                }
            } while (changed);
            merged.Add(current);
        }
        return merged;
    }
}
