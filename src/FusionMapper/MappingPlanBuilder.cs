using System.Reflection;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;

namespace FusionMapper;

static class MappingPlanBuilder
{
    public static Expression BuildCreationExpression<TSource, TTarget>(ParameterExpression sourceParam)
    {
        var targetType = typeof(TTarget);
        var sourceType = typeof(TSource);

        // Выбираем конструктор
        var ctor = SelectConstructor(targetType, sourceType, out var paramExprs) 
                    ?? throw new MappingException($"No suitable constructor found for type '{targetType.FullName}'.");

        // Создаём NewExpression
        NewExpression newExpr;
        if (ctor.GetParameters().Length == 0)
        {
            newExpr = Expression.New(ctor);
        }
        else
        {
            var args = ctor.GetParameters().Select(p => paramExprs[p.Name!]).ToArray();
            newExpr = Expression.New(ctor, args);
        }

        // Инициализированные через конструктор члены
        HashSet<string> initializedMemberNames = [.. ctor.GetParameters().Select(p => p.Name!)];

        // Все записываемые члены target
        var targetMembers = targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Cast<MemberInfo>()
            .Concat(targetType.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => !f.IsInitOnly && !f.IsLiteral))
            .ToArray();

        // Требуемые члены (required)
        var requiredMembers = targetMembers.Where(HasRequiredAttribute).ToArray();

        List<MemberBinding> bindings = [];
        foreach (var member in targetMembers)
        {
            if (initializedMemberNames.Contains(member.Name))
                continue;

            if (TryGetSourceMemberAccess(typeof(TSource), sourceParam, member.Name, out var accessExpr, out _))
            {
                // Конвертация типов
                var targetMemberType = member.GetMemberType();
                accessExpr = ConvertExpression(accessExpr!, targetMemberType);

                var binding = Expression.Bind(member, accessExpr);
                bindings.Add(binding);
                initializedMemberNames.Add(member.Name);
            }
            else
            {
                // Если член required, но не найден – ошибка
                if (requiredMembers.Contains(member))
                {
                    throw new MappingException($"Required member '{member.Name}' cannot be mapped from source type '{sourceType.FullName}'.");
                }
                // Иначе пропускаем (оставляем значение по умолчанию)
            }
        }

        // Проверяем, что все required инициализированы
        if (ctor.GetCustomAttribute<SetsRequiredMembersAttribute>() == null)
        {
            foreach (var req in requiredMembers.Where(req => !initializedMemberNames.Contains(req.Name)))
            {
                throw new MappingException($"Required member '{req.Name}' was not initialized.");
            }
        }

