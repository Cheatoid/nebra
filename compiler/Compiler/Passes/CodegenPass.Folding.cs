using System.Globalization;
using Nebra.Compiler.Codegen;
using Nebra.IR;

namespace Nebra.Compiler.Passes;

public sealed partial class CodegenPass
{
    /// <summary>
    /// A value an expression of literals evaluates to while compiling: a number, kept as an
    /// integer or a float the way Lua 5.3 and later do, a string, a boolean or nil.
    /// </summary>
    private sealed record Constant(object? Value)
    {
        public bool IsTruthy => Value is not (null or false);
    }

    /// <summary>
    /// Writes <paramref name="expr"/> as the literal it evaluates to when it consists of literals
    /// and operators Lua applies itself, such as <c>(1 + 2.0) * -0x3</c>. Anything whose result
    /// depends on the runtime, such as a division by zero, a float turned into text or an
    /// operator an extension or class supplies, is left as written.
    /// </summary>
    private static bool TryEmitFolded(LuaGenerator gen, Expr expr)
    {
        if (!TryFold(gen, expr, out var constant) || ConstantText(gen, constant) is not { } text) return false;

        gen.Write(text);
        return true;
    }

    private static bool TryFold(LuaGenerator gen, Expr expr, out Constant constant)
    {
        constant = new Constant(null);
        switch (expr)
        {
            case NumberLiteralExpr number when ParseNumber(gen, number) is { } value:
                constant = new Constant(value);
                return true;
            case StringLiteralExpr text:
                constant = new Constant(text.Value);
                return true;
            case BoolLiteralExpr boolean:
                constant = new Constant(boolean.Value);
                return true;
            case NilLiteralExpr:
                return true;
            case ParenExpr paren:
                return TryFold(gen, paren.Inner, out constant);
            case UnaryExpr { Extension: null } unary when TryFold(gen, unary.Operand, out var operand):
                return FoldUnary(gen, unary.Op, operand, out constant);
            case BinaryExpr { Extension: null, IsRange: false } binary
                when TryFold(gen, binary.Left, out var left) && TryFold(gen, binary.Right, out var right):
                return FoldBinary(gen, binary, left, right, out constant);
            default:
                return false;
        }
    }

