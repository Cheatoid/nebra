using Nebra.Compiler.Annotations;
using Nebra.Diagnostics;
using Nebra.IR;

namespace Nebra.Compiler.Passes;

/// <summary>
/// Checks an annotation definition file the way the annotation loader reads it, with every
/// problem placed where it is written: annotations inside the file, an <c>annotation</c> table
/// the loader cannot read, a missing <c>apply</c> function. Gives the <c>args</c> parameter of
/// <c>apply</c> the shape the table's <c>params</c> declare, so its fields are checked and
/// completed. Runs only for annotation files.
/// </summary>
public sealed class CheckAnnotationFilePass() : Pass(PassName, PassScope.PerFile, noErrors: false, ResolveLibsPass.PassName)
{
    public const string PassName = "CheckAnnotationFile";

    private const string MetaName = "annotation";
    private const string ApplyName = "apply";

    public override bool Run(PassContext context)
    {
        if (!context.Config.AnnotationFile || context.File == null || context.File.IsDeclarationFile) return true;

        var body = context.File.Hir.Body;
        var name = Path.GetFileNameWithoutExtension(context.File.Filename ?? "");
        var fileStart = new TextSpan(context.File.Filename, 1, 1, 1, 1);

        foreach (var annotation in ResolveAnnotationsPass.AnnotationsIn(body))
        {
            context.Diag.Report(annotation.Span, DiagnosticCode.ErrAnnotationInAnnotationFile);
        }

        if (ResolveAnnotationsPass.TryExtractMeta(body, out _, out var parameters, out var error))
        {
            TypeArguments(context, body, parameters);
        }
        else
        {
            context.Diag.Report(MetaSpan(body) ?? fileStart, DiagnosticCode.ErrAnnotationMetaInvalid, name, error ?? "unknown");
        }

        if (!ResolveAnnotationsPass.HasApplyFunction(body))
        {
            context.Diag.Report(fileStart, DiagnosticCode.ErrAnnotationMissingApply, name);
        }

        return true;
    }

    private static TextSpan? MetaSpan(List<Stmt> body)
    {
        return body
            .Select(statement => statement is ExportStmt export ? export.Declaration : statement)
            .OfType<LocalDecl>()
            .FirstOrDefault(local => local.Variables.Any(variable => variable.Name.Name == MetaName))
            ?.Span;
    }

    /// <summary>
    /// Annotates the second parameter of <c>apply</c>, when it has no type of its own, with a
    /// table type holding one field per declared parameter; one that is optional and has no
    /// default may be nil.
    /// </summary>
    private static void TypeArguments(PassContext context, List<Stmt> body, List<AnnotationParamSpec> parameters)
    {
        var apply = ApplyParameters(body);
        if (apply is not { Count: >= 2 } || apply[1].TypeAnnotation != null || apply[1].IsVararg) return;

        var original = apply[1];
        var span = original.Span;
        var fields = parameters
            .Select(parameter => new StructTypeField(new NameRef(parameter.Name, span), FieldType(context, parameter, span), false, span))
            .ToList();

        apply[1] = new Parameter(original.ID, original.Name, new StructTypeRef(context.NodeAlloc.Next(), span, fields), false, original.DefaultValue, span);
    }

    private static List<Parameter>? ApplyParameters(List<Stmt> body)
    {
        foreach (var statement in body)
        {
            var declaration = statement is ExportStmt export ? export.Declaration : statement;
            switch (declaration)
            {
                case FunctionDecl { NamePath.Count: 1, MethodName: null } function when function.NamePath[0].Name == ApplyName:
                    return function.Parameters;
                case LocalFunctionDecl function when function.Name.Name == ApplyName:
                    return function.Parameters;
            }
        }

        return null;
    }

    private static TypeRef FieldType(PassContext context, AnnotationParamSpec parameter, TextSpan span)
    {
        TypeRef type = parameter.TypeName.ToLowerInvariant() switch
        {
            "string" => new PrimitiveTypeRef(context.NodeAlloc.Next(), span, TypeKind.PrimitiveString),
            "number" => new PrimitiveTypeRef(context.NodeAlloc.Next(), span, TypeKind.PrimitiveNumber),
            "boolean" or "bool" => new PrimitiveTypeRef(context.NodeAlloc.Next(), span, TypeKind.PrimitiveBool),
            "table" or "any" => new PrimitiveTypeRef(context.NodeAlloc.Next(), span, TypeKind.PrimitiveAny),
            _ => new NamedTypeRef(context.NodeAlloc.Next(), span, new NameRef(parameter.TypeName, span))
        };

        var mayBeNil = !parameter.Required && parameter.DefaultValue == null && type.Kind != TypeKind.PrimitiveAny;
        return mayBeNil ? new NullableTypeRef(context.NodeAlloc.Next(), span, type) : type;
    }
}
