using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FusionMapper.SourceGenerator;

class SourceEmmiter
{
    static readonly string AssemblyName = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name;
    static readonly string AssemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString() ?? "1.0.0.0";
    public static string EmitMappers(SourceProductionContext spc, ImmutableArray<Mapped> input)
    {
        StringBuilder sb = new(input.Length * 300);
        AppendGenerateFileHeader(sb);
        sb.AppendLine("#pragma warning disable CS8321"); // The local function 'Map__FusionMapper_Tests_ItemSource____To__FusionMapper_Tests_ItemTarget__' is declared but never used
        sb.AppendLine("#pragma warning disable CS8629"); // Suppress erors Nullable<T> -> T
        sb.AppendLine();
        sb.AppendLine($"namespace {AssemblyName};");
        sb.AppendLine();
        sb.AppendLine($$"""
            [global::System.CodeDom.Compiler.GeneratedCodeAttribute("{{AssemblyName}}", "{{AssemblyVersion}}")]
            static class Generated
            {
            """);
        
        EmitMappers(sb, spc, input);

        sb.AppendLine("}");
        sb.AppendLine("#pragma warning restore CS8629");
        sb.AppendLine("#pragma warning restore CS8321");
        return sb.ToString();
    }

    public static string EmitInitializer(ImmutableArray<Mapped> candidates, int dotnetVersion)
    {
        StringBuilder sb = new(candidates.Count() * 200);

        AppendGenerateFileHeader(sb);
        sb.AppendLine($"namespace {AssemblyName};");
        sb.AppendLine();
        sb.AppendLine($$"""
            static file class Initalizer
            {
                [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticField, Name = "cache")]
                static extern ref global::System.Collections.Concurrent.ConcurrentDictionary<(global::System.Type Source, global::System.Type Target), global::System.Linq.Expressions.LambdaExpression> GetCache(in global::FusionMapper.ExpressionRewriter c);
            
                [global::System.CodeDom.Compiler.GeneratedCodeAttribute("{{AssemblyName}}", "{{AssemblyVersion}}")]
                [global::System.Runtime.CompilerServices.ModuleInitializer]
                internal static void Initialize()
                {
                    var cache = GetCache(null!);
            """);

        EmitInitializer(sb, candidates, dotnetVersion);

        if (dotnetVersion < 9)
        {
            sb.AppendLine();
            sb.AppendLine("""
                    private static void SetField(global::System.Type type, string name, object value)
                    {
                        var field = type.GetField(
                            name,
                            global::System.Reflection.BindingFlags.Static |
                            global::System.Reflection.BindingFlags.NonPublic);

                        if (field is null)
                            return;

                        field.SetValue(null, value);
                    }
                """);
        }
        sb.AppendLine("}");

        if (dotnetVersion >= 9)
        {
            sb.AppendLine();
            sb.AppendLine($$"""
                [global::System.CodeDom.Compiler.GeneratedCodeAttribute("{{AssemblyName}}", "{{AssemblyVersion}}")]
                static file class FusionMapperAccessor<TSource, TTarget>
                {
                    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticField, Name = "creator")]
                    public static extern ref global::System.Func<TSource, TTarget> Creator(global::FusionMapper.FusionMapper<TSource, TTarget> t);
            
                    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticField, Name = "assigner")]
                    public static extern ref global::System.Func<TSource, TTarget, TTarget> Assigner(global::FusionMapper.FusionMapper<TSource, TTarget> t);
                
                    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticField, Name = "projector")]
                    public static extern ref global::System.Linq.Expressions.Expression<global::System.Func<TSource, TTarget>> Projector(global::FusionMapper.FusionMapper<TSource, TTarget> t);
                }
                """);
        }
        return sb.ToString();
    }