    private static object? ParseNumber(LuaGenerator gen, NumberLiteralExpr number)
    {
        var raw = number.Raw.Replace("_", "");
        switch (number.Kind)
        {
            case NumberKind.Int when long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var integer):
                return gen.Features.HasIntegerType ? (object)integer : (double)integer;
            case NumberKind.Hex when raw.Length is > 2 and <= 18 && ulong.TryParse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex):
                return gen.Features.HasIntegerType ? (object)unchecked((long)hex) : (double)hex;
            case NumberKind.Float when double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var real):
                return real;
            default:
                return null;
        }
    }

    private static bool FoldUnary(LuaGenerator gen, UnaryOp op, Constant operand, out Constant constant)
    {
        constant = new Constant(null);
        switch (op)
        {
            case UnaryOp.LogicalNot:
                constant = new Constant(!operand.IsTruthy);
                return true;
            case UnaryOp.Negate when operand.Value is long integer:
                constant = new Constant(unchecked(-integer));
                return true;
            case UnaryOp.Negate when operand.Value is double real:
                constant = new Constant(-real);
                return true;
            case UnaryOp.Length when operand.Value is string text && text.All(char.IsAscii):
                constant = new Constant(gen.Features.HasIntegerType ? (object)(long)text.Length : (double)text.Length);
                return true;
            case UnaryOp.BitwiseNot when operand.Value is long integer && gen.Features.BitwiseStyle == BitwiseStyle.Operator:
                constant = new Constant(~integer);
                return true;
            default:
                return false;
        }
    }

    private static bool FoldBinary(LuaGenerator gen, BinaryExpr binary, Constant left, Constant right, out Constant constant)
    {
        constant = new Constant(null);
        var op = binary.Op;
        if (op == BinaryOp.Add && gen.IsConfiguredConcatOp(op) && (left.Value is string || right.Value is string))
            op = BinaryOp.Concat;

        switch (op)
        {
            case BinaryOp.And:
                constant = left.IsTruthy ? right : left;
                return true;
            case BinaryOp.Or:
                constant = left.IsTruthy ? left : right;
                return true;
            case BinaryOp.NilCoalesce:
                constant = left.Value != null ? left : right;
                return true;
            case BinaryOp.Eq or BinaryOp.Neq when left.Value is not double && right.Value is not double:
            {
                var equal = Equals(left.Value, right.Value) || (left.Value is long a && right.Value is long b && a == b);
                constant = new Constant(op == BinaryOp.Eq ? equal : !equal);
                return true;
            }
            case BinaryOp.Concat when ConcatText(left) is { } first && ConcatText(right) is { } second:
                constant = new Constant(first + second);
                return true;
            case BinaryOp.Lt or BinaryOp.Lte or BinaryOp.Gt or BinaryOp.Gte when left.Value is long or double && right.Value is long or double:
            {
                var (a, b) = (Convert.ToDouble(left.Value, CultureInfo.InvariantCulture), Convert.ToDouble(right.Value, CultureInfo.InvariantCulture));
                constant = new Constant(op switch { BinaryOp.Lt => a < b, BinaryOp.Lte => a <= b, BinaryOp.Gt => a > b, _ => a >= b });
                return true;
            }
            case BinaryOp.Add or BinaryOp.Sub or BinaryOp.Mul or BinaryOp.Div or BinaryOp.FloorDiv or BinaryOp.Mod or BinaryOp.Pow:
                return FoldArithmetic(op, left.Value, right.Value, out constant);
            case BinaryOp.BitwiseAnd or BinaryOp.BitwiseOr or BinaryOp.BitwiseXor or BinaryOp.LShift or BinaryOp.RShift
                when left.Value is long a && right.Value is long b && gen.Features.BitwiseStyle == BitwiseStyle.Operator:
                constant = new Constant(op switch
                {
                    BinaryOp.BitwiseAnd => a & b,
                    BinaryOp.BitwiseOr => a | b,
                    BinaryOp.BitwiseXor => a ^ b,
                    BinaryOp.LShift => Shift(a, b),
                    _ => Shift(a, -b),
                });
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Lua's arithmetic: integers stay integers under <c>+ - * // %</c>, wrapping around on
    /// overflow; <c>/</c> and <c>^</c> and any float operand give a float. A division by zero is
    /// left to the runtime.
    /// </summary>
    private static bool FoldArithmetic(BinaryOp op, object? left, object? right, out Constant constant)
    {
        constant = new Constant(null);
        if (left is long a && right is long b && op is not (BinaryOp.Div or BinaryOp.Pow))
        {
            if (op is BinaryOp.FloorDiv or BinaryOp.Mod && b == 0) return false;
            constant = new Constant(op switch
            {
                BinaryOp.Add => unchecked(a + b),
                BinaryOp.Sub => unchecked(a - b),
                BinaryOp.Mul => unchecked(a * b),
                BinaryOp.FloorDiv => FloorDivide(a, b),
                _ => FloorModulo(a, b),
            });
            return true;
        }

        if (left is not (long or double) || right is not (long or double)) return false;

        var x = Convert.ToDouble(left, CultureInfo.InvariantCulture);
        var y = Convert.ToDouble(right, CultureInfo.InvariantCulture);
        if (op is BinaryOp.Div or BinaryOp.FloorDiv or BinaryOp.Mod && y == 0) return false;

        var result = op switch
        {
            BinaryOp.Add => x + y,
            BinaryOp.Sub => x - y,
            BinaryOp.Mul => x * y,
            BinaryOp.Div => x / y,
            BinaryOp.FloorDiv => Math.Floor(x / y),
            BinaryOp.Mod => x - Math.Floor(x / y) * y,
            _ => Math.Pow(x, y),
        };
        if (!double.IsFinite(result)) return false;

        constant = new Constant(result);
        return true;
    }

    private static long FloorDivide(long a, long b)
    {
        if (a == long.MinValue && b == -1) return a;
        var quotient = a / b;
        return a % b != 0 && (a < 0) != (b < 0) ? quotient - 1 : quotient;
    }

    private static long FloorModulo(long a, long b)
    {
        if (b == -1) return 0;
        var remainder = a % b;
        return remainder != 0 && (remainder < 0) != (b < 0) ? remainder + b : remainder;
    }

    private static long Shift(long value, long by)
    {
        if (by is <= -64 or >= 64) return 0;
        return by >= 0 ? (long)((ulong)value << (int)by) : (long)((ulong)value >> (int)-by);
    }

    /// <summary>
    /// The text <c>..</c> makes of a constant: strings as they are and integers in decimal.
    /// Floats are left out, since how they turn into text differs between Lua versions.
    /// </summary>
    private static string? ConcatText(Constant constant) => constant.Value switch
    {
        string text => text,
        long integer => integer.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>
    /// Lua source for <paramref name="constant"/>, or <c>null</c> when it cannot be written as a
    /// literal. A float keeps a decimal point on targets with an integer subtype so it stays a
    /// float; a negative number is parenthesized so it reads the same next to any operator.
    /// </summary>
    private static string? ConstantText(LuaGenerator gen, Constant constant)
    {
        switch (constant.Value)
        {
            case null:
                return "nil";
            case bool boolean:
                return boolean ? "true" : "false";
            case string text:
                return "\"" + EscapeLuaString(text) + "\"";
            case long integer:
                return integer < 0 ? $"({integer.ToString(CultureInfo.InvariantCulture)})" : integer.ToString(CultureInfo.InvariantCulture);
            case double real:
            {
                if (real == 0 && double.IsNegative(real)) return null;
                var text = real.ToString("R", CultureInfo.InvariantCulture);
                if (gen.Features.HasIntegerType && !text.Contains('.') && !text.Contains('E')) text += ".0";
                return real < 0 ? $"({text})" : text;
            }
            default:
                return null;
        }
    }
}
