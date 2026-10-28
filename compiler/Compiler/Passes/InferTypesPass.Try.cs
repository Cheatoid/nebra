using Nebra.IR;

namespace Nebra.Compiler.Passes;

public sealed partial class InferTypesPass
{
    /// <summary>
    /// The number of try, catch and finally blocks around the statement being checked, within the
    /// function it belongs to. Their code runs in a function of its own under <c>pcall</c>, so no
    /// <c>break</c>, <c>continue</c> or <c>goto</c> can leave them.
    /// </summary>
    private int _tryDepth;

    /// <summary>The number of finally blocks around the statement being checked, which cannot return.</summary>
    private int _finallyDepth;

    /// <summary>
    /// Checks a try statement. A loop around it is out of reach of its blocks, and the error a
    /// catch clause binds may be any value <c>error</c> was given.
    /// </summary>
    private void ResolveTry(PassContext pc, TryStmt tryStmt)
    {
        var enclosingLoops = _loopDepth;
        _loopDepth = 0;
        _tryDepth++;
        try
        {
            ResolveStmts(pc, tryStmt.Body);

            if (tryStmt.Catch != null)
            {
                if (tryStmt.Catch.Name is { } name && name.Sym != SymID.Invalid)
                    pc.Pkg!.Syms.SetType(name.Sym, pc.Types.PrimAny.ID);
                ResolveStmts(pc, tryStmt.Catch.Body);
            }

            if (tryStmt.Finally != null)
            {
                _finallyDepth++;
                ResolveStmts(pc, tryStmt.Finally);
                _finallyDepth--;
            }
        }
        finally
        {
            _tryDepth--;
            _loopDepth = enclosingLoops;
        }
    }
}
