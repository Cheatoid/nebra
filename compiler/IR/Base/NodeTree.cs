namespace Nebra.IR;

/// <summary>
/// Walks a program tree: every statement, expression, parameter and type reference beneath a
/// node, indexed by ID. Shared by passes that look at every node and by the language server.
/// A tree recovered from a syntax error may lack a part, such as the value of an unfinished
/// <c>x = </c>; the walk skips what is missing.
/// </summary>
public static class NodeTree
{
    /// <summary>The script and every node in it.</summary>
    public static Dictionary<NodeID, Node> Index(IRScript script)
    {
        var reg = new Dictionary<NodeID, Node> { [script.ID] = script };
        RegisterStmtList(script.Body, reg);
        if (script.Return != null) RegisterStmt(script.Return, reg);
        return reg;
    }

    /// <summary><paramref name="statement"/> and every node nested inside it.</summary>
    public static Dictionary<NodeID, Node> Index(Stmt statement)
    {
        var reg = new Dictionary<NodeID, Node>();
        RegisterStmt(statement, reg);
        return reg;
    }

    /// <summary><paramref name="expression"/> and every node nested inside it.</summary>
    public static Dictionary<NodeID, Node> Index(Expr expression)
    {
        var reg = new Dictionary<NodeID, Node>();
        RegisterExpr(expression, reg);
        return reg;
    }

    private static void RegisterStmtList(List<Stmt> stmts, Dictionary<NodeID, Node> reg)
    {
        foreach (var s in stmts) RegisterStmt(s, reg);
    }

    private static void RegisterStmt(Stmt? stmt, Dictionary<NodeID, Node> reg)
    {
        if (stmt == null) return;
        reg[stmt.ID] = stmt;
        switch (stmt)
        {
            case FunctionDecl fd:
                foreach (var p in fd.Parameters) reg[p.ID] = p;
                RegisterStmtList(fd.Body, reg);
                if (fd.ReturnStmt != null) RegisterStmt(fd.ReturnStmt, reg);
                break;
            case LocalFunctionDecl lfd:
                foreach (var p in lfd.Parameters) reg[p.ID] = p;
                RegisterStmtList(lfd.Body, reg);
                if (lfd.ReturnStmt != null) RegisterStmt(lfd.ReturnStmt, reg);
                break;
            case LocalDecl ld:
                foreach (var v in ld.Values) RegisterExpr(v, reg);
                break;
            case EnumDecl ed:
                foreach (var m in ed.Members)
                    if (m.Value != null) RegisterExpr(m.Value, reg);
                break;
            case AssignStmt a:
                foreach (var t in a.Targets) RegisterExpr(t, reg);
                foreach (var v in a.Values) RegisterExpr(v, reg);
                break;
            case ExprStmt es:
                RegisterExpr(es.Expression, reg);
                break;
            case DoBlockStmt db:
                RegisterStmtList(db.Body, reg);
                break;
            case TryStmt tryStmt:
                if (tryStmt.Catch != null) reg[tryStmt.Catch.ID] = tryStmt.Catch;
                foreach (var block in tryStmt.Blocks) RegisterStmtList(block, reg);
                break;
            case WhileStmt ws:
                RegisterExpr(ws.Condition, reg);
                RegisterStmtList(ws.Body, reg);
                break;
            case RepeatStmt rs:
                RegisterStmtList(rs.Body, reg);
                RegisterExpr(rs.Condition, reg);
                break;
            case IfStmt ifs:
                RegisterExpr(ifs.Condition, reg);
                RegisterStmtList(ifs.Body, reg);
                foreach (var ei in ifs.ElseIfs) RegisterStmtList(ei.Body, reg);
                if (ifs.ElseBody != null) RegisterStmtList(ifs.ElseBody, reg);
                break;
            case NumericForStmt nf:
                RegisterExpr(nf.Start, reg);
                RegisterExpr(nf.Limit, reg);
                if (nf.Step != null) RegisterExpr(nf.Step, reg);
                RegisterStmtList(nf.Body, reg);
                break;
            case GenericForStmt gf:
                foreach (var iter in gf.Iterators) RegisterExpr(iter, reg);
                RegisterStmtList(gf.Body, reg);
                break;
            case ReturnStmt ret:
                foreach (var v in ret.Values) RegisterExpr(v, reg);
                break;
            case ExportStmt exp:
                RegisterStmt(exp.Declaration, reg);
                break;
            case MatchStmt ms:
                RegisterExpr(ms.Scrutinee, reg);
                foreach (var arm in ms.Arms)
                {
                    if (arm.Pattern.ValueExpr != null) RegisterExpr(arm.Pattern.ValueExpr, reg);
                    if (arm.Guard != null) RegisterExpr(arm.Guard, reg);
                    RegisterStmtList(arm.Body, reg);
                }
                break;
            case DeferStmt ds:
                if (ds.Call != null) RegisterExpr(ds.Call, reg);
                if (ds.Block != null) RegisterStmtList(ds.Block, reg);
                break;
            case GuardStmt gs:
                RegisterExpr(gs.Condition, reg);
                if (gs.ElseExpr != null) RegisterExpr(gs.ElseExpr, reg);
                break;
            case ClassDecl cd:
                foreach (var classConstructor in cd.Constructors)
                {
                    foreach (var p in classConstructor.Parameters) reg[p.ID] = p;
                    RegisterStmtList(classConstructor.Body, reg);
                    if (classConstructor.ReturnStmt != null) RegisterStmt(classConstructor.ReturnStmt, reg);
                }
                foreach (var method in cd.Methods)
                {
                    foreach (var p in method.Parameters) reg[p.ID] = p;
                    RegisterStmtList(method.Body, reg);
                    if (method.ReturnStmt != null) RegisterStmt(method.ReturnStmt, reg);
                }
                foreach (var accessor in cd.Accessors)
                {
                    foreach (var p in accessor.Parameters) reg[p.ID] = p;
                    RegisterStmtList(accessor.Body, reg);
                    if (accessor.ReturnStmt != null) RegisterStmt(accessor.ReturnStmt, reg);
                }
                foreach (var field in cd.Fields)
                {
                    if (field.DefaultValue != null) RegisterExpr(field.DefaultValue, reg);
                }
                break;
            case InterfaceDecl:
                break;
        }
    }

