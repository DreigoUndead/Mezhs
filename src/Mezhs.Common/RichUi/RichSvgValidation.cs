using System.Globalization;
using System.Text.RegularExpressions;

namespace Mezhs.Common.RichUi;

// Server-only SVG value bounds. Unknown attributes have already failed the tag registry.
internal static class RichSvgValidation
{
    // SVG-specific policy belongs to the registered SVG tag family. The
    // canonical structural parser only asks the tag's validator for a result.
    internal static bool ValidForTag(
        RichTagDefinition? parent,
        RichTagDefinition child,
        IReadOnlyDictionary<string, string> attributes) =>
        (parent?.Context != "svg" || AllowsChild(parent.Name, child.Name)) &&
        Valid(child.Name, attributes);

    private static bool Match(string s, string pattern) =>
        Regex.IsMatch(s, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static bool N(string s, double lo, double hi) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
        double.IsFinite(n) && n >= lo && n <= hi;
    private static bool Ns(string s, double lo, double hi, int count = 0)
    {
        var values = s.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
        return values.Length is > 0 and <= 200 && (count == 0 || values.Length == count) &&
            values.All(v => Match(v, @"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?$") && N(v, lo, hi));
    }
    public static bool Valid(string tag, IReadOnlyDictionary<string, string> attrs)
    {
        foreach (var (name, v) in attrs)
        {
            if (v.Length > 2048 || v.IndexOfAny(['<', '>', '\\', ';']) >= 0 ||
                Match(v, @"(?i:url\s*\(|(?:javascript|data)\s*:)")) return false;
            if (tag != "svg" && name is "role" or "aria-label" or "viewBox" or "preserveAspectRatio")
                return false;
            bool ok = name switch
            {
                "role" => v == "img",
                "aria-label" => v.Trim().Length is > 0 and <= 200,
                "viewBox" => Ns(v, -10000, 10000, 4) &&
                    v.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries)
                        .Skip(2).All(x => N(x, 0.000001, 10000)),
                "preserveAspectRatio" => Match(v, @"^(?:none|x(?:Min|Mid|Max)Y(?:Min|Mid|Max) (?:meet|slice))$"),
                "d" => tag == "path" && v.Length <= 1024 &&
                    Match(v, @"^[MmLlHhVvCcSsQqTtAaZz\d+.,\s\-eE]+$") &&
                    Regex.Matches(v, @"[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?",
                        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
                        .All(m => N(m.Value, -10000, 10000)),
                "points" => tag is "polygon" or "polyline" &&
                    Match(v, @"^[\d+.,\s\-eE]+$") && Ns(v, -10000, 10000),
                "fill" or "stroke" => Match(v, @"^(?:none|currentColor|transparent|#[0-9a-fA-F]{3,8}|[a-zA-Z]{1,24}|rgba?\([\d.,%\s]+\))$"),
                "strokeLinecap" => v is "round" or "square" or "butt",
                "strokeLinejoin" => v is "round" or "bevel" or "miter",
                "strokeDasharray" => Ns(v, 0, 200),
                "textAnchor" => v is "start" or "middle" or "end",
                "dominantBaseline" => v is "auto" or "middle" or "central" or "hanging" or "alphabetic",
                "fontFamily" => Match(v, @"^[\w ,'-]{1,64}$"),
                "fontWeight" => Match(v, @"^(?:normal|bold|[1-9]00)$"),
                "transform" => Match(v, @"^(?:(?:translate|scale|rotate)\([\d+.,\s\-]+\)\s*){1,3}$"),
                "opacity" or "fillOpacity" or "strokeOpacity" => Ns(v, 0, 1, 1),
                "fontSize" => Ns(v, 1, 80, 1),
                "strokeWidth" => Ns(v, 0, 30, 1),
                  "width" when tag == "svg" && v == "100%" => true,
                "width" when tag == "svg" => Ns(v, 1, 1600, 1),
                "height" when tag == "svg" => Ns(v, 1, 800, 1),
               "x" or "y" or "x1" or "x2" or "y1" or "y2" or "cx" or "cy" or "r" or "rx" or "ry" or "width" or "height" =>
                    Ns(v, -10000, 10000, 1),
                _ => false
            };
            if (!ok || tag != "svg" && name is "width" or "height" && tag is not ("rect" or "ellipse")) return false;
        }
        return true;
    }
    public static bool AllowsChild(string? parent, string child) =>
        parent is "svg" or "g" ? child is not ("svg" or "tspan") :
        parent is "text" or "tspan" && child == "tspan";
}

// A per-parse SVG resource guard, used only through registered SVG handlers.
// The generic structural parser knows nothing about SVG budgets or syntax.
internal sealed class RichSvgQuota
{
    private int _tags;
    private int _characters;

    public void Reset()
    {
        _tags = 0;
        _characters = 0;
    }

    public string? RegisterTag(RichTagContent content)
    {
        if (++_tags > 250 || content.Depth > 12)
            return "SVG resource budget exceeded.";
        _characters += content.Parameters.Sum(item => item.Value.Length);
        return _characters > 24_000 ? "SVG content budget exceeded." : null;
    }

    public string? RegisterLiteral(string raw)
    {
        _characters += raw.Length;
        return _characters > 24_000 ? "SVG content budget exceeded." : null;
    }
}