    public static string EmitInterceptors(
        IEnumerable<Interceptable> candidates,
        AccessorFieldNames accessorFields)
    {
        StringBuilder sb = new();
        AppendGenerateFileHeader(sb);

        sb.AppendLine($$"""
            namespace System.Runtime.CompilerServices
            {
                [global::System.Diagnostics.Conditional("DEBUG")]
                [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]
                sealed file class InterceptsLocationAttribute : global::System.Attribute
                {
                    public InterceptsLocationAttribute(int version, string data)
                    {
                        _ = version;
                        _ = data;
                    }
                }
            }
            #pragma warning disable CS8321

            namespace {{AssemblyName}}
            {
                static file class SourceAccessor<T>
                {
                    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "{{accessorFields.SourceValueField}}")]
                    public static extern ref T GetValue(in global::FusionMapper.FusionSource<T> t);

                    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "{{accessorFields.ProjectionValueField}}")]
                    public static extern ref global::System.Linq.IQueryable<T> GetQueryable(in global::FusionMapper.FusionProjection<T> t);
                }

                static file class Interceptors
                {

            """);

        var groups = candidates.GroupBy(c => (c.Source, c.Target, c.Kind));
        foreach (var group in groups)
        {
            var (source, target, kind) = group.Key;
            var methodName = GetMethodName(source, target, kind);

            foreach (var interceptable in group)
            {
                var location = interceptable.InterceptableLocation;
                Indent(sb, 2);
                sb.AppendLine($"""[global::System.Runtime.CompilerServices.InterceptsLocation({location.Version}, "{location.Data}")]""");
            }
            EmitInterceptor(sb, kind, source, target, methodName);
            sb.AppendLine();
        }

        sb.AppendLine("""
                }
            }
            #pragma warning restore CS8321
            """);

        return sb.ToString();
    }


    private static void EmitInitializer(StringBuilder sb, ImmutableArray<Mapped> candidates, int dotnetVersion)
    {
        sb.AppendLine("#pragma warning disable CS8974"); // Suppress Converting method group 'method' to non-delegate type 'type'. Did you intend to invoke the method?

        var projectionsToAddToCache = from m in candidates
                                      where m.IsInsideExpressionTree
                                      group m by (m.Source, m.Target) into g
                                      select g.Key;

        foreach (var (source, target) in projectionsToAddToCache)
        {
            var methodName = $"global::{AssemblyName}.Generated." + GetMethodName(source, target, CallKind.ProjectionTo);
            Indent(sb, 2);
            sb.AppendLine($"cache.TryAdd((typeof({source.Runtime}), typeof({target.Runtime})),{methodName});");
        }
        sb.AppendLine();

        var methods = from m in candidates
                      where !m.IsInsideExpressionTree
                      group m by (m.Source, m.Target, m.Kind) into g
                      select g.Key;
        foreach (var method in methods)
        {
            var (source, target, kind) = method;
            var methodName = $"global::{AssemblyName}.Generated." + GetMethodName(source, target, kind);
            if (dotnetVersion >= 9)
            {
                EmitRegistration(sb, kind, source, target, methodName);
            }
            else
            {
                EmitNet8Registration(sb, kind, source, target, methodName);
            }
        }

        sb.AppendLine("#pragma warning restore CS8974");
        sb.AppendLine("     }");
    }


    private static void EmitInterceptor(
        StringBuilder sb,
        CallKind kind,
        TypeModel source,
        TypeModel target,
        string methodName)
    {
        AppendGeneratedMethodAttributes(sb, 2);

        switch (kind)
        {
            case CallKind.SourceTo:
                AppendSourceToMethod(sb, source, target, methodName);
                break;

            case CallKind.SourceToExisting:
                AppendSourceToExistingMethod(sb, source, target, methodName);
                break;

            case CallKind.ProjectionTo:
                AppendProjectionToMethod(sb, source, target, methodName);
                break;
        }
    }


