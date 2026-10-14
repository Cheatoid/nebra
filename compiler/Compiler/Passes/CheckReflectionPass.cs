using Nebra.Configuration;
using Nebra.Diagnostics;
using Nebra.IR;

namespace Nebra.Compiler.Passes;

/// <summary>
/// Checks the uses of runtime reflection against what the build records: an id passed to
/// <c>reflect.get</c>, <c>reflect.ref</c> or <c>reflect.implementorsOf</c> that names nothing,
/// a declaration <c>reflect.Of</c> asks about that the configured mode leaves out, and any use
/// of <c>reflect</c> while reflection is turned off. Keeps the registry in the cache for tooling.
/// </summary>
public sealed class CheckReflectionPass() : Pass(PassName, PassScope.PerBuild, noErrors: false, InferTypesPass.PassName)
{
    public const string PassName = "CheckReflection";

    private const string ReflectName = "reflect";
    private static readonly string[] IdMethods = ["get", "ref", "implementorsOf"];

    public override bool Run(PassContext context)
    {
        var registry = ReflectionRegistry.Collect(context);
        context.Cache[ReflectionRegistry.CacheKey] = registry;

        foreach (var package in context.Pkgs.Where(package => !package.IsLibrary))
        {
            foreach (var file in package.Files.Where(file => !file.IsDeclarationFile))
            {
                foreach (var call in NodeTree.Index(file.Hir).Values.OfType<FunctionCallExpr>())
                {
                    CheckCall(context, package, registry, call);
                }
            }
        }

        return true;
    }

    private static void CheckCall(PassContext context, PackageContext package, ReflectionRegistry registry, FunctionCallExpr call)
    {
        if (call.Callee is not DotAccessExpr { Object: NameExpr owner } access) return;
        if (!IsReflectLibrary(package, owner)) return;

        var method = access.FieldName.Name;
        if (context.Config.Reflection.Mode == ReflectionMode.None)
        {
            context.Diag.Report(call.Span, DiagnosticCode.WarnReflectionDisabled, method);
            return;
        }

        if (IdMethods.Contains(method) && call.Arguments is [StringLiteralExpr { Value.Length: > 0 } id, ..])
        {
            CheckId(context, registry, id);
        }

        if (method == "Of" && call.Arguments is [NameExpr subject])
        {
            CheckSubject(context, package, registry, subject);
        }
    }

    private static void CheckId(PassContext context, ReflectionRegistry registry, StringLiteralExpr id)
    {
        var entry = registry.Find(id.Value);
        if (entry == null)
        {
            context.Diag.Report(id.Span, DiagnosticCode.WarnUnknownReflectionId, id.Value);
            return;
        }

        if (!entry.Recorded)
        {
            context.Diag.Report(id.Span, DiagnosticCode.WarnNotReflected, entry.Name);
        }
    }

    private static void CheckSubject(PassContext context, PackageContext package, ReflectionRegistry registry, NameExpr subject)
    {
        if (!package.Syms.GetByID(subject.Name.Sym, out var symbol)) return;
        if (symbol.Kind is not (SymbolKind.Class or SymbolKind.Interface or SymbolKind.Enum)) return;
        if (!context.Types.GetByID(symbol.Type, out var type) || type.ReflectionId == null) return;

        var entry = registry.Find(type.ReflectionId);
        if (entry is { Recorded: false })
        {
            context.Diag.Report(subject.Span, DiagnosticCode.WarnNotReflected, entry.Name);
        }
    }

    /// <summary>Whether <paramref name="owner"/> is the built-in <c>reflect</c> library rather than a name of the program's own.</summary>
    private static bool IsReflectLibrary(PackageContext package, NameExpr owner)
    {
        return owner.Name.Name == ReflectName
            && package.Syms.GetByID(owner.Name.Sym, out var symbol)
            && symbol.Flags.HasFlag(SymbolFlags.External);
    }
}
