using System.Collections.Concurrent;
using Nebra.Compiler.Annotations;
using Nebra.Configuration;
using Nebra.Diagnostics;
using Nebra.IR;
using Nebra.PackageManager;

namespace Nebra.Compiler.Passes;

/// <summary>
/// Build-scoped pass that discovers annotation definition files (<c>.neb</c>) from
/// <c>Config.Annotations</c>, sub-compiles each to Lua, extracts their <c>meta</c>
/// declaration and registers them in an <see cref="AnnotationRegistry"/> stored on
/// <c>PassContext.Cache</c>. Consumed by <see cref="ApplyAnnotationsPass"/>.
/// </summary>
public sealed class ResolveAnnotationsPass() : Pass(PassName, PassScope.PerBuild)
{
    public const string PassName = "ResolveAnnotations";

    public override bool Run(PassContext context)
    {
        var registry = new AnnotationRegistry();
        context.Cache[AnnotationRegistry.CacheKey] = registry;

        var baseDir = context.Config.ProjectRoot;
        foreach (var entry in context.Config.Annotations)
        {
            var fullPath = Path.IsPathRooted(entry) ? entry : Path.Combine(baseDir, entry);
            if (Directory.Exists(fullPath))
            {
                foreach (var file in Directory.EnumerateFiles(fullPath, "*.neb", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".d.neb", StringComparison.OrdinalIgnoreCase) || !LooksLikeAnnotationFile(file)) continue;
                    LoadAnnotationFile(context, registry, file);
                }
            }
            else if (File.Exists(fullPath))
            {
                LoadAnnotationFile(context, registry, fullPath);
            }
            else
            {
                context.Diag.Report(TextSpan.Empty, DiagnosticCode.ErrAnnotationPathNotFound, entry);
            }
        }

