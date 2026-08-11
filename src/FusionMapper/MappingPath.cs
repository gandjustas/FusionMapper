namespace FusionMapper;

sealed class MappingPath
{
    private readonly Stack<PathElement> path = [];

    void ThrowIfRecursive(PathElement p)
    {
        if (path.Contains(p))
        {
            throw new MappingException(
                $"Recursive mapping detected between '{p.Source.FullName}' and '{p.Target.FullName}'. " +
                $"Path: {string.Join(" -> ", path.Select(p => p.Source.Name + "->" + p.Target.Name))} -> {p.Source.Name}. " +
                "Recursive and cyclic type graphs are not supported.");
        }
    }

    public Scope Push(Type target, Type source)
    {
        PathElement p = new(target, source);
        ThrowIfRecursive(p);
        path.Push(p);
        return new Scope(this);
    }

    public readonly struct Scope(MappingPath owner) : IDisposable
    {
        public void Dispose()
        {
            owner.path.Pop();
        }
    }

    private readonly record struct PathElement(Type Target, Type Source);
}