    private static void AppendSourceToMethod(
        StringBuilder sb,
        TypeModel source,
        TypeModel target,
        string methodName)
    {
        var receiver = $"global::FusionMapper.{FusionMapperInterceptorGenerator.FusionSourceType}";

        sb.AppendLine($$"""
                    public static {{target.Signature}} {{methodName}}(this in {{receiver}}<{{source.Signature}}> receiver)
                    {
            """);
        AppendGetSource(sb, source);
        sb.AppendLine($$"""
                        return global::{{AssemblyName}}.Generated.{{methodName}}(source);
                    }
            """);
    }

    private static void AppendSourceToExistingMethod(
        StringBuilder sb,
        TypeModel source,
        TypeModel target,
        string methodName)
    {
        var receiver = $"global::FusionMapper.{FusionMapperInterceptorGenerator.FusionSourceType}";

        sb.AppendLine($$"""
                    public static {{target.Signature}} {{methodName}}(this in {{receiver}}<{{source.Signature}}> receiver, {{target.Signature}} target)
                    {
            """);
        AppendGetSource(sb, source);
        sb.AppendLine($$"""
                        return global::{{AssemblyName}}.Generated.{{methodName}}(source, target);
                    }
            """);
    }

    private static void AppendProjectionToMethod(
        StringBuilder sb,
        TypeModel source,
        TypeModel target,
        string methodName)
    {
        var receiver = $"global::FusionMapper.{FusionMapperInterceptorGenerator.FusionProjectionType}";

        sb.AppendLine($$"""
                public static global::System.Linq.IQueryable<{{target.Signature}}> {{methodName}}(this in {{receiver}}<{{source.Signature}}> receiver)
                {
                    ref global::System.Linq.IQueryable<{{source.Signature}}> source = ref SourceAccessor<{{source.Signature}}>.GetQueryable(in receiver);
                    var rewrittenSource = global::FusionMapper.ExpressionRewriter.Rewrite(source);
                    return global::System.Linq.Queryable.Select(rewrittenSource, global::{{AssemblyName}}.Generated.{{methodName}});
                }
        """);
    }



    private static void AppendGetSource(StringBuilder sb, TypeModel source)
    {
        if (source.Annotation != NullableAnnotation.NotAnnotated)
        {
            sb.AppendLine("#pragma warning disable CS8620");
        }
        Indent(sb, 3);
        sb.AppendLine($"ref {source.Signature} source = ref SourceAccessor<{source.Signature}>.GetValue(in receiver);");

        if (source.Annotation != NullableAnnotation.NotAnnotated)
        {
            sb.AppendLine("#pragma warning restore CS8620");
        }
    }

    private static void AppendIndented(StringBuilder sb, IEnumerable<string> lines, int level)
    {
        foreach (var line in lines)
        {
            Indent(sb, level);
            sb.AppendLine(line);
        }
    }


    private static void EmitMappers(StringBuilder sb, SourceProductionContext spc, IEnumerable<Mapped> candidates)
    {
        var regularCalls = candidates.Where(c => !c.IsInsideExpressionTree).ToLookup(c => (c.Source, c.Target, c.Kind, c.Mapping));

        var insideExpressionTree = candidates
            .Where(c => c.IsInsideExpressionTree)
            .ToLookup(c => (c.Source, c.Target, CallKind.ProjectionTo, c.Mapping));

        var groups = regularCalls.Concat(insideExpressionTree.Where(g => !regularCalls.Contains(g.Key)));
        foreach (var group in groups)
        {
            var(source, target, kind, mapping) = group.Key;
            try
            {
                var methodName = GetMethodName(source, target, kind);
                switch (kind)
                {
                    case CallKind.SourceTo:
                        var emittedMapping = MappingEmitter.Emit(kind, mapping).ToArray();
                        AppendGeneratedMethodAttributes(sb, 1);
                        sb.AppendLine($"    public static {target.Signature} {methodName}({source.Signature} source)");
                        sb.AppendLine("    {");
                        AppendIndented(sb, emittedMapping, 2);
                        sb.AppendLine("    }");
                        sb.AppendLine();
                        break;

                    case CallKind.SourceToExisting:
                        emittedMapping = [.. MappingEmitter.Emit(kind, mapping)];
                        AppendGeneratedMethodAttributes(sb, 1);
                        sb.AppendLine($"    public static {target.Signature} {methodName}({source.Signature} source, {target.Signature} target)");
                        sb.AppendLine("    {");
                        AppendIndented(sb, emittedMapping, 2);
                        sb.AppendLine("    }");
                        sb.AppendLine();
                        break;
                    case CallKind.ProjectionTo:
                        emittedMapping = [.. MappingEmitter.Emit(kind, mapping)];
                        sb.AppendLine($$"""
                                public static global::System.Linq.Expressions.Expression<global::System.Func<{{source.Signature}}, {{target.Signature}}>> {{methodName}} = {{emittedMapping[0]}};
                            """);
                        sb.AppendLine();
                        break;
                }
            }
            catch (Exception ex)
            {
                var loc = group.First().Location!;

                spc.ReportDiagnostic(Diagnostic.Create(
                    FusionMapperInterceptorGenerator.IncompatibleMappingRule,
                    loc,
                    group.Skip(1).Select(x => x.Location),
                    source.FullName,
                    target.FullName,
                    ex.Message));
            }
        }

    }