        foreach (var pkg in GetInstalledPackages(context))
        {
            var roots = pkg.AnnotationRoots.Count > 0
                ? pkg.AnnotationRoots
                : [pkg.RootPath];
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in InstalledPackages.EnumerateFilesSafely(root, "*.neb"))
                {
                    if (file.EndsWith(".d.neb", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!LooksLikeAnnotationFile(file)) continue;
                    LoadAnnotationFile(context, registry, file);
                }
            }
        }

        return true;
    }

    private static IReadOnlyList<InstalledPackage> GetInstalledPackages(PassContext context)
    {
        if (context.Cache.TryGetValue(InstalledPackages.CacheKey, out var cached)
            && cached is IReadOnlyList<InstalledPackage> list)
            return list;
        return InstalledPackages.Discover(context.Config.ProjectRoot);
    }

    /// <summary>
    /// Cheap textual heuristic that tells annotation files from the helper modules next to them,
    /// and avoids sub-compiling every <c>.neb</c> file in a package. A valid annotation file must
    /// export an <c>annotation</c> table and an <c>apply</c> function.
    /// </summary>
    private static bool LooksLikeAnnotationFile(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch { return false; }
        return LooksLikeAnnotationSource(text);
    }

    public static bool LooksLikeAnnotationSource(string text)
    {
        return text.Contains("export local annotation", StringComparison.Ordinal)
            || text.Contains("export function apply", StringComparison.Ordinal)
            || text.Contains("export local function apply", StringComparison.Ordinal);
    }

    /// <summary>
    /// The outcome of compiling one annotation file: its definition, or the problems that kept it
    /// from being one.
    /// </summary>
    private sealed record AnnotationLoad(AnnotationDefinition? Definition, IReadOnlyList<(DiagnosticCode Code, object[] Args)> Problems,
        IReadOnlyDictionary<string, string>? Dependencies = null)
    {
        /// <summary>Whether every module the file imported still reads as it did when it was compiled.</summary>
        public bool IsCurrent()
        {
            return Dependencies == null || Dependencies.All(dependency => ReadsAs(dependency.Key, dependency.Value));
        }

        private static bool ReadsAs(string path, string source)
        {
            try
            {
                return File.ReadAllText(path) == source;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Compiled annotation files, keyed by everything the compilation depends on. Annotation
    /// plugins change rarely, and a long-running process such as the language server would
    /// otherwise compile each of them again on every build.
    /// </summary>
    private static readonly ConcurrentDictionary<string, AnnotationLoad> Loads = new(StringComparer.Ordinal);

    private static void LoadAnnotationFile(PassContext ctx, AnnotationRegistry registry, string path)
    {
        var subConfig = ctx.Config.Clone();
        subConfig.AnnotationFile = true;
        subConfig.Code = new CodeSection
        {
            IndexBase = 1,
            ConcatOperator = ctx.Config.Code.ConcatOperator,
            StringInterpolation = ctx.Config.Code.StringInterpolation,
            AltBooleanOperators = ctx.Config.Code.AltBooleanOperators,
            Semicolons = ctx.Config.Code.Semicolons,
            StripUnused = ctx.Config.Code.StripUnused,
            Libs = [..ctx.Config.Code.Libs],
        };

        string source;
        try
        {
            source = File.ReadAllText(path);
        }
        catch (IOException)
        {
            ctx.Diag.Report(TextSpan.Empty, DiagnosticCode.ErrAnnotationCompileFailed, Path.GetFileNameWithoutExtension(path));
            return;
        }

        var key = string.Join("\0", path, source, subConfig.Target, subConfig.ProjectRoot,
            subConfig.Code.ConcatOperator, subConfig.Code.StringInterpolation, subConfig.Code.AltBooleanOperators,
            subConfig.Code.Semicolons, subConfig.Code.StripUnused,
            string.Join(",", subConfig.Code.Libs), string.Join(",", subConfig.Globals));
        if (!Loads.TryGetValue(key, out var load) || !load.IsCurrent())
        {
            load = CompileAnnotationFile(path, source, subConfig);
            Loads[key] = load;
        }

        foreach (var (code, args) in load.Problems)
        {
            ctx.Diag.Report(TextSpan.Empty, code, args);
        }

        if (load.Definition != null && !registry.TryAdd(load.Definition))
        {
            ctx.Diag.Report(TextSpan.Empty, DiagnosticCode.ErrAnnotationDuplicateName, load.Definition.Name);
        }
    }

    private static AnnotationLoad CompileAnnotationFile(string path, string source, Configuration.Config subConfig)
    {
        var annotationName = Path.GetFileNameWithoutExtension(path);
        var problems = new List<(DiagnosticCode, object[])>();

        var subCompiler = new NebraCompiler { Config = subConfig };
        subCompiler.AddSource(path, source);

        var pm = new PassManager();
        pm.BuildOrder(PassManager.AnnotationFilePipeline);
        var ok = pm.Run(subCompiler.Diagnostics, subCompiler.AllPackages,
            subCompiler.TypeUniverse, subCompiler.SymAlloc, subCompiler.ScopeAlloc,
            subCompiler.NodeAlloc, subCompiler.Names, subCompiler.Cache, subCompiler.Config);

        if (!ok || subCompiler.Diagnostics.HasErrors)
        {
            return new AnnotationLoad(null, [(DiagnosticCode.ErrAnnotationCompileFailed, [annotationName])]);
        }

        var files = subCompiler.AllPackages.SelectMany(pkg => pkg.Files).Distinct().ToList();
        var file = files.FirstOrDefault(candidate => candidate.Filename == path);
        if (file == null || file.GeneratedLua == null)
        {
            return new AnnotationLoad(null, [(DiagnosticCode.ErrAnnotationCompileFailed, [annotationName])]);
        }

        if (ContainsAnnotations(file.Hir.Body))
        {
            problems.Add((DiagnosticCode.ErrAnnotationInAnnotationFile, []));
        }

        if (!TryExtractMeta(file.Hir.Body, out var targets, out var parameters, out var metaError))
        {
            problems.Add((DiagnosticCode.ErrAnnotationMetaInvalid, [annotationName, metaError ?? "unknown"]));
            return new AnnotationLoad(null, problems);
        }

        if (!HasApplyFunction(file.Hir.Body))
        {
            problems.Add((DiagnosticCode.ErrAnnotationMissingApply, [annotationName]));
            return new AnnotationLoad(null, problems);
        }

        var modules = files
            .Where(module => module != file && module.Filename != null && module.GeneratedLua != null && !module.IsDeclarationFile)
            .ToDictionary(module => module.Filename!, module => module.GeneratedLua!);
        var imports = files
            .Where(importer => importer.Filename != null)
            .ToDictionary(importer => importer.Filename!, importer => ResolvedImports(subCompiler.Cache, importer, modules));
        var dependencies = files
            .Where(module => module != file && module.Filename != null && !module.IsDeclarationFile && File.Exists(module.Filename))
            .ToDictionary(module => module.Filename!, module => module.Content);

        var definition = new AnnotationDefinition(annotationName, targets, parameters, file.GeneratedLua, path, modules, imports);
        return new AnnotationLoad(definition, problems, dependencies);
    }

    /// <summary>
    /// The file each module name <paramref name="importer"/> imports resolved to, limited to the
    /// modules that were compiled to Lua.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ResolvedImports(Dictionary<string, object> cache, PreparsedFile importer,
        IReadOnlyDictionary<string, string> modules)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var import in importer.Hir.Body.OfType<ImportStmt>())
        {
            if (!cache.TryGetValue($"import_resolved:{importer.Filename}:{import.Module.Name}", out var entry)) continue;
            if (entry is not ResolvedModule { File.Filename: { } target } || !modules.ContainsKey(target)) continue;
            resolved[import.Module.Name] = target;
        }

        return resolved;
    }

    public static bool TryExtractMeta(List<Stmt> body, out HashSet<AnnotationTargetKind> targets, out List<AnnotationParamSpec> parameters, out string? error)
    {
        targets = [AnnotationTargetKind.Function];
        parameters = [];
        error = null;

        TableConstructorExpr? metaTable = null;
        foreach (var stmt in body)
        {
            if (stmt is ExportStmt { Declaration: LocalDecl ld }
                && ld.Variables.Count == 1
                && ld.Variables[0].Name.Name == "annotation"
                && ld.Values.Count == 1
                && ld.Values[0] is TableConstructorExpr tc)
            {
                metaTable = tc;
                break;
            }
        }

        if (metaTable == null)
        {
            error = "missing `export local annotation = { ... }` declaration";
            return false;
        }

        foreach (var field in metaTable.Fields)
        {
            if (field.Name == null) continue;
            switch (field.Name.Name)
            {
                case "target":
                case "targets":
                    if (!TryParseTargets(field.Value, out var parsedTargets, out var targetErr))
                    {
                        error = targetErr;
                        return false;
                    }
                    targets = parsedTargets;
                    break;
                case "params":
                    if (field.Value is TableConstructorExpr paramsTable)
                    {
                        foreach (var paramField in paramsTable.Fields)
                        {
                            if (paramField.Name == null) continue;
                            if (paramField.Value is not TableConstructorExpr paramSpec) continue;
                            var (typeName, defaultValue, required) = ParseParamSpec(paramSpec);
                            parameters.Add(new AnnotationParamSpec(paramField.Name.Name, typeName, defaultValue, required));
                        }
                    }
                    break;
            }
        }

        return true;
    }

    private static bool TryParseTargets(Expr expr, out HashSet<AnnotationTargetKind> targets, out string? error)
    {
        targets = [];
        error = null;

        if (expr is TableConstructorExpr listExpr)
        {
            foreach (var f in listExpr.Fields)
            {
                if (f.Name != null)
                {
                    error = "`meta.target` list must contain only positional entries";
                    return false;
                }
                if (!TryParseSingleTarget(f.Value, out var single, out var singleErr))
                {
                    error = singleErr;
                    return false;
                }
                targets.Add(single);
            }
            if (targets.Count == 0)
            {
                error = "`meta.target` list must contain at least one AnnotationTarget";
                return false;
            }
            return true;
        }

        if (!TryParseSingleTarget(expr, out var parsed, out var err))
        {
            error = err;
            return false;
        }
        targets.Add(parsed);
        return true;
    }

    private static bool TryParseSingleTarget(Expr expr, out AnnotationTargetKind target, out string? error)
    {
        target = AnnotationTargetKind.Function;
        error = null;

        string? targetName = null;
        if (expr is DotAccessExpr dot && dot.Object is NameExpr ne && ne.Name.Name == "AnnotationTarget")
            targetName = dot.FieldName.Name;
        else if (expr is StringLiteralExpr sl)
            targetName = sl.Value;
        else if (expr is NameExpr bare)
            targetName = bare.Name.Name;

        if (targetName == null)
        {
            error = "`meta.target` entries must be AnnotationTarget enum values";
            return false;
        }

        if (!Enum.TryParse<AnnotationTargetKind>(targetName, ignoreCase: true, out target))
        {
            error = $"unknown AnnotationTarget '{targetName}'";
            return false;
        }
        return true;
    }

    private static (string typeName, object? defaultValue, bool required) ParseParamSpec(TableConstructorExpr spec)
    {
        var typeName = "any";
        object? defaultValue = null;
        var required = true;
        var hasDefault = false;

        foreach (var field in spec.Fields)
        {
            if (field.Name == null) continue;
            switch (field.Name.Name)
            {
                case "type":
                    if (field.Value is StringLiteralExpr s) typeName = s.Value;
                    break;
                case "default":
                    defaultValue = ConstFoldLiteral(field.Value);
                    hasDefault = true;
                    break;
                case "required":
                    if (field.Value is BoolLiteralExpr b) required = b.Value;
                    break;
            }
        }
        if (hasDefault) required = false;
        return (typeName, defaultValue, required);
    }

    /// <summary>
    /// Folds a literal expression into a plain C# value. Returns null for unsupported shapes.
    /// </summary>
    private static object? ConstFoldLiteral(Expr expr)
    {
        return expr switch
        {
            NilLiteralExpr => null,
            BoolLiteralExpr b => b.Value,
            StringLiteralExpr s => s.Value,
            NumberLiteralExpr n => ParseNumber(n.Raw),
            UnaryExpr { Op: UnaryOp.Negate, Operand: NumberLiteralExpr nn } => NegateNumber(ParseNumber(nn.Raw)),
            TableConstructorExpr t => FoldTable(t),
            _ => null,
        };
    }

    private static object ParseNumber(string raw)
    {
        if (long.TryParse(raw, out var l)) return l;
        if (double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return 0L;
    }

    private static object NegateNumber(object n) => n switch
    {
        long l => -l,
        double d => -d,
        _ => n,
    };

    private static object? FoldTable(TableConstructorExpr t)
    {
        var hasNamed = t.Fields.Any(f => f.Name != null);
        if (hasNamed)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var f in t.Fields)
                if (f.Name != null) dict[f.Name.Name] = ConstFoldLiteral(f.Value);
            return dict;
        }
        var list = new List<object?>();
        foreach (var f in t.Fields) list.Add(ConstFoldLiteral(f.Value));
        return list;
    }

    /// <summary>
    /// Recursively checks whether any declaration in the body carries <c>@</c>-annotations.
    /// Annotation definition files must not use annotations themselves to prevent recursion.
    /// </summary>
    private static bool ContainsAnnotations(List<Stmt> body) => AnnotationsIn(body).Any();

    /// <summary>The annotations written on the top-level declarations of an annotation file and on their members.</summary>
    public static IEnumerable<Annotation> AnnotationsIn(List<Stmt> body)
    {
        foreach (var stmt in body)
        {
            var decl = stmt is ExportStmt export ? export.Declaration : stmt as Decl;
            var annotations = decl switch
            {
                FunctionDecl function => function.Annotations,
                LocalFunctionDecl function => function.Annotations,
                LocalDecl local => local.Annotations,
                ClassDecl classDecl => classDecl.Annotations
                    .Concat(classDecl.Fields.SelectMany(field => field.Annotations))
                    .Concat(classDecl.Methods.SelectMany(method => method.Annotations)),
                EnumDecl enumDecl => enumDecl.Annotations.Concat(enumDecl.Members.SelectMany(member => member.Annotations)),
                InterfaceDecl interfaceDecl => interfaceDecl.Annotations,
                _ => []
            };

            foreach (var annotation in annotations)
            {
                yield return annotation;
            }
        }
    }

    public static bool HasApplyFunction(List<Stmt> body)
    {
        foreach (var stmt in body)
        {
            if (stmt is ExportStmt ex)
            {
                if (ex.Declaration is FunctionDecl fd && fd.NamePath.Count == 1 && fd.NamePath[0].Name == "apply")
                    return true;
                if (ex.Declaration is LocalFunctionDecl lfd && lfd.Name.Name == "apply")
                    return true;
            }
        }
        return false;
    }
}