    private static void RegisterExpr(Expr? expr, Dictionary<NodeID, Node> reg)
    {
        if (expr == null) return;
        reg[expr.ID] = expr;
        switch (expr)
        {
            case ParenExpr pe: RegisterExpr(pe.Inner, reg); break;
            case BinaryExpr bin: RegisterExpr(bin.Left, reg); RegisterExpr(bin.Right, reg); break;
            case UnaryExpr un: RegisterExpr(un.Operand, reg); break;
            case DotAccessExpr dot: RegisterExpr(dot.Object, reg); break;
            case IndexAccessExpr idx: RegisterExpr(idx.Object, reg); RegisterExpr(idx.Index, reg); break;
            case FunctionCallExpr call:
                RegisterExpr(call.Callee, reg);
                foreach (var a in call.Arguments) RegisterExpr(a, reg);
                break;
            case MethodCallExpr mc:
                RegisterExpr(mc.Object, reg);
                foreach (var a in mc.Arguments) RegisterExpr(a, reg);
                break;
            case FunctionDefExpr fd:
                foreach (var p in fd.Parameters) reg[p.ID] = p;
                RegisterStmtList(fd.Body, reg);
                if (fd.ReturnStmt != null) RegisterStmt(fd.ReturnStmt, reg);
                break;
            case TableConstructorExpr tc:
                foreach (var f in tc.Fields)
                {
                    if (f.Key != null) RegisterExpr(f.Key, reg);
                    RegisterExpr(f.Value, reg);
                }
                break;
            case InterpolatedStringExpr interp:
                foreach (var p in interp.Parts)
                {
                    if (p is InterpExprPart ep) RegisterExpr(ep.Expression, reg);
                }
                break;
            case NonNilAssertExpr nna:
                RegisterExpr(nna.Inner, reg);
                break;
            case IncDecExpr incDec:
                RegisterExpr(incDec.Target, reg);
                break;
            case TypeCheckExpr tchk:
                RegisterExpr(tchk.Inner, reg);
                break;
            case TypeCastExpr tcast:
                RegisterExpr(tcast.Inner, reg);
                break;
            case TypeOfExpr tof:
                RegisterExpr(tof.Inner, reg);
                break;
            case InstanceOfExpr iof:
                RegisterExpr(iof.Inner, reg);
                break;
            case NewExpr ne:
                foreach (var a in ne.Arguments) RegisterExpr(a, reg);
                break;
            case SuperCallExpr sc:
                foreach (var a in sc.Arguments) RegisterExpr(a, reg);
                break;
        }
    }

}