    private static void EmitRegistration(
    StringBuilder sb,
    CallKind kind,
    TypeModel source,
    TypeModel target,
    string methodName)
    {
        var accessorType= $"FusionMapperAccessor<{source.Signature}, {target.Signature}>";

        Indent(sb, 2);
        switch (kind)
        {
            case CallKind.SourceTo:
                sb.AppendLine($"{accessorType}.Creator(null!) = {methodName};");
                break;

            case CallKind.SourceToExisting:
                sb.AppendLine($"{accessorType}.Assigner(null!) = {methodName};");
                break;

            case CallKind.ProjectionTo:
                sb.AppendLine($"{accessorType}.Projector(null!) = {methodName};");
                break;
        }
    }

    private static void EmitNet8Registration(
    StringBuilder sb,
    CallKind kind,
    TypeModel source,
    TypeModel target,
    string methodName)
    {
        var mapperType = $"typeof(global::FusionMapper.FusionMapper<{source.Runtime}, {target.Runtime}>)";
        Indent(sb, 2);
        switch (kind)
        {
            case CallKind.SourceTo:
                sb.AppendLine($"SetField({mapperType}, \"creator\", {methodName});");
                break;

            case CallKind.SourceToExisting:
                sb.AppendLine($"SetField({mapperType}, \"assigner\", {methodName});");
                break;

            case CallKind.ProjectionTo:
                sb.AppendLine($"SetField({mapperType}, \"projector\", {methodName});");
                break;
        }
    }

    private static void AppendGeneratedMethodAttributes(StringBuilder sb, int level)
    {
        Indent(sb, level);
        sb.AppendLine($"[global::System.CodeDom.Compiler.GeneratedCodeAttribute(\"{AssemblyName}\", \"{AssemblyVersion}\")]");
        Indent(sb, level);
        sb.AppendLine("[global::System.Runtime.CompilerServices.CompilerGeneratedAttribute]");
        Indent(sb, level);
        sb.AppendLine("[global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining | global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]");
    }

    private static string GetMethodName(TypeModel source, TypeModel target, CallKind kind)
    {
        var prefix = kind switch
        {
            CallKind.SourceTo => "Map",
            CallKind.SourceToExisting => "Map",
            CallKind.ProjectionTo => "Project",
            _ => "Map"
        };

        return $"{prefix}__{source.NullableAnnotatedIndentifier}__To__{target.NullableAnnotatedIndentifier}";
    }


    private static void AppendGenerateFileHeader(StringBuilder sb)
    {
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
    }

    private static void Indent(StringBuilder sb, int level)
    {
        for (int i = 0; i < level * 4; i++)
        {
            sb.Append(' ');
        }
    }
}