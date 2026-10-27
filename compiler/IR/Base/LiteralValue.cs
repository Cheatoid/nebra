using System.Globalization;

namespace Nebra.IR;

/// <summary>The kinds of value a literal type can stand for.</summary>
public enum LiteralKind
{
    String,
    Number,
    Boolean,
}

/// <summary>
/// The value of a literal type such as <c>"user:login"</c>, <c>-1</c> or <c>true</c>. Two literals
/// are the same value exactly when their <see cref="Text"/> is the same, so <c>1</c> and
/// <c>1.0</c>, or <c>'a'</c> and <c>"a"</c>, are one literal.
/// </summary>
public sealed record LiteralValue(LiteralKind Kind, string Text, string? StringValue)
{
    public static LiteralValue Of(string value) => new(LiteralKind.String, Quote(value), value);

    public static LiteralValue Of(bool value) => new(LiteralKind.Boolean, value ? "true" : "false", null);

    public static LiteralValue Of(NumberLiteralExpr literal, bool negative = false)
    {
        var value = Parse(literal);
        return new LiteralValue(LiteralKind.Number, (negative ? -value : value).ToString("R", CultureInfo.InvariantCulture), null);
    }

    /// <summary>The literal value of <paramref name="expression"/>, or <c>null</c> when it is no literal.</summary>
    public static LiteralValue? Of(Expr expression)
    {
        return expression switch
        {
            StringLiteralExpr text => Of(text.Value),
            BoolLiteralExpr boolean => Of(boolean.Value),
            NumberLiteralExpr number => Of(number),
            UnaryExpr { Op: UnaryOp.Negate, Operand: NumberLiteralExpr number } => Of(number, true),
            _ => null,
        };
    }

    private static double Parse(NumberLiteralExpr literal)
    {
        var raw = literal.Raw.Replace("_", "");
        if (literal.Kind == NumberKind.Hex) return long.Parse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static string Quote(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        return "\"" + escaped + "\"";
    }
}
