using Nebra.Compiler.Codegen;
using Nebra.IR;

namespace Nebra.Compiler.Passes;

public sealed partial class CodegenPass
{
    /// <summary>
    /// Whether a subclass without a constructor of its own has arguments to hand on to
    /// <paramref name="baseClass"/>'s: the nearest constructor up the chain takes any, or the
    /// chain reaches a class declared for code compiled elsewhere, whose constructor is unknown.
    /// </summary>
    private static bool InheritsConstructorArguments(ClassType? baseClass)
    {
        for (var current = baseClass; current != null; current = current.BaseClass)
        {
            if (!current.IsCompiledFromSource) return true;
            if (current.ConstructorType is { } constructor) return constructor.ParamTypes.Count > 0 || constructor.IsVararg;
        }

        return baseClass == null;
    }

    /// <summary>
    /// Whether a derived class's constructor does nothing but create the base instance: no
    /// field defaults of its own, and a constructor, if any, without a body or <c>super</c> call.
    /// </summary>
    private static bool OnlyWrapsBaseConstructor(ClassDecl cd)
    {
        if (cd.Fields.Any(field => !field.IsStatic && field.DefaultValue != null)) return false;
        return cd.Constructor == null || (cd.Constructor.Body.Count == 0 && cd.Constructor.ReturnStmt == null && cd.Constructor.Parameters.Count == 0);
    }

    /// <summary>
    /// The metamethods a class may carry on its table, which Lua looks up on the metatable alone,
    /// so a subclass copies them from its base class.
    /// </summary>
    private static readonly string[] CopiedMetamethods =
    [
        "__add", "__sub", "__mul", "__div", "__mod", "__pow", "__unm", "__concat", "__len", "__eq", "__lt", "__le",
        "__idiv", "__band", "__bor", "__bxor", "__bnot", "__shl", "__shr", "__call", "__tostring", "__close", "__pairs",
    ];

    /// <summary>
    /// The metamethods a subclass of <paramref name="baseClass"/> copies from it. When the whole
    /// chain is compiled from source, those are exactly the ones it declares, often none; a class
    /// declared for code compiled elsewhere may carry any of them.
    /// </summary>
    private static List<string> InheritedMetamethods(ClassType? baseClass)
    {
        var declared = new SortedSet<string>(StringComparer.Ordinal);
        for (var current = baseClass; current != null; current = current.BaseClass)
        {
            if (!current.IsCompiledFromSource) return [.. CopiedMetamethods];

            foreach (var name in current.Methods.Keys)
            {
                if (name == "__op_call") declared.Add("__call");
                else if (CopiedMetamethods.Contains(name)) declared.Add(name);
            }
        }

        return baseClass == null ? [.. CopiedMetamethods] : [.. declared];
    }

    /// <summary>
    /// Whether <paramref name="classType"/> or a class it extends declares the operator
    /// <paramref name="name"/>, such as <c>__op_index</c> for <c>operator []</c>. Lua does not
    /// inherit metamethods, so every class below the one declaring it generates its own.
    /// </summary>
    private static bool HasClassOperator(ClassType classType, string name)
    {
        for (var current = classType; current != null; current = current.BaseClass)
        {
            if (current.Methods.ContainsKey(name)) return true;
        }

        return false;
    }

    /// <summary>
    /// Emits the set of instance fields the class and the classes it extends declare. The index
    /// operators leave these alone, so a declared field still reads <c>nil</c> before it is set
    /// and its assignment stores it on the instance.
    /// </summary>
    private static void EmitDeclaredFieldSet(LuaGenerator gen, string className, ClassType classType)
    {
        var fields = new SortedSet<string>(StringComparer.Ordinal);
        for (var current = classType; current != null; current = current.BaseClass)
            fields.UnionWith(current.InstanceFields.Keys);

        var entries = string.Join(", ", fields.Select(field => $"[\"{field}\"] = true"));
        gen.WriteLine($"{className}.__nebra_fields = {{ {entries} }}");
        gen.WriteSemicolon();
    }

    /// <summary>
    /// Emits the <c>__index</c> of a class with <c>operator []</c>: a member of the class or a
    /// declared field wins, any other key goes to the operator.
    /// </summary>
    private static void EmitIndexMetamethod(LuaGenerator gen, string className)
    {
        gen.WriteLine($"{className}.__index = function(t, k)");
        gen.Indent();
        gen.WriteLine($"local v = {className}[k]");
        gen.WriteLine($"if v ~= nil or {className}.__nebra_fields[k] then return v end");
        gen.WriteLine($"return {className}.__op_index(t, k)");
        gen.Dedent();
        gen.WriteLine("end");
        gen.WriteSemicolon();
    }

    /// <summary>
    /// Emits the <c>__newindex</c> of a class with <c>operator []=</c>: a declared field is stored
    /// on the instance, any other key goes to the operator.
    /// </summary>
    private static void EmitNewIndexMetamethod(LuaGenerator gen, string className)
    {
        gen.WriteLine($"{className}.__newindex = function(t, k, v)");
        gen.Indent();
        gen.WriteLine($"if {className}.__nebra_fields[k] then rawset(t, k, v) return end");
        gen.WriteLine($"{className}.__op_newindex(t, k, v)");
        gen.Dedent();
        gen.WriteLine("end");
        gen.WriteSemicolon();
    }

    /// <summary>
    /// Emits an assignment with a target written through an extension's <c>operator []=</c>. The
    /// values are taken first, as a Lua assignment takes them, then each target receives its own:
    /// such a target by a call of the operator, any other by a plain assignment.
    /// </summary>
    private void EmitAssignThroughExtensions(PassContext ctx, PackageContext pkg, LuaGenerator gen, AssignStmt assign)
    {
        var values = assign.Targets.Select(_ => gen.FreshTemp("__value")).ToList();
        gen.Write("local " + string.Join(", ", values) + " = ");
        EmitExprList(ctx, pkg, gen, assign.Values);
        gen.NewLine();
        gen.WriteSemicolon();

        for (var i = 0; i < assign.Targets.Count; i++)
        {
            if (assign.Targets[i] is IndexAccessExpr { Extension: { } extension } target)
            {
                gen.Write(extension.LuaName + "(");
                EmitExpr(ctx, pkg, gen, target.Object);
                gen.Write(", ");
                EmitExpr(ctx, pkg, gen, target.Index);
                gen.Write($", {values[i]})");
            }
            else
            {
                EmitExpr(ctx, pkg, gen, assign.Targets[i]);
                gen.Write($" = {values[i]}");
            }

            gen.NewLine();
            gen.WriteSemicolon();
        }
    }

    /// <summary>Emits the <c>__call</c> of a class with <c>operator ()</c>, which hands the call to the operator.</summary>
    private static void EmitCallMetamethod(LuaGenerator gen, string className)
    {
        gen.WriteLine($"{className}.__call = function(self, ...) return self:__op_call(...) end");
        gen.WriteSemicolon();
    }
}
