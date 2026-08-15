using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FusionMapper.SourceGeneration;

static class InterceptorGenerator
{
    public static string Execute(IEnumerable<Candidate> candidates)
    {

        StringBuilder sb = GenerateHeader();

        foreach (var group in candidates.GroupBy(c => (c.Source, c.Target, c.Kind)))
        {
            foreach (var candidate in group)
            {
                AppendInterceptsLocation(sb, candidate.Interceptable);                
            }
            GenerateInterceptorMethod(sb, group.Key.Kind, group.Key.Source, group.Key.Target);
            sb.AppendLine();
        }
        sb.AppendLine($$"""
                }
            }
            """);
        return sb.ToString();

    }

    

    private static StringBuilder GenerateHeader()
    {
        var sb =  new StringBuilder($$""""
            #nullable enable

            namespace System.Runtime.CompilerServices
            {
                // this type is needed by the compiler to implement interceptors - it doesn't need to
                // come from the runtime itself, though
            
                [global::System.Diagnostics.Conditional("DEBUG")] // not needed post-build, so: evaporate
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
            
            namespace FusionMapper.Interceptors
            {
            """");

        sb.AppendLine("""
                static file class SourceAccessor<T>
                {
                    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "<value>P")]
                    public static extern ref T GetValue(in global::FusionMapper.FusionSource<T> t);

                    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "<value>P")]
                    public static extern ref global::System.Linq.IQueryable<T> GetQueryable(in global::FusionMapper.FusionProjection<T> t);
                }
            
            """);



        sb.AppendLine($$"""
                static file class Interceptors
                {
                    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticMethod, Name = nameof(GetCreationLambda))]
                    static extern global::System.Linq.Expressions.LambdaExpression GetCreationLambda(global::FusionMapper.FusionMapper.State c, Type source, Type target);

                    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticMethod, Name = nameof(GetCreationDelegate))]
                    static extern global::System.Delegate GetCreationDelegate(global::FusionMapper.FusionMapper.State c, Type source, Type target);
            
                    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticMethod, Name = nameof(GetAssignmentDelegate))]
                    static extern global::System.Delegate GetAssignmentDelegate(global::FusionMapper.FusionMapper.State c, Type source, Type target);
            
                    [global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticMethod, Name = nameof(Rewrite))]
                    static extern global::System.Linq.IQueryable<T> Rewrite<T>(global::FusionMapper.FusionMapper.State c, global::System.Linq.IQueryable<T> query);
            
            """);

        return sb;
    }


    private static void GenerateInterceptorMethod(StringBuilder sb, CallKind kind, TypeModel source, TypeModel target)
    {
        var methodName = "__" + target.NullableAnnotatedIndentifier;
        if (source.Annotation != NullableAnnotation.NotAnnotated) methodName = "Nullable" + methodName;

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
        sb.AppendLine();
    }


    private static void AppendInterceptsLocation(StringBuilder sb, InterceptableLocation location)
    {
        sb.AppendLine(
            $"""
                    [global::System.Runtime.CompilerServices.InterceptsLocation({location.Version}, "{location.Data}")]
            """);
    }

    private static void AppendSourceToMethod(StringBuilder sb, TypeModel source, TypeModel target, string methodName)
    {
        var receiver = $"global::FusionMapper.{FusionMapperInterceptorGenerator.FusionSourceType}";
        sb.AppendLine($$"""
                public static {{target.Signature}} Map__{{methodName}}(this in {{receiver}}<{{source.Signature}}> receiver)
                {
        """);
        AppendGetSource(sb, source);
        AppendCreationNullChecks(sb, source, target);
        sb.AppendLine($$"""
                    var func = (global::System.Func<{{source.Signature}}, {{target.Signature}}>)GetCreationDelegate(null!, typeof({{source.Runtime}}), typeof({{target.Runtime}}));
                    return func(source);
                }
        """);
    }
    private static void AppendSourceToExistingMethod(StringBuilder sb, TypeModel source, TypeModel target, string methodName)
    {
        var receiver = $"global::FusionMapper.{FusionMapperInterceptorGenerator.FusionSourceType}";
        sb.AppendLine($$"""
                public static {{target.Signature}} Map__{{methodName}}(this in {{receiver}}<{{source.Signature}}> receiver, {{target.Signature}} target)
                {
        """);
        AppendGetSource(sb, source);
        AppendAssignmentNullChecks(sb, source, target);
        sb.AppendLine($$"""
                    var action = (global::System.Action<{{source.Signature}}, {{target.Signature}}>)GetAssignmentDelegate(null!, typeof({{source.Runtime}}), typeof({{target.Runtime}}));
                    action(source, target);
                    return target;
                }
        """);
    }

    private static void AppendProjectionToMethod(StringBuilder sb, TypeModel source, TypeModel target, string methodName)
    {
        var receiver = $"global::FusionMapper.{FusionMapperInterceptorGenerator.FusionProjectionType}";
        sb.AppendLine($$"""
                public static global::System.Linq.IQueryable<{{target.Signature}}> Project__{{methodName}}(this in {{receiver}}<{{source.Signature}}> receiver)
                {
                    ref System.Linq.IQueryable<{{source.Signature}}> source = ref SourceAccessor<{{source.Runtime}}>.GetQueryable(in receiver);
                    global::System.ArgumentNullException.ThrowIfNull(source);
                    var rewrittenSource = Rewrite(null!, source);
                    var lambda = (global::System.Linq.Expressions.Expression<global::System.Func<{{source.Signature}}, {{target.Signature}}>>)GetCreationLambda(null!, typeof({{source.Runtime}}), typeof({{target.Runtime}}));
                    return global::System.Linq.Queryable.Select<{{source.Signature}}, {{target.Signature}}>(rewrittenSource, lambda);
                }
        """);
    }

    private static void AppendGetSource(StringBuilder sb, TypeModel source)
    {
        if (source.Annotation != NullableAnnotation.NotAnnotated)
        {
            sb.AppendLine("#pragma warning disable CS8620");
        }
        sb.AppendLine($$"""
                    ref {{source.Runtime}} source = ref SourceAccessor<{{source.Runtime}}>.GetValue(in receiver);
        """);
        if (source.Annotation != NullableAnnotation.NotAnnotated)
        {
            sb.AppendLine("#pragma warning restore CS8620");
        }
    }

    private static void AppendCreationNullChecks(StringBuilder sb, TypeModel source, TypeModel target)
    {
        if (source.IsNullableByNullability && target.IsNullableByNullability)
        {
            sb.AppendLine("            if (source is null) return default!;");
        }
        else if (source.IsReference && !target.IsNullableByNullability)
        {
            sb.AppendLine("            global::System.ArgumentNullException.ThrowIfNull(source);");
        }
    }

    private static void AppendAssignmentNullChecks(StringBuilder sb, TypeModel source, TypeModel target)
    {
        if (source.CanBeNullRuntime || source.IsNullableValue)
        {
            if (source.IsNullableValue)
            {
                sb.AppendLine("            if (!source.HasValue) return target;");
            }
            else if (source.IsReference)
            {
                if (source.IsNullableByNullability)
                {
                    sb.AppendLine("            if (source is null) return target;");
                }
                else
                {
                    sb.AppendLine("            global::System.ArgumentNullException.ThrowIfNull(source);");
                }
            }
        }

        if (target.IsReference)
        {
            sb.AppendLine("            global::System.ArgumentNullException.ThrowIfNull(target);");
        }
    }

}