        return bindings.Count > 0
            ? Expression.MemberInit(newExpr, bindings)
            : newExpr;
    }

    public static Expression BuildAssignmentExpression<TSource, TTarget>(ParameterExpression sourceParam, ParameterExpression targetParam)
    {
        var targetType = typeof(TTarget);
        var sourceType = typeof(TSource);

        // Записываемые члены (исключая init-only)
        var targetMembers = targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && !IsInitOnly(p))
            .Cast<MemberInfo>()
            .Concat(targetType.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => !f.IsInitOnly && !f.IsLiteral))
            .ToArray();
            
        List<Expression> assignments = [];
        foreach (var member in targetMembers)
        {
            if (TryGetSourceMemberAccess(typeof(TSource), sourceParam, member.Name, out var accessExpr, out _))
            {
                var targetMemberType = member.GetMemberType();
                accessExpr = ConvertExpression(accessExpr!, targetMemberType);

                var assign = Expression.Assign(Expression.MakeMemberAccess(targetParam, member), accessExpr);
                assignments.Add(assign);
            }
            // Если не найдено, не изменяем target
        }

        return Expression.Block(assignments);
    }

    static ConstructorInfo? SelectConstructor(Type targetType, Type sourceType, out Dictionary<string, Expression> parameterExpressions)
    {
        parameterExpressions = [];
        var constructors = targetType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        // Сначала пробуем конструктор без параметров
        var ctorNoParams = constructors.FirstOrDefault(c => c.GetParameters().Length == 0);
        if (ctorNoParams != null)
            return ctorNoParams;

        // Ищем конструкторы, все параметры которых маппятся из source
        List<(ConstructorInfo Ctor, Dictionary<string, Expression> ParamExprs)> candidates = [];
        foreach (var ctor in constructors)
        {
            var parameters = ctor.GetParameters();
            Dictionary<string, Expression> dict = [];
            bool allFound = true;
            // Используем фиктивный параметр для построения выражений доступа
            var dummy = Expression.Parameter(sourceType, "dummy");
            foreach (var param in parameters.Select(p => p.Name!))
            {
                if (TryGetSourceMemberAccess(sourceType, dummy, param, out var accessExpr, out _))
                {
                    dict[param] = accessExpr!;
                }
                else
                {
                    allFound = false;
                    break;
                }
            }
            if (allFound)
                candidates.Add((ctor, dict));
        }

        if (candidates.Count == 0)
            return null;

        // Выбираем конструктор с наибольшим числом параметров
        var maxParamCount = candidates.Max(c => c.Ctor.GetParameters().Length);
        var best = candidates.Where(c => c.Ctor.GetParameters().Length == maxParamCount).ToArray();
        if (best.Length > 1)
        {
            throw new MappingException($"Ambiguous constructors for type '{targetType.FullName}'. Multiple constructors with the same number of bindable parameters.");
        }

        var (Ctor, ParamExprs) = best[0];
        parameterExpressions = ParamExprs;
        return Ctor;
    }



    static Expression ConvertExpression(Expression expr, Type targetType)
    {
        if (expr.Type == targetType)
            return expr;

        // Поддержка Nullable<T> -> T и T -> Nullable<T>
        if (expr.Type.IsGenericType && expr.Type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            var underlying = Nullable.GetUnderlyingType(expr.Type);
            if (targetType == underlying)
                return Expression.Convert(expr, targetType); // .Value неявно, но лучше явно
        }
        else if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            var underlying = Nullable.GetUnderlyingType(targetType);
            if (expr.Type == underlying)
                return expr; // неявное преобразование
        }

        // Общее преобразование (может упасть в рантайме)
        return Expression.Convert(expr, targetType);
    }

    static bool TryGetSourceMemberAccess(Type sourceType, Expression sourceExpr, string targetMemberName, out Expression? accessExpr, out MemberInfo? memberInfo)
    {
        var members = sourceType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Cast<MemberInfo>()
            .Concat(sourceType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            .ToArray();

        // 1. Точное совпадение (с учётом регистра)
        var exact = members.Where(m => m.Name.Equals(targetMemberName, StringComparison.Ordinal)).ToArray();
        if (exact.Length == 1)
        {
            memberInfo = exact[0];
            accessExpr = Expression.MakeMemberAccess(sourceExpr, memberInfo);
            return true;
        }
        if (exact.Length > 1)
        {
            throw new MappingException($"Ambiguous exact match for member '{targetMemberName}'.");
        }

        // 2. Регистронезависимое совпадение
        var insensitive = members.Where(m => m.Name.Equals(targetMemberName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (insensitive.Length == 1)
        {
            memberInfo = insensitive[0];
            accessExpr = Expression.MakeMemberAccess(sourceExpr, memberInfo);
            return true;
        }
        if (insensitive.Length > 1)
        {
            throw new MappingException($"Ambiguous case-insensitive match for member '{targetMemberName}'. Candidates: {string.Join(", ", insensitive.Select(m => m.Name))}");
        }

        // 3. Flattening – пока не реализован, но задел
        // TODO: реализовать flattening согласно спецификации

        accessExpr = null;
        memberInfo = null;
        return false;
    }

    public static Type GetMemberType(this MemberInfo member) => member switch
    {
        PropertyInfo p => p.PropertyType,
        FieldInfo f => f.FieldType,
        _ => throw new InvalidOperationException("Unsupported member type")
    };

    public static bool IsInitOnly(PropertyInfo property)
    {
        var setMethod = property.SetMethod;
        if (setMethod == null)
            return true; // нет сеттера

        var parameters = setMethod.GetParameters();
        if (parameters.Length == 0)
            return false;

        var lastParam = parameters[^1];
        var modReqs = lastParam.GetRequiredCustomModifiers();
        return modReqs.Any(t => t.Name == "IsExternalInit" && t.Namespace == "System.Runtime.CompilerServices");
    }

    public static bool HasRequiredAttribute(MemberInfo member)
        => member.GetCustomAttribute<RequiredMemberAttribute>() != null;
}