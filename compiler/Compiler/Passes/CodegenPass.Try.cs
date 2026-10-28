using Nebra.Compiler.Codegen;
using Nebra.IR;

namespace Nebra.Compiler.Passes;

public sealed partial class CodegenPass
{
    /// <summary>
    /// The name of the try helper while the body or catch clause of a try statement is emitted,
    /// whose <c>return</c> leaves the function the statement is in: it returns the helper's
    /// marker before the values, so the helper can tell it from the block simply ending.
    /// <c>null</c> elsewhere, also in a function declared inside such a block.
    /// </summary>
    private string? _tryReturnHelper;

    /// <summary>
    /// Emits a try statement as a call of the try helper, each block as a function of its own. A
    /// statement whose blocks return from the enclosing function returns the values the helper
    /// hands on; one whose blocks use the enclosing function's <c>...</c> passes it on.
    /// </summary>
    private void EmitTry(PassContext ctx, PackageContext pkg, LuaGenerator gen, TryStmt tryStmt)
    {
        var helper = gen.GetTryHelper();
        var (returns, usesVarargs) = TryBlockUses(tryStmt);
        var enclosingReturn = _tryReturnHelper;

        if (returns)
        {
            gen.WriteLine("do");
            gen.Indent();
            gen.Write("local __try = ");
        }

        gen.Write(helper + "(function(...)");
        gen.NewLine();
        EmitTryBlock(ctx, pkg, gen, tryStmt.Body, helper);
        gen.Write("end, ");

        if (tryStmt.Catch != null)
        {
            var errorName = tryStmt.Catch.Name != null ? ResolveName(ctx, pkg, tryStmt.Catch.Name) : "_";
            gen.Write($"function({errorName}, ...)");
            gen.NewLine();
            EmitTryBlock(ctx, pkg, gen, tryStmt.Catch.Body, helper);
            gen.Write("end, ");
        }
        else
        {
            gen.Write("nil, ");
        }

        if (tryStmt.Finally != null)
        {
            gen.Write("function(...)");
            gen.NewLine();
            EmitTryBlock(ctx, pkg, gen, tryStmt.Finally, null);
            gen.Write("end, ");
        }
        else
        {
            gen.Write("nil, ");
        }

        gen.Write(tryStmt.Catch == null && tryStmt.Finally == null ? "true" : "false");
        gen.Write(usesVarargs ? ", ...)" : ")");
        gen.NewLine();
        gen.WriteSemicolon();

        if (!returns) return;

        var unpackFn = gen.Features.HasTableUnpack ? "table.unpack" : "unpack";
        var marker = enclosingReturn != null ? enclosingReturn + "_return, " : "";
        gen.WriteLine($"if __try then return {marker}{unpackFn}(__try, 1, __try.n) end");
        gen.Dedent();
        gen.WriteLine("end");
    }

    private void EmitTryBlock(PassContext ctx, PackageContext pkg, LuaGenerator gen, List<Stmt> block, string? returnHelper)
    {
        var enclosingReturn = _tryReturnHelper;
        _tryReturnHelper = returnHelper;
        gen.Indent();
        EmitStmtList(ctx, pkg, gen, block);
        gen.Dedent();
        _tryReturnHelper = enclosingReturn;
    }

    /// <summary>
    /// Whether the body or catch clause of <paramref name="tryStmt"/> returns from the enclosing
    /// function, and whether any of its blocks reads the enclosing function's <c>...</c>; code
    /// in a function declared inside the blocks does neither.
    /// </summary>
    private static (bool Returns, bool UsesVarargs) TryBlockUses(TryStmt tryStmt)
    {
        var nodes = NodeTree.Index(tryStmt);
        var nested = nodes.Values
            .Where(node => node is FunctionDefExpr or FunctionDecl or LocalFunctionDecl or ClassDecl or ExtendDecl)
            .SelectMany(node => node is Expr expression ? NodeTree.Index(expression).Keys : NodeTree.Index((Stmt)node).Keys)
            .ToHashSet();
        var own = nodes.Values.Where(node => !nested.Contains(node.ID)).ToList();

        var finallyNodes = tryStmt.Finally != null
            ? tryStmt.Finally.SelectMany(statement => NodeTree.Index(statement).Keys).ToHashSet()
            : [];
        var returns = own.Any(node => node is ReturnStmt or GuardStmt && !finallyNodes.Contains(node.ID));
        return (returns, own.Any(node => node is VarargExpr));
    }
}